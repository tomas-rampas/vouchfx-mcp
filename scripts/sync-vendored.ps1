#Requires -Version 7.0
<#
.SYNOPSIS
    Syncs or verifies the vendored engine artefacts (vendored/) against the
    commit pinned in the repo-root ENGINE_PIN file.

.DESCRIPTION
    vouchfx-mcp vendors three byte-exact copies of engine-authored artefacts —
    the composed JSON Schema, the generated language reference, and the
    recipe library — from the vouchfx engine repository at the exact commit
    recorded in ENGINE_PIN. This script is the single source of truth for
    both producing those copies (-Update) and detecting drift between them
    and the pinned commit (-Verify — the CI gate in
    .github/workflows/build.yml's vendored-drift job).

    Byte fidelity is the entire point: -Update writes each HTTP response body
    to disk exactly as received (no re-encoding, no newline normalisation,
    no BOM addition), and -Verify compares by SHA-256 hash of the raw file
    bytes, never as decoded text — so a single differing byte anywhere in a
    65 KB schema file is caught just as reliably as a whole-file rewrite.

.PARAMETER LocalEngineRepo
    Explicitly read the same three artefacts from immutable objects in a local
    Git repository at ENGINE_PIN's full commit SHA. Never reads working files
    or fetches missing objects. Requires Git supporting --no-lazy-fetch; older
    Git fails closed. Replacement objects are disabled. Each artefact is
    limited to 4 MiB and each native Git process to 30 seconds. Relative paths
    use the current PowerShell filesystem location; filesystem PSDrives work,
    and other providers are refused.

.PARAMETER Update
    Download all three artefacts at the pinned commit into a temporary
    directory and, only once every download has succeeded, copy them into
    vendored/, overwriting whatever is there. The download phase is
    all-or-nothing: a failed download aborts before any file in vendored/ is
    touched. The subsequent local copy step is NOT itself transactional — a
    Copy-Item failure partway through (e.g. disk full, permission denied on
    the third file) can leave vendored/ with a mix of old and new content.
    This is an accepted tradeoff, not a gap: -Update is a maintainer-run,
    interactive command whose result is always reviewed as a `git diff`
    before committing, so a partial copy would show up there as an
    incomplete/inconsistent diff rather than silently shipping — unlike
    -Verify (what CI actually runs), which only reads vendored/ and never
    writes to it.

.PARAMETER Verify
    Download all three artefacts at the pinned commit to a temporary
    location and byte-compare each (via SHA-256) against the corresponding
    file already committed in vendored/, printing one OK/DRIFT line per file
    that names both the file and the pinned ref. This is the default mode
    when neither switch is supplied, and is what CI runs. Exits 0 only if
    every file matches the pinned commit; exits non-zero on any drift,
    missing vendored file, or download failure.

.EXAMPLE
    pwsh ./scripts/sync-vendored.ps1
    Verifies vendored/ against the current ENGINE_PIN (default mode, no
    switches needed).

.EXAMPLE
    pwsh ./scripts/sync-vendored.ps1 -Update
    Re-downloads all three artefacts at the pinned commit and overwrites
    vendored/ with them. Run this after bumping ENGINE_PIN, then commit the
    result.

.EXAMPLE
    pwsh ./scripts/sync-vendored.ps1 -Update -LocalEngineRepo ../vouchfx
    Reads the exact pinned commit's blobs locally, including for an unpublished
    candidate. Use the same argument with -Verify to check their raw bytes.

.NOTES
    Intended to be invoked as  pwsh -File scripts/sync-vendored.ps1 [-Update]
    (exactly how CI and the examples above run it) rather than dot-sourced:
    every exit path below calls `exit` with an explicit code, which would
    close an interactive dot-sourcing session's shell along with the script.
#>
[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(ParameterSetName = 'Update')]
    [switch]
    $Update,

    [Parameter(ParameterSetName = 'Verify')]
    [switch]
    $Verify,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $LocalEngineRepo
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Invoke-WebRequest's progress-bar rendering is slow (especially on
# non-Windows runners writing to a captured, non-interactive stream) and adds
# nothing here — three small files download in well under a second without it.
$ProgressPreference = 'SilentlyContinue'

# ── Path mapping: vendored/<local file> -> engine repo path (relative to the ──
# vouchfx engine repo root) at the commit ENGINE_PIN records. Add a new pair
# here — and to vendored/README.md's mapping table, and to the
# EmbeddedResource wiring in src/Vouchfx.Mcp/Vouchfx.Mcp.csproj — whenever a
# new artefact is vendored.
$script:ArtefactMap = [ordered]@{
    'composed-schema.v1.json' = 'tools/vscode-vouchfx/src/schema/composed-schema.v1.json'
    'language-reference.md'   = 'docs/language-reference.md'
    'recipes.md'              = 'docs/recipes.md'
}

$script:EngineRepoSlug = 'tomas-rampas/vouchfx'

function ConvertTo-SafeDisplayString
{
    <#
    .SYNOPSIS
        Renders $Value safely for display in a thrown error message: every character outside
        printable ASCII (0x20-0x7E) is replaced with a literal \uXXXX escape rather than passed
        through raw.

    .DESCRIPTION
        Mirrors src/Vouchfx.Mcp/EnginePin.cs's SanitiseForDisplay exactly (same printable-ASCII
        boundary, same \uXXXX escape format). Every throw below that splices a raw ENGINE_PIN
        field into its message runs the value through this first — not only the field that failed
        its own validation, but the raw pin line in the field-count-mismatch message too — so a
        malformed value can never carry a raw control byte (e.g. an ANSI/terminal escape sequence)
        into any output this script produces, whether that is the success-path "ENGINE_PIN: ..."
        Write-Host line or a failure-path error message written to stderr by the catch block
        further down. Validating the character set (see the version/commit-SHA checks below) is
        the primary defence; this is the belt-and-braces backstop for the value that failed that
        very validation, which by definition is the one place a hostile byte is guaranteed to
        still be in flight when a message is constructed.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Value
    )

    $builder = [System.Text.StringBuilder]::new($Value.Length)
    foreach ($c in $Value.ToCharArray())
    {
        if ($c -ge [char]0x20 -and $c -le [char]0x7E)
        {
            [void]$builder.Append($c)
        }
        else
        {
            [void]$builder.Append('\u{0:x4}' -f [int]$c)
        }
    }

    $builder.ToString()
}

function Get-EnginePin
{
    <#
    .SYNOPSIS
        Parses the "<version> <full-40-char-lowercase-commit-sha>" pin line
        out of an ENGINE_PIN file: the first non-comment (#), non-blank line.

    .DESCRIPTION
        Mirrors four of src/Vouchfx.Mcp/EnginePin.cs's rules exactly (see
        that file's ValidateVersion, ValidateCommitSha, AllZerosCommitSha,
        and SanitiseForDisplay): the version field's allowed-character set
        (ASCII letters, digits, '.', '-', '+' only — enforced BEFORE the
        parsed version can reach any output, since ENGINE_PIN is
        attacker-influenceable via a PR and this value is echoed to
        Write-Host in CI logs), the commit SHA's exactly-40-lowercase-hex
        shape, the explicit rejection of the all-zeros placeholder SHA (a
        clear error naming the rule here, rather than a confusing 404 from
        GitHub once that SHA reaches a download URL), and — via
        ConvertTo-SafeDisplayString above — sanitising any raw failing value
        before it is spliced into a thrown message, so a hostile byte cannot
        reach output via the error path either.

        It does NOT mirror EnginePin.cs's exact field-splitting rule: the C#
        side splits strictly on the ASCII space character (' ',
        `Split(' ', StringSplitOptions.RemoveEmptyEntries)`), while this
        function splits on any run of Unicode whitespace (`-split '\s+'`).
        Both agree on every input the real ENGINE_PIN file uses (fields
        separated by ordinary ASCII spaces), so this is a difference in
        generality — a line using a tab as a separator, say — not a
        behavioural gap for any pin either half of the repo actually
        encounters.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "ENGINE_PIN not found at '$Path'."
    }

    $pinLine = Get-Content -LiteralPath $Path |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('#') } |
        Select-Object -First 1

    if (-not $pinLine)
    {
        throw "ENGINE_PIN at '$Path' has no pin line — only comment/blank lines were found."
    }

    $fields = $pinLine -split '\s+'
    if ($fields.Count -ne 2)
    {
        $safePinLine = ConvertTo-SafeDisplayString -Value $pinLine
        throw "Malformed ENGINE_PIN line '$safePinLine': expected exactly two space-separated " +
            "fields '<version> <full-40-char-lowercase-commit-sha>', found $($fields.Count)."
    }

    $version = $fields[0]
    $commitSha = $fields[1]

    # Validated BEFORE this function returns anything — i.e. before $version
    # can reach $refLabel/Write-Host in the caller — because ENGINE_PIN is
    # attacker-influenceable (a PR can edit it) and this field is echoed to
    # CI logs verbatim. Mirrors EnginePin.cs's ValidateVersion character set
    # exactly: only ASCII letters, digits, '.', '-', or '+' are allowed, which
    # in particular rules out control characters (e.g. ANSI/terminal escape
    # sequences) surviving into a log line.
    if ($version -notmatch '^[A-Za-z0-9.+-]+$')
    {
        $safeVersion = ConvertTo-SafeDisplayString -Value $version
        throw "Malformed ENGINE_PIN version '$safeVersion': expected only ASCII letters, digits, " +
            "'.', '-', or '+' characters (no spaces, control characters, or other symbols)."
    }

    if ($commitSha -notmatch '^[0-9a-f]{40}$')
    {
        $safeCommitSha = ConvertTo-SafeDisplayString -Value $commitSha
        throw "Malformed ENGINE_PIN commit SHA '$safeCommitSha': expected exactly 40 lowercase " +
            'hexadecimal characters.'
    }

    # Mirrors EnginePin.cs's AllZerosCommitSha guard: reject the placeholder
    # explicitly, with a clear error naming what is wrong, rather than
    # letting it through to fail obscurely as a 404 once it reaches a
    # raw.githubusercontent.com download URL.
    if ($commitSha -eq ('0' * 40))
    {
        throw 'ENGINE_PIN commit SHA is the all-zeros placeholder — replace it with the real ' +
            'vouchfx engine commit SHA before use.'
    }

    [pscustomobject]@{
        Version   = $version
        CommitSha = $commitSha
    }
}

function Get-ArtefactDownloadUri
{
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $CommitSha,

        [Parameter(Mandatory)]
        [string]
        $EnginePath
    )

    "https://raw.githubusercontent.com/$script:EngineRepoSlug/$CommitSha/$EnginePath"
}

function Invoke-ArtefactDownload
{
    <#
    .SYNOPSIS
        Downloads $Uri to $OutFile, byte-exact, failing hard on any non-2xx
        response or transport error.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $Uri,

        [Parameter(Mandatory)]
        [string]
        $OutFile
    )

    try
    {
        # -OutFile streams the raw response body straight to disk — no text
        # decoding, no encoding conversion, no newline translation — which is
        # exactly the byte-fidelity contract this script exists to uphold.
        # Invoke-WebRequest already throws a terminating error on any non-2xx
        # status by default, which is the "fail hard on HTTP != 200" behaviour
        # this script needs; the catch below just attaches the URL for
        # diagnosis. -MaximumRedirection 0: raw.githubusercontent.com serves a
        # commit-SHA-addressed path directly (200) and should never need to
        # redirect for a valid pinned ref, so fail closed instead of silently
        # following a 3xx to who-knows-where. Confirmed empirically that a
        # redirect response under -MaximumRedirection 0 makes
        # Invoke-WebRequest throw an HttpResponseException (not silently
        # return the 3xx), so it is caught and reported below exactly like
        # any other download failure.
        Invoke-WebRequest -Uri $Uri -OutFile $OutFile -MaximumRedirection 0
    }
    catch
    {
        throw "Download failed for '$Uri': $($_.Exception.Message)"
    }

    if (-not (Test-Path -LiteralPath $OutFile -PathType Leaf))
    {
        throw "Download of '$Uri' reported success but produced no file at '$OutFile'."
    }
}

function Invoke-LocalGitRead
{
    # Drain both native byte streams concurrently. A blocked read, excessive output,
    # or a process that keeps running after closing stdout must fail within the deadline.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$GitPath,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$OutFile
    )

    $start = [System.Diagnostics.ProcessStartInfo]::new($GitPath)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Do not let inherited Git environment variables redirect this repository or
    # create trace files. Repository configuration is read, never changed.
    foreach ($key in @($start.Environment.Keys))
    {
        if ($key.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase))
        {
            [void]$start.Environment.Remove($key)
        }
    }
    $start.Environment['GIT_NO_LAZY_FETCH'] = '1'
    $start.Environment['GIT_NO_REPLACE_OBJECTS'] = '1'
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    foreach ($argument in @('--no-lazy-fetch', '--no-replace-objects', '--no-optional-locks', '-C', $Repository) + $Arguments)
    {
        $start.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $output = $null
    $errors = [System.IO.MemoryStream]::new()
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    try
    {
        if (-not $process.Start()) { throw 'Native Git did not start.' }
        $started = $true
        $output = [System.IO.File]::Open($OutFile, [System.IO.FileMode]::CreateNew)
        $stdoutBuffer = [byte[]]::new(8192)
        $stderrBuffer = [byte[]]::new(8192)
        $stdoutTask = $process.StandardOutput.BaseStream.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderrTask = $process.StandardError.BaseStream.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        while ($null -ne $stdoutTask -or $null -ne $stderrTask)
        {
            $remaining = 30000 - [int]$clock.ElapsedMilliseconds
            $pending = [System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask | Where-Object { $null -ne $_ })
            if ($remaining -le 0 -or [System.Threading.Tasks.Task]::WaitAny($pending, $remaining) -lt 0)
            {
                throw 'Native Git exceeded the 30-second process limit.'
            }
            if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted)
            {
                $count = $stdoutTask.GetAwaiter().GetResult()
                if ($count -eq 0) { $stdoutTask = $null }
                else
                {
                    if ($output.Length + $count -gt 4MB) { throw 'Native Git output exceeds the 4 MiB artefact limit.' }
                    $output.Write($stdoutBuffer, 0, $count)
                    $stdoutTask = $process.StandardOutput.BaseStream.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
                }
            }
            if ($null -ne $stderrTask -and $stderrTask.IsCompleted)
            {
                $count = $stderrTask.GetAwaiter().GetResult()
                if ($count -eq 0) { $stderrTask = $null }
                else
                {
                    if ($errors.Length + $count -gt 16KB) { throw 'Native Git stderr exceeds the 16 KiB diagnostic limit.' }
                    $errors.Write($stderrBuffer, 0, $count)
                    $stderrTask = $process.StandardError.BaseStream.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
                }
            }
        }
        $remaining = 30000 - [int]$clock.ElapsedMilliseconds
        if ($remaining -le 0 -or -not $process.WaitForExit($remaining))
        {
            throw 'Native Git exceeded the 30-second process limit.'
        }
        if ($process.ExitCode -ne 0)
        {
            $detail = [System.Text.Encoding]::UTF8.GetString($errors.ToArray())
            throw "Native Git exited $($process.ExitCode): $detail"
        }
    }
    catch
    {
        # Local paths and native diagnostics are data; never pass terminal controls through.
        throw (ConvertTo-SafeDisplayString -Value $_.Exception.Message)
    }
    finally
    {
        if ($started -and -not $process.HasExited)
        {
            $process.Kill($true)
            [void]$process.WaitForExit(1000)
        }
        if ($null -ne $output) { $output.Dispose() }
        $errors.Dispose()
        $process.Dispose()
    }
}

function Read-LocalGitArtefact
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$GitPath,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$EnginePath,
        [Parameter(Mandatory)][string]$OutFile
    )

    $metadata = "$OutFile.metadata"
    Invoke-LocalGitRead -GitPath $GitPath -Repository $Repository -Arguments @('ls-tree', '-z', '--full-tree', $CommitSha, '--', $EnginePath) -OutFile $metadata
    $entry = [System.IO.File]::ReadAllText($metadata, [System.Text.UTF8Encoding]::new($false, $true))
    Remove-Item -LiteralPath $metadata
    $pattern = '\A(100644|100755) blob ([0-9a-f]{40})\t' + [regex]::Escape($EnginePath) + "`0\z"
    if ($entry -notmatch $pattern)
    {
        throw "Pinned artefact '$EnginePath' is missing or is not a regular Git blob."
    }
    $blobSha = $Matches[2]
    Invoke-LocalGitRead -GitPath $GitPath -Repository $Repository -Arguments @('cat-file', '-s', $blobSha) -OutFile $metadata
    $sizeText = [System.IO.File]::ReadAllText($metadata).Trim()
    Remove-Item -LiteralPath $metadata
    $size = 0L
    if ($sizeText -notmatch '^[0-9]+$' -or -not [long]::TryParse($sizeText, [ref]$size) -or $size -gt 4MB)
    {
        throw "Pinned artefact '$EnginePath' exceeds the 4 MiB limit or has an invalid size."
    }
    Invoke-LocalGitRead -GitPath $GitPath -Repository $Repository -Arguments @('cat-file', 'blob', $blobSha) -OutFile $OutFile
    if ((Get-Item -LiteralPath $OutFile).Length -ne $size)
    {
        throw "Pinned artefact '$EnginePath' did not produce its exact declared byte count."
    }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$EnginePinPath = Join-Path $RepoRoot 'ENGINE_PIN'
$VendoredDir = Join-Path $RepoRoot 'vendored'
$Mode = $PSCmdlet.ParameterSetName # 'Update' or 'Verify' (default)

$TempDir = $null

try
{
    $pin = Get-EnginePin -Path $EnginePinPath
    $refLabel = "$($pin.Version) ($($pin.CommitSha))"
    Write-Host "ENGINE_PIN: $refLabel — mode: $Mode"

    $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "vouchfx-mcp-vendored-sync-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

    $localInput = $PSBoundParameters.ContainsKey('LocalEngineRepo')
    if ($localInput)
    {
        # Preserve the remote parser's existing behaviour, but immutable local input
        # must be the literal lowercase object ID, not a Git revision expression.
        if ($pin.CommitSha -cnotmatch '^[0-9a-f]{40}$')
        {
            throw 'Local ENGINE_PIN must name an exact 40-character lowercase commit SHA.'
        }
        # PowerShell's location can differ from the OS process working directory.
        # Resolve PSPaths (including filesystem PSDrives) without changing either.
        $provider = $null
        $drive = $null
        $repository = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
            $LocalEngineRepo, [ref]$provider, [ref]$drive)
        if ($provider.Name -cne 'FileSystem')
        {
            throw 'LocalEngineRepo must resolve to the FileSystem provider.'
        }
        $gitPath = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $commitTypeFile = Join-Path $TempDir 'commit-type'
        # cat-file -t refuses missing/noncommit objects. No ^{commit} dereference,
        # floating ref, replacement interpretation, lazy fetch or worktree read.
        Invoke-LocalGitRead -GitPath $gitPath -Repository $repository -Arguments @('cat-file', '-t', $pin.CommitSha) -OutFile $commitTypeFile
        if ([System.IO.File]::ReadAllText($commitTypeFile).Trim() -cne 'commit')
        {
            throw 'ENGINE_PIN does not name a local Git commit object.'
        }
    }

    # Download all three artefacts to the temp directory FIRST, before touching
    # vendored/ at all. This guarantees the DOWNLOAD phase is all-or-nothing:
    # either every download below succeeds and the copy loop further down
    # runs, or one throws and the catch block exits non-zero having copied
    # nothing into vendored/. It does NOT make the copy loop itself
    # transactional — see the .PARAMETER Update comment-based help above for
    # what guarantee that step actually has (and does not have).
    $downloadedFiles = [ordered]@{}
    foreach ($entry in $script:ArtefactMap.GetEnumerator())
    {
        $localName = $entry.Key
        $enginePath = $entry.Value
        $tempFile = Join-Path $TempDir $localName
        if ($localInput)
        {
            Read-LocalGitArtefact -GitPath $gitPath -Repository $repository -CommitSha $pin.CommitSha -EnginePath $enginePath -OutFile $tempFile
        }
        else
        {
            $uri = Get-ArtefactDownloadUri -CommitSha $pin.CommitSha -EnginePath $enginePath
            Write-Verbose "Downloading $localName from $uri"
            Invoke-ArtefactDownload -Uri $uri -OutFile $tempFile
        }
        $downloadedFiles[$localName] = $tempFile
    }

    if ($Mode -eq 'Update')
    {
        if (-not (Test-Path -LiteralPath $VendoredDir -PathType Container))
        {
            New-Item -ItemType Directory -Path $VendoredDir -Force | Out-Null
        }

        foreach ($entry in $downloadedFiles.GetEnumerator())
        {
            $destination = Join-Path $VendoredDir $entry.Key
            Copy-Item -LiteralPath $entry.Value -Destination $destination -Force
            Write-Host "UPDATED  $($entry.Key)  — now synced to $refLabel"
        }

        Write-Host "vendored/ updated: $($downloadedFiles.Count) artefact(s) synced to $refLabel."
        exit 0
    }

    # -Verify (default mode): byte-compare each downloaded artefact against
    # the corresponding file already committed in vendored/, via SHA-256 of
    # the raw bytes (never as decoded text).
    $driftCount = 0
    foreach ($entry in $downloadedFiles.GetEnumerator())
    {
        $localName = $entry.Key
        $tempFile = $entry.Value
        $vendoredFile = Join-Path $VendoredDir $localName

        if (-not (Test-Path -LiteralPath $vendoredFile -PathType Leaf))
        {
            Write-Host "DRIFT  $localName  — missing from vendored/ (expected from $refLabel)"
            $driftCount++
            continue
        }

        $downloadedHash = (Get-FileHash -LiteralPath $tempFile -Algorithm SHA256).Hash
        $vendoredHash = (Get-FileHash -LiteralPath $vendoredFile -Algorithm SHA256).Hash

        if ($downloadedHash -eq $vendoredHash)
        {
            Write-Host "OK     $localName  — matches $refLabel"
        }
        else
        {
            Write-Host "DRIFT  $localName  — differs from $refLabel"
            $driftCount++
        }
    }

    if ($driftCount -gt 0)
    {
        # A plain low-level stderr write, not Write-Error: this is an expected,
        # already-fully-reported business outcome (the per-file DRIFT lines
        # above), not an unhandled exception — it must not be swallowed by the
        # catch block below (Write-Error is a terminating error under
        # $ErrorActionPreference = 'Stop' and would otherwise be re-wrapped
        # there with a confusing double "failed: ... drifted" message).
        [Console]::Error.WriteLine(
            "$driftCount of $($downloadedFiles.Count) vendored artefact(s) drifted from " +
            "$refLabel. Run 'pwsh ./scripts/sync-vendored.ps1 -Update' to resync, review the " +
            'diff, then commit the result.'
        )
        exit 1
    }

    Write-Host "All $($downloadedFiles.Count) vendored artefact(s) match $refLabel."
    exit 0
}
catch
{
    # Unexpected failures only (malformed ENGINE_PIN, download/transport
    # errors): the drift-count case above exits directly and never reaches
    # here. Plain stderr write for the same reason as above — a clean,
    # single-line message is easier to read in a CI log than Write-Error's
    # decorated, multi-line error-record formatting.
    $failure = $_.Exception.Message
    if ($PSBoundParameters.ContainsKey('LocalEngineRepo'))
    {
        $failure = ConvertTo-SafeDisplayString -Value $failure
    }
    [Console]::Error.WriteLine("sync-vendored.ps1 failed: $failure")
    exit 1
}
finally
{
    if ($TempDir -and (Test-Path -LiteralPath $TempDir))
    {
        # Delete only this invocation's GUID-named staging directory under TEMP.
        $resolvedTempDir = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $TempDir).Path)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([char[]]'\/')
        if ([System.IO.Path]::GetDirectoryName($resolvedTempDir) -eq $tempRoot -and
            [System.IO.Path]::GetFileName($resolvedTempDir) -match '^vouchfx-mcp-vendored-sync-[0-9a-f]{32}$')
        {
            Remove-Item -LiteralPath $resolvedTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

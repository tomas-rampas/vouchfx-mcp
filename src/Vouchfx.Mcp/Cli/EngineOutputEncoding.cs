using System.Runtime.InteropServices;
using System.Text;

namespace Vouchfx.Mcp.Cli;

/// <summary>
/// THE single resolution site for the encoding the <c>vouchfx</c> engine writes its redirected
/// stdout/stderr in — the Windows console's active OUTPUT code page, or UTF-8 everywhere that
/// notion does not apply. Resolved ONCE for the process lifetime; the console output code page
/// does not change under a live server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a type of its own rather than a private helper on
/// <see cref="VouchfxCliProcessRunner"/> (where it used to live).</b> Two callers now need the same
/// answer, and they must not be able to disagree: the runner DECODES the engine's bytes with it,
/// and <c>Schema/GetSchemaOrchestrator</c> MODELS the engine's encode with it (issue #89 — it
/// projects the vendored schema through this same encoding so a lossy console page stops being
/// reported as schema drift). A second copy of the resolution would let the decode and the model
/// drift apart, which is exactly the failure mode that comparison exists to remove.
/// </para>
/// <para>
/// <b>Never <c>SetConsoleOutputCP</c>.</b> This resolves what the console IS; it never changes it.
/// A stdio MCP server shares its console with whatever host spawned it, so repointing that host's
/// code page would be this server reaching outside its own process boundary.
/// </para>
/// <para>
/// <b>MEASURED 2026-09-15</b> (Windows 11, console output code page 852, OEM 852, ACP 1250, pinned
/// CLI v1.0.0-rc.5): a parent on a WINDOW-LESS CONSOLE — a server spawned with redirected stdio and
/// <c>CreateNoWindow = true</c>, i.e. exactly how an MCP host launches this process — still observes
/// <c>GetConsoleOutputCP()</c> = <b>852</b>, the machine's OEM page, and so does a
/// <c>CreateNoWindow = true</c> CHILD spawned from it (<c>Console.OutputEncoding.CodePage</c> = 852
/// in both). It is NOT 0 and NOT 65001 — and the reason is exactly the terminology: such a process
/// HAS a console, it simply has no window, so the code-page query is answered by a real console. An
/// earlier version of this comment claimed the opposite (that 0 means no console is attached and
/// that the child then defaults ITS redirected stdout to UTF-8) and was FALSE. The correction
/// matters beyond tidiness: it means a headless MCP deployment is affected by a lossy OEM code page
/// exactly as much as a cp852 terminal is, which is why issue #89's "VFX-D-1106 on every call" was a
/// field problem rather than a maintainer-terminal curiosity.
/// </para>
/// <para>
/// <b>NAMED UNMEASURED RESIDUAL: the genuinely console-less case, which is where the <c>0</c> branch
/// lives.</b> A process started with <c>DETACHED_PROCESS</c>, a Windows service host, or a GUI
/// parent that passes no console has NO console at all — a different configuration from the one
/// above, and one nothing here has measured. INFERRED, not measured: there
/// <c>GetConsoleOutputCP()</c> returns 0, this resolver answers UTF-8, and the engine child — which
/// <c>VouchfxCliProcessRunner</c> spawns WITHOUT <c>CreateNoWindow</c> — would be given a freshly
/// allocated console at the machine's OEM page and encode its output in THAT. Decode and projection
/// would then both be wrong in the same direction, and #89 would recur in that configuration. So the
/// <c>0</c> branch is not "the right pairing"; it is an untested guess that preserves the
/// pre-#70 behaviour, and it is the first thing to measure if VFX-D-1106 is ever reported from a
/// service or detached deployment.
/// </para>
/// <para>
/// <b>And <c>chcp 65001</c> in the launching shell does not reach such a server</b> — MEASURED the
/// same day, with a probe that reports its own and its child's code page: after <c>chcp 65001</c>
/// the launcher sees 65001, but a child started with <c>CreateNoWindow = true</c> and redirected
/// stdio still sees <b>852</b>, while the same child started with <c>CreateNoWindow = false</c>
/// inherits the console and sees 65001. That is precisely the shape an MCP host uses, so the
/// <c>chcp 65001</c> workaround VFX-D-1106 USED to recommend was effective only for a server that
/// inherits a console — never for a host-spawned one, which is the common deployment. Modelling the
/// code page here, rather than asking an operator to change it, is what makes the outcome
/// independent of that distinction, and is why that remedy is no longer documented.
/// </para>
/// <para>
/// <b>Coupling to the engine, stated so it is not forgotten.</b> Everything here assumes the engine
/// writes its redirected stdout in the console output code page. If the engine is ever fixed to
/// emit UTF-8 when redirected (the engine-side half of issue #70), that change arrives with a new
/// <c>ENGINE_PIN</c>, and THIS type must be revisited at the same time — decoding a UTF-8-emitting
/// engine's output with cp852 would then corrupt it, and <c>get_schema</c>'s projection would model
/// a transcoding that no longer happens. The same latent coupling exists in <c>run_suite</c>'s
/// <c>VouchfxCliSuiteRunner</c>, which still decodes the engine's redirected event bytes as UTF-8
/// and is deliberately out of scope here (it stays scoped to #70).
/// </para>
/// </remarks>
public static class EngineOutputEncoding
{
    /// <summary>
    /// UTF-8's code page number. <see cref="Encoding.UTF8"/>'s own
    /// <see cref="Encoding.CodePage"/> is this value, which is what makes
    /// <see cref="IsUtf8(Encoding)"/> a property of the encoding rather than of the instance a
    /// caller happened to construct.
    /// </summary>
    private const int Utf8CodePage = 65001;

    /// <summary>
    /// The encoding this host's engine child writes its redirected output in, resolved once.
    /// </summary>
    public static Encoding Current { get; } = Resolve();

    /// <summary>
    /// Whether <paramref name="encoding"/> is UTF-8 — compared by
    /// <see cref="Encoding.CodePage"/> rather than by reference, so a caller-constructed
    /// <c>new UTF8Encoding(...)</c> (tests inject one) answers the same as
    /// <see cref="Encoding.UTF8"/>.
    /// </summary>
    public static bool IsUtf8(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.CodePage == Utf8CodePage;
    }

    /// <summary>
    /// Resolves the engine's output encoding, falling back to UTF-8 on every path that is not a
    /// positively-identified Windows console code page.
    /// </summary>
    /// <remarks>
    /// <b>The UTF-8 fallbacks are not all equally sound, so do not read them as one rule.</b>
    /// Non-Windows genuinely has no console-code-page notion and .NET uses UTF-8 for redirected
    /// output there — correct. 65001 IS UTF-8 — correct by definition. A code page of <b>0</b> is the
    /// NAMED UNMEASURED RESIDUAL described in this type's remarks: no measured configuration reaches
    /// it, and the inference is that on a genuinely console-less host UTF-8 here would be WRONG
    /// (the engine child would get a fresh OEM-page console). It is kept because it preserves the
    /// pre-#70 behaviour, not because it is known to pair correctly. Any failure resolving or
    /// constructing the encoding falls back to UTF-8 for the same conservative reason.
    /// </remarks>
    private static Encoding Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Encoding.UTF8;
        }

        try
        {
            int codePage = GetConsoleOutputCP();
            if (codePage is 0 or Utf8CodePage)
            {
                return Encoding.UTF8;
            }

            // Encoding.GetEncoding(852 | 1250 | 1252 | …) throws NotSupportedException in the in-box
            // runtime without this provider (MEASURED). Registration is process-global and APPENDS to
            // a provider list rather than replacing an entry, so repeating it is harmless (the same
            // instance simply resolves first) but not literally idempotent — which is why this runs
            // once, from a static initialiser, and only past the UTF-8 short-circuit: the UTF-8 case
            // never touches the global provider at all.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            return Encoding.GetEncoding(codePage);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: any failure
        // resolving the console code page (no console, an unknown/unsupported page, a provider
        // problem) must fall back to the previous UTF-8 behaviour rather than break every CLI relay.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Encoding.UTF8;
        }
    }

    // DllImport rather than [LibraryImport]: the source-generated variant requires
    // <AllowUnsafeBlocks>, which this project deliberately does not enable, and a blittable int
    // return needs no marshalling generation anyway.
    // DefaultDllImportSearchPaths(System32) pins the load to the system directory (defense-in-depth
    // for CA5392): kernel32 is always preloaded so this is not a live hijack risk, but it is one line
    // of hygiene that also silences the analyzer if it is ever promoted to an error.
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetConsoleOutputCP();
}

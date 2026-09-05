using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1207 — a secret LITERAL is embedded in the suite where a <c>${secret:…}</c> reference
/// belongs. The one semantic code spec §5.5 marks <c>error</c>, and the one whose finding flips the
/// suite's verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>A literal is the opposite of a reference, and telling them apart is the first thing this rule
/// does.</b> A value containing <c>${</c> is a REFERENCE — the correct practice, the thing this
/// finding tells the author to adopt — and is skipped whole, before any heuristic runs. That
/// ordering is not an optimisation: <c>"Server=db;Password=${secret:db/pw}"</c> matches the
/// connection-string heuristic textually, and reporting it would tell an author who did the right
/// thing to do the right thing.
/// </para>
/// <para>
/// <b>The heuristics are spec §4.8's own list</b>, no more and no less: connection strings with
/// passwords, <c>AKIA…</c>, <c>-----BEGIN … PRIVATE KEY</c>, and long high-entropy tokens.
/// </para>
/// <para>
/// <b>Three STRUCTURAL arms at <c>error</c>, one INFERRED arm at <c>warning</c> — and the split is
/// the whole point of this rule's severity story.</b> A private-key PEM header, an
/// <c>AKIA</c>/<c>ASIA</c> body, and a <c>password=</c> with a real value beside it are shapes that
/// are a secret or are nothing: each one names its own kind, so a match is a statement of fact and
/// gets the severity that flips <see cref="SuiteAnalysis.Valid"/>. The ENTROPY arm is a guess about
/// an opaque token — it cannot distinguish a JWT from a base64 message payload or a long build path
/// — so it reports at <see cref="SemanticFinding.Warning"/> and has no verdict-flipping power at
/// all. Measured false positives are why: at 4.0 bits/char with <c>/</c> in the charset it fired at
/// <c>error</c> severity on a 40-character <c>.csproj</c> path (4.03 bits), a <c>net8.0</c> publish
/// path (4.09), and a base64 Kafka <c>payload</c> — turning three valid suites into
/// <c>valid: false</c>. A finding that cannot be sure must not be able to fail a build.
/// </para>
/// <para>
/// <b>What the entropy arm still demands, so it stays narrow even as advice.</b> The WHOLE scalar
/// must be one unbroken token of at least <see cref="MinimumHighEntropyLength"/> base64/URL-safe
/// characters — <b><c>/</c> excluded</b>, because it is the one charset member that turns a
/// filesystem or URL path into a "token" — mixing upper case, lower case and digits, at or above
/// <see cref="MinimumEntropyBitsPerCharacter"/> bits per character. That excludes by construction
/// the things real suites are full of: SQL and prose (spaces), JSON bodies and URLs (punctuation
/// outside the charset), paths (the slash), and lower-case hex digests (no upper case).
/// </para>
/// <para>
/// <b>The offending value is never reproduced — not in the message, not in the path.</b> The
/// finding names the SHAPE that matched and points at the node; an author looking at their own file
/// needs nothing more, and anyone else must not receive the secret this server just found.
/// </para>
/// <para>
/// <b>This rule walks EVERY node, so it carries a <see cref="SuitePathBuilder"/> rather than a
/// <see cref="SuitePath"/>.</b> See that type's remarks for the measurement (37.8 s → 4.1 s on a
/// 4.3&#160;MB finding-free suite): a path is materialised in the finding arm and nowhere else.
/// </para>
/// </remarks>
internal sealed class SecretLiteralRule : ISemanticRule
{
    /// <summary>Shortest run of token characters the entropy arm will consider. See the class remarks.</summary>
    public const int MinimumHighEntropyLength = 40;

    /// <summary>Longest run the entropy arm considers — past this it is a payload, not a credential.</summary>
    public const int MaximumHighEntropyLength = 512;

    /// <summary>Shannon entropy, in bits per character, at or above which a token looks generated.</summary>
    /// <remarks>
    /// <para>
    /// <b>4.5, from a measurement rather than from a round number.</b> 4.0 bits/char is roughly "16
    /// equally-likely symbols", and the original argument for it was that a lower-case hex digest
    /// sits near 4.0 exactly. That argument was already redundant — the mixed-case requirement is
    /// what excludes a digest, since a lower-case digest has no upper case — and 4.0 turned out to
    /// sit in the middle of the real distribution rather than below it. Measured on this repo's own
    /// probe corpus: long <c>.csproj</c>/<c>net8.0</c> build paths land at <b>4.03–4.09</b>, while a
    /// real JWT reaches <b>5.33</b> and base64 credential blobs <b>4.97–5.20</b>. 4.5 sits in the gap
    /// with room on both sides; it is not a threshold anything in the corpus straddles.
    /// </para>
    /// </remarks>
    public const double MinimumEntropyBitsPerCharacter = 4.5;

    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.SecretLiteralInSuite;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();
        Walk(context, context.Document, new SuitePathBuilder(), findings);
        return findings;
    }

    private void Walk(
        SemanticAnalysisContext context, JsonElement element, SuitePathBuilder path, List<Diagnostic> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // A property NAME can itself carry a `${…}` reference (nothing stops an author
                    // naming a capture after one), and splicing it into a JSONPath would publish it
                    // through Diagnostic.Path — which the Analyse choke point fails the call for.
                    // TryPushProperty declines such a name, so the node is reported at its PARENT
                    // instead; the value is still scanned.
                    var pushed = path.TryPushProperty(property.Name);

                    Walk(context, property.Value, path, findings);

                    if (pushed)
                    {
                        path.Pop();
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    path.PushIndex(index);
                    Walk(context, item, path, findings);
                    path.Pop();
                    index++;
                }

                break;

            case JsonValueKind.String:
                if (DescribeSecretShape(element.GetString()) is { } shape)
                {
                    findings.Add(SemanticFinding.Create(
                        context,
                        Code,
                        shape.Severity,
                        $"This value looks like {shape.Description} written literally into the suite. "
                        + "Replace it with a secret reference so the engine resolves it at run time "
                        + "and this server never sees it (see this code's catalogue page for the "
                        + "syntax). The value itself is deliberately not reproduced here.",
                        // THE one path materialisation on this walk. Everything above carries
                        // segments; only a real finding pays for a string.
                        path.Build()));
                }

                break;

            default:
                // Numbers, booleans and null cannot carry a credential.
                break;
        }
    }

    /// <summary>One matched secret shape: the fixed phrase naming it, and the severity it earns.</summary>
    /// <remarks>
    /// The <see cref="Description"/> is a compile-time constant per shape — never anything derived
    /// from the value, which must not reach a message.
    /// </remarks>
    private readonly record struct SecretShape(string Description, string Severity);

    /// <summary>
    /// Names the secret SHAPE <paramref name="value"/> matches, or <see langword="null"/> when it
    /// matches none.
    /// </summary>
    private static SecretShape? DescribeSecretShape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // FIRST, and decisive: a reference is the correct practice, not a finding. See the class
        // remarks for why this cannot be reordered.
        if (value.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }

        if (ContainsPrivateKeyPemHeader(value))
        {
            return new SecretShape("a PEM-encoded private key", SemanticFinding.Error);
        }

        if (ContainsAwsAccessKeyId(value))
        {
            return new SecretShape("an AWS access key id", SemanticFinding.Error);
        }

        if (ContainsInlinePassword(value))
        {
            return new SecretShape(
                "a connection string carrying an inline password", SemanticFinding.Error);
        }

        return LooksHighEntropy(value.Trim())
            ? new SecretShape("a long, high-entropy credential", SemanticFinding.Warning)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="value"/> carries a PEM header for a PRIVATE key — never for a
    /// certificate, a public key, or a certificate signing request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A certificate is a public artefact, and this rule's severity fails the suite.</b>
    /// <c>-----BEGIN CERTIFICATE-----</c>, <c>-----BEGIN PUBLIC KEY-----</c> and
    /// <c>-----BEGIN CERTIFICATE REQUEST-----</c> are all material designed to be handed out; a
    /// suite that pins a server certificate to assert a TLS handshake is doing something correct,
    /// and invalidating it would be a wrong finding on a valid document at the one severity that
    /// cannot be ignored. Only the private forms are secrets, and they announce themselves in the
    /// label: RFC 7468's own <c>PRIVATE KEY</c> / <c>ENCRYPTED PRIVATE KEY</c>, plus OpenSSH's
    /// <c>OPENSSH PRIVATE KEY</c> and the legacy algorithm-qualified spellings
    /// (<c>RSA PRIVATE KEY</c>, <c>EC PRIVATE KEY</c>, …) which all end in the same two words.
    /// </para>
    /// <para>
    /// Testing for <c>PRIVATE KEY</c> after a <c>-----BEGIN</c>, rather than for the whole label,
    /// is what covers the algorithm-qualified spellings without enumerating them — the label is
    /// bounded (the line ends at <c>-----</c>), so the search window is bounded too.
    /// </para>
    /// </remarks>
    private static bool ContainsPrivateKeyPemHeader(string value)
    {
        const string opener = "-----BEGIN";
        const string privateKeyLabel = "PRIVATE KEY";

        var from = 0;
        while (from <= value.Length - opener.Length)
        {
            var at = value.IndexOf(opener, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            // The label runs from just after "-----BEGIN" to the closing dashes of the same line.
            var labelStart = at + opener.Length;
            var labelEnd = value.IndexOf("-----", labelStart, StringComparison.Ordinal);
            if (labelEnd < 0)
            {
                labelEnd = value.Length;
            }

            if (value.AsSpan(labelStart, labelEnd - labelStart)
                     .Contains(privateKeyLabel, StringComparison.Ordinal))
            {
                return true;
            }

            from = at + opener.Length;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="value"/> contains an AWS access key id — one of the documented
    /// four-character prefixes followed by exactly 16 upper-case alphanumerics.
    /// </summary>
    /// <remarks>
    /// A hand-written scan rather than a regular expression, for the reason
    /// <see cref="PlaceholderScanner"/> gives at length: this walks untrusted suite content bounded
    /// only by <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>, and a backtracking pattern over text
    /// that size is the shape a ReDoS input targets. This is O(n) with no backtracking.
    /// </remarks>
    private static bool ContainsAwsAccessKeyId(string value)
    {
        // AKIA = long-term user key, ASIA = temporary STS key. Both are credentials; the other
        // documented prefixes (AIDA, AROA, …) identify PRINCIPALS rather than keys and are not
        // secret, so including them would manufacture false positives on a legitimate ARN.
        string[] prefixes = ["AKIA", "ASIA"];
        const int bodyLength = 16;

        foreach (var prefix in prefixes)
        {
            var from = 0;
            while (from <= value.Length - prefix.Length)
            {
                var at = value.IndexOf(prefix, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                if (at + prefix.Length + bodyLength <= value.Length &&
                    IsUpperAlphanumericRun(value, at + prefix.Length, bodyLength))
                {
                    return true;
                }

                from = at + 1;
            }
        }

        return false;
    }

    private static bool IsUpperAlphanumericRun(string value, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            var c = value[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a connection string with a password spelled out in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both spellings the ADO.NET/JDBC families use (<c>password=</c> and <c>pwd=</c>), matched
    /// case-insensitively because connection-string keys are, and required to be followed by an
    /// actual value: <c>Password=;</c> (an empty password, common in a local-dev template) and
    /// <c>Password={pw}</c> (an interpolated placeholder — the author already parameterised it) are
    /// both correct and must not be reported.
    /// </para>
    /// <para>
    /// <b>Two more shapes that are a TEMPLATE rather than a password</b>, and are excluded for the
    /// same reason as <c>{pw}</c>: an angle-bracket placeholder (<c>Password=&lt;your-password&gt;</c>
    /// — the universal spelling in a README or a sample config) and a format specifier
    /// (<c>Password=%s</c>, <c>Password=%1$s</c>) that something else fills in. Neither is a
    /// credential, and this arm reports at <see cref="SemanticFinding.Error"/>, so a match on one
    /// would invalidate a suite for carrying documentation.
    /// </para>
    /// <para>
    /// <b>A real literal still fires.</b> <c>Password=hunter2</c> is exactly what this arm exists
    /// for; nothing below softens that. The exclusions test the FIRST character of the value only,
    /// which is what makes them cheap and what keeps them from swallowing a password that merely
    /// contains a percent sign.
    /// </para>
    /// </remarks>
    private static bool ContainsInlinePassword(string value)
    {
        string[] keys = ["password=", "pwd="];

        foreach (var key in keys)
        {
            var at = value.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }

            var valueStart = at + key.Length;
            if (valueStart < value.Length &&
                value[valueStart] is not (';' or '{' or '<' or '%' or ' ' or '\t' or '\r' or '\n'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the WHOLE of <paramref name="value"/> is one long, mixed-case, high-entropy token.
    /// </summary>
    private static bool LooksHighEntropy(string value)
    {
        if (value.Length < MinimumHighEntropyLength || value.Length > MaximumHighEntropyLength)
        {
            return false;
        }

        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;

        foreach (var c in value)
        {
            if (c >= 'A' && c <= 'Z')
            {
                hasUpper = true;
            }
            else if (c >= 'a' && c <= 'z')
            {
                hasLower = true;
            }
            else if (c >= '0' && c <= '9')
            {
                hasDigit = true;
            }
            else if (c is not ('+' or '=' or '_' or '-' or '.'))
            {
                // Any character outside the base64/URL-safe/JWT charset means this scalar is prose,
                // SQL, JSON or a URL rather than one opaque token. That single test is what keeps
                // this arm off everything a real suite is made of.
                //
                // `/` is NOT in the set, and its absence is the single most load-bearing character
                // decision here. Standard base64 uses it, so excluding it costs a small number of
                // true positives on unpadded standard-alphabet blobs — but including it admitted
                // every long PATH in the corpus (a 40-character `.csproj` project path, a `net8.0`
                // publish directory), which is what a real suite is actually full of. Paths are the
                // dominant long-token shape in a test suite; raw standard-base64 secrets are not.
                return false;
            }
        }

        return hasUpper && hasLower && hasDigit && ShannonEntropy(value) >= MinimumEntropyBitsPerCharacter;
    }

    /// <summary>Shannon entropy of <paramref name="value"/> in bits per character.</summary>
    private static double ShannonEntropy(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in value)
        {
            counts[c] = counts.TryGetValue(c, out var seen) ? seen + 1 : 1;
        }

        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var probability = (double)count / value.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }
}

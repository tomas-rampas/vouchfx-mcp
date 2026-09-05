using System.Reflection;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1207 — a secret LITERAL embedded in the suite, the one semantic code the spec
/// marks <c>error</c> and the one whose Gherkin scenario says the suite's verdict flips with it.
/// </summary>
/// <remarks>
/// <b>Distinct from a <c>${secret:…}</c> reference, which is the CORRECT practice</b> and must never
/// be reported: this rule fires on a value the author pasted in, never on one they parameterised.
/// Heuristics are spec §4.8's own list — connection strings with passwords, <c>AKIA…</c>,
/// <c>-----BEGIN</c>, long high-entropy tokens.
/// </remarks>
public class SecretLiteralRuleTests
{
    [Fact]
    public void AConnectionStringCarryingAnInlinePassword_IsAnError()
    {
        // Gherkin scenario 4's input: "a suite step embedding a connection string containing a
        // literal password".
        using var fixture = SemanticRuleFixture.For("""
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  connectionString: "Server=db;Database=orders;User Id=sa;Password=hunter2-not-a-real-one;"
            steps:
              - id: assert-row
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 1"
                expect:
                  rowCount: 1
            """);

        var finding = Assert.Single(fixture.Run(new SecretLiteralRule()));

        Assert.Equal("VFX-D-1207", finding.Code);
        Assert.Equal("error", finding.Severity);

        // The value is NEVER echoed — that is the whole point of a secret-literal finding.
        Assert.DoesNotContain("hunter2", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // spec §4.8's named shapes, one case each — with the severity each ARM earns. The three
    // STRUCTURAL shapes are `error` (each is a secret or is nothing); the ENTROPY shape is
    // `warning`, because it is a guess about an opaque token and a guess must not fail a build.
    [InlineData("AKIAIOSFODNN7EXAMPLE", "error")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "error")]
    [InlineData("Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MGFiY2RlZmdoaWprbG1ub3A=", "warning")]
    public void EachNamedSecretShape_IsDetectedAtItsArmsSeverity(string literal, string expectedSeverity)
    {
        using var fixture = SemanticRuleFixture.For($"""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                headers:
                  authorization: "{literal}"
            """);

        var finding = Assert.Single(fixture.Run(new SecretLiteralRule()));

        Assert.Equal("VFX-D-1207", finding.Code);
        Assert.Equal(expectedSeverity, finding.Severity);
        Assert.DoesNotContain(literal, finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // PUBLIC PEM material. A certificate, a public key and a CSR are all artefacts designed to be
    // handed out, and a suite that pins one to assert a TLS handshake is doing something correct —
    // so reporting it at the one severity that fails the suite would invalidate a valid document.
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    [InlineData("-----BEGIN CERTIFICATE REQUEST-----")]
    public void APublicPemArtefact_IsNotASecret(string pem)
    {
        using var fixture = SemanticRuleFixture.For($"""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: POST
                path: /trust
                body: "{pem}"
            """);

        Assert.Empty(fixture.Run(new SecretLiteralRule()));
    }

    [Theory]
    // The private forms, including the two spellings that are not RFC 7468's bare label.
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void APrivateKeyPem_IsAnError(string pem)
    {
        using var fixture = SemanticRuleFixture.For($"""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: POST
                path: /trust
                body: "{pem}"
            """);

        var finding = Assert.Single(fixture.Run(new SecretLiteralRule()));

        Assert.Equal("VFX-D-1207", finding.Code);
        Assert.Equal("error", finding.Severity);
    }

    [Theory]
    // A PLACEHOLDER is a template, not a credential — the same reasoning that already excluded
    // `Password={pw}` and `Password=;`. An angle-bracket stand-in and a format specifier are the
    // two spellings a README or a sample config uses.
    [InlineData("Server=db;Password=<your-password>;")]
    [InlineData("Server=db;Password=%s;")]
    [InlineData("Server=db;Password=;")]
    [InlineData("Server=db;Password={pw};")]
    public void APlaceholderShapedPassword_IsNotReported(string connectionString)
    {
        using var fixture = SemanticRuleFixture.For($"""
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  connectionString: "{connectionString}"
            steps:
              - id: assert-row
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 1"
                expect:
                  rowCount: 1
            """);

        Assert.Empty(fixture.Run(new SecretLiteralRule()));
    }

    [Fact]
    public void ASecretReference_IsTheCorrectPracticeAndIsNeverReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                headers:
                  authorization: "Bearer ${secret:vault/api-token}"
            """);

        Assert.Empty(fixture.Run(new SecretLiteralRule()));
    }

    [Fact]
    public void OrdinarySuiteProse_IsNotMistakenForASecret()
    {
        // The false-positive guard, REBUILT. Its first version was vacuous: SQL with spaces and a
        // JSON body with quotes both die on the entropy arm's CHARSET test long before entropy is
        // computed, so it proved only that the charset test exists. Every input below is one the
        // charset test ADMITS — an unbroken run of base64/URL-safe characters — so each one reaches
        // the entropy computation and is a real test of the threshold and the excluded slash.
        //
        // The two PATH shapes are the ones the fix removes outright, and both were measured firing
        // at ERROR severity — flipping valid:false — on valid suites: a >=40-character project path
        // (4.03 bits/char) and a net8.0 publish path (4.09), each admitted only because `/` used to
        // be in the charset. Dropping the slash is what silences them, and both would still be
        // under the raised 4.5 threshold even if it had not.
        //
        // (The third measured false positive, a base64 Kafka payload, is a different fix and has its
        // own test below: it really is high-entropy, so the threshold cannot exclude it and the
        // SEVERITY demotion is what defuses it.)
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: assert-row
                type: db-assert.postgres
                target: orders-db
                query: "SELECT count(*) FROM orders WHERE status = 'shipped' AND created_at > now() - interval '1 day'"
                expect:
                  rowCount: 1
              - id: call
                type: http.rest
                target: orders-api
                method: POST
                path: /orders?includeArchived=false&expand=lines
                body: '{"customer":"acme","lines":[{"sku":"ABC-123","quantity":2}]}'
              - id: run-fixture-loader
                type: script.csharp
                code: "Vars[\"loaded\"] = 1;"
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                headers:
                  x-project: "src/Vouchfx.Engine.Compilation/Vouchfx.Engine.Compilation.csproj"
                  x-publish-dir: "artifacts/publish/net8.0/linux-x64/Vouchfx.Engine.Cli.deps.json"
            """);

        Assert.Empty(fixture.Run(new SecretLiteralRule()));
    }

    [Fact]
    public void ABase64MessagePayload_IsAdviceOnly_AndNeverFlipsTheVerdict()
    {
        // The third measured false positive, and the one the entropy arm CANNOT be tuned out of: a
        // base64-encoded message body really is high-entropy (4.97–5.20 bits/char measured, against
        // the 4.5 threshold), and there is no textual property that separates it from a base64
        // credential. Since the arm cannot be right about it, it is not allowed to be decisive about
        // it: the finding reports as advice, and the suite stays valid.
        const string suite = """
            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: "eyJvcmRlcklkIjoxMjM0NSwiY3VzdG9tZXIiOiJhY21lIiwibGluZXMiOlt7InNrdSI6IkFCQyJ9XX0="
            """;

        using var fixture = SemanticRuleFixture.For(suite);

        var finding = Assert.Single(fixture.Run(new SecretLiteralRule()));
        Assert.Equal("VFX-D-1207", finding.Code);
        Assert.Equal("warning", finding.Severity);

        // The claim that actually matters, asserted where the verdict is computed.
        var analysis = Vouchfx.Mcp.Validation.SuiteValidator.AnalyseYaml(
            suite, Vouchfx.Mcp.Validation.ValidationLevel.Semantic);

        Assert.True(analysis.Valid);
    }

    [Fact]
    public void AGenuinelyHighEntropyToken_IsStillCaught_AsAWarning()
    {
        // The other side of the threshold, so the FP guard above cannot be satisfied by simply
        // switching the entropy arm off. A real JWT measures 5.33 bits/char against the 4.5
        // threshold — and it is still only a WARNING, because the arm is a guess.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                headers:
                  authorization: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ"
            """);

        var finding = Assert.Single(fixture.Run(new SecretLiteralRule()));

        Assert.Equal("VFX-D-1207", finding.Code);
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void TheEntropyArmNeverFlipsTheVerdict_ButAStructuralArmDoes()
    {
        // Gherkin scenario 4's second clause ("And ok is false"), asserted where the verdict is
        // actually computed — and its NEW boundary. `valid` is the shipped spelling of the spec's
        // `ok` (US-S2-02's field-name note).
        //
        // The structural half: an AWS key id is a secret or is nothing, so it fails the suite.
        var structural = Vouchfx.Mcp.Validation.SuiteValidator.AnalyseYaml(
            """
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                headers:
                  authorization: "AKIAIOSFODNN7EXAMPLE"
            """,
            Vouchfx.Mcp.Validation.ValidationLevel.Full);

        Assert.Contains(structural.SemanticDiagnostics, d => d.Code == "VFX-D-1207" && d.Severity == "error");
        Assert.False(structural.Valid);

        // ...and the SCHEMA channel is untouched by it: the engine would accept this document, and
        // `errors` is the channel that says so.
        Assert.Empty(structural.Errors);

        // The ENTROPY half: reported, and the suite stays valid. This is the property that makes an
        // entropy false positive an annoyance rather than a broken build.
        var entropic = Vouchfx.Mcp.Validation.SuiteValidator.AnalyseYaml(
            """
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                headers:
                  authorization: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0"
            """,
            Vouchfx.Mcp.Validation.ValidationLevel.Full);

        Assert.Contains(entropic.SemanticDiagnostics, d => d.Code == "VFX-D-1207" && d.Severity == "warning");
        Assert.DoesNotContain(entropic.SemanticDiagnostics, d => d.Severity == "error");
        Assert.True(entropic.Valid);
    }

    [Fact]
    public void TheWalkMaterialisesAPathOnlyInTheFindingArm()
    {
        // THE performance regression guard, and it is deliberately STRUCTURAL rather than a
        // stopwatch — the same choice, for the same reasons, that
        // SemanticPassTests.NoRuleSourceFileCanParseAnythingItself makes.
        //
        // The measured hazard: this rule visits EVERY node, and its first revision built a SuitePath
        // (two interpolated strings, each copying the whole prefix) at every one of them on the way
        // down. A 4.3 MB FINDING-FREE suite took 37.8 seconds against the validation worker's
        // 10-second wall clock, surfacing as VFX-E-1150 (a killed worker) rather than as a slow
        // rule — so a timing test would be flaky AND uninformative about the cause. What actually
        // makes the cost impossible is that the walk carries SEGMENTS and materialises once, inside
        // the finding arm. That is what this asserts, from two directions.

        // 1. The recursive walk's own signature: it carries the builder, and cannot be handed a
        //    materialised path to append to.
        var walk = typeof(SecretLiteralRule)
            .GetMethod("Walk", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(walk);
        var parameterTypes = walk!.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(SuitePathBuilder), parameterTypes);
        Assert.DoesNotContain(typeof(SuitePath), parameterTypes);

        // 2. The source builds a path exactly once, and that one call sits inside the argument list
        //    of the findings.Add(...) that reports a match — never on the descent.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot.FullName, "src", "Vouchfx.Mcp", "Validation", "Semantics", "SecretLiteralRule.cs"));

        var occurrences = source.Split(".Build()").Length - 1;
        Assert.True(occurrences == 1, $"Expected exactly one path materialisation; found {occurrences}.");

        var buildAt = source.IndexOf(".Build()", StringComparison.Ordinal);
        var addAt = source.LastIndexOf("findings.Add(", buildAt, StringComparison.Ordinal);

        Assert.True(addAt >= 0, "The one .Build() call is not inside a findings.Add(...) argument list.");
        Assert.DoesNotContain("break;", source[addAt..buildAt], StringComparison.Ordinal);
    }

    [Theory]
    // A right-to-left override, a zero-width space and a byte-order mark are all legal YAML mapping
    // keys. Before this review every Path assertion in the suite used well-behaved ASCII, so the
    // channel that carries an author-chosen key straight to a host had no coverage at all for the
    // one thing TextSanitiser exists to stop.
    [InlineData("\u202e", "\\u202e")]
    [InlineData("\u200b", "\\u200b")]
    [InlineData("\ufeff", "\\ufeff")]
    public void AFindingOnANonPrintableKey_ReportsAnEscapedPath(string raw, string escaped)
    {
        using var fixture = SemanticRuleFixture.For($"""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                headers:
                  "auth{raw}orization": "AKIAIOSFODNN7EXAMPLE"
            """);

        var finding = Assert.Single(fixture.Run(new SecretLiteralRule()));

        Assert.Equal($"$.steps[0].headers['auth{escaped}orization']", finding.Path);
        Assert.DoesNotContain(raw, finding.Path!, StringComparison.Ordinal);
    }

    /// <summary>Mirrors <c>SemanticPassTests.RepoRoot</c> exactly — see that property's remarks.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");

            return testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");
        }
    }
}

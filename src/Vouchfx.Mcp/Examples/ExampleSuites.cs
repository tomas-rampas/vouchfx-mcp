namespace Vouchfx.Mcp.Examples;

// Vouchfx.Mcp.Examples — the repo-local sample suites (Sprint 5 / US-S5-01 AC-004).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHAT "COMPLETE, PASSING SAMPLE" MEANS HERE, STATED HONESTLY
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// The AC asks for "a complete, passing sample .e2e.yaml". Two of those three words are MEASURED in
// this repository and the third is not, so the distinction is recorded rather than smoothed over:
//
//   * COMPLETE — measured. Each file is a whole document with every block a real suite needs
//     (metadata, environment, steps), not a fragment. ExampleSuiteCatalogueTests parses each one.
//   * SCHEMA-VALID — measured. Each file goes through the SAME SuiteValidator pipeline
//     validate_suite uses, against the SAME vendored composed schema, and must produce zero schema
//     errors. An example that drifts from what the engine accepts fails the build.
//   * PASSING AGAINST A RUNNING ENVIRONMENT — NOT measured, and cannot be here. Every suite names
//     container images (myco/orders-service:latest) that do not exist; running one needs Docker and
//     the pinned CLI, which no test in this repository depends on (CLAUDE.md's testing conventions).
//     So "passing" is claimed on SHAPE — idiomatic, schema-valid, drawn from the pinned engine's own
//     vendored/recipes.md patterns — and a host copying one must substitute its own images. Each
//     file says so in its own comments.
//
// Nothing about that is a shortfall in the examples; it is what a repository with no sample app can
// truthfully assert. US-S5-07's M4 drill is where a suite gets run for real.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY EMBEDDED RATHER THAN READ FROM examples/ ON DISK
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// The same discipline the vendored documents and the VFX-* catalogue pages already follow (see
// Docs/VendoredDocRepository and ErrorCatalogue/DiagnosticPageRepository): a manifest resource
// resolves identically from a source checkout and from the installed `Vouchfx.Mcp` dotnet tool,
// whereas a loose-file read needs a base-directory guess that is wrong in one of those two cases.
// It also means serving an example touches no filesystem at all, which keeps this resource inside
// the CLI-free, offline class.

/// <summary>
/// Everything <see cref="Vouchfx.Mcp.Resources.ExampleResourceRegistry"/> needs to know about one
/// repo-local sample suite.
/// </summary>
/// <param name="Name">
/// The <c>{name}</c> segment of <c>vouchfx://examples/{name}</c> — a flat identifier, matched
/// ORDINALLY, and also the file's own stem under <c>examples/</c>.
/// </param>
/// <param name="Title">A human-readable title, used as the MCP resource's <c>Name</c>.</param>
/// <param name="Description">One or two sentences saying what pattern this example teaches.</param>
/// <param name="EmbeddedResourceName">
/// The exact manifest resource name embedded into this assembly (see <c>Vouchfx.Mcp.csproj</c>'s
/// <c>EmbeddedResource</c> items) — the ONLY place this example's content is ever read from.
/// </param>
/// <param name="FileName">
/// The file's name under the repository's <c>examples/</c> directory. Carried purely so
/// <c>ExampleSuiteCatalogueTests</c> can assert the catalogue and the directory have not drifted
/// apart; nothing at run time reads the disk.
/// </param>
public sealed record ExampleSuite(
    string Name, string Title, string Description, string EmbeddedResourceName, string FileName);

/// <summary>
/// The fixed catalogue of sample suites this server serves under
/// <c>vouchfx://examples/{name}</c> — see this file's header for what is and is not asserted about
/// them.
/// </summary>
public static class ExampleSuites
{
    /// <summary>The smallest complete suite: two <c>http.rest</c> steps against one service.</summary>
    public static readonly ExampleSuite HttpSmoke = new(
        Name: "http-smoke",
        Title: "Smallest complete suite: HTTP smoke test",
        Description:
            "The narrowest whole .e2e.yaml document there is — metadata, one environment service, "
            + "variables, and two http.rest steps with explicit status assertions. Start here: it "
            + "shows every top-level block and both required step fields without any pattern layered "
            + "on top. Comment-annotated line by line.",
        EmbeddedResourceName: "Vouchfx.Mcp.Examples.http-smoke.e2e.yaml",
        FileName: "http-smoke.e2e.yaml");

    /// <summary>State threading plus engine-owned polling — the shape most real suites take.</summary>
    public static readonly ExampleSuite CaptureAndRetry = new(
        Name: "capture-and-retry",
        Title: "State threading with capture, and polling with verifyMode: RETRY",
        Description:
            "The pattern most real suites are built from: an action step, a `capture` that pulls its "
            + "server-assigned id out of the response, a `{placeholder}` threading that id into a "
            + "later step, and `verifyMode: RETRY` with an explicit `timeout` so an asynchronous "
            + "effect is polled for rather than slept on. Includes a managed postgres dependency and "
            + "parameterised SQL. Comment-annotated.",
        EmbeddedResourceName: "Vouchfx.Mcp.Examples.capture-and-retry.e2e.yaml",
        FileName: "capture-and-retry.e2e.yaml");

    /// <summary>Secret references and an asynchronous message-queue round trip.</summary>
    public static readonly ExampleSuite SecretsAndMessaging = new(
        Name: "secrets-and-messaging",
        Title: "Secret references and a message-queue round trip",
        Description:
            "How to pass a credential without ever writing one down: every secret is a "
            + "${secret:env/NAME} REFERENCE the engine resolves at step-execution time and redacts "
            + "from every output — never a literal, which validate_suite flags as VFX-D-1207. Also "
            + "shows an mq-expect.kafka match block and a cache-assert.redis read-model check, both "
            + "under RETRY. Comment-annotated.",
        EmbeddedResourceName: "Vouchfx.Mcp.Examples.secrets-and-messaging.e2e.yaml",
        FileName: "secrets-and-messaging.e2e.yaml");

    /// <summary>
    /// Every example this server serves, in the order it advertises them — simplest first, because a
    /// host reading the list top-down should meet the whole-document shape before any pattern.
    /// </summary>
    public static IReadOnlyList<ExampleSuite> All { get; } = [HttpSmoke, CaptureAndRetry, SecretsAndMessaging];

    /// <summary>
    /// Finds an example by its <see cref="ExampleSuite.Name"/>, or <see langword="null"/> when the
    /// name is not one this server carries.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing because the argument is UNTRUSTED caller input arriving off
    /// a URI — an unknown name is an ordinary EDGE case, never a programming error inside this
    /// assembly. Contrast <see cref="Vouchfx.Mcp.Docs.VendoredDocuments.Find"/>, whose only callers
    /// pass compile-time constants and which therefore throws.
    /// </remarks>
    public static ExampleSuite? TryFind(string name) =>
        All.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
}

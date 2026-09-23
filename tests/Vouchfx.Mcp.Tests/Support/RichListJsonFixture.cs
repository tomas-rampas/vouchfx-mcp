using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Builds a Spec A bar-B <c>vouchfx list --json</c> document from the embedded vendored schema
/// catalogue so MCP integration tests can exercise live catalogue tools without a real CLI. Each
/// entry also carries the four members engine v1.0.0-rc.6 added (<c>tier</c>,
/// <c>supportedVerifyModes</c>, <c>docsUrl</c>, <c>example</c>), in the shape the pinned engine emits
/// them for a Core type.
/// </summary>
internal static class RichListJsonFixture
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// The <c>supportedVerifyModes</c> the pinned engine reports for every step type: it compiles
    /// <c>RETRY</c> for all of them and no provider can opt out.
    /// </summary>
    private static readonly string[] EveryVerifyMode = ["IMMEDIATE", "RETRY"];

    /// <summary>
    /// Lazy so the first consumer pays the one-time serialisation cost; every subsequent caller
    /// reuses the same document text.
    /// </summary>
    public static string Json { get; } = Build();

    /// <summary>
    /// A thin pre-Spec-A <c>list --json</c> document (type/family/provider only) used to prove
    /// EDGE-004 fail-fast when bar-B fields are missing.
    /// </summary>
    public static string ThinJson { get; } = """
        {
          "schemaVersion": 1,
          "engineVersion": "1.0.0-thin-fixture",
          "stepTypes": [
            {
              "type": "http.rest",
              "family": "http",
              "provider": "rest"
            },
            {
              "type": "db-assert.postgres",
              "family": "db-assert",
              "provider": "postgres"
            }
          ]
        }
        """;

    /// <summary>
    /// A minimal valid bar-B document with a single step type (parser unit tests).
    /// </summary>
    public static string SingleHttpRestJson { get; } = """
        {
          "schemaVersion": 1,
          "engineVersion": "1.2.3-fixture",
          "stepTypes": [
            {
              "type": "http.rest",
              "family": "http",
              "provider": "rest",
              "requiredFields": ["method", "path", "target"],
              "optionalFields": ["body", "expect", "headers"],
              "captureSupported": true,
              "familyIntent": "Call HTTP endpoints (REST or SOAP) on services under test and assert responses."
            }
          ]
        }
        """;

    private static string Build()
    {
        var stepTypes = StepTypeCatalogue.All.Select(t => new
        {
            type = t.Type,
            family = t.Family,
            provider = t.Provider,
            requiredFields = t.RequiredFields,
            optionalFields = t.OptionalFields,
            captureSupported = t.CaptureSupported,
            familyIntent = t.FamilyIntent,
            tier = "core",
            supportedVerifyModes = EveryVerifyMode,
            docsUrl = "https://vouchfx.io/language-reference/#" + t.Type.Replace(".", string.Empty, StringComparison.Ordinal),
            example = $"steps:\n  - id: example\n    type: {t.Type}\n",
        }).ToArray();

        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                engineVersion = "1.0.0-test-fixture",
                stepTypes,
            },
            Options);
    }
}

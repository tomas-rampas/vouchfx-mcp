using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Builds a Spec A bar-B <c>vouchfx list --json</c> document from the embedded vendored schema
/// catalogue so MCP integration tests can exercise live catalogue tools without a real CLI.
/// </summary>
internal static class RichListJsonFixture
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

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

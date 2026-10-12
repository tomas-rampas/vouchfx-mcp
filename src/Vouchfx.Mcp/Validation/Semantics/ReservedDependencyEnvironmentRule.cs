using System.Collections.Frozen;
using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>Authoring-time advice for managed dependency names the engine sets itself.</summary>
/// <remarks>
/// This is an engine-owned, type-specific table, not a general container-environment policy.
/// The exact pinned source table and every refusal are checked by the real parity probe.
/// Services and external driver configuration are outside this rule's authority.
/// </remarks>
internal sealed class ReservedDependencyEnvironmentRule : ISemanticRule
{
    public static FrozenDictionary<string, FrozenSet<string>> EngineOwnedKeys { get; } =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["elasticsearch"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "discovery.type",
                "xpack.security.enabled",
                "ES_JAVA_OPTS",
                "cluster.routing.allocation.disk.threshold_enabled",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["minio"] = new HashSet<string>(StringComparer.Ordinal) { "MINIO_ROOT_USER", "MINIO_ROOT_PASSWORD" }.ToFrozenSet(StringComparer.Ordinal),
            ["s3"] = new HashSet<string>(StringComparer.Ordinal) { "RUSTFS_ACCESS_KEY", "RUSTFS_SECRET_KEY" }.ToFrozenSet(StringComparer.Ordinal),
            ["azureservicebus"] = new HashSet<string>(StringComparer.Ordinal) { "ACCEPT_EULA", "MSSQL_SA_PASSWORD", "SQL_SERVER" }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public string Code => VfxCodeCatalogue.ReservedDependencyEnvironmentName;

    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Facts.HasExternalEnvironmentSource ||
            context.Document.ValueKind != JsonValueKind.Object ||
            !context.Document.TryGetProperty("environment", out var environment) ||
            environment.ValueKind != JsonValueKind.Object ||
            !environment.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var findings = new List<Diagnostic>();
        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (SuiteDocument.StringProperty(dependency.Value, "type") is not { } type ||
                !EngineOwnedKeys.TryGetValue(type, out var reserved) ||
                !dependency.Value.TryGetProperty("env", out var env) ||
                env.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var entry in env.EnumerateObject())
            {
                if (!reserved.Contains(entry.Name))
                {
                    continue;
                }

                var path = SuitePath.Root.Property("environment").Property("dependencies");
                if (!dependency.Name.Contains("${", StringComparison.Ordinal))
                {
                    path = path.Property(dependency.Name).Property("env").Property(entry.Name);
                }

                // Never read the value: a container configuration mistake must not expose a secret.
                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Error,
                    $"The engine sets {SemanticFinding.Identifier(entry.Name)} for a managed "
                    + $"{SemanticFinding.Identifier(type)} dependency. Remove that env entry, or declare "
                    + "the backend as a service with image when you need control of its environment.",
                    path));
            }
        }

        return findings;
    }
}

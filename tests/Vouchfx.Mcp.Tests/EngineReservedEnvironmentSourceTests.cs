namespace Vouchfx.Mcp.Tests;

public class EngineReservedEnvironmentSourceTests
{
    private const string Source = """
        class Mapper
        {
            private static readonly Dictionary<string, IReadOnlySet<string>> s_engineSetEnvKeys =
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    ["elasticsearch"] = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "ES_JAVA_OPTS",
                        "discovery.type",
                    },
                    ["s3"] = new HashSet<string>(StringComparer.Ordinal) { "RUSTFS_SECRET_KEY" },
                };
        }
        """;

    [Fact]
    public void EveryEntry_IsExtractedAndSorted_IndependentOfFormatting()
    {
        Assert.Equal(
            ["elasticsearch/ES_JAVA_OPTS", "elasticsearch/discovery.type", "s3/RUSTFS_SECRET_KEY"],
            EngineReservedEnvironmentSource.Rows(Source));
    }

    [Fact]
    public void AnAddedOrRemovedKey_ChangesTheCompleteTable()
    {
        var original = EngineReservedEnvironmentSource.Rows(Source);
        var added = EngineReservedEnvironmentSource.Rows(Source.Replace("\"ES_JAVA_OPTS\",", "\"ES_JAVA_OPTS\", \"NEW_ENGINE_KEY\",", StringComparison.Ordinal));
        Assert.NotEqual(original, added);
        Assert.Contains("elasticsearch/NEW_ENGINE_KEY", added);
        var removed = EngineReservedEnvironmentSource.Rows(Source.Replace("\"ES_JAVA_OPTS\",", "", StringComparison.Ordinal));
        Assert.NotEqual(original, removed);
        Assert.DoesNotContain("elasticsearch/ES_JAVA_OPTS", removed);
    }

    [Theory]
    [InlineData("s_engineSetEnvKeys", "renamedTable")]
    [InlineData("new HashSet<string>(StringComparer.Ordinal) { \"RUSTFS_SECRET_KEY\" }", "BuildKeys()")]
    [InlineData("\"discovery.type\",", "\"ES_JAVA_OPTS\",")]
    public void AChangedTableShapeOrDuplicate_FailsClosed(string before, string after)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => EngineReservedEnvironmentSource.Rows(Source.Replace(before, after, StringComparison.Ordinal)));
    }
}

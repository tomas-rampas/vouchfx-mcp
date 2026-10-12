using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vouchfx.Mcp.Tests;

/// <summary>Extracts the entire engine-owned table, so new keys cannot pass a known-key-only probe.</summary>
internal static class EngineReservedEnvironmentSource
{
    internal static string[] Rows(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var errors = tree.GetDiagnostics().Where(x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.Empty(errors);
        var variable = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            x => x.Identifier.ValueText == "s_engineSetEnvKeys");
        var table = Assert.IsType<ObjectCreationExpressionSyntax>(variable.Initializer!.Value).Initializer;
        Assert.NotNull(table);
        Assert.NotEmpty(table.Expressions);
        var rows = new List<string>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expression in table.Expressions)
        {
            var assignment = Assert.IsType<AssignmentExpressionSyntax>(expression);
            var index = Assert.IsType<ImplicitElementAccessSyntax>(assignment.Left);
            var kind = StringLiteral(Assert.Single(index.ArgumentList.Arguments).Expression);
            Assert.True(types.Add(kind), "The engine-owned table contains a duplicate type.");
            var keys = Assert.IsType<ObjectCreationExpressionSyntax>(assignment.Right).Initializer;
            Assert.NotNull(keys);
            Assert.NotEmpty(keys.Expressions);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys.Expressions)
            {
                var name = StringLiteral(key);
                Assert.True(names.Add(name), "The engine-owned table contains a duplicate key.");
                rows.Add(kind + "/" + name);
            }
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static string StringLiteral(ExpressionSyntax expression)
    {
        var literal = Assert.IsType<LiteralExpressionSyntax>(expression);
        Assert.True(literal.IsKind(SyntaxKind.StringLiteralExpression));
        return literal.Token.ValueText;
    }
}

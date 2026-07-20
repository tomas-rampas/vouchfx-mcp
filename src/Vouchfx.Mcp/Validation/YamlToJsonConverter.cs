using System.Globalization;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Converts raw <c>.e2e.yaml</c> text into a <see cref="JsonDocument"/> suitable for evaluating
/// against the embedded composed JSON Schema.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the vouchfx engine's own YAML→JSON bridge
/// (<c>Vouchfx.Engine.Compilation.Schema.SchemaResources.ConvertYamlToJsonDocument</c>) exactly —
/// same <see cref="DeserializerBuilder"/> configuration, same plain-scalar type inference for
/// integers/floats/booleans, same <c>SerializerBuilder().JsonCompatible()</c> re-emission — so
/// that validate_suite's verdict for a given suite can never diverge from what a real
/// <c>vouchfx</c> engine run would report because of a YAML-scalar-resolution difference between
/// two independently-written converters.
/// </para>
/// <para>
/// Plain (untagged) scalars that parse as an integer or floating-point number are emitted as
/// JSON numbers rather than quoted strings, and plain <c>true</c>/<c>false</c> (case-insensitive)
/// as JSON booleans — preserving the <c>type: integer</c> / <c>type: number</c> / <c>type: boolean</c>
/// constraints the schema declares. Quoted, literal, or folded scalars are always left as JSON
/// strings, since explicit quoting is exactly how a YAML author opts out of this inference (e.g.
/// <c>version: "1.0"</c>). YAML 1.1 aliases (<c>yes</c>/<c>no</c>/<c>on</c>/<c>off</c>) are
/// deliberately NOT recognised as booleans, to avoid coercing unrelated string fields that happen
/// to contain those words.
/// </para>
/// </remarks>
internal static class YamlToJsonConverter
{
    /// <summary>
    /// Converts <paramref name="yamlText"/> to a <see cref="JsonDocument"/>.
    /// </summary>
    /// <exception cref="YamlException">The YAML text is not syntactically valid.</exception>
    /// <exception cref="InvalidOperationException">The YAML document is empty.</exception>
    public static JsonDocument Convert(string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .WithNodeTypeResolver(new PlainScalarTypeResolver())
            .Build();

        var graph = deserializer.Deserialize<object?>(yamlText);
        if (graph is null)
        {
            throw new InvalidOperationException("The YAML document is empty.");
        }

        var jsonSerializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();

        var json = jsonSerializer.Serialize(graph);
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Recognises plain (untagged) scalars that represent an integer, a floating-point number, or
    /// a boolean, so the deserialiser returns the CLR type (<see cref="long"/>, <see cref="double"/>,
    /// or <see cref="bool"/>) instead of the default <see cref="string"/>.
    /// </summary>
    private sealed class PlainScalarTypeResolver : INodeTypeResolver
    {
        bool INodeTypeResolver.Resolve(NodeEvent? nodeEvent, ref Type currentType)
        {
            if (nodeEvent is Scalar scalar && scalar.Style == ScalarStyle.Plain && currentType == typeof(object))
            {
                var value = scalar.Value;

                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    currentType = typeof(long);
                    return true;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    currentType = typeof(double);
                    return true;
                }

                if (bool.TryParse(value, out _))
                {
                    currentType = typeof(bool);
                    return true;
                }
            }

            return false;
        }
    }
}

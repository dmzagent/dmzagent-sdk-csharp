using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// JSON normalization per contract-tests runner-spec.md: sort keys at
/// every level, omit insignificant whitespace, omit trailing newline.
/// Used to compare a captured POST body to the canonical expected body.
/// </summary>
internal static class JsonNormalize
{
    public static string Canonicalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return CanonicalizeElement(doc.RootElement);
    }

    public static string Canonicalize(JsonElement element) => CanonicalizeElement(element);

    private static string CanonicalizeElement(JsonElement element)
    {
        using var ms     = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            WriteCanonical(writer, element);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, System.StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
        }
    }
}

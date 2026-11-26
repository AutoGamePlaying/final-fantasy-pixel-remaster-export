using System.Text.Encodings.Web;
using System.Text.Json;
using AssetsTools.NET;

namespace FfprExport;

internal static class FieldJsonWriter
{
    public static readonly JsonWriterOptions IndentedOptions = TextFileWriter.CreateJsonWriterOptions(
        indented: true,
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    public static readonly JsonSerializerOptions SerializerOptions = TextFileWriter.CreateJsonSerializerOptions(
        writeIndented: true,
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    public static readonly JsonSerializerOptions CompactSerializerOptions = TextFileWriter.CreateJsonSerializerOptions(
        writeIndented: false,
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    public static void WriteFieldValue(Utf8JsonWriter writer, AssetTypeValueField field)
    {
        var template = field.TemplateField;
        var value = field.Value;

        if (value is not null)
        {
            switch (value.ValueType)
            {
                case AssetValueType.Bool:
                    writer.WriteBooleanValue(value.AsBool);
                    return;
                case AssetValueType.Int8:
                    writer.WriteNumberValue(value.AsSByte);
                    return;
                case AssetValueType.Int16:
                    writer.WriteNumberValue(value.AsShort);
                    return;
                case AssetValueType.Int32:
                    writer.WriteNumberValue(value.AsInt);
                    return;
                case AssetValueType.Int64:
                    writer.WriteNumberValue(value.AsLong);
                    return;
                case AssetValueType.UInt8:
                    writer.WriteNumberValue(value.AsByte);
                    return;
                case AssetValueType.UInt16:
                    writer.WriteNumberValue(value.AsUShort);
                    return;
                case AssetValueType.UInt32:
                    writer.WriteNumberValue(value.AsUInt);
                    return;
                case AssetValueType.UInt64:
                    writer.WriteNumberValue(value.AsULong);
                    return;
                case AssetValueType.Float:
                    var f = value.AsFloat;
                    if (float.IsNaN(f) || float.IsInfinity(f))
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        writer.WriteNumberValue(f);
                    }

                    return;
                case AssetValueType.Double:
                    var d = value.AsDouble;
                    if (double.IsNaN(d) || double.IsInfinity(d))
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        writer.WriteNumberValue(d);
                    }

                    return;
                case AssetValueType.String:
                    writer.WriteStringValue(value.AsString ?? string.Empty);
                    return;
                case AssetValueType.ByteArray:
                    writer.WriteBase64StringValue(value.AsByteArray ?? []);
                    return;
                case AssetValueType.Array:
                    writer.WriteStartArray();
                    foreach (var child in field.Children)
                    {
                        WriteFieldValue(writer, child);
                    }

                    writer.WriteEndArray();
                    return;
                case AssetValueType.ManagedReferencesRegistry:
                    WriteManagedReferencesRegistry(writer, value.AsManagedReferencesRegistry);
                    return;
            }
        }

        // Collapse standard Unity wrapper structs that contain a single Array child (e.g., vector / map / set).
        if (field.Children.Count == 1
            && field.Children[0].TemplateField.IsArray
            && string.Equals(field.Children[0].FieldName, "Array", StringComparison.Ordinal))
        {
            WriteFieldValue(writer, field.Children[0]);
            return;
        }

        writer.WriteStartObject();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in field.Children)
        {
            var propName = string.IsNullOrEmpty(child.FieldName) ? "item" : child.FieldName;
            if (!seenNames.Add(propName))
            {
                var suffix = 1;
                while (!seenNames.Add($"{propName}_{suffix}"))
                {
                    suffix++;
                }

                propName = $"{propName}_{suffix}";
            }

            writer.WritePropertyName(propName);
            WriteFieldValue(writer, child);
        }

        writer.WriteEndObject();
    }

    private static void WriteManagedReferencesRegistry(Utf8JsonWriter writer, ManagedReferencesRegistry? registry)
    {
        if (registry is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("version", registry.version);
        writer.WritePropertyName("references");
        writer.WriteStartArray();
        if (registry.references is not null)
        {
            foreach (var r in registry.references)
            {
                writer.WriteStartObject();
                writer.WriteNumber("rid", r.rid);
                writer.WriteString("class", r.type?.ClassName ?? string.Empty);
                writer.WriteString("namespace", r.type?.Namespace ?? string.Empty);
                writer.WriteString("assembly", r.type?.AsmName ?? string.Empty);
                writer.WritePropertyName("data");
                if (r.data is not null)
                {
                    WriteFieldValue(writer, r.data);
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

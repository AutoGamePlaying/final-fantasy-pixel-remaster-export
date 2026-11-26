using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FfprExport.Db;

internal static partial class DbParsers
{
    public static readonly JsonSerializerOptions CompactJsonOptions = TextFileWriter.CreateJsonSerializerOptions(
        writeIndented: false,
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    public static readonly JsonSerializerOptions PrettyJsonOptions = TextFileWriter.CreateJsonSerializerOptions(
        writeIndented: true,
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    public static readonly JsonWriterOptions CompactWriterOptions = TextFileWriter.CreateJsonWriterOptions(
        indented: false,
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    // Strips FFPR control/icon/Korean-particle tags such as <IC_BAG>, <P>, <CT>, <WAIT00>, <josa(이,가)>.
    [GeneratedRegex(@"<[A-Za-z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex MessageTagRegex();

    public static string StripMessageTags(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0)
        {
            return value;
        }

        return MessageTagRegex().Replace(value, string.Empty);
    }

    public static (IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows) ParseCsv(string content)
    {
        var text = StripBom(content);
        var records = new List<string[]>();
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                continue;
            }

            if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
                fieldStarted = false;
                continue;
            }

            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                if (fields.Count > 0 || current.Length > 0 || fieldStarted)
                {
                    fields.Add(current.ToString());
                    records.Add(fields.ToArray());
                    fields.Clear();
                    current.Clear();
                    fieldStarted = false;
                }

                continue;
            }

            current.Append(ch);
            fieldStarted = true;
        }

        if (fields.Count > 0 || current.Length > 0 || fieldStarted)
        {
            fields.Add(current.ToString());
            records.Add(fields.ToArray());
        }

        if (records.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string[]>());
        }

        var headers = records[0];
        var rows = records.Count > 1 ? records.GetRange(1, records.Count - 1) : new List<string[]>();
        return (headers, rows);
    }

    public static IReadOnlyList<(string Key, string Value, int LineNumber)> ParseMessageTsv(string content)
    {
        var text = StripBom(content);
        var result = new List<(string Key, string Value, int LineNumber)>();
        var lineNumber = 0;
        var pos = 0;

        while (pos < text.Length)
        {
            var end = pos;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r')
            {
                end++;
            }

            lineNumber++;
            var line = text[pos..end];
            if (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n')
            {
                pos = end + 2;
            }
            else if (end < text.Length)
            {
                pos = end + 1;
            }
            else
            {
                pos = end;
            }

            if (line.Length == 0)
            {
                continue;
            }

            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            var key = line[..tabIndex];
            var val = line[(tabIndex + 1)..];
            result.Add((key, val, lineNumber));
        }

        return result;
    }

    public static string StripBom(string text)
    {
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static string SerializeRowObject(IReadOnlyList<string> headers, string[] row)
    {
        using var stream = new MemoryStream();
        using (var writer = TextFileWriter.CreateUtf8JsonWriter(stream, CompactWriterOptions))
        {
            writer.WriteStartObject();
            for (var i = 0; i < headers.Count; i++)
            {
                var value = i < row.Length ? row[i] : string.Empty;
                writer.WriteString(headers[i], value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeStringArray(IReadOnlyList<string> items)
    {
        return JsonSerializer.Serialize(items, CompactJsonOptions);
    }

    public static string NormalizeJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = TextFileWriter.CreateUtf8JsonWriter(stream, CompactWriterOptions))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeRawTextAsJson(string rawText)
    {
        using var stream = new MemoryStream();
        using (var writer = TextFileWriter.CreateUtf8JsonWriter(stream, CompactWriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("raw", rawText);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static long ParseLongOrDefault(string? raw, long defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public static long? TryParseLong(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static string NormalizeMessageKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        return string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase) ? string.Empty : trimmed;
    }

    public static byte[] PackInt32GridLittleEndian(IReadOnlyList<int> cells)
    {
        var bytes = new byte[cells.Count * sizeof(int)];
        for (var i = 0; i < cells.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int), sizeof(int)), cells[i]);
        }

        return bytes;
    }

    public static int ReadInt32GridCell(byte[] blob, int width, int height, int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                L.Format("Db_GridCoordOutOfRange", x, y, width, height));
        }

        var offset = ((y * width) + x) * sizeof(int);
        if (offset + sizeof(int) > blob.Length)
        {
            throw new InvalidDataException(L.Format("Db_GridBlobTooShort", blob.Length, offset + sizeof(int)));
        }

        return BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(offset, sizeof(int)));
    }

    public static (int TileX, int TileY) ComputeTileCoordinates(
        double pxX,
        double pxY,
        bool hasGid,
        int tileWidth,
        int tileHeight)
    {
        var tw = tileWidth > 0 ? tileWidth : 16;
        var th = tileHeight > 0 ? tileHeight : 16;
        var tileX = (int)Math.Floor(pxX / tw);
        var tileY = hasGid
            ? (int)Math.Floor(pxY / th) - 1
            : (int)Math.Floor(pxY / th);
        return (tileX, tileY);
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeSourceSetSha256(IEnumerable<(string RelativePath, string FileSha256)> entries)
    {
        var sorted = entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)sorted.Count);
        hasher.AppendData(lenBuf);

        foreach (var (relPath, fileHash) in sorted)
        {
            AppendLengthPrefixedUtf8(hasher, relPath);
            AppendLengthPrefixedUtf8(hasher, fileHash);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    public static void AppendLengthPrefixedUtf8(IncrementalHash hasher, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)bytes.Length);
        hasher.AppendData(lenBuf);
        hasher.AppendData(bytes);
    }

    public static string ToRelativeUnixPath(string rootDirectory, string fullPath)
    {
        return Path.GetRelativePath(rootDirectory, fullPath).Replace('\\', '/');
    }

    public static string GetStringProp(JsonElement obj, string name, string defaultValue = "")
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? defaultValue,
                JsonValueKind.Number => prop.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => defaultValue,
            };
        }

        return defaultValue;
    }

    public static int GetIntProp(JsonElement obj, string name, int defaultValue = 0)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var iv))
            {
                return iv;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var dv))
            {
                return (int)dv;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
            {
                return sv;
            }
        }

        return defaultValue;
    }

    public static long? GetNullableLongProp(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var lv))
            {
                return lv;
            }

            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
            {
                return sv;
            }
        }

        return null;
    }

    public static double GetDoubleProp(JsonElement obj, string name, double defaultValue = 0.0)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var dv))
            {
                return dv;
            }

            if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sv))
            {
                return sv;
            }
        }

        return defaultValue;
    }

    public static bool GetBoolProp(JsonElement obj, string name, bool defaultValue = false)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.TryGetInt64(out var n) && n != 0,
                JsonValueKind.String => bool.TryParse(prop.GetString(), out var b) ? b : defaultValue,
                _ => defaultValue,
            };
        }

        return defaultValue;
    }
}

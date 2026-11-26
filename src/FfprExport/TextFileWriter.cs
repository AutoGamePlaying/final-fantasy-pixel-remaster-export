using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FfprExport;

/// <summary>
/// Centralized UTF-8 (without BOM) LF ("\n") text and JSON writing helpers.
/// Every tool-generated text file across CLI and GUI must go through this class so
/// Linux and Windows produce byte-for-byte identical outputs.
/// </summary>
internal static class TextFileWriter
{
    public const string LineEnding = "\n";

    public static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string NormalizeLineEndings(string content)
    {
        if (content.IndexOf('\r') < 0)
        {
            return content;
        }

        return content.Replace("\r\n", LineEnding, StringComparison.Ordinal).Replace('\r', '\n');
    }

    public static JsonWriterOptions CreateJsonWriterOptions(
        bool indented,
        JavaScriptEncoder? encoder = null)
    {
        return new JsonWriterOptions
        {
            Indented = indented,
            NewLine = LineEnding,
            Encoder = encoder,
        };
    }

    public static JsonSerializerOptions CreateJsonSerializerOptions(
        bool writeIndented,
        JavaScriptEncoder? encoder = null)
    {
        return new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            NewLine = LineEnding,
            Encoder = encoder,
        };
    }

    public static Utf8JsonWriter CreateUtf8JsonWriter(Stream stream, JsonWriterOptions options)
    {
        var normalized = options;
        normalized.NewLine = LineEnding;
        return new Utf8JsonWriter(stream, normalized);
    }

    public static StreamWriter CreateStreamWriter(Stream stream, bool leaveOpen = false)
    {
        return new StreamWriter(stream, Utf8WithoutBom, bufferSize: 1024, leaveOpen: leaveOpen)
        {
            NewLine = LineEnding,
        };
    }

    public static StreamWriter CreateStreamWriter(string targetPath, bool append = false)
    {
        var ioTarget = PathSupport.ToIoPath(targetPath);
        EnsureParentDirectory(ioTarget);
        return new StreamWriter(ioTarget, append, Utf8WithoutBom)
        {
            NewLine = LineEnding,
        };
    }

    public static long WriteAllText(string targetPath, string content)
    {
        var ioTarget = PathSupport.ToIoPath(targetPath);
        EnsureParentDirectory(ioTarget);
        var bytes = Utf8WithoutBom.GetBytes(NormalizeLineEndings(content));
        File.WriteAllBytes(ioTarget, bytes);
        return bytes.LongLength;
    }

    public static long WriteAllTextAtomic(string targetPath, string content)
    {
        var bytes = Utf8WithoutBom.GetBytes(NormalizeLineEndings(content));
        WriteBytesAtomic(targetPath, bytes);
        return bytes.LongLength;
    }

    public static long WriteJsonAtomic<T>(
        string targetPath,
        T value,
        JsonSerializerOptions options,
        bool trailingNewLine = false)
    {
        var json = JsonSerializer.Serialize(value, options);
        if (trailingNewLine)
        {
            json += LineEnding;
        }

        return WriteAllTextAtomic(targetPath, json);
    }

    public static long WriteUtf8JsonAtomic(
        string targetPath,
        JsonWriterOptions options,
        Action<Utf8JsonWriter> writeAction)
    {
        using var ms = new MemoryStream();
        using (var writer = CreateUtf8JsonWriter(ms, options))
        {
            writeAction(writer);
        }

        var bytes = ms.ToArray();
        WriteBytesAtomic(targetPath, bytes);
        return bytes.LongLength;
    }

    internal static void WriteBytesAtomic(string targetPath, byte[] bytes)
    {
        var ioTarget = PathSupport.ToIoPath(targetPath);
        EnsureParentDirectory(ioTarget);
        var tempPath = $"{ioTarget}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, ioTarget, overwrite: true);
    }

    private static void EnsureParentDirectory(string ioTarget)
    {
        var parent = Path.GetDirectoryName(ioTarget);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}

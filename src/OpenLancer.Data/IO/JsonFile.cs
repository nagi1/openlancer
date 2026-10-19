using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenLancer.Data.IO;

/// <summary>
/// A JSON file that the tools write again: an object whose fields the writer keeps, also the fields that it does not
/// know. The file keeps its byte order mark and its line ends. The writer indents with two spaces.
/// </summary>
/// <remarks>
/// The readers of mod and profile files skip JSON comments. A comment cannot stay when the file is written again, so
/// <see cref="Read"/> refuses a file with a comment.
/// </remarks>
internal sealed class JsonFile
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        // Parse refuses a file with a comment before it parses the file. Fields skips the comments.
        CommentHandling = JsonCommentHandling.Skip,
    };

    private JsonFile(JsonObject root, bool byteOrderMark, string newLine)
    {
        Root = root;
        ByteOrderMark = byteOrderMark;
        NewLine = newLine;
    }

    /// <summary>The fields. Field names compare without letter case, as the readers compare them.</summary>
    public JsonObject Root { get; }

    public bool ByteOrderMark { get; }

    public string NewLine { get; }

    /// <summary>A new file with no fields, no byte order mark and LF line ends.</summary>
    public static JsonFile Create() => new(new JsonObject(NodeOptions), false, "\n");

    /// <exception cref="InvalidDataException">The file is not a JSON object, or it has a comment.</exception>
    public static JsonFile Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>The message of a file with a comment, which OpenLancer cannot write again.</summary>
    public const string CommentMessage = "The file has a comment. Remove the comments, then try again.";

    /// <summary>Parses the bytes of a file that the caller read, for example to hash the same bytes.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a JSON object, or they have a comment.</exception>
    public static JsonFile Parse(byte[] bytes)
    {
        var json = Body(bytes, out var bom);
        if (HasComment(json.Span))
            throw new InvalidDataException(CommentMessage);
        var newLine = json.Span.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        return new JsonFile(ParseObject(json.Span), bom, newLine);
    }

    /// <summary>
    /// The fields of the bytes for a read without a write. It skips comments, as the readers of mod and profile files
    /// do. A caller that has a comment must not write the file again, because the comment would go.
    /// </summary>
    /// <param name="hasComment">True when the bytes have a comment.</param>
    /// <exception cref="InvalidDataException">The bytes are not a JSON object.</exception>
    public static JsonObject Fields(byte[] bytes, out bool hasComment)
    {
        var json = Body(bytes, out _);
        hasComment = HasComment(json.Span);
        return ParseObject(json.Span);
    }

    /// <summary>The bytes after the byte order mark.</summary>
    private static ReadOnlyMemory<byte> Body(byte[] bytes, out bool byteOrderMark)
    {
        var preamble = Encoding.UTF8.Preamble;
        byteOrderMark = bytes.AsSpan().StartsWith(preamble);
        return byteOrderMark ? bytes.AsMemory(preamble.Length) : bytes.AsMemory();
    }

    private static JsonObject ParseObject(ReadOnlySpan<byte> json)
    {
        try
        {
            var obj = JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonObject ??
                      throw new InvalidDataException("The file must hold one JSON object.");
            // The object reads its fields on first use. A field name that is there two times throws then.
            _ = obj.Count;
            return obj;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Two fields of the file have the same name: " + e.Message, e);
        }
    }

    /// <summary>The bytes of the file: the byte order mark, the indented UTF-8 text and a line end.</summary>
    public byte[] ToBytes()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = NewLine,
            // Infocards hold RDL XML. Keep its '<', '>' and '&', and the characters outside ASCII, as they are.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var text = Encoding.UTF8.GetBytes(Root.ToJsonString(options) + NewLine);
        return ByteOrderMark ? [.. Encoding.UTF8.Preamble, .. text] : text;
    }

    private static bool HasComment(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Allow,
        });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                    return true;
            }
        }
        catch (JsonException)
        {
            // The parse reports the error.
        }
        return false;
    }
}

using System.Text;

namespace jd.resolver;

/// <summary>
/// A document handed to jd as it was read, so a request can carry it as well as a file can (docs/architecture/application.md section 3). Each kind's
/// loader decodes it; a client never does.
/// </summary>
/// <param name="Name">The name its errors print: the CLI passes the path as typed, so messages read as they did when the loaders opened files.</param>
/// <param name="Content">The exact bytes, for the loaders that hash or decode them strictly.</param>
public sealed record SourceDocument(string Name, ReadOnlyMemory<byte> Content)
{
    /// <summary>The content as text, decoded as <c>File.ReadAllText</c> decodes a file: UTF-8, or the encoding a byte-order mark names; the mark is dropped.</summary>
    public string Text()
    {
        // The reader File.ReadAllText builds over a file, here over the bytes; it reads memory, so ReadToEnd does no I/O.
        using var reader = new StreamReader(new MemoryStream(Content.ToArray(), writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

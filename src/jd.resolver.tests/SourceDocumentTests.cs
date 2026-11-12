using System.Text;
using Xunit;

namespace jd.resolver.tests;

// A document's text is what File.ReadAllText gives for the same bytes (docs/architecture/application.md section 8, risk b).
public sealed class SourceDocumentTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("jd-source-document-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    public static TheoryData<string, byte[]> Contents => new()
    {
        { "no mark", Encoding.UTF8.GetBytes("kind: Workload\nname: café\n") },
        { "a UTF-8 mark", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("kind: Workload\n")] },
        { "a UTF-16 mark", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("kind: Workload\n")] },
        { "a UTF-16 big-endian mark", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("kind: Workload\n")] },
        { "an invalid byte", [.. "kind: W"u8, 0xFF, .. "rkload\n"u8] },
        { "a cut multi-byte character at the end", [.. "name: caf"u8, 0xC3] },
        { "nothing", [] },
    };

    [Theory]
    [MemberData(nameof(Contents))]
    public void The_text_is_the_files_text_for_the_same_bytes(string content, byte[] bytes)
    {
        var path = Path.Combine(_temp, "document.yaml");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(File.ReadAllText(path), new SourceDocument(content, bytes).Text());
    }

    [Fact]
    public void A_utf8_mark_is_not_part_of_the_text_and_an_invalid_byte_is_the_replacement_character()
    {
        Assert.Equal("kind: W�rkload\n", new SourceDocument("x", (byte[])[.. Encoding.UTF8.GetPreamble(), .. "kind: W"u8, 0xFF, .. "rkload\n"u8]).Text());
    }
}

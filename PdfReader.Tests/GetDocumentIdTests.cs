using System.Security.Cryptography;
using System.Text;
using NovoRender.PDFReader;

namespace PdfReader.Tests;

public class GetDocumentIdTests : IDisposable
{
    private readonly string _tempDir;

    public GetDocumentIdTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PdfDocIdTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private FileInfo CreateTempPdf(byte[] content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, content);
        return new FileInfo(path);
    }

    [Fact]
    public void HexIdInTrailer()
    {
        var content = Encoding.Latin1.GetBytes(
            "%PDF-1.4\nsome content\ntrailer\n<</ID [<A1B2C3D4> <E5F6>]>>\n%%EOF");

        var file = CreateTempPdf(content);
        var id = PdfDocumentId.GetDocumentId(file);

        Assert.Equal("a1b2c3d4", id);
    }

    [Fact]
    public void LiteralIdInTrailer()
    {
        var content = Encoding.Latin1.GetBytes(
            "%PDF-1.4\nsome content\ntrailer\n<</ID [(ABC) (DEF)]>>\n%%EOF");

        var file = CreateTempPdf(content);
        var id = PdfDocumentId.GetDocumentId(file);

        Assert.Equal("414243", id);
    }

    [Fact]
    public void NoId_FallsBackToSha256()
    {
        var content = Encoding.Latin1.GetBytes(
            "%PDF-1.4\nsome content\ntrailer\n<</Size 42>>\n%%EOF");

        var file = CreateTempPdf(content);
        var id = PdfDocumentId.GetDocumentId(file);

        var expected = Convert.ToHexStringLower(SHA256.HashData(content));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void IdBeyondInitialWindow_ExpandingWindowFindsIt()
    {
        // Create content where the /ID is beyond the initial 8KB window
        var padding = new string('X', 9000);
        var trailer = "\ntrailer\n<</ID [<DEADBEEF> <CAFEBABE>]>>\n%%EOF";
        var content = Encoding.Latin1.GetBytes($"%PDF-1.4\n{padding}{trailer}");

        var file = CreateTempPdf(content);
        var id = PdfDocumentId.GetDocumentId(file);

        Assert.Equal("deadbeef", id);
    }
}
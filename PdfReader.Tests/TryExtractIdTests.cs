using NovoRender.PDFReader;

namespace PdfReader.Tests;

public class TryExtractIdTests
{
    [Fact]
    public void HexString_ExtractsLowercasedId()
    {
        var tail = "trailer\n<</ID [<A1B2C3D4E5F6> <ignored>]>>";

        Assert.True(PdfDocumentId.TryExtractId(tail, out var id));
        Assert.Equal("a1b2c3d4e5f6", id);
    }

    [Fact]
    public void HexString_StripsWhitespace()
    {
        var tail = "trailer\n<</ID [<A1 B2\tC3\nD4> <ignored>]>>";

        Assert.True(PdfDocumentId.TryExtractId(tail, out var id));
        Assert.Equal("a1b2c3d4", id);
    }

    [Fact]
    public void LiteralString_ReturnsHexEncodedBytes()
    {
        // "ABC" -> bytes 0x41, 0x42, 0x43
        var tail = "trailer\n<</ID [(ABC) (ignored)]>>";

        Assert.True(PdfDocumentId.TryExtractId(tail, out var id));
        Assert.Equal("414243", id);
    }

    [Fact]
    public void LiteralString_WithEscapes()
    {
        // "\n" -> 0x0A
        var tail = "trailer\n<</ID [(\\n) (ignored)]>>";

        Assert.True(PdfDocumentId.TryExtractId(tail, out var id));
        Assert.Equal("0a", id);
    }

    [Fact]
    public void LiteralString_WithEscapedParentheses()
    {
        // Binary ID containing 0x29 ')' encoded as \) in the literal string
        var tail = "trailer\n<</ID [(A\\)B) (ignored)]>>";

        Assert.True(PdfDocumentId.TryExtractId(tail, out var id));
        Assert.Equal("412942", id); // A=0x41, )=0x29, B=0x42
    }

    [Fact]
    public void NoMatch_ReturnsFalse()
    {
        var tail = "trailer\n<</Size 42>>";

        Assert.False(PdfDocumentId.TryExtractId(tail, out var id));
        Assert.Null(id);
    }
}

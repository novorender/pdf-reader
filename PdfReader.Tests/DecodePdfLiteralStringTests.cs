using NovoRender.PDFReader;

namespace PdfReader.Tests;

public class DecodePdfLiteralStringTests
{
    [Fact]
    public void SimpleAscii()
    {
        var result = PdfDocumentId.DecodePdfLiteralString("Hello");
        Assert.Equal("Hello"u8.ToArray(), result);
    }

    [Theory]
    [InlineData("n", (byte)'\n')]
    [InlineData("r", (byte)'\r')]
    [InlineData("t", (byte)'\t')]
    [InlineData("b", (byte)'\b')]
    [InlineData("f", (byte)'\f')]
    [InlineData("\\", (byte)'\\')]
    [InlineData("(", (byte)'(')]
    [InlineData(")", (byte)')')]
    public void EscapeSequences(string escaped, byte expected)
    {
        var result = PdfDocumentId.DecodePdfLiteralString($"\\{escaped}");
        Assert.Equal([expected], result);
    }

    [Fact]
    public void OctalEscape_ThreeDigits()
    {
        // \101 = 65 = 'A'
        var result = PdfDocumentId.DecodePdfLiteralString("\\101");
        Assert.Equal([(byte)'A'], result);
    }

    [Fact]
    public void OctalEscape_TwoDigits()
    {
        // \11 = 9 = '\t'
        var result = PdfDocumentId.DecodePdfLiteralString("\\11");
        Assert.Equal([(byte)'\t'], result);
    }

    [Fact]
    public void OctalEscape_OneDigit()
    {
        // \0 = 0
        var result = PdfDocumentId.DecodePdfLiteralString("\\0");
        Assert.Equal([(byte)0], result);
    }

    [Fact]
    public void UnknownEscape_BackslashIgnored()
    {
        // \q -> just 'q' per PDF spec
        var result = PdfDocumentId.DecodePdfLiteralString("\\q");
        Assert.Equal([(byte)'q'], result);
    }

    [Fact]
    public void EmptyString()
    {
        var result = PdfDocumentId.DecodePdfLiteralString("");
        Assert.Empty(result);
    }

    [Fact]
    public void TrailingBackslash()
    {
        // Trailing backslash with no char after -> treated as literal '\'
        var result = PdfDocumentId.DecodePdfLiteralString("A\\");
        Assert.Equal([(byte)'A', (byte)'\\'], result);
    }

    [Fact]
    public void LineContinuation_LF()
    {
        // Backslash + LF is ignored per PDF spec
        var result = PdfDocumentId.DecodePdfLiteralString("A\\\nB");
        Assert.Equal("AB"u8.ToArray(), result);
    }

    [Fact]
    public void LineContinuation_CR()
    {
        var result = PdfDocumentId.DecodePdfLiteralString("A\\\rB");
        Assert.Equal("AB"u8.ToArray(), result);
    }

    [Fact]
    public void LineContinuation_CRLF()
    {
        var result = PdfDocumentId.DecodePdfLiteralString("A\\\r\nB");
        Assert.Equal("AB"u8.ToArray(), result);
    }
}
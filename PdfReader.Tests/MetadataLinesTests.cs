using System.Text.Json;

using NovoRender.PDFReader;

namespace PdfReader.Tests;

// Verifies the model-tree shape emitted by PdfToImageConverter.BuildMetadataLines:
// single-page => one leaf row; multi-page => a file container row + one leaf per page
// (numeric path 1..n, display name "Page N"). This is the parser-side contract the
// downstream processing pipelines consume generically.
public class MetadataLinesTests
{
    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    private static string Prop(JsonElement row, string key)
    {
        foreach (var p in row.GetProperty("properties").EnumerateArray())
        {
            if (p[0].GetString() == key) return p[1].GetString();
        }
        return null;
    }

    [Fact]
    public void SinglePage_EmitsOneLeafWithEmptyPath()
    {
        var lines = PdfToImageConverter
            .BuildMetadataLines(1, "doc", new (uint, uint)[] { (100, 200) }, new[] { "p.jpeg" })
            .ToList();

        var row = Parse(Assert.Single(lines));
        Assert.Equal("", row.GetProperty("path").GetString());
        Assert.Equal(1, row.GetProperty("type").GetInt32()); // Leaf
        Assert.Equal("doc", Prop(row, "Procore/Id"));
        Assert.Equal("100,200", Prop(row, "Novorender/Document/Size"));
        Assert.Equal("p.jpeg", Prop(row, "Novorender/Document/Preview"));
    }

    [Fact]
    public void MultiPage_EmitsContainerPlusOneLeafPerPage()
    {
        var sizes = new (uint, uint)[] { (10, 20), (30, 40), (50, 60) };
        var previews = new[] { "a.jpeg", "b.jpeg", "c.jpeg" };

        var rows = PdfToImageConverter
            .BuildMetadataLines(3, "doc", sizes, previews)
            .Select(Parse)
            .ToList();

        Assert.Equal(4, rows.Count);

        // Container row: empty path, Internal (type 0), bare document id, no page image.
        var container = Assert.Single(rows, r => r.GetProperty("type").GetInt32() == 0);
        Assert.Equal("", container.GetProperty("path").GetString());
        Assert.Equal("", container.GetProperty("name").GetString());
        Assert.Equal("doc", Prop(container, "Procore/Id"));
        Assert.Null(Prop(container, "Novorender/Document/Preview"));

        // Page leaves: numeric path 1..3, display name "Page N", per-page id/size/preview.
        for (var i = 0; i < 3; i++)
        {
            var page = Assert.Single(rows, r => r.GetProperty("path").GetString() == (i + 1).ToString());
            Assert.Equal(1, page.GetProperty("type").GetInt32());
            Assert.Equal($"Page {i + 1}", page.GetProperty("name").GetString());
            Assert.Equal($"doc_{i}", Prop(page, "Procore/Id"));
            Assert.Equal(previews[i], Prop(page, "Novorender/Document/Preview"));
        }
    }
}

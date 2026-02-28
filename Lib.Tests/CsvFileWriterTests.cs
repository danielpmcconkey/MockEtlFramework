using System.Text;
using Lib.DataFrames;
using Lib.Modules;

namespace Lib.Tests;

public class CsvFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public CsvFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MockEtlTests_Csv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static DataFrame MakeTestFrame() => DataFrame.FromObjects(new[]
    {
        new { Id = 1, Name = "Alice",   City = "New York" },
        new { Id = 2, Name = "Bob",     City = "London"   },
        new { Id = 3, Name = "Charlie", City = "Paris"    }
    });

    private string ReadOutput(string fileName) =>
        File.ReadAllText(Path.Combine(_tempDir, fileName));

    private string[] ReadLines(string fileName) =>
        File.ReadAllLines(Path.Combine(_tempDir, fileName));

    [Fact]
    public void Execute_WritesHeaderAndDataRows()
    {
        var path = Path.Combine(_tempDir, "basic.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var lines = ReadLines("basic.csv");
        Assert.Equal(4, lines.Length); // header + 3 data rows
        Assert.Equal("Id,Name,City", lines[0]);
        Assert.Equal("1,Alice,New York", lines[1]);
    }

    [Fact]
    public void Execute_NoHeader_SkipsHeaderRow()
    {
        var path = Path.Combine(_tempDir, "noheader.csv");
        var writer = new CsvFileWriter("data", path, includeHeader: false);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var lines = ReadLines("noheader.csv");
        Assert.Equal(3, lines.Length); // data rows only
        Assert.Equal("1,Alice,New York", lines[0]);
    }

    [Fact]
    public void Execute_FieldWithComma_IsQuoted()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Last, First", ["Value"] = (object?)1 }
        });
        var path = Path.Combine(_tempDir, "comma.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        var lines = ReadLines("comma.csv");
        Assert.Equal("\"Last, First\",1", lines[1]);
    }

    [Fact]
    public void Execute_FieldWithDoubleQuote_IsEscaped()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "She said \"hello\"", ["Value"] = (object?)1 }
        });
        var path = Path.Combine(_tempDir, "quote.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        var lines = ReadLines("quote.csv");
        Assert.Equal("\"She said \"\"hello\"\"\",1", lines[1]);
    }

    [Fact]
    public void Execute_NullValues_RenderAsEmpty()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["A"] = "x", ["B"] = null, ["C"] = "z" }
        });
        var path = Path.Combine(_tempDir, "nulls.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        var lines = ReadLines("nulls.csv");
        Assert.Equal("x,,z", lines[1]);
    }

    [Fact]
    public void Execute_TrailerFormat_WritesTrailerLine()
    {
        var path = Path.Combine(_tempDir, "trailer.csv");
        var writer = new CsvFileWriter("data", path, trailerFormat: "TRAILER|{row_count}|{date}");
        var state = new Dictionary<string, object>
        {
            ["data"] = MakeTestFrame(),
            ["__maxEffectiveDate"] = new DateOnly(2024, 11, 15)
        };

        writer.Execute(state);

        var lines = ReadLines("trailer.csv");
        Assert.Equal(5, lines.Length); // header + 3 data + trailer
        Assert.Equal("TRAILER|3|2024-11-15", lines[4]);
    }

    [Fact]
    public void Execute_TrailerFormat_TimestampToken_Resolves()
    {
        var path = Path.Combine(_tempDir, "ts.csv");
        var writer = new CsvFileWriter("data", path, trailerFormat: "END|{timestamp}");
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var lines = ReadLines("ts.csv");
        var trailer = lines.Last();
        Assert.StartsWith("END|", trailer);
        Assert.Contains("T", trailer); // ISO 8601 timestamp
    }

    [Fact]
    public void Execute_OverwriteMode_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "overwrite.csv");
        File.WriteAllText(path, "old,stale,data\n1,2,3\n4,5,6\n");

        var writer = new CsvFileWriter("data", path, writeMode: WriteMode.Overwrite);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var lines = ReadLines("overwrite.csv");
        Assert.Equal("Id,Name,City", lines[0]); // new header, not old
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Execute_AppendMode_AppendsWithoutHeader()
    {
        var path = Path.Combine(_tempDir, "append.csv");
        File.WriteAllText(path, "Id,Name,City\n1,Alice,New York\n");

        var writer = new CsvFileWriter("data", path, writeMode: WriteMode.Append);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var content = ReadOutput("append.csv");
        // Original header + original row + 3 appended data rows (no second header)
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal("Id,Name,City", lines[0]); // only one header
    }

    [Fact]
    public void Execute_Utf8NoBom()
    {
        var path = Path.Combine(_tempDir, "encoding.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var bytes = File.ReadAllBytes(path);
        // UTF-8 BOM is EF BB BF — verify it's absent
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "File should not have a UTF-8 BOM");
    }

    [Fact]
    public void Execute_LfLineEndings()
    {
        var path = Path.Combine(_tempDir, "lf.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var content = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(content);
        Assert.DoesNotContain("\r\n", text);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void Execute_CreatesParentDirectory()
    {
        var path = Path.Combine(_tempDir, "nested", "deep", "output.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Execute_MissingDataFrame_ThrowsKeyNotFoundException()
    {
        var path = Path.Combine(_tempDir, "missing.csv");
        var writer = new CsvFileWriter("nonexistent", path);
        var state = new Dictionary<string, object>();

        Assert.Throws<KeyNotFoundException>(() => writer.Execute(state));
    }

    [Fact]
    public void Execute_ReturnsSharedStateUnchanged()
    {
        var path = Path.Combine(_tempDir, "state.csv");
        var writer = new CsvFileWriter("data", path);
        var state = new Dictionary<string, object>
        {
            ["data"] = MakeTestFrame(),
            ["other"] = "keep me"
        };

        var result = writer.Execute(state);

        Assert.Same(state, result);
        Assert.Equal("keep me", result["other"]);
    }
}

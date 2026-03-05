using System.Text;
using Lib.DataFrames;
using Lib.Modules;

namespace Lib.Tests;

public class CsvFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly DateOnly TestDate = new(2024, 11, 15);
    private const string TestDateStr = "2024-11-15";

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

    private CsvFileWriter MakeWriter(string fileName = "output.csv",
        bool includeHeader = true, string? trailerFormat = null,
        WriteMode writeMode = WriteMode.Overwrite, string lineEnding = "\n") =>
        new("data", _tempDir, "testjob", fileName, includeHeader, trailerFormat, writeMode, lineEnding);

    private Dictionary<string, object> MakeState(DataFrame? df = null) => new()
    {
        ["data"] = df ?? MakeTestFrame(),
        [DataSourcing.EtlEffectiveDateKey] = TestDate
    };

    /// <summary>
    /// Output path for the test partition: {tempDir}/testjob/{date}/{fileName}
    /// </summary>
    private string OutputPath(string fileName = "output.csv") =>
        Path.Combine(_tempDir, "testjob", TestDateStr, fileName);

    private string ReadOutput(string fileName = "output.csv") =>
        File.ReadAllText(OutputPath(fileName));

    private string[] ReadLines(string fileName = "output.csv") =>
        File.ReadAllLines(OutputPath(fileName));

    [Fact]
    public void Execute_WritesHeaderAndDataRows()
    {
        MakeWriter().Execute(MakeState());

        var lines = ReadLines();
        Assert.Equal(4, lines.Length); // header + 3 data rows
        Assert.Equal("Id,Name,City,etl_effective_date", lines[0]);
        Assert.Equal($"1,Alice,New York,{TestDateStr}", lines[1]);
    }

    [Fact]
    public void Execute_InjectsEtlEffectiveDateColumn()
    {
        MakeWriter().Execute(MakeState());

        var lines = ReadLines();
        // Every data row should end with the date
        Assert.All(lines.Skip(1), line => Assert.EndsWith(TestDateStr, line));
    }

    [Fact]
    public void Execute_NoHeader_SkipsHeaderRow()
    {
        MakeWriter(includeHeader: false).Execute(MakeState());

        var lines = ReadLines();
        Assert.Equal(3, lines.Length); // data rows only
        Assert.Equal($"1,Alice,New York,{TestDateStr}", lines[0]);
    }

    [Fact]
    public void Execute_FieldWithComma_IsQuoted()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Last, First", ["Value"] = (object?)1 }
        });
        MakeWriter().Execute(MakeState(df));

        var lines = ReadLines();
        Assert.Equal($"\"Last, First\",1,{TestDateStr}", lines[1]);
    }

    [Fact]
    public void Execute_FieldWithDoubleQuote_IsEscaped()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "She said \"hello\"", ["Value"] = (object?)1 }
        });
        MakeWriter().Execute(MakeState(df));

        var lines = ReadLines();
        Assert.Equal($"\"She said \"\"hello\"\"\",1,{TestDateStr}", lines[1]);
    }

    [Fact]
    public void Execute_NullValues_RenderAsEmpty()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["A"] = "x", ["B"] = null, ["C"] = "z" }
        });
        MakeWriter().Execute(MakeState(df));

        var lines = ReadLines();
        Assert.Equal($"x,,z,{TestDateStr}", lines[1]);
    }

    [Fact]
    public void Execute_TrailerFormat_WritesTrailerLine()
    {
        MakeWriter(trailerFormat: "TRAILER|{row_count}|{date}").Execute(MakeState());

        var lines = ReadLines();
        Assert.Equal(5, lines.Length); // header + 3 data + trailer
        Assert.Equal($"TRAILER|3|{TestDateStr}", lines[4]);
    }

    [Fact]
    public void Execute_TrailerFormat_TimestampToken_Resolves()
    {
        MakeWriter(trailerFormat: "END|{timestamp}").Execute(MakeState());

        var lines = ReadLines();
        var trailer = lines.Last();
        Assert.StartsWith("END|", trailer);
        Assert.Contains("T", trailer); // ISO 8601 timestamp
    }

    [Fact]
    public void Execute_OverwriteMode_WritesToDatePartition()
    {
        MakeWriter().Execute(MakeState());

        Assert.True(File.Exists(OutputPath()));
        // Verify the partition directory structure
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "testjob", TestDateStr)));
    }

    [Fact]
    public void Execute_OverwriteMode_RerunOverwritesPartition()
    {
        // First run
        MakeWriter().Execute(MakeState());
        // Second run — should overwrite the same partition
        var df = DataFrame.FromObjects(new[] { new { Id = 99, Name = "Replacement", City = "Nowhere" } });
        MakeWriter().Execute(MakeState(df));

        var lines = ReadLines();
        Assert.Equal(2, lines.Length); // header + 1 row (not 4)
        Assert.Contains("Replacement", lines[1]);
    }

    [Fact]
    public void Execute_AppendMode_FirstRunWritesNormally()
    {
        MakeWriter(writeMode: WriteMode.Append).Execute(MakeState());

        var lines = ReadLines();
        Assert.Equal(4, lines.Length); // header + 3 rows
    }

    [Fact]
    public void Execute_AppendMode_UnionsWithPriorPartition()
    {
        // Create a prior partition with 2 rows
        var priorDate = new DateOnly(2024, 11, 14);
        var priorDir = Path.Combine(_tempDir, "testjob", "2024-11-14");
        Directory.CreateDirectory(priorDir);
        File.WriteAllText(Path.Combine(priorDir, "output.csv"),
            "Id,Name,City,etl_effective_date\n1,Alice,New York,2024-11-14\n2,Bob,London,2024-11-14\n");

        // New data has 1 row
        var newDf = DataFrame.FromObjects(new[] { new { Id = 3, Name = "Charlie", City = "Paris" } });
        MakeWriter(writeMode: WriteMode.Append).Execute(MakeState(newDf));

        var lines = ReadLines();
        // header + 2 prior rows + 1 new row = 4 lines
        Assert.Equal(4, lines.Length);
        // All rows should have today's date (not the prior date)
        Assert.All(lines.Skip(1), line => Assert.EndsWith(TestDateStr, line));
    }

    [Fact]
    public void Execute_Utf8NoBom()
    {
        MakeWriter().Execute(MakeState());

        var bytes = File.ReadAllBytes(OutputPath());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "File should not have a UTF-8 BOM");
    }

    [Fact]
    public void Execute_LfLineEndings()
    {
        MakeWriter().Execute(MakeState());

        var content = File.ReadAllBytes(OutputPath());
        var text = Encoding.UTF8.GetString(content);
        Assert.DoesNotContain("\r\n", text);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void Execute_CrlfLineEndings()
    {
        MakeWriter(lineEnding: "\r\n").Execute(MakeState());

        var bytes = File.ReadAllBytes(OutputPath());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\r\n", text);
        var stripped = text.Replace("\r\n", "");
        Assert.DoesNotContain("\n", stripped);
    }

    [Fact]
    public void Execute_DefaultLineEndingIsLf()
    {
        MakeWriter().Execute(MakeState());

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(OutputPath()));
        Assert.DoesNotContain("\r\n", text);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void Execute_CreatesDirectoryStructure()
    {
        MakeWriter().Execute(MakeState());
        Assert.True(File.Exists(OutputPath()));
    }

    [Fact]
    public void Execute_MissingDataFrame_ThrowsKeyNotFoundException()
    {
        var writer = new CsvFileWriter("nonexistent", _tempDir, "testjob", "output.csv");
        var state = new Dictionary<string, object> { [DataSourcing.EtlEffectiveDateKey] = TestDate };

        Assert.Throws<KeyNotFoundException>(() => writer.Execute(state));
    }

    [Fact]
    public void Execute_MissingEffectiveDate_ThrowsInvalidOperationException()
    {
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        Assert.Throws<InvalidOperationException>(() => MakeWriter().Execute(state));
    }

    [Fact]
    public void Execute_ReturnsSharedStateUnchanged()
    {
        var state = MakeState();
        state["other"] = "keep me";

        var result = MakeWriter().Execute(state);

        Assert.Same(state, result);
        Assert.Equal("keep me", result["other"]);
    }
}

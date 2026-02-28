using Lib.DataFrames;
using Lib.Modules;
using Parquet;

namespace Lib.Tests;

public class ParquetFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ParquetFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MockEtlTests_Parquet_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static DataFrame MakeTestFrame() => DataFrame.FromObjects(new[]
    {
        new { Id = 1, Name = "Alice", Balance = 100.50, Active = true },
        new { Id = 2, Name = "Bob",   Balance = 200.75, Active = false },
        new { Id = 3, Name = "Charlie", Balance = 0.0,  Active = true }
    });

    [Fact]
    public void Execute_SinglePart_WritesSingleFile()
    {
        var outputDir = Path.Combine(_tempDir, "single");
        var writer = new ParquetFileWriter("data", outputDir, numParts: 1);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var files = Directory.GetFiles(outputDir, "*.parquet");
        Assert.Single(files);
        Assert.EndsWith("part-00000.parquet", files[0]);
    }

    [Fact]
    public void Execute_MultipleParts_SplitsRowsAcrossFiles()
    {
        var outputDir = Path.Combine(_tempDir, "multi");
        var writer = new ParquetFileWriter("data", outputDir, numParts: 2);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var files = Directory.GetFiles(outputDir, "*.parquet").OrderBy(f => f).ToArray();
        Assert.Equal(2, files.Length);
        Assert.EndsWith("part-00000.parquet", files[0]);
        Assert.EndsWith("part-00001.parquet", files[1]);
    }

    [Fact]
    public void Execute_MultipleParts_TotalRowCountPreserved()
    {
        var outputDir = Path.Combine(_tempDir, "rowcount");
        var writer = new ParquetFileWriter("data", outputDir, numParts: 2);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var totalRows = 0;
        foreach (var file in Directory.GetFiles(outputDir, "*.parquet"))
        {
            using var stream = File.OpenRead(file);
            using var reader = ParquetReader.CreateAsync(stream).Result;
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var groupReader = reader.OpenRowGroupReader(g);
                totalRows += (int)groupReader.RowCount;
            }
        }
        Assert.Equal(3, totalRows);
    }

    [Fact]
    public void Execute_OverwriteMode_DeletesExistingParquetFiles()
    {
        var outputDir = Path.Combine(_tempDir, "overwrite");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "old-part.parquet"), "stale");

        var writer = new ParquetFileWriter("data", outputDir, numParts: 1, writeMode: WriteMode.Overwrite);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var files = Directory.GetFiles(outputDir, "*.parquet");
        Assert.Single(files);
        Assert.EndsWith("part-00000.parquet", files[0]);
    }

    [Fact]
    public void Execute_CreatesDirectoryIfMissing()
    {
        var outputDir = Path.Combine(_tempDir, "nested", "deep", "dir");
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        Assert.True(Directory.Exists(outputDir));
        Assert.Single(Directory.GetFiles(outputDir, "*.parquet"));
    }

    [Fact]
    public void Execute_MissingDataFrame_ThrowsKeyNotFoundException()
    {
        var outputDir = Path.Combine(_tempDir, "missing");
        var writer = new ParquetFileWriter("nonexistent", outputDir);
        var state = new Dictionary<string, object>();

        Assert.Throws<KeyNotFoundException>(() => writer.Execute(state));
    }

    [Fact]
    public void Execute_ReturnsSharedStateUnchanged()
    {
        var outputDir = Path.Combine(_tempDir, "state");
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object>
        {
            ["data"] = MakeTestFrame(),
            ["other"] = "keep me"
        };

        var result = writer.Execute(state);

        Assert.Same(state, result);
        Assert.Equal("keep me", result["other"]);
    }

    [Fact]
    public void Execute_NullValues_WrittenSuccessfully()
    {
        var outputDir = Path.Combine(_tempDir, "nulls");
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Alice", ["Score"] = (object?)42 },
            new Dictionary<string, object?> { ["Name"] = null,    ["Score"] = null }
        });
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        Assert.Single(Directory.GetFiles(outputDir, "*.parquet"));
    }

    [Fact]
    public void Execute_ParquetSchemaMatchesDataFrameColumns()
    {
        var outputDir = Path.Combine(_tempDir, "schema");
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        writer.Execute(state);

        var file = Directory.GetFiles(outputDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var fieldNames = reader.Schema.DataFields.Select(f => f.Name).ToList();

        Assert.Equal(4, fieldNames.Count);
        Assert.Contains("Id", fieldNames);
        Assert.Contains("Name", fieldNames);
        Assert.Contains("Balance", fieldNames);
        Assert.Contains("Active", fieldNames);
    }

    [Fact]
    public void Execute_DateOnlyColumn_WritesNativeDateType()
    {
        var outputDir = Path.Combine(_tempDir, "dateonly");
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Alice", ["BirthDate"] = new DateOnly(1990, 5, 15) },
            new Dictionary<string, object?> { ["Name"] = "Bob",   ["BirthDate"] = new DateOnly(1985, 12, 1) }
        });
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        // Parquet.Net reads DATE back as DateTime on the CLR side, but the
        // underlying Parquet type is DATE (date32 in pyarrow). Verify the
        // field exists and is a date/time type rather than string.
        var file = Directory.GetFiles(outputDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var dateField = reader.Schema.DataFields.First(f => f.Name == "BirthDate");
        Assert.Equal(typeof(DateTime), dateField.ClrType);
    }

    [Fact]
    public void Execute_DateTimeColumn_WritesNativeDateTimeType()
    {
        var outputDir = Path.Combine(_tempDir, "datetime");
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Event"] = "Login", ["Timestamp"] = new DateTime(2024, 11, 15, 10, 30, 0) },
            new Dictionary<string, object?> { ["Event"] = "Logout", ["Timestamp"] = new DateTime(2024, 11, 15, 17, 0, 0) }
        });
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        var file = Directory.GetFiles(outputDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var tsField = reader.Schema.DataFields.First(f => f.Name == "Timestamp");
        Assert.Equal(typeof(DateTime), tsField.ClrType);
    }

    [Fact]
    public void Execute_DateOnlyColumn_NullsHandledCorrectly()
    {
        var outputDir = Path.Combine(_tempDir, "datenull");
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Alice", ["EndDate"] = new DateOnly(2025, 1, 1) },
            new Dictionary<string, object?> { ["Name"] = "Bob",   ["EndDate"] = null }
        });
        var writer = new ParquetFileWriter("data", outputDir);
        var state = new Dictionary<string, object> { ["data"] = df };

        writer.Execute(state);

        Assert.Single(Directory.GetFiles(outputDir, "*.parquet"));
    }
}

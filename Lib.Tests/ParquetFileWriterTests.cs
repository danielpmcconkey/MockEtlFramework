using Lib.DataFrames;
using Lib.Modules;
using Parquet;

namespace Lib.Tests;

public class ParquetFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly DateOnly TestDate = new(2024, 11, 15);
    private const string TestDateStr = "2024-11-15";

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

    private ParquetFileWriter MakeWriter(int numParts = 1, WriteMode writeMode = WriteMode.Overwrite) =>
        new("data", _tempDir, "testjob", "testtable", "output", numParts, writeMode);

    private Dictionary<string, object> MakeState(DataFrame? df = null) => new()
    {
        ["data"] = df ?? MakeTestFrame(),
        [DataSourcing.EtlEffectiveDateKey] = TestDate
    };

    /// <summary>
    /// Parquet output dir: {tempDir}/testjob/testtable/{date}/output/
    /// </summary>
    private string ParquetDir => Path.Combine(_tempDir, "testjob", "testtable", TestDateStr, "output");

    [Fact]
    public void Execute_SinglePart_WritesSingleFile()
    {
        MakeWriter().Execute(MakeState());

        var files = Directory.GetFiles(ParquetDir, "*.parquet");
        Assert.Single(files);
        Assert.EndsWith("part-00000.parquet", files[0]);
    }

    [Fact]
    public void Execute_MultipleParts_SplitsRowsAcrossFiles()
    {
        MakeWriter(numParts: 2).Execute(MakeState());

        var files = Directory.GetFiles(ParquetDir, "*.parquet").OrderBy(f => f).ToArray();
        Assert.Equal(2, files.Length);
        Assert.EndsWith("part-00000.parquet", files[0]);
        Assert.EndsWith("part-00001.parquet", files[1]);
    }

    [Fact]
    public void Execute_MultipleParts_TotalRowCountPreserved()
    {
        MakeWriter(numParts: 2).Execute(MakeState());

        var totalRows = 0;
        foreach (var file in Directory.GetFiles(ParquetDir, "*.parquet"))
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
    public void Execute_OverwriteMode_DeletesExistingParquetInPartition()
    {
        // Pre-create the partition dir with a stale file
        Directory.CreateDirectory(ParquetDir);
        File.WriteAllText(Path.Combine(ParquetDir, "old-part.parquet"), "stale");

        MakeWriter().Execute(MakeState());

        var files = Directory.GetFiles(ParquetDir, "*.parquet");
        Assert.Single(files);
        Assert.EndsWith("part-00000.parquet", files[0]);
    }

    [Fact]
    public void Execute_CreatesDirectoryStructure()
    {
        MakeWriter().Execute(MakeState());

        Assert.True(Directory.Exists(ParquetDir));
        Assert.Single(Directory.GetFiles(ParquetDir, "*.parquet"));
    }

    [Fact]
    public void Execute_MissingDataFrame_ThrowsKeyNotFoundException()
    {
        var writer = new ParquetFileWriter("nonexistent", _tempDir, "testjob", "testtable", "output");
        var state = new Dictionary<string, object> { [DataSourcing.EtlEffectiveDateKey] = TestDate };

        Assert.Throws<KeyNotFoundException>(() => writer.Execute(state));
    }

    [Fact]
    public void Execute_MissingEffectiveDate_ThrowsInvalidOperationException()
    {
        var writer = MakeWriter();
        var state = new Dictionary<string, object> { ["data"] = MakeTestFrame() };

        Assert.Throws<InvalidOperationException>(() => writer.Execute(state));
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

    [Fact]
    public void Execute_NullValues_WrittenSuccessfully()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Alice", ["Score"] = (object?)42 },
            new Dictionary<string, object?> { ["Name"] = null,    ["Score"] = null }
        });
        MakeWriter().Execute(MakeState(df));

        Assert.Single(Directory.GetFiles(ParquetDir, "*.parquet"));
    }

    [Fact]
    public void Execute_InjectsEtlEffectiveDateColumn()
    {
        MakeWriter().Execute(MakeState());

        var file = Directory.GetFiles(ParquetDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var fieldNames = reader.Schema.DataFields.Select(f => f.Name).ToList();

        Assert.Contains("etl_effective_date", fieldNames);
    }

    [Fact]
    public void Execute_ParquetSchemaMatchesDataFrameColumnsPlus_EtlDate()
    {
        MakeWriter().Execute(MakeState());

        var file = Directory.GetFiles(ParquetDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var fieldNames = reader.Schema.DataFields.Select(f => f.Name).ToList();

        // Original 4 columns + etl_effective_date
        Assert.Equal(5, fieldNames.Count);
        Assert.Contains("Id", fieldNames);
        Assert.Contains("Name", fieldNames);
        Assert.Contains("Balance", fieldNames);
        Assert.Contains("Active", fieldNames);
        Assert.Contains("etl_effective_date", fieldNames);
    }

    [Fact]
    public void Execute_DateOnlyColumn_WritesNativeDateType()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Alice", ["BirthDate"] = new DateOnly(1990, 5, 15) },
            new Dictionary<string, object?> { ["Name"] = "Bob",   ["BirthDate"] = new DateOnly(1985, 12, 1) }
        });
        MakeWriter().Execute(MakeState(df));

        var file = Directory.GetFiles(ParquetDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var dateField = reader.Schema.DataFields.First(f => f.Name == "BirthDate");
        Assert.Equal(typeof(DateTime), dateField.ClrType);
    }

    [Fact]
    public void Execute_DateTimeColumn_WritesNativeDateTimeType()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Event"] = "Login", ["Timestamp"] = new DateTime(2024, 11, 15, 10, 30, 0) },
            new Dictionary<string, object?> { ["Event"] = "Logout", ["Timestamp"] = new DateTime(2024, 11, 15, 17, 0, 0) }
        });
        MakeWriter().Execute(MakeState(df));

        var file = Directory.GetFiles(ParquetDir, "*.parquet").Single();
        using var stream = File.OpenRead(file);
        using var reader = ParquetReader.CreateAsync(stream).Result;
        var tsField = reader.Schema.DataFields.First(f => f.Name == "Timestamp");
        Assert.Equal(typeof(DateTime), tsField.ClrType);
    }

    [Fact]
    public void Execute_DateOnlyColumn_NullsHandledCorrectly()
    {
        var df = new DataFrame(new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Alice", ["EndDate"] = new DateOnly(2025, 1, 1) },
            new Dictionary<string, object?> { ["Name"] = "Bob",   ["EndDate"] = null }
        });
        MakeWriter().Execute(MakeState(df));

        Assert.Single(Directory.GetFiles(ParquetDir, "*.parquet"));
    }

    [Fact]
    public void Execute_AppendMode_FirstRunWritesNormally()
    {
        MakeWriter(writeMode: WriteMode.Append).Execute(MakeState());

        Assert.True(Directory.Exists(ParquetDir));
        Assert.Single(Directory.GetFiles(ParquetDir, "*.parquet"));
    }

    [Fact]
    public void Execute_AppendMode_UnionsWithPriorPartition()
    {
        // First: write a prior partition with 2 rows
        var priorDate = new DateOnly(2024, 11, 14);
        var priorDf = DataFrame.FromObjects(new[]
        {
            new { Id = 1, Name = "Alice", Balance = 100.50, Active = true },
            new { Id = 2, Name = "Bob",   Balance = 200.75, Active = false }
        });
        // Inject etl_effective_date so the prior parquet has it
        priorDf = priorDf.WithColumn("etl_effective_date", _ => "2024-11-14");

        var priorParquetDir = Path.Combine(_tempDir, "testjob", "testtable", "2024-11-14", "output");
        Directory.CreateDirectory(priorParquetDir);

        // Write prior parquet manually using the writer (slightly roundabout but ensures correct format)
        var priorWriter = new ParquetFileWriter("data", _tempDir, "testjob", "testtable", "output",
            numParts: 1, writeMode: WriteMode.Overwrite);
        var priorState = new Dictionary<string, object>
        {
            ["data"] = DataFrame.FromObjects(new[]
            {
                new { Id = 1, Name = "Alice", Balance = 100.50, Active = true },
                new { Id = 2, Name = "Bob",   Balance = 200.75, Active = false }
            }),
            [DataSourcing.EtlEffectiveDateKey] = priorDate
        };
        priorWriter.Execute(priorState);

        // Now write today's append with 1 new row
        var newDf = DataFrame.FromObjects(new[]
        {
            new { Id = 3, Name = "Charlie", Balance = 0.0, Active = true }
        });
        MakeWriter(writeMode: WriteMode.Append).Execute(MakeState(newDf));

        // Read today's partition — should have 3 total rows (2 prior + 1 new)
        var totalRows = 0;
        foreach (var file in Directory.GetFiles(ParquetDir, "*.parquet"))
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
}

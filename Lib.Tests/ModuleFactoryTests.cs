using System.Text.Json;
using Lib.Modules;

namespace Lib.Tests;

public class ModuleFactoryTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Create_DataSourcing_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""DataSourcing"",
            ""resultName"": ""customers"",
            ""schema"": ""datalake"",
            ""table"": ""customers"",
            ""columns"": [""id"", ""first_name""],
            ""minEffectiveDate"": ""2024-01-01"",
            ""maxEffectiveDate"": ""2024-01-31""
        }");
        Assert.IsType<DataSourcing>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataSourcing_WithOptionalFilter_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""DataSourcing"",
            ""resultName"": ""customers"",
            ""schema"": ""datalake"",
            ""table"": ""customers"",
            ""columns"": [""id""],
            ""minEffectiveDate"": ""2024-01-01"",
            ""maxEffectiveDate"": ""2024-01-31"",
            ""additionalFilter"": ""id > 100""
        }");
        Assert.IsType<DataSourcing>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_Transformation_ReturnsCorrectType()
    {
        var el = Parse(@"{""type"": ""Transformation"", ""resultName"": ""result"", ""sql"": ""SELECT 1""}");
        Assert.IsType<Transformation>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataFrameWriter_Overwrite_ReturnsCorrectType()
    {
        var el = Parse(@"{""type"": ""DataFrameWriter"", ""source"": ""result"", ""targetTable"": ""output"", ""writeMode"": ""Overwrite""}");
        Assert.IsType<DataFrameWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataFrameWriter_Append_ReturnsCorrectType()
    {
        var el = Parse(@"{""type"": ""DataFrameWriter"", ""source"": ""result"", ""targetTable"": ""output"", ""writeMode"": ""Append""}");
        Assert.IsType<DataFrameWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataFrameWriter_WithTargetSchema_ReturnsCorrectType()
    {
        var el = Parse(@"{""type"": ""DataFrameWriter"", ""source"": ""result"", ""targetTable"": ""output"", ""writeMode"": ""Overwrite"", ""targetSchema"": ""double_secret_curated""}");
        Assert.IsType<DataFrameWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_External_ReturnsCorrectType()
    {
        var el = Parse(@"{""type"": ""External"", ""assemblyPath"": ""/some/path.dll"", ""typeName"": ""MyNamespace.MyClass""}");
        Assert.IsType<External>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_ParquetFileWriter_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""ParquetFileWriter"",
            ""source"": ""output"",
            ""outputDirectory"": ""Output/poc4"",
            ""jobDirName"": ""test_job"",
            ""fileName"": ""test_output"",
            ""numParts"": 2,
            ""writeMode"": ""Overwrite""
        }");
        Assert.IsType<ParquetFileWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_ParquetFileWriter_DefaultNumParts_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""ParquetFileWriter"",
            ""source"": ""output"",
            ""outputDirectory"": ""Output/poc4"",
            ""jobDirName"": ""test_job"",
            ""fileName"": ""test_output"",
            ""writeMode"": ""Overwrite""
        }");
        Assert.IsType<ParquetFileWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_CsvFileWriter_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""CsvFileWriter"",
            ""source"": ""output"",
            ""outputDirectory"": ""Output/poc4"",
            ""jobDirName"": ""test_job"",
            ""fileName"": ""test.csv"",
            ""writeMode"": ""Overwrite""
        }");
        Assert.IsType<CsvFileWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_CsvFileWriter_WithTrailer_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""CsvFileWriter"",
            ""source"": ""output"",
            ""outputDirectory"": ""Output/poc4"",
            ""jobDirName"": ""test_job"",
            ""fileName"": ""test.csv"",
            ""trailerFormat"": ""TRAILER|{row_count}"",
            ""writeMode"": ""Overwrite""
        }");
        Assert.IsType<CsvFileWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_CsvFileWriter_WithCrlfLineEnding_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""CsvFileWriter"",
            ""source"": ""output"",
            ""outputDirectory"": ""Output/poc4"",
            ""jobDirName"": ""test_job"",
            ""fileName"": ""test.csv"",
            ""writeMode"": ""Overwrite"",
            ""lineEnding"": ""CRLF""
        }");
        Assert.IsType<CsvFileWriter>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataSourcing_WithLookbackDays_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""DataSourcing"",
            ""resultName"": ""customers"",
            ""schema"": ""datalake"",
            ""table"": ""customers"",
            ""columns"": [""id"", ""first_name""],
            ""lookbackDays"": 3
        }");
        Assert.IsType<DataSourcing>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataSourcing_WithMostRecentPrior_ReturnsCorrectType()
    {
        var el = Parse(@"{
            ""type"": ""DataSourcing"",
            ""resultName"": ""customers"",
            ""schema"": ""datalake"",
            ""table"": ""customers"",
            ""columns"": [""id"", ""first_name""],
            ""mostRecentPrior"": true
        }");
        Assert.IsType<DataSourcing>(ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataSourcing_LookbackAndMostRecentPrior_Throws()
    {
        var el = Parse(@"{
            ""type"": ""DataSourcing"",
            ""resultName"": ""customers"",
            ""schema"": ""datalake"",
            ""table"": ""customers"",
            ""columns"": [""id"", ""first_name""],
            ""lookbackDays"": 3,
            ""mostRecentPrior"": true
        }");
        Assert.Throws<ArgumentException>(() => ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_DataSourcing_LookbackAndStaticDates_Throws()
    {
        var el = Parse(@"{
            ""type"": ""DataSourcing"",
            ""resultName"": ""customers"",
            ""schema"": ""datalake"",
            ""table"": ""customers"",
            ""columns"": [""id"", ""first_name""],
            ""lookbackDays"": 3,
            ""minEffectiveDate"": ""2024-01-01""
        }");
        Assert.Throws<ArgumentException>(() => ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_UnknownType_ThrowsInvalidOperationException()
    {
        var el = Parse(@"{""type"": ""UnknownModule""}");
        Assert.Throws<InvalidOperationException>(() => ModuleFactory.Create(el));
    }

    [Fact]
    public void Create_MissingTypeField_ThrowsKeyNotFoundException()
    {
        var el = Parse(@"{""resultName"": ""foo""}");
        Assert.Throws<KeyNotFoundException>(() => ModuleFactory.Create(el));
    }
}

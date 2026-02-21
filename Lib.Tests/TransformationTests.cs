using Lib.DataFrames;
using Lib.Modules;

namespace Lib.Tests;

public class TransformationTests
{
    private static DataFrame MakePeopleFrame() => DataFrame.FromObjects(new[]
    {
        new { Name = "Alice",   Age = 25, City = "New York" },
        new { Name = "Bob",     Age = 30, City = "London"   },
        new { Name = "Charlie", Age = 35, City = "New York" }
    });

    [Fact]
    public void Execute_BasicSelect_ReturnsAllRows()
    {
        var state = new Dictionary<string, object> { ["people"] = MakePeopleFrame() };
        var result = new Transformation("out", "SELECT * FROM people").Execute(state);
        Assert.Equal(3, ((DataFrame)result["out"]).Count);
    }

    [Fact]
    public void Execute_WhereClause_FiltersRows()
    {
        var state = new Dictionary<string, object> { ["people"] = MakePeopleFrame() };
        var result = new Transformation("out", "SELECT * FROM people WHERE Age > 25").Execute(state);
        Assert.Equal(2, ((DataFrame)result["out"]).Count);
    }

    [Fact]
    public void Execute_SelectColumns_ReturnsOnlySpecifiedColumns()
    {
        var state = new Dictionary<string, object> { ["people"] = MakePeopleFrame() };
        var result = new Transformation("out", "SELECT Name, City FROM people").Execute(state);
        var df = (DataFrame)result["out"];
        Assert.Equal(2, df.Columns.Count);
        Assert.Contains("Name", df.Columns);
        Assert.Contains("City", df.Columns);
    }

    [Fact]
    public void Execute_Join_CombinesTwoFrames()
    {
        var customers = DataFrame.FromObjects(new[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" }
        });
        var orders = DataFrame.FromObjects(new[]
        {
            new { CustomerId = 1, Amount = 100 },
            new { CustomerId = 1, Amount = 200 }
        });
        var state = new Dictionary<string, object> { ["customers"] = customers, ["orders"] = orders };
        var result = new Transformation("out",
            "SELECT c.Name, o.Amount FROM customers c JOIN orders o ON c.Id = o.CustomerId")
            .Execute(state);
        Assert.Equal(2, ((DataFrame)result["out"]).Count);
    }

    [Fact]
    public void Execute_GroupBy_AggregatesCorrectly()
    {
        var state = new Dictionary<string, object> { ["people"] = MakePeopleFrame() };
        var result = new Transformation("out",
            "SELECT City, COUNT(*) as cnt FROM people GROUP BY City")
            .Execute(state);
        Assert.Equal(2, ((DataFrame)result["out"]).Count);
    }

    [Fact]
    public void Execute_PreservesExistingSharedState()
    {
        var state = new Dictionary<string, object> { ["people"] = MakePeopleFrame() };
        var result = new Transformation("out", "SELECT * FROM people").Execute(state);
        Assert.True(result.ContainsKey("people"));
        Assert.True(result.ContainsKey("out"));
    }

    [Fact]
    public void Execute_OnlyDataFramesRegisteredAsTables()
    {
        // Non-DataFrame entries in shared state should be silently ignored, not cause an error
        var state = new Dictionary<string, object>
        {
            ["people"] = MakePeopleFrame(),
            ["someString"] = "not a dataframe"
        };
        var result = new Transformation("out", "SELECT * FROM people").Execute(state);
        Assert.Equal(3, ((DataFrame)result["out"]).Count);
    }
}

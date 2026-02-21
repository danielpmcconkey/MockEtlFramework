using Lib.DataFrames;

namespace Lib.Tests;

public class DataFrameTests
{
    private static DataFrame MakePeopleFrame() => DataFrame.FromObjects(new[]
    {
        new { Name = "Alice", Age = 25, City = "New York" },
        new { Name = "Bob",   Age = 30, City = "London"   },
        new { Name = "Charlie", Age = 35, City = "New York" }
    });

    [Fact]
    public void Count_ReturnsCorrectRowCount()
    {
        var df = MakePeopleFrame();
        Assert.Equal(3, df.Count);
    }

    [Fact]
    public void Columns_ReturnsCorrectSchema()
    {
        var df = MakePeopleFrame();
        Assert.Equal(new[] { "Name", "Age", "City" }, df.Columns);
    }

    [Fact]
    public void Select_ReturnsOnlySpecifiedColumns()
    {
        var df = MakePeopleFrame().Select("Name", "City");
        Assert.Equal(new[] { "Name", "City" }, df.Columns);
        Assert.Equal(3, df.Count);
    }

    [Fact]
    public void Filter_ReturnsOnlyMatchingRows()
    {
        var df = MakePeopleFrame().Filter(row => (int)row["Age"]! > 25);
        Assert.Equal(2, df.Count);
        Assert.All(df.Rows, row => Assert.True((int)row["Age"]! > 25));
    }

    [Fact]
    public void Filter_ReturnsEmptyWhenNothingMatches()
    {
        var df = MakePeopleFrame().Filter(row => (int)row["Age"]! > 100);
        Assert.Equal(0, df.Count);
    }

    [Fact]
    public void WithColumn_AddsNewColumn()
    {
        var df = MakePeopleFrame().WithColumn("AgeDoubled", row => (int)row["Age"]! * 2);
        Assert.Contains("AgeDoubled", df.Columns);
        Assert.Equal(4, df.Columns.Count);
        Assert.Equal(50, (int)df.Rows[0]["AgeDoubled"]!);
    }

    [Fact]
    public void WithColumn_OverwritesExistingColumn()
    {
        var df = MakePeopleFrame().WithColumn("Age", row => (int)row["Age"]! + 1);
        Assert.Equal(3, df.Columns.Count); // no extra column added
        Assert.Equal(26, (int)df.Rows[0]["Age"]!);
    }

    [Fact]
    public void Drop_RemovesColumn()
    {
        var df = MakePeopleFrame().Drop("City");
        Assert.DoesNotContain("City", df.Columns);
        Assert.Equal(2, df.Columns.Count);
        Assert.Equal(3, df.Count);
    }

    [Fact]
    public void OrderBy_SortsAscending()
    {
        var df = MakePeopleFrame().OrderBy("Age");
        var ages = df.Rows.Select(r => (int)r["Age"]!).ToList();
        Assert.Equal(new[] { 25, 30, 35 }, ages);
    }

    [Fact]
    public void OrderBy_SortsDescending()
    {
        var df = MakePeopleFrame().OrderBy("Age", ascending: false);
        var ages = df.Rows.Select(r => (int)r["Age"]!).ToList();
        Assert.Equal(new[] { 35, 30, 25 }, ages);
    }

    [Fact]
    public void Limit_ReturnsFirstNRows()
    {
        var df = MakePeopleFrame().Limit(2);
        Assert.Equal(2, df.Count);
        Assert.Equal("Alice", (string)df.Rows[0]["Name"]!);
    }

    [Fact]
    public void Union_CombinesTwoDataFrames()
    {
        var combined = MakePeopleFrame().Union(MakePeopleFrame());
        Assert.Equal(6, combined.Count);
    }

    [Fact]
    public void Union_ThrowsOnMismatchedColumns()
    {
        var df1 = MakePeopleFrame();
        var df2 = MakePeopleFrame().Select("Name", "Age");
        Assert.Throws<ArgumentException>(() => df1.Union(df2));
    }

    [Fact]
    public void Distinct_RemovesDuplicateRows()
    {
        var df = MakePeopleFrame().Union(MakePeopleFrame()).Distinct();
        Assert.Equal(3, df.Count);
    }

    [Fact]
    public void Join_InnerJoin_ReturnsOnlyMatchingRows()
    {
        var left = DataFrame.FromObjects(new[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" }
        });
        var right = DataFrame.FromObjects(new[]
        {
            new { Id = 1, Score = 95 },
            new { Id = 3, Score = 80 }
        });

        var joined = left.Join(right, "Id");
        Assert.Equal(1, joined.Count);
        Assert.Equal("Alice", (string)joined.Rows[0]["Name"]!);
        Assert.Equal(95, (int)joined.Rows[0]["Score"]!);
    }

    [Fact]
    public void Join_LeftJoin_IncludesUnmatchedLeftRows()
    {
        var left = DataFrame.FromObjects(new[]
        {
            new { Id = 1, Name = "Alice" },
            new { Id = 2, Name = "Bob" }
        });
        var right = DataFrame.FromObjects(new[]
        {
            new { Id = 1, Score = 95 }
        });

        var joined = left.Join(right, "Id", "left");
        Assert.Equal(2, joined.Count);
        Assert.Null(joined.Rows.First(r => r["Name"]!.ToString() == "Bob")["Score"]);
    }

    [Fact]
    public void GroupBy_Count_ReturnsOneRowPerGroup()
    {
        var df = MakePeopleFrame().GroupBy("City").Count();
        Assert.Equal(2, df.Count); // New York and London
    }

    [Fact]
    public void GroupBy_Count_ReturnsCorrectCounts()
    {
        var df = MakePeopleFrame().GroupBy("City").Count();
        var nyRow = df.Rows.First(r => r["City"]!.ToString() == "New York");
        Assert.Equal(2, (int)nyRow["count"]!);
    }
}

using Lib.DataFrames;

namespace MockEtlFramework;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Hello, World!");
        
        // Create from objects
        var people = new[]
        {
            new { Name = "Alice", Age = 25, City = "New York" },
            new { Name = "Bob", Age = 30, City = "London" },
            new { Name = "Charlie", Age = 35, City = "New York" }
        };
        var df = DataFrame.FromObjects(people);

// Show data
        df.Show();

// Filter and select
        var filtered = df.Filter(row => (int)row["Age"] > 25)
            .Select("Name", "City");

// Group by and aggregate
        var grouped = df.GroupBy("City").Count();

// Add new column
        var withSalary = df.WithColumn("Salary", row => (int)row["Age"] * 1000);

    }
}
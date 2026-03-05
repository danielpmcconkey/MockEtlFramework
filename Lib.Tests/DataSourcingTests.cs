using Lib.Modules;

namespace Lib.Tests;

public class DataSourcingTests
{
    private static DataSourcing MakeModule(
        DateOnly? minDate = null, DateOnly? maxDate = null,
        int? lookbackDays = null, bool mostRecentPrior = false,
        bool mostRecent = false) => new(
        "test_result", "datalake", "test_table", new[] { "id", "name" },
        minDate, maxDate, "", lookbackDays, mostRecentPrior, mostRecent);

    // --- Validation: mutually exclusive modes ---

    [Fact]
    public void Constructor_LookbackAndStaticDates_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeModule(minDate: new DateOnly(2024, 1, 1), lookbackDays: 3));
    }

    [Fact]
    public void Constructor_MostRecentPriorAndStaticDates_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeModule(maxDate: new DateOnly(2024, 1, 31), mostRecentPrior: true));
    }

    [Fact]
    public void Constructor_LookbackAndMostRecentPrior_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeModule(lookbackDays: 3, mostRecentPrior: true));
    }

    [Fact]
    public void Constructor_NegativeLookbackDays_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MakeModule(lookbackDays: -1));
    }

    [Fact]
    public void Constructor_LookbackOnly_DoesNotThrow()
    {
        var module = MakeModule(lookbackDays: 5);
        Assert.NotNull(module);
    }

    [Fact]
    public void Constructor_MostRecentPriorOnly_DoesNotThrow()
    {
        var module = MakeModule(mostRecentPrior: true);
        Assert.NotNull(module);
    }

    // --- ResolveDateRange: lookback mode ---

    [Fact]
    public void ResolveDateRange_Lookback_ReturnsCorrectRange()
    {
        var module = MakeModule(lookbackDays: 3);
        var state = new Dictionary<string, object>
        {
            [DataSourcing.EtlEffectiveDateKey] = new DateOnly(2024, 10, 15)
        };

        var (min, max) = module.ResolveDateRange(state)!.Value;

        Assert.Equal(new DateOnly(2024, 10, 12), min);
        Assert.Equal(new DateOnly(2024, 10, 15), max);
    }

    [Fact]
    public void ResolveDateRange_LookbackZero_MinEqualsMax()
    {
        var module = MakeModule(lookbackDays: 0);
        var state = new Dictionary<string, object>
        {
            [DataSourcing.EtlEffectiveDateKey] = new DateOnly(2024, 10, 15)
        };

        var (min, max) = module.ResolveDateRange(state)!.Value;

        Assert.Equal(new DateOnly(2024, 10, 15), min);
        Assert.Equal(new DateOnly(2024, 10, 15), max);
    }

    // --- ResolveDateRange: default / fallback ---

    [Fact]
    public void ResolveDateRange_NoModes_FallsBackToEtlEffectiveDate()
    {
        var module = MakeModule();
        var state = new Dictionary<string, object>
        {
            [DataSourcing.EtlEffectiveDateKey] = new DateOnly(2024, 10, 15)
        };

        var (min, max) = module.ResolveDateRange(state)!.Value;

        Assert.Equal(new DateOnly(2024, 10, 15), min);
        Assert.Equal(new DateOnly(2024, 10, 15), max);
    }

    [Fact]
    public void ResolveDateRange_StaticDates_UsesStaticDates()
    {
        var module = MakeModule(
            minDate: new DateOnly(2024, 1, 1),
            maxDate: new DateOnly(2024, 1, 31));
        var state = new Dictionary<string, object>
        {
            [DataSourcing.EtlEffectiveDateKey] = new DateOnly(2024, 10, 15)
        };

        var (min, max) = module.ResolveDateRange(state)!.Value;

        Assert.Equal(new DateOnly(2024, 1, 1), min);
        Assert.Equal(new DateOnly(2024, 1, 31), max);
    }

    [Fact]
    public void ResolveDateRange_MissingEtlDate_Throws()
    {
        var module = MakeModule();
        var state = new Dictionary<string, object>();

        Assert.Throws<InvalidOperationException>(() => module.ResolveDateRange(state));
    }

    [Fact]
    public void ResolveDateRange_Lookback_MissingEtlDate_Throws()
    {
        var module = MakeModule(lookbackDays: 3);
        var state = new Dictionary<string, object>();

        Assert.Throws<InvalidOperationException>(() => module.ResolveDateRange(state));
    }

    [Fact]
    public void ResolveDateRange_MostRecentPrior_MissingEtlDate_Throws()
    {
        // mostRecentPrior also needs __etlEffectiveDate to know what "prior" means
        var module = MakeModule(mostRecentPrior: true);
        var state = new Dictionary<string, object>();

        Assert.Throws<InvalidOperationException>(() => module.ResolveDateRange(state));
    }

    // --- mostRecent mode: constructor validation ---

    [Fact]
    public void Constructor_MostRecentOnly_DoesNotThrow()
    {
        var module = MakeModule(mostRecent: true);
        Assert.NotNull(module);
    }

    [Fact]
    public void Constructor_MostRecentAndStaticDates_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeModule(minDate: new DateOnly(2024, 1, 1), mostRecent: true));
    }

    [Fact]
    public void Constructor_MostRecentAndLookback_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeModule(lookbackDays: 3, mostRecent: true));
    }

    [Fact]
    public void Constructor_MostRecentAndMostRecentPrior_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeModule(mostRecentPrior: true, mostRecent: true));
    }

    [Fact]
    public void ResolveDateRange_MostRecent_MissingEtlDate_Throws()
    {
        var module = MakeModule(mostRecent: true);
        var state = new Dictionary<string, object>();

        Assert.Throws<InvalidOperationException>(() => module.ResolveDateRange(state));
    }
}

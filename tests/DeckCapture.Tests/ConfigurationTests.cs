using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "deckcapture-config-tests-" + Guid.NewGuid().ToString("N"));
    private AppConfiguration Defaults() => ConfigurationRules.DevelopmentDefaults(temp);

    [Fact]
    public void DevelopmentDefaultsAreValid() => Assert.Empty(ConfigurationRules.Validate(Defaults()));

    [Fact]
    public void HiddenRequiredMetadataIsRejected()
    {
        var config = Defaults();
        config.MetadataFields["tapeId"] = new(false, true);
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicatePhysicalInputIsRejectedIgnoringCase()
    {
        var config = Defaults() with { Channels = [new() { Id = "a", SourceKind = "DeckLink", DeviceName = "DeckLink Duo (1)" },
            new() { Id = "b", SourceKind = "DeckLink", DeviceName = "decklink duo (1)" }] };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("only one channel", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 2, 40)]
    [InlineData(20, 0, 40)]
    [InlineData(20, -1, 40)]
    [InlineData(20, 2, 0)]
    [InlineData(double.NaN, 2, 40)]
    [InlineData(20, double.PositiveInfinity, 40)]
    [InlineData(20, 2, double.NegativeInfinity)]
    public void AllTransferSpeedsMustBeFiniteAndPositive(double inside, double outside, double aggregate)
    {
        var config = Defaults() with { Transfer = new() { PerTransferMiBps = inside, OutsideWindowMiBps = outside, AggregateMiBps = aggregate } };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("finite positive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("22:00", "22:00")]
    [InlineData("24:00", "06:00")]
    [InlineData("22:00:01", "06:00")]
    [InlineData("22:00", "6:00")]
    public void InvalidOrUnboundedWindowsAreRejected(string start, string end)
    {
        var config = Defaults() with { Transfer = new() { Windows = [new() { StartLocal = start, EndLocal = end }] } };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("Transfer windows", StringComparison.Ordinal));
    }

    [Fact]
    public void UndefinedOrEmptyWindowDaysAreRejected()
    {
        foreach (var days in new DayOfWeek[][] { [], [(DayOfWeek)20] })
            Assert.Contains(ConfigurationRules.Validate(Defaults() with { Transfer = new() { Windows = [new() { StartDays = days }] } }),
                error => error.Contains("Transfer windows", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CacheAndDestinationOverlapIsRejectedInEitherDirection(bool cacheIsParent)
    {
        var config = Defaults() with { CacheRoot = cacheIsParent ? temp : Path.Combine(temp, "nested"),
            Destinations = [new("test", "Test", cacheIsParent ? Path.Combine(temp, "nested") : temp)] };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void SimilarPrefixSiblingsDoNotOverlap()
    {
        Assert.False(PathSafety.Overlaps(Path.Combine(temp, "archive"), Path.Combine(temp, "archive-old")));
    }

    [Fact]
    public void MissingCacheRootReturnsValidationErrors()
    {
        Assert.NotEmpty(ConfigurationRules.Validate(Defaults() with { CacheRoot = null! }));
    }

    [Theory]
    [InlineData("channels")]
    [InlineData("destinations")]
    [InlineData("windows")]
    [InlineData("recipients")]
    [InlineData("metadata")]
    [InlineData("destinationRoot")]
    public void NullEntriesReturnValidationErrorsWithoutThrowing(string entry)
    {
        var config = Defaults();
        config = entry switch
        {
            "channels" => config with { Channels = [null!] },
            "destinations" => config with { Destinations = [null!] },
            "windows" => config with { Transfer = new() { Windows = [null!] } },
            "recipients" => config with { Notifications = new() { Recipients = [null!] } },
            "metadata" => config with { MetadataFields = new() { ["title"] = null! } },
            "destinationRoot" => config with { Destinations = [new("null", "Null root", null!)] },
            _ => throw new ArgumentException(entry)
        };
        Assert.NotEmpty(ConfigurationRules.Validate(config));
    }

    [Theory]
    [InlineData(double.NaN, -50)]
    [InlineData(double.PositiveInfinity, -50)]
    [InlineData(double.NegativeInfinity, -50)]
    [InlineData(.99, double.NaN)]
    [InlineData(.99, double.PositiveInfinity)]
    [InlineData(.99, double.NegativeInfinity)]
    public void DetectionThresholdsMustBeFinite(double blackRatio, double silence)
    {
        var config = Defaults() with { Detection = new() { BlackRatio = blackRatio, SilenceDbfs = silence } };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("detection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedAbsoluteRootsReturnValidationErrorsWithoutThrowing()
    {
        Assert.NotEmpty(ConfigurationRules.Validate(Defaults() with { CacheRoot = Path.Combine(temp, "bad\0root") }));
        Assert.NotEmpty(ConfigurationRules.Validate(Defaults() with { Destinations = [new("bad", "Bad", Path.Combine(temp, "bad\0root"))] }));
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("1:00")]
    [InlineData("12:00:01")]
    [InlineData("")]
    public void DailyDigestRequiresExactLocalMinute(string daily)
    {
        Assert.Contains(ConfigurationRules.Validate(Defaults() with { Notifications = new() { DailyDigestLocalTime = daily } }),
            error => error.Contains("Daily digest", StringComparison.Ordinal));
    }

    [Fact]
    public void DailyDigestMayBeOmittedOrUseExactHourAndMinute()
    {
        Assert.Empty(ConfigurationRules.Validate(Defaults() with { Notifications = new() { DailyDigestLocalTime = null } }));
        Assert.Empty(ConfigurationRules.Validate(Defaults() with { Notifications = new() { DailyDigestLocalTime = "23:59" } }));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1_000_000_001L)]
    public void ReservationRateHasFiniteOperationalUpperBound(long rate)
    {
        Assert.Contains(ConfigurationRules.Validate(Defaults() with { Capture = new() { ReservationBytesPerSecond = rate } }),
            error => error.Contains("capture profile", StringComparison.Ordinal));
    }

    [Fact]
    public void MaximumReservationRateCanReserveTwelveHoursWithoutOverflow()
    {
        const long rate = 1_000_000_000;
        Assert.Empty(ConfigurationRules.Validate(Defaults() with { Capture = new() { ReservationBytesPerSecond = rate } }));
        Assert.True(checked(rate * (43200 + 30 + 3L) * 2) > 0);
    }

    [Fact]
    public void NetworkShareCannotBeTheCaptureCache()
    {
        Assert.Contains(ConfigurationRules.Validate(Defaults() with { CacheRoot = @"\\archive-server\capture-cache" }),
            error => error.Contains("cache must be local", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9.log")]
    [InlineData("COM\u00b9.txt")]
    [InlineData("CONIN$")]
    [InlineData("master:alternate")]
    [InlineData("../escape")]
    [InlineData("folder\\escape")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(".")]
    [InlineData("..")]
    public void UnsafeWindowsFileNamesAreRejected(string name)
    {
        Assert.Throws<CommandException>(() => PathSafety.ValidateFileName(name));
    }

    [Theory]
    [InlineData("Station 12 Tape 3")]
    [InlineData("safe-name_2026")]
    [InlineData("A.B")]
    public void OrdinaryFileNamesAreAccepted(string name) => PathSafety.ValidateFileName(name);

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/../../escape")]
    [InlineData("folder:alternate")]
    [InlineData("NUL")]
    [InlineData("folder/trailing.")]
    public void UnsafeDestinationFoldersAreRejected(string relative)
    {
        Assert.Throws<CommandException>(() => PathSafety.UnderRoot(temp, relative));
    }

    [Fact]
    public void FileSystemRootRemainsAbsoluteWhenCombining()
    {
        var root = Path.GetPathRoot(temp)!;
        Assert.Equal(Path.Combine(root, "ordinary-folder"), PathSafety.UnderRoot(root, "ordinary-folder"));
    }

    [Fact]
    public void RootAndNestedDestinationLinksAreRejected()
    {
        Directory.CreateDirectory(temp);
        var actual = Path.Combine(temp, "actual");
        Directory.CreateDirectory(Path.Combine(actual, "nested"));
        var link = Path.Combine(temp, "link");
        Directory.CreateSymbolicLink(link, actual);
        Assert.Throws<CommandException>(() => PathSafety.UnderRoot(link, ""));
        Assert.Throws<CommandException>(() => PathSafety.UnderRoot(temp, "link/nested"));
        // A configured root inside a link must check ancestors too.
        Assert.Throws<CommandException>(() => PathSafety.UnderRoot(Path.Combine(link, "nested"), ""));
    }

    public void Dispose()
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }
}

namespace RfScout.Tests;

/// <summary>
/// The view models touch WPF types (Dispatcher, DrawingContext-shaped value types) and the plot
/// controls are WPF, so the test host has to be able to load the Windows Desktop assemblies
/// exactly once. A test project that references a WinExe project but not the WPF framework
/// reference resolves PresentationCore from its own TPA list as well, PresentationCore loads
/// twice in one process, and the first WPF type load throws
/// "DefaultDispatcher was defaulted twice" (dotnet/wpf#5359). That failure looks like a broken
/// view model and is really a csproj setting, so this test pins the setting.
/// </summary>
public sealed class WpfAssemblyTests
{
    [Fact]
    public void WindowsBaseAssembliesLoadExactlyOnce()
    {
        System.Windows.Rect rect = new(0, 0, 10, 10);
        Assert.Equal(100, rect.Width * rect.Height);

        var windowsBase = typeof(System.Windows.Rect).Assembly;
        Assert.Equal(1, AppDomain.CurrentDomain.GetAssemblies()
            .Count(a => string.Equals(a.GetName().Name, windowsBase.GetName().Name, StringComparison.Ordinal)));
    }

    [Fact]
    public void CoreTypesAndWpfTypesCoexistInOneLoadContext()
    {
        object? spectrum = new Core.SpectrumFrame
        {
            StartHz = 100_000_000,
            StopHz = 108_000_000,
            BinWidthHz = 78_125,
            PowerDb = [-40f, -30f],
            FilledBins = 2,
            UtcTimestamp = DateTimeOffset.UnixEpoch,
        };

        var frame = (Core.SpectrumFrame)spectrum;
        System.Windows.Point point = new(frame.BinCount, frame.FilledBins);
        Assert.Equal(2, point.X);
        Assert.NotEqual(typeof(Core.SpectrumFrame).Assembly, typeof(System.Windows.Point).Assembly);
    }

    [Fact]
    public void ADispatcherExistsOnTheTestThread()
    {
        // The view models marshal snapshots onto the UI thread through the Dispatcher; in tests
        // there is no running dispatcher, but the object must exist so the code path is reachable.
        Assert.NotNull(System.Windows.Threading.Dispatcher.CurrentDispatcher);
    }
}

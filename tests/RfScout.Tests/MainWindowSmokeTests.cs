using System.Windows;
using RfScout.ViewModels;

namespace RfScout.Tests;

/// <summary>
/// Builds the real main window and the view model the way the app does. The unit tests used to
/// stop at the view model, so a window that crashed in layout, or XAML missing its controls,
/// shipped unnoticed.
/// </summary>
public sealed class MainWindowSmokeTests
{
    [Fact]
    public void MainWindowBuildsAndLaysOut() => Sta.Run(() =>
    {
        var window = new MainWindow();
        try
        {
            window.Measure(new Size(1280, 820));
            window.Arrange(new Rect(0, 0, 1280, 820));
            window.UpdateLayout();

            Assert.NotNull(window.FindName("Spectrum"));
            Assert.NotNull(window.FindName("Waterfall"));
            Assert.NotNull(window.FindName("PeakTable"));
            Assert.NotNull(window.FindName("SourceBox"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void RfSwitchesStayOffWithoutConfirmation() => Sta.Run(() =>
    {
        using var model = new MainViewModel();
        model.RfAmp = true;
        model.AntennaPower = true;

        Assert.False(model.RfAmp);
        Assert.False(model.AntennaPower);
    });

    [Fact]
    public void RfSwitchesTurnOnWhenConfirmed() => Sta.Run(() =>
    {
        using var model = new MainViewModel();
        model.NeedsConfirmation += _ => true;
        model.RfAmp = true;

        Assert.True(model.RfAmp);
    });
}

/// <summary>Runs a test body on its own STA thread, as WPF objects require.</summary>
internal static class Sta
{
    internal static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

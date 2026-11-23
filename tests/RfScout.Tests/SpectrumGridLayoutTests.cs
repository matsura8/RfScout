using System.Windows;
using RfScout.Controls;

namespace RfScout.Tests;

/// <summary>
/// SpectrumGrid sits in auto-sized layout slots, where WPF measures it with an unbounded size.
/// Returning that size made the main window throw at startup ("should not return
/// PositiveInfinity as its DesiredSize"), which no headless test caught.
/// </summary>
public sealed class SpectrumGridLayoutTests
{
    [Fact]
    public void UnboundedMeasureGivesAFiniteSize() => RunOnSta(() =>
    {
        var grid = new SpectrumGrid();
        grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.False(double.IsInfinity(grid.DesiredSize.Width));
        Assert.False(double.IsInfinity(grid.DesiredSize.Height));
        Assert.Equal(SpectrumGrid.DefaultWidth, grid.DesiredSize.Width);
        Assert.Equal(SpectrumGrid.DefaultHeight, grid.DesiredSize.Height);
    });

    [Fact]
    public void BoundedMeasureFillsTheOfferedSpace() => RunOnSta(() =>
    {
        var grid = new SpectrumGrid();
        grid.Measure(new Size(260, double.PositiveInfinity));

        Assert.Equal(260, grid.DesiredSize.Width);
        Assert.Equal(SpectrumGrid.DefaultHeight, grid.DesiredSize.Height);
    });

    private static void RunOnSta(Action body)
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

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RfScout.Core;
using RfScout.Core.Spectrum;
using RfScout.Services.HackRf;
using RfScout.ViewModels;
using Xunit;

namespace RfScout.Tests;

/// <summary>
/// Release QA exercises the finished application path, not only device primitives. This test
/// builds the real window, opens the declared HackRF through the window's composition root,
/// and records what the live spectrum, waterfall, peak table, analyze and locate panes show.
/// </summary>
[Trait("Category", "Hardware")]
[Collection(HackRfHardwareGroup.Name)]
public sealed class ReleaseQaLiveUiHardwareTests
{
    [Fact]
    public void HackRfFmUiPathFillsPlotsAndSupportsAnalyzeAndLocate() => Sta.Run(() =>
    {
        string qaDirectory = Path.Combine(Directory.GetCurrentDirectory(), "out", "qa");
        Directory.CreateDirectory(qaDirectory);
        var observations = new List<string>();

        var window = new MainWindow();
        var model = (MainViewModel)window.DataContext;
        model.RadioFactory = _ => new HackRfDevice();
        model.SettingsDirectory = Path.Combine(qaDirectory, "settings-live-ui");
        model.SourceKind = SweepSourceKind.HackRf;
        SweepPreset fmPreset = MainViewModel.Presets.First(p => p.Name == "FM broadcast 88-108");
        model.SelectedPreset = fmPreset;
        model.Start();

        try
        {
            Assert.True(model.IsSweeping, "the HackRF sweep did not start: " + model.StatusText);
            SpectrumFrame frame = WaitForFrame(model, m => m.Frames.TryGetSnapshot(out SpectrumFrame? f, out _) && f is { FilledBins: > 0 }, TimeSpan.FromSeconds(20));
            Assert.True(frame.SweepCompleted, "the first HackRF frame did not complete a sweep");
            Assert.Equal(88_000_000L, frame.StartHz);
            Assert.Equal(108_000_000L, frame.StopHz);
            Assert.Equal(frame.BinCount, frame.FilledBins);
            WaitFor(model, m => m.Waterfall.Rows > 0 && m.Waterfall.Writes > 0, TimeSpan.FromSeconds(10));
            Render(window, Path.Combine(qaDirectory, "hackrf-fm-sweep.png"));

            WaitFor(model, m => m.PeakRows.Count > 0, TimeSpan.FromSeconds(20));
            PeakRow peak = model.PeakRows.OrderByDescending(p => p.SnrDb).First();
            model.SelectedPeak = peak;
            window.UpdateLayout();
            Render(window, Path.Combine(qaDirectory, "hackrf-fm-peaks.png"));
            observations.Add($"FM peak table: {model.PeakRows.Count}; strongest {peak.FrequencyText}, {peak.PowerDb:F1} dBFS, SNR {peak.SnrDb:F1} dB.");

            Run(model.AnalyzePeakAsync(peak), TimeSpan.FromSeconds(30));
            WaitFor(model, m => !m.IsAnalyzing, TimeSpan.FromSeconds(5));
            Render(window, Path.Combine(qaDirectory, "hackrf-fm-analyze.png"));
            observations.Add($"Analyze: {model.ClassificationText}; capture {model.CapturePathText}; decode {model.DecodeLogText}");

            Run(model.LocatePeakAsync(peak), TimeSpan.FromSeconds(15));
            Assert.True(model.IsLocating, "locate did not start: " + model.StatusText);
            WaitFor(model, m => m.LocateGraph().Count > 0, TimeSpan.FromSeconds(10));
            window.UpdateLayout();
            Render(window, Path.Combine(qaDirectory, "hackrf-fm-locate.png"));
            observations.Add($"Locate: current {model.LocateCurrentText}, hold {model.LocatePeakText}, graph points {model.LocateGraph().Count}.");

            model.ExitLocate();
            Assert.False(model.IsLocating, "locate did not exit");
            observations.Add("Locate exited cleanly.");
        }
        finally
        {
            model.Stop();
            window.Close();
            File.WriteAllLines(Path.Combine(qaDirectory, "hackrf-fm-ui-observations.txt"), observations);
            foreach (string observation in observations)
            {
                Log(observation);
            }
        }
    });

    [Theory]
    [InlineData("433 ISM 432-435")]
    [InlineData("1090 ADS-B 1088-1092")]
    public void HackRfPresetProducesLiveSpectrumAndRecordsDecoders(string presetName) => Sta.Run(() =>
    {
        string qaDirectory = Path.Combine(Directory.GetCurrentDirectory(), "out", "qa");
        Directory.CreateDirectory(qaDirectory);
        var model = new MainViewModel();
        model.RadioFactory = _ => new HackRfDevice();
        model.SettingsDirectory = Path.Combine(qaDirectory, "settings-live-" + presetName.Replace(' ', '-'));
        model.SourceKind = SweepSourceKind.HackRf;
        SweepPreset preset = MainViewModel.Presets.First(p => p.Name == presetName);
        model.SelectedPreset = preset;
        model.Start();
        try
        {
            Assert.True(model.IsSweeping, "the HackRF sweep did not start: " + model.StatusText);
            SpectrumFrame frame = WaitForFrame(model, m => m.Frames.TryGetSnapshot(out SpectrumFrame? f, out _) && f is { FilledBins: > 0 }, TimeSpan.FromSeconds(20));
            Assert.Equal(frame.BinCount, frame.FilledBins);
            WaitFor(model, m => m.Waterfall.Writes > 0, TimeSpan.FromSeconds(10));
            Log($"{presetName}: {frame.FilledBins}/{frame.BinCount} bins, waterfall writes {model.Waterfall.Writes}, peaks {model.PeakRows.Count}.");
            if (model.PeakRows.Count > 0)
            {
                PeakRow peak = model.PeakRows.OrderByDescending(p => p.SnrDb).First();
                Run(model.AnalyzePeakAsync(peak), TimeSpan.FromSeconds(30));
                WaitFor(model, m => !m.IsAnalyzing, TimeSpan.FromSeconds(5));
                Log($"{presetName}: analyze {peak.FrequencyText} -> {model.ClassificationText}; {model.DecodeLogText}");
            }
            else
            {
                Log($"{presetName}: no peaks on air; logged rather than failed.");
            }
        }
        finally
        {
            model.Stop();
            model.Dispose();
        }
    });

    private static SpectrumFrame WaitForFrame(MainViewModel model, Func<MainViewModel, bool> condition, TimeSpan timeout)
    {
        SpectrumFrame? frame = WaitFor(model, condition, timeout);
        return frame ?? throw new InvalidOperationException("The frame disappeared before the snapshot could be read.");
    }

    private static SpectrumFrame? WaitFor(MainViewModel model, Func<MainViewModel, bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(model))
            {
                model.Frames.TryGetSnapshot(out SpectrumFrame? ready, out _);
                return ready;
            }

            PumpDispatcher(100);
        }

        throw new TimeoutException($"Timed out waiting for {timeout.TotalSeconds:F0}s. Status: {model.StatusText}");
    }

    private static void PumpDispatcher(int milliseconds)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DateTime until = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        do
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(10);
        }
        while (DateTime.UtcNow < until);
    }

    private static void Run(Task task, TimeSpan timeout)
    {
        if (!task.Wait(timeout))
        {
            throw new TimeoutException($"UI action did not finish in {timeout.TotalSeconds:F0}s.");
        }
    }

    private static void Render(Window window, string path)
    {
        window.Measure(new Size(1280, 820));
        window.Arrange(new Rect(0, 0, 1280, 820));
        window.UpdateLayout();
        var target = new RenderTargetBitmap(1280, 820, 96, 96, PixelFormats.Pbgra32);
        target.Render(window.Content as Visual ?? throw new InvalidOperationException("The window has no content."));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[release QA] {message}");
        TestContext.Current.TestOutputHelper?.WriteLine(message);
    }
}

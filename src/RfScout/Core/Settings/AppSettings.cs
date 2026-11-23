using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfScout.Core.Settings;

/// <summary>
/// The per-user settings file: <c>%LOCALAPPDATA%\RfScout\settings.json</c>.
///
/// M6 keeps exactly what the brief asks for and nothing else: the last span, the gains, the
/// peak threshold, the colour map and the band-plan region, plus the waterfall dB window.
/// Everything has a default, and a missing, unreadable or corrupt file falls back to those
/// defaults rather than throwing, so a bad write can never stop the app from starting.
/// Values are validated on load and clamped to the ranges the UI enforces.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Start of the last span, MHz. Default: the full 1 MHz to 6 GHz sweep.</summary>
    public double StartMHz { get; init; } = Defaults.StartMHz;

    /// <summary>Stop of the last span, MHz.</summary>
    public double StopMHz { get; init; } = Defaults.StopMHz;

    /// <summary>FFT bin width in Hz of the last span.</summary>
    public int BinWidthHz { get; init; } = Defaults.BinWidthHz;

    /// <summary>LNA gain, 0 to 40 dB in 8 dB steps.</summary>
    public int LnaGainDb { get; init; } = Defaults.LnaGainDb;

    /// <summary>Baseband VGA gain, 0 to 62 dB in 2 dB steps.</summary>
    public int VgaGainDb { get; init; } = Defaults.VgaGainDb;

    /// <summary>Peak detection threshold in dB above the noise floor.</summary>
    public double PeakThresholdDb { get; init; } = Defaults.PeakThresholdDb;

    /// <summary>Waterfall colour map name (see <see cref="ColorMaps"/>).</summary>
    public string ColorMap { get; init; } = Defaults.ColorMap;

    /// <summary>Band-plan region key, e.g. "US".</summary>
    public string Region { get; init; } = Defaults.Region;

    /// <summary>Waterfall dB window minimum.</summary>
    public double DbMin { get; init; } = Defaults.DbMin;

    /// <summary>Waterfall dB window maximum.</summary>
    public double DbMax { get; init; } = Defaults.DbMax;

    /// <summary>The values used when the file is absent, unreadable or corrupt.</summary>
    public static class Defaults
    {
        public const double StartMHz = 1.0;
        public const double StopMHz = 6000.0;
        public const int BinWidthHz = 25_000;
        public const int LnaGainDb = 32;
        public const int VgaGainDb = 24;
        public const double PeakThresholdDb = 10.0;
        public const string ColorMap = "Viridis";
        public const string Region = "US";
        public const double DbMin = -140.0;
        public const double DbMax = -20.0;
    }

    /// <summary>Hard frequency bounds the file is trusted within (the sweep range of the HackRF).</summary>
    public const double MinMHz = 0.0;
    public const double MaxMHz = 6000.0;
}
/// <summary>
/// Reads and writes <see cref="AppSettings"/>. Load never throws: any problem falls back to the
/// defaults, which is the behaviour the M6 brief requires for a missing or corrupt file.
/// </summary>
public static class AppSettingsStore
{
    /// <summary>File name inside the settings folder.</summary>
    public const string FileName = "settings.json";

    /// <summary>Maximum accepted settings-file size (a normal file is under 1 KB).</summary>
    public const long MaxFileBytes = 64 * 1024;

    /// <summary>Default settings folder: <c>%LOCALAPPDATA%\RfScout</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RfScout");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Load the settings, falling back to the defaults when the file is missing, unreadable,
    /// oversized or corrupt. A corrupt file is renamed to settings.json.bad once so the next
    /// save can start clean without losing the evidence.
    /// </summary>
    public static AppSettings Load(string? directory = null)
    {
        string path = Path.Combine(directory ?? DefaultDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            // Size cap: a settings file is a few hundred bytes; refuse anything silly so a
            // corrupt or hostile file cannot balloon memory before the parser even runs.
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                Quarantine(path);
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
            if (loaded is null)
            {
                Quarantine(path);
                return new AppSettings();
            }

            return Sanitize(loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Quarantine(path);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Save the settings, creating the folder when needed. Failures are returned rather than
    /// thrown: losing settings is never a reason to crash the app.
    /// </summary>
    public static bool Save(AppSettings settings, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string dir = directory ?? DefaultDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, WriteOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Clamp every value into the range the UI accepts, so a hand-edited or half-written file
    /// cannot push the app into a state the controls would reject.
    /// </summary>
    internal static AppSettings Sanitize(AppSettings s)
    {
        double start = Fix(s.StartMHz, AppSettings.MinMHz, AppSettings.MaxMHz, AppSettings.Defaults.StartMHz);
        double stop = Fix(s.StopMHz, AppSettings.MinMHz, AppSettings.MaxMHz, AppSettings.Defaults.StopMHz);
        if (stop <= start)
        {
            (start, stop) = (AppSettings.Defaults.StartMHz, AppSettings.Defaults.StopMHz);
        }

        int bin = (int)Fix(s.BinWidthHz, 1_000, 1_000_000, AppSettings.Defaults.BinWidthHz);
        int lna = (int)Fix(s.LnaGainDb, 0, 40, AppSettings.Defaults.LnaGainDb);
        int vga = (int)Fix(s.VgaGainDb, 0, 62, AppSettings.Defaults.VgaGainDb);
        double threshold = Fix(s.PeakThresholdDb, 3.0, 60.0, AppSettings.Defaults.PeakThresholdDb);
        double dbMin = Fix(s.DbMin, -200.0, 40.0, AppSettings.Defaults.DbMin);
        double dbMax = Fix(s.DbMax, -200.0, 40.0, AppSettings.Defaults.DbMax);
        if (dbMax - dbMin < 20.0)
        {
            (dbMin, dbMax) = (AppSettings.Defaults.DbMin, AppSettings.Defaults.DbMax);
        }

        string map = ColorMaps.Normalize(s.ColorMap);
        string region = string.IsNullOrWhiteSpace(s.Region) ? AppSettings.Defaults.Region : s.Region.Trim();
        if (region.Length > 32)
        {
            region = AppSettings.Defaults.Region;
        }

        return s with
        {
            StartMHz = start,
            StopMHz = stop,
            BinWidthHz = bin,
            LnaGainDb = lna,
            VgaGainDb = vga,
            PeakThresholdDb = threshold,
            ColorMap = map,
            Region = region,
            DbMin = dbMin,
            DbMax = dbMax,
        };
    }

    private static double Fix(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".bad", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keeping the corrupt file in place is fine; the next load falls back again.
        }
    }
}

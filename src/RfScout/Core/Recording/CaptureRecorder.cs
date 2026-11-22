using System.Globalization;
using System.IO;
using RfScout.Core;

namespace RfScout.Core.Recording;

/// <summary>
/// Writes narrowband captures to disk as .cs8 (interleaved complex signed 8-bit, the exact
/// bytes the radio produced) plus a SigMF .sigmf-meta sidecar, under
/// <c>%LOCALAPPDATA%\RfScout\captures</c> by default.
///
/// A size cap keeps the folder bounded: after every write the recorder deletes the oldest
/// files (by their own timestamp, not the filesystem's, so a copied-in file cannot outlive the
/// cap) until the folder is back under the limit. The cap and the folder are constructor
/// parameters so tests can point at a temp directory and use a tiny budget.
/// </summary>
public sealed class CaptureRecorder
{
    /// <summary>Default capture folder: %LOCALAPPDATA%\RfScout\captures.</summary>
    public static string DefaultCaptureDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RfScout",
        "captures");

    /// <summary>Default cap: 2 GiB of captures before the oldest go.</summary>
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _directory;
    private readonly long _maxTotalBytes;

    public CaptureRecorder(string? directory = null, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        if (maxTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), maxTotalBytes, "Cap must be positive.");
        }

        _directory = directory ?? DefaultCaptureDirectory;
        _maxTotalBytes = maxTotalBytes;
    }

    /// <summary>The folder this recorder writes into (created on first write).</summary>
    public string CaptureDirectory => _directory;

    /// <summary>
    /// Write one capture: <c>rfscout_&lt;utc&gt;_&lt;khz&gt;khz.cs8</c> plus its .sigmf-meta.
    /// Returns the paths actually written. Throws on I/O failure; a partial .cs8 is deleted.
    /// </summary>
    public RecordedCapture Save(
        IqCapture capture,
        ClassificationSummary? classification = null,
        string? serialNumber = null)
    {
        ArgumentNullException.ThrowIfNull(capture);

        System.IO.Directory.CreateDirectory(_directory);

        string stamp = capture.UtcStart.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string baseName = string.Format(
            CultureInfo.InvariantCulture,
            "rfscout_{0}_{1:F3}MHz",
            stamp,
            capture.CenterHz / 1e6);
        string cs8Path = Path.Combine(_directory, baseName + ".cs8");
        string metaPath = Path.Combine(_directory, baseName + ".sigmf-meta");

        try
        {
            using (var file = new FileStream(cs8Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(capture.Samples.Span);
            }

            SigMfMeta meta = SigMfMeta.ForCapture(
                capture.CenterHz,
                capture.SampleRateHz,
                capture.SampleCount,
                capture.UtcStart,
                capture.LnaGainDb,
                capture.VgaGainDb,
                capture.RfAmpEnabled,
                capture.AntennaPower,
                serialNumber,
                classification);

            File.WriteAllText(metaPath, meta.ToJson());
        }
        catch
        {
            // Never leave a half-written .cs8 behind: the pair is the unit.
            TryDelete(cs8Path);
            TryDelete(metaPath);
            throw;
        }

        Rotate();

        return new RecordedCapture(cs8Path, metaPath);
    }

    /// <summary>
    /// Delete oldest-first (by the timestamp encoded in the file name, falling back to the
    /// write time) until the folder fits under the cap. A file being deleted is skipped if it
    /// is locked rather than failing the save that triggered the rotation.
    /// </summary>
    public void Rotate()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return;
        }

        var pairs = new List<(DateTimeOffset When, string Cs8, string Meta, long Size)>();
        long total = 0;

        foreach (string cs8 in System.IO.Directory.GetFiles(_directory, "*.cs8"))
        {
            string meta = Path.ChangeExtension(cs8, ".sigmf-meta");
            long size = new FileInfo(cs8).Length;
            if (File.Exists(meta))
            {
                size += new FileInfo(meta).Length;
            }

            pairs.Add((StampOf(cs8), cs8, meta, size));
            total += size;
        }

        if (total <= _maxTotalBytes)
        {
            return;
        }

        pairs.Sort((a, b) => a.When.CompareTo(b.When));
        foreach ((_, string cs8, string meta, long size) in pairs)
        {
            if (total <= _maxTotalBytes)
            {
                break;
            }

            TryDelete(cs8);
            TryDelete(meta);
            total -= size;
        }
    }

    /// <summary>Current total bytes of .cs8/.sigmf-meta pairs in the folder.</summary>
    public long TotalBytes()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return 0;
        }

        long total = 0;
        foreach (string file in System.IO.Directory.GetFiles(_directory, "*.cs8"))
        {
            total += new FileInfo(file).Length;
            string meta = Path.ChangeExtension(file, ".sigmf-meta");
            if (File.Exists(meta))
            {
                total += new FileInfo(meta).Length;
            }
        }

        return total;
    }

    /// <summary>
    /// The UTC timestamp encoded in the file name (rfscout_&lt;yyyyMMddTHHmmssZ&gt;_...), or the
    /// file's own write time when the name does not parse.
    /// </summary>
    private static DateTimeOffset StampOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string[] parts = name.Split('_');
        if (parts.Length >= 2 &&
            DateTimeOffset.TryParseExact(
                parts[1],
                "yyyyMMddTHHmmssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset stamp))
        {
            return stamp;
        }

        return new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Locked by a playback session: leave it, the next rotation tries again.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The two paths written by one <see cref="CaptureRecorder.Save"/>.</summary>
public sealed record RecordedCapture(string Cs8Path, string MetaPath)
{
    /// <summary>True when both files exist, i.e. the pair survived rotation.</summary>
    public bool Exists => File.Exists(Cs8Path) && File.Exists(MetaPath);
}

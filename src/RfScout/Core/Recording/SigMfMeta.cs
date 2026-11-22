using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfScout.Core.Recording;

/// <summary>
/// The .sigmf-meta sidecar for a .cs8 capture, written as SigMF 1.0: a global block with the
/// sample format and capture range, and one annotation per analyze result so a later reader
/// (or a human) can see what the capture was believed to be without re-running the classifier.
///
/// Only public facts go in the file: frequency, rate, gains, timestamps, class names. No
/// decoded content, per the receive-only rule.
/// </summary>
public sealed class SigMfMeta
{
    /// <summary>The format string SigMF uses for interleaved signed 8-bit I/Q.</summary>
    public const string SampleFormat = "ci8_le,ci8_le";

    /// <summary>Writer version string placed in the meta's _rfscout field.</summary>
    public const string Generator = "RfScout";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Center frequency of the capture in Hz (RFC 3987 style "core" of the capture range).</summary>
    [JsonPropertyName("core")]
    public required double Core { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    [JsonPropertyName("sample_rate")]
    public required double SampleRate { get; init; }

    /// <summary>Global sample format.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = SampleFormat;

    /// <summary>Capture start as ISO-8601 UTC.</summary>
    [JsonPropertyName("capture_start")]
    public required string CaptureStart { get; init; }

    /// <summary>Capture length in samples.</summary>
    [JsonPropertyName("captures")]
    public required IReadOnlyList<CaptureRange> Captures { get; init; }

    /// <summary>Per-capture annotations (the classification, gains, device serial).</summary>
    [JsonPropertyName("annotations")]
    public IReadOnlyList<Annotation> Annotations { get; init; } = [];

    /// <summary>Which SigMF version this document follows.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.0.2";

    /// <summary>Free-form writer identification (not part of the SigMF schema, tolerated by readers).</summary>
    [JsonPropertyName("_rfscout")]
    public WriterInfo? Writer { get; init; }

    /// <summary>Serialise to indented JSON text (UTF-8, no BOM when written).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>
    /// Parse a .sigmf-meta file. Throws <see cref="InvalidDataException"/> when the JSON is
    /// malformed or misses the fields a playback reader needs (core, sample_rate, captures).
    /// </summary>
    public static SigMfMeta Parse(string json)
    {
        SigMfMeta? meta;
        try
        {
            meta = JsonSerializer.Deserialize<SigMfMeta>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("sigmf-meta is not valid JSON.", ex);
        }

        if (meta is null || meta.Captures is not { Count: > 0 } || meta.SampleRate <= 0 || double.IsNaN(meta.Core))
        {
            throw new InvalidDataException("sigmf-meta is missing core, sample_rate or captures.");
        }

        return meta;
    }

    /// <summary>Build the meta document for one recorded capture.</summary>
    public static SigMfMeta ForCapture(
        long centerHz,
        int sampleRateHz,
        int sampleCount,
        DateTimeOffset utcStart,
        int lnaGainDb,
        int vgaGainDb,
        bool rfAmpEnabled,
        bool antennaPower,
        string? serialNumber,
        ClassificationSummary? classification)
    {
        var annotations = new List<Annotation>
        {
            new()
            {
                CoreSample = 0,
                SampleCount = sampleCount,
                Description = string.Format(
                    CultureInfo.InvariantCulture,
                    "gains lna {0} dB vga {1} dB amp {2} bias {3}",
                    lnaGainDb,
                    vgaGainDb,
                    rfAmpEnabled ? "on" : "off",
                    antennaPower ? "on" : "off"),
            },
        };

        if (classification is not null)
        {
            annotations.Add(new Annotation
            {
                CoreSample = 0,
                SampleCount = sampleCount,
                Description = string.Format(
                    CultureInfo.InvariantCulture,
                    "classified {0} ({1:P0})",
                    classification.ClassText,
                    classification.Confidence),
            });
        }

        return new SigMfMeta
        {
            Core = centerHz,
            SampleRate = sampleRateHz,
            CaptureStart = utcStart.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Captures = [new CaptureRange { Core = 0, Length = sampleCount }],
            Annotations = annotations,
            Writer = new WriterInfo
            {
                Generator = Generator,
                Serial = serialNumber,
            },
        };
    }
}

/// <summary>One capture range inside the file: sample offset and length.</summary>
public sealed class CaptureRange
{
    /// <summary>Sample index where the capture starts.</summary>
    [JsonPropertyName("core")]
    public long Core { get; init; }

    /// <summary>Length of the capture in samples.</summary>
    [JsonPropertyName("length")]
    public required long Length { get; init; }
}

/// <summary>A SigMF annotation: a note attached to a sample range.</summary>
public sealed class Annotation
{
    /// <summary>First sample of the annotated range.</summary>
    [JsonPropertyName("core_sample")]
    public long CoreSample { get; init; }

    /// <summary>Length of the annotated range in samples.</summary>
    [JsonPropertyName("sample_count")]
    public long? SampleCount { get; init; }

    /// <summary>Human-readable note.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }
}

/// <summary>Which tool wrote the file, and which radio recorded it.</summary>
public sealed class WriterInfo
{
    /// <summary>Writer name and version.</summary>
    [JsonPropertyName("generator")]
    public required string Generator { get; init; }

    /// <summary>Radio serial number, if known.</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; init; }
}

using System.Globalization;
using System.Text.Json;
using RfScout.Core.Decoders;

namespace RfScout.Core.Export;

/// <summary>
/// One peak as the export sees it: the tracked facts plus its band and identifications. The
/// ViewModel builds these from its rows so the exporter never touches WPF types.
/// </summary>
public sealed record PeakExportRow(
    int Id,
    double CenterHz,
    double PowerDb,
    double SnrDb,
    double BandwidthHz,
    double DutyCycle,
    string Activity,
    string Band,
    string BestGuess,
    double Confidence,
    DateTimeOffset LastSeenUtc,
    IReadOnlyList<DeviceIdentification> Identifications);

/// <summary>
/// Exports the peak table and its identifications to CSV and JSON.
///
/// CSV: one row per peak, plus one row per identification (the ident sheet), every cell run
/// through <see cref="CsvBuilder"/> so over-the-air text cannot execute as a formula.
/// JSON: one document with both arrays, invariant culture, camelCase, indented.
/// </summary>
public static class PeakExport
{
    /// <summary>Column order for the peaks sheet (CSV header and JSON field names match).</summary>
    public static IReadOnlyList<string> PeakColumns { get; } =
        ["id", "frequency_hz", "power_dbfs", "snr_db", "bandwidth_hz", "duty", "activity", "band", "best_guess", "confidence", "last_seen_utc"];

    /// <summary>Column order for the identifications sheet.</summary>
    public static IReadOnlyList<string> IdentificationColumns { get; } =
        ["peak_id", "protocol", "device_type", "id", "confidence", "source", "extra"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>CSV text for the peak table (sheet name is up to the writer opening it).</summary>
    public static string PeaksToCsv(IReadOnlyList<PeakExportRow> peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        return CsvBuilder.Build(PeakColumns, peaks.Select(PeakCells));
    }

    /// <summary>CSV text for the identification table, one row per identification.</summary>
    public static string IdentificationsToCsv(IReadOnlyList<PeakExportRow> peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        var rows = new List<IReadOnlyList<string>>();
        foreach (PeakExportRow peak in peaks)
        {
            foreach (DeviceIdentification id in peak.Identifications)
            {
                rows.Add(IdentificationCells(peak.Id, id));
            }
        }

        return CsvBuilder.Build(IdentificationColumns, rows);
    }

    /// <summary>JSON document with both tables, for machine handoff.</summary>
    public static string ToJson(IReadOnlyList<PeakExportRow> peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        var doc = new
        {
            GeneratedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Peaks = peaks.Select(PeakObject).ToList(),
            Identifications = peaks
                .SelectMany(peak => peak.Identifications.Select(id => IdentificationObject(peak.Id, id)))
                .ToList(),
        };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    private static IReadOnlyList<string> PeakCells(PeakExportRow p) =>
        [
            p.Id.ToString(CultureInfo.InvariantCulture),
            Num(p.CenterHz),
            Num(p.PowerDb),
            Num(p.SnrDb),
            Num(p.BandwidthHz),
            Num(p.DutyCycle),
            p.Activity,
            p.Band,
            p.BestGuess,
            Num(p.Confidence),
            p.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture),
        ];

    private static string[] IdentificationCells(int peakId, DeviceIdentification id)
    {
        string extra = string.Join(';', id.Extra.Select(kv => kv.Key + "=" + kv.Value));
        return new[]
        {
            peakId.ToString(CultureInfo.InvariantCulture),
            id.Protocol,
            id.DeviceType,
            id.Id,
            Num(DeviceIdentification.ClampConfidence(id.Confidence)),
            id.Source,
            extra,
        };
    }

    private static object PeakObject(PeakExportRow p) => new
    {
        Id = p.Id,
        FrequencyHz = p.CenterHz,
        PowerDbfs = p.PowerDb,
        SnrDb = p.SnrDb,
        BandwidthHz = p.BandwidthHz,
        Duty = p.DutyCycle,
        Activity = p.Activity,
        Band = p.Band,
        BestGuess = p.BestGuess,
        Confidence = p.Confidence,
        LastSeenUtc = p.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture),
    };

    private static object IdentificationObject(int peakId, DeviceIdentification id) => new
    {
        PeakId = peakId,
        Protocol = id.Protocol,
        DeviceType = id.DeviceType,
        Id = id.Id,
        Confidence = DeviceIdentification.ClampConfidence(id.Confidence),
        Source = id.Source,
        Extra = id.Extra,
    };

    private static string Num(double value) =>
        double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
}

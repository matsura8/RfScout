using System.Globalization;

namespace RfScout.Core.Decoders;

/// <summary>
/// What a decoder identified about one emitter. M4's whole product is this record: a protocol
/// name, a device type, a public identifier (ICAO address, MMSI, sensor id, RDS station PS name),
/// a few extra public fields, and a confidence.
///
/// Hard rule 3 (decode identifiers, not private content): <see cref="Id"/> and
/// <see cref="Extra"/> carry only identifiers and public broadcast metadata that is meant to be
/// received. Decoders must never put decoded message content, decrypted text, or stored payload
/// bytes in here. Classification-only entries (rule 3's "everything else") use
/// <see cref="ProtocolClassification"/> with a descriptive Id such as
/// "2.4 GHz, ~20 MHz OFDM, bursty: likely Wi-Fi".
/// </summary>
/// <param name="Protocol">Decoder/protocol family name, e.g. "adsb", "rds", "ais", "rtl_433", "ook", "classification".</param>
/// <param name="DeviceType">What kind of emitter this is, e.g. "aircraft", "ship", "FM station", "ISM sensor".</param>
/// <param name="Id">The public identifier, or the classification sentence when there is none.</param>
/// <param name="Extra">Extra public fields (callsign, position, PI code...), never private content.</param>
/// <param name="Confidence">0..1 how sure the decoder is.</param>
/// <param name="Source">Which decoder produced this, for the detail pane.</param>
public sealed record DeviceIdentification(
    string Protocol,
    string DeviceType,
    string Id,
    IReadOnlyDictionary<string, string> Extra,
    double Confidence,
    string Source)
{
    /// <summary>Protocol key used by classification-only entries (rule 3: label, never decode).</summary>
    public const string ProtocolClassification = "classification";

    /// <summary>Clamp helper so a decoder cannot publish a nonsense confidence.</summary>
    public static double ClampConfidence(double c) => double.IsFinite(c) ? Math.Clamp(c, 0.0, 1.0) : 0.0;

    /// <summary>One-line form for the peak table's "best guess" column.</summary>
    public string ShortText => string.Format(
        CultureInfo.InvariantCulture,
        "{0}: {1} {2}",
        Protocol,
        DeviceType,
        Id);

    /// <summary>Multi-line form for the detail pane, including the extra public fields.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{ShortText} ({Confidence:P0}, via {Source})");
        foreach ((string key, string value) in Extra)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n  {key}: {value}");
        }

        return sb.ToString();
    }
}

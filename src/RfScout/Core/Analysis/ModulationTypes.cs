using System.Globalization;

namespace RfScout.Core.Analysis;

/// <summary>The modulation modes M3 claims to recognise, plus the honest fallback.</summary>
public enum ModulationClass
{
    Unknown = 0,
    Cw,
    Am,
    Nfm,
    Wfm,
    Ook,
    Fsk2,
    Fsk4,
    PskLike,
    OfdmWideband,
    Chirp,
}

/// <summary>
/// One rule that fired: its name, the feature values it looked at, and the vote it cast.
/// The detail pane shows these so the classification is explainable, not a black box.
/// </summary>
public sealed record ClassificationReason(string Rule, string Evidence, double Weight);

/// <summary>
/// A classification: the winning class, a 0..1 confidence, and the reasons that decided it.
/// Produced by <see cref="ModulationClassifier"/>.
/// </summary>
public sealed record ClassificationResult(
    ModulationClass Class,
    double Confidence,
    IReadOnlyList<ClassificationReason> Reasons)
{
    /// <summary>Class name as shown in the table and detail pane.</summary>
    public string ClassText => Class switch
    {
        ModulationClass.Cw => "CW",
        ModulationClass.Am => "AM",
        ModulationClass.Nfm => "NFM",
        ModulationClass.Wfm => "WFM",
        ModulationClass.Ook => "OOK/ASK",
        ModulationClass.Fsk2 => "2-FSK/GFSK",
        ModulationClass.Fsk4 => "4-FSK",
        ModulationClass.PskLike => "PSK-like",
        ModulationClass.OfdmWideband => "OFDM/wideband",
        ModulationClass.Chirp => "chirp",
        _ => "unknown",
    };

    /// <summary>Reasons joined for the detail pane and the run log.</summary>
    public string DescribeReasons() =>
        string.Join("; ", System.Linq.Enumerable.Select(Reasons, r => $"{r.Rule} ({r.Evidence}) weight {r.Weight:F2}"));

    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1:P0}: {2}",
        ClassText,
        Confidence,
        DescribeReasons());
}

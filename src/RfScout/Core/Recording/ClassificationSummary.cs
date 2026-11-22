namespace RfScout.Core.Recording;

/// <summary>
/// The two public facts about a classification that belong in a .sigmf-meta annotation: the
/// class name and the confidence. Deliberately not the full <see cref="Analysis.ClassificationResult"/>:
/// the reasons quote measured levels, which are fine to store, but keeping the recorded summary
/// tiny means the recording layer never depends on the analysis layer's shape.
/// </summary>
public sealed record ClassificationSummary(string ClassText, double Confidence);

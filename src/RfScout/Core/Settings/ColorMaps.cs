using RfScout.Core.Display;

namespace RfScout.Core.Settings;

/// <summary>
/// The colour maps the settings file may name, and the mapping to <see cref="ColorMapKind"/>.
/// Kept as names (not the enum's integers) in settings.json so a renumbered enum cannot
/// silently change an existing user's waterfall.
/// </summary>
public static class ColorMaps
{
    /// <summary>Every accepted name, first entry is the default.</summary>
    public static IReadOnlyList<string> Names { get; } = ["Viridis", "Grayscale"];

    /// <summary>Resolve a settings name to a <see cref="ColorMapKind"/>; unknown names become Viridis.</summary>
    public static ColorMapKind KindFor(string? name) =>
        string.Equals(name?.Trim(), "Grayscale", StringComparison.OrdinalIgnoreCase)
            ? ColorMapKind.Grayscale
            : ColorMapKind.Viridis;

    /// <summary>The canonical spelling of a name, or the default when it is unknown.</summary>
    public static string Normalize(string? name) =>
        KindFor(name) == ColorMapKind.Grayscale ? "Grayscale" : "Viridis";
}

using System.Text;

namespace RfScout.Core.Export;

/// <summary>
/// Minimal RFC 4180 CSV writer with spreadsheet formula-injection guarding.
///
/// A cell whose first character is one of = + - @ (or a tab/CR that would let a formula hide
/// behind it) is prefixed with a single quote so Excel and LibreOffice show the text instead of
/// evaluating it. Peak tables routinely carry decoder-supplied text (rtl_433 model names, RDS
/// PS names) that originates over the air, so this is the export-side half of the threat model.
/// </summary>
public static class CsvBuilder
{
    /// <summary>Characters that make a spreadsheet evaluate a cell as a formula.</summary>
    public const string FormulaPrefixes = "=+-@";

    /// <summary>Escape one cell: quote when needed, guard formula-looking values.</summary>
    public static string Escape(string? value)
    {
        string text = value ?? string.Empty;

        // Guard first, against the first *printable* character: a leading space or quote can
        // also smuggle a formula into some spreadsheet builds.
        string probe = text.TrimStart(' ', '\t', '\'');
        bool injectable =
            probe.Length > 0 &&
            (FormulaPrefixes.Contains(probe[0]) || text[0] == '\t' || text[0] == '\r');

        string body = injectable ? "'" + text : text;

        bool needsQuotes =
            body.Contains(',', StringComparison.Ordinal) ||
            body.Contains('"', StringComparison.Ordinal) ||
            body.Contains('\n', StringComparison.Ordinal) ||
            body.Contains('\r', StringComparison.Ordinal) ||
            (body.Length > 0 && (body[0] == ' ' || body[^1] == ' '));

        if (!needsQuotes)
        {
            return body;
        }

        return '"' + body.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    /// <summary>Join one row of cells (already-typed values are formatted invariant-culture by callers).</summary>
    public static string Row(IEnumerable<string> cells) => string.Join(',', cells.Select(Escape));

    /// <summary>Build a whole document: header row plus data rows, CRLF-terminated lines.</summary>
    public static string Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Row(header)).Append("\r\n");
        foreach (IReadOnlyList<string> row in rows)
        {
            sb.Append(Row(row)).Append("\r\n");
        }

        return sb.ToString();
    }
}

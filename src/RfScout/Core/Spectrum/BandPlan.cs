using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfScout.Core.Spectrum;

/// <summary>One allocation in a band plan: what lives between two frequencies.</summary>
/// <param name="Name">Human-readable band name, shown in the peak table's Band column.</param>
/// <param name="StartHz">Lower edge in Hz, inclusive.</param>
/// <param name="StopHz">Upper edge in Hz, inclusive (a point allocation has StartHz == StopHz).</param>
/// <param name="Modulations">Modulations typically seen here, for the classifier's prior and the UI.</param>
/// <param name="Decoders">Decoder keys M4 should try on a peak in this band (empty until M4 exists).</param>
public sealed record BandAllocation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startHz")] long StartHz,
    [property: JsonPropertyName("stopHz")] long StopHz,
    [property: JsonPropertyName("modulations")] IReadOnlyList<string> Modulations,
    [property: JsonPropertyName("decoders")] IReadOnlyList<string> Decoders)
{
    /// <summary>True when <paramref name="hz"/> falls inside this allocation.</summary>
    public bool Contains(double hz) => hz >= StartHz && hz <= StopHz;

    /// <summary>Width of the allocation in Hz (0 for a point allocation).</summary>
    public long WidthHz => StopHz - StartHz;

    /// <summary>Range text for tooltips and the detail pane.</summary>
    public string RangeText => StartHz == StopHz
        ? Display.FrequencyFormat.MHz(StartHz)
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0} - {1}",
            Display.FrequencyFormat.MHz(StartHz),
            Display.FrequencyFormat.MHz(StopHz));
}

/// <summary>
/// The band plan: named allocations per region, loaded from <c>Core/Spectrum/bandplan.json</c>.
///
/// The peak table's "band" column and (from M3/M4) the classifier prior and decoder selection
/// both need to answer "what is allowed to live at 433.92 MHz?". That answer is policy, not code:
/// it differs by region, changes with allocations, and must be reviewable without recompiling, so
/// it lives in a JSON file shipped next to the code and validated by a test on load
/// (<c>BandPlanTests</c>).
///
/// The file is parsed with <see cref="JsonSerializerOptions"/> in strict, case-sensitive mode and
/// every allocation is checked: names non-empty, ranges ordered, modulations and decoders present.
/// A malformed file throws System.IO.InvalidDataException with the offending index rather than
/// half-loading, because a silently truncated plan would mislabel bands.
///
/// Overlapping allocations are allowed on purpose (LTE n71 and n12 share spectrum, Wi-Fi and ISM
/// 2.4 GHz overlap): AllocationsAt returns every match, narrowest first, and the
/// table shows the first match.
/// </summary>
public sealed class BandPlan
{
    /// <summary>File name next to the assembly; copied by the csproj.</summary>
    public const string DefaultFileName = "bandplan.json";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Dictionary<string, IReadOnlyList<BandAllocation>> _regions;
    private readonly IReadOnlyList<BandAllocation> _default;

    private BandPlan(string defaultRegion, Dictionary<string, IReadOnlyList<BandAllocation>> regions, IReadOnlyList<BandAllocation> defaultAllocations)
    {
        DefaultRegion = defaultRegion;
        _regions = regions;
        _default = defaultAllocations;
    }

    /// <summary>Region key the plan falls back to, e.g. "US".</summary>
    public string DefaultRegion { get; }

    /// <summary>Region keys the file defines, e.g. ["US", "EU"].</summary>
    public IReadOnlyList<string> Regions => [.. _regions.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Allocations for the default region.</summary>
    public IReadOnlyList<BandAllocation> Allocations => _default;

    /// <summary>Allocations for a region; falls back to the default region when it is unknown.</summary>
    public IReadOnlyList<BandAllocation> AllocationsFor(string region) =>
        _regions.TryGetValue(region, out IReadOnlyList<BandAllocation>? found) ? found : _default;

    /// <summary>Every allocation containing <paramref name="hz"/> in the default region, narrowest first.</summary>
    public IReadOnlyList<BandAllocation> AllocationsAt(double hz) => NarrowFirst(AllocationsFor(DefaultRegion), hz);

    /// <summary>Every allocation containing <paramref name="hz"/> in <paramref name="region"/>, narrowest first.</summary>
    public IReadOnlyList<BandAllocation> AllocationsAt(string region, double hz) => NarrowFirst(AllocationsFor(region), hz);

    /// <summary>The narrowest allocation containing <paramref name="hz"/>, or null in unallocated spectrum.</summary>
    public BandAllocation? BestMatch(double hz) => BestMatchFor(DefaultRegion, hz);

    /// <summary>The narrowest allocation containing <paramref name="hz"/> in <paramref name="region"/>.</summary>
    public BandAllocation? BestMatchFor(string region, double hz) => NarrowFirst(AllocationsFor(region), hz).FirstOrDefault();

    /// <summary>Band name for the table: the narrowest match, or "unallocated".</summary>
    public string BandName(double hz) => BestMatch(hz)?.Name ?? "unallocated";

    /// <summary>
    /// Load the shipped plan (next to the assembly). Throws when the copy step did not run, so a
    /// broken build fails loudly rather than mislabelling bands from a stale file.
    /// </summary>
    public static BandPlan LoadShipped()
    {
        string path = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        if (File.Exists(path))
        {
            return Parse(File.ReadAllText(path, System.Text.Encoding.UTF8), path);
        }

        throw new FileNotFoundException(
            $"The band plan file was not found next to the assembly: {path}",
            path);
    }

    /// <summary>Parse plan JSON and validate every allocation. Throws InvalidDataException on a bad file.</summary>
    public static BandPlan Parse(string json, string origin = "bandplan.json")
    {
        ArgumentNullException.ThrowIfNull(json);

        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(json, FileOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{origin}: not a valid band plan file: {ex.Message}", ex);
        }

        if (doc is null || doc.Regions is null || doc.Regions.Count == 0)
        {
            throw new InvalidDataException($"{origin}: band plan must define at least one region.");
        }

        if (string.IsNullOrWhiteSpace(doc.DefaultRegion))
        {
            throw new InvalidDataException($"{origin}: band plan must name a defaultRegion.");
        }

        var regions = new Dictionary<string, IReadOnlyList<BandAllocation>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string region, List<RawAllocation>? raws) in doc.Regions)
        {
            if (raws is null || raws.Count == 0)
            {
                throw new InvalidDataException($"{origin}: region '{region}' has no allocations.");
            }

            var allocations = new List<BandAllocation>(raws.Count);
            for (int i = 0; i < raws.Count; i++)
            {
                RawAllocation raw = raws[i];
                string where = $"{origin}: region '{region}' allocation {i}";
                if (string.IsNullOrWhiteSpace(raw.Name))
                {
                    throw new InvalidDataException($"{where} has no name.");
                }

                if (raw.StartHz <= 0 || raw.StopHz < raw.StartHz)
                {
                    throw new InvalidDataException(
                        $"{where} has an invalid range {raw.StartHz}..{raw.StopHz} Hz.");
                }

                if (raw.StartHz > SweepMath.MaxTunableHz || raw.StopHz > SweepMath.MaxTunableHz)
                {
                    throw new InvalidDataException(
                        $"{where} is above the HackRF's tuning ceiling of {SweepMath.MaxTunableHz / 1e6:F0} MHz.");
                }

                allocations.Add(new BandAllocation(
                    raw.Name.Trim(),
                    raw.StartHz,
                    raw.StopHz,
                    raw.Modulations ?? [],
                    raw.Decoders ?? []));
            }

            regions[region] = allocations;
        }

        if (!regions.TryGetValue(doc.DefaultRegion, out IReadOnlyList<BandAllocation>? defaultAllocations))
        {
            throw new InvalidDataException(
                $"{origin}: defaultRegion '{doc.DefaultRegion}' is not defined in regions.");
        }

        return new BandPlan(doc.DefaultRegion, regions, defaultAllocations);
    }

    private static List<BandAllocation> NarrowFirst(IReadOnlyList<BandAllocation> allocations, double hz)
    {
        var matches = new List<BandAllocation>();
        foreach (BandAllocation allocation in allocations)
        {
            if (allocation.Contains(hz))
            {
                matches.Add(allocation);
            }
        }

        matches.Sort(static (a, b) => a.WidthHz.CompareTo(b.WidthHz));
        return matches;
    }

    private sealed class Document
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("defaultRegion")]
        public string? DefaultRegion { get; set; }

        [JsonPropertyName("regions")]
        public Dictionary<string, List<RawAllocation>?>? Regions { get; set; }
    }

    private sealed class RawAllocation
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("startHz")]
        public long StartHz { get; set; }

        [JsonPropertyName("stopHz")]
        public long StopHz { get; set; }

        [JsonPropertyName("modulations")]
        public string[]? Modulations { get; set; }

        [JsonPropertyName("decoders")]
        public string[]? Decoders { get; set; }
    }
}

using System.Globalization;
using System.IO;
using RfScout.Core.Analysis;

namespace RfScout.Core.Decoders;

/// <summary>
/// Runs the official rtl_433 (Windows x64 release, kept in <c>tools\rtl_433\</c>, version and
/// SHA-256 recorded in <c>tools\rtl_433\README.md</c>) over a capture to identify ISM
/// 315/433/868/915 MHz OOK/FSK telemetry.
///
/// The bridge writes the capture bytes as a temporary .cs8 and invokes
/// <c>rtl_433 -f &lt;MHz&gt; -s &lt;rate&gt; -r &lt;file&gt; -F json -q -a</c> with a wall-clock
/// timeout and an output size limit, then parses the JSON lines into identifications (model, id,
/// channel). rtl_433's output is untrusted input (threat model): the parser tolerates missing,
/// nested or hostile fields, caps every string, and never throws.
///
/// When the binary is missing the bridge reports that honestly (Supports=false) so the pipeline
/// falls back to <see cref="OokPulseDecoder"/>.
/// </summary>
public sealed class Rtl433Bridge : IDecoder
{
    /// <summary>Band-plan key for this decoder.</summary>
    public const string Key = "rtl_433";

    /// <summary>Wall-clock limit for one rtl_433 run.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Cap on captured stdout+stderr; anything past this is discarded and the run is treated as noisy.</summary>
    public const int MaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>Cap on identifications parsed from one run (a hostile binary can print a lot).</summary>
    public const int MaxIdentifications = 64;

    /// <summary>Cap on any single field copied out of the JSON.</summary>
    public const int MaxFieldLength = 96;

    private readonly string? _exePath;

    /// <summary>Locate the shipped binary by default; pass null to force the fallback path.</summary>
    public Rtl433Bridge(string? exePath = null)
    {
        _exePath = exePath ?? DefaultExePath();
    }

    string IDecoder.Key => Key;

    /// <summary>tools\rtl_433\rtl_433.exe next to the RfScout assembly's repository root, or null.</summary>
    public static string? DefaultExePath()
    {
        foreach (string candidate in Rtl433Paths.Candidates(AppContext.BaseDirectory))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when the binary exists and the capture sits in an ISM band it can decode.</summary>
    public bool Supports(DecoderContext context) =>
        _exePath is not null
        && context.SampleRateHz > 0
        && context.BandWants(Key)
        && !context.Iq.IsEmpty;

    public IReadOnlyList<DeviceIdentification> Decode(DecoderContext context)
    {
        if (_exePath is null || context.Iq.IsEmpty || context.SampleRateHz <= 0)
        {
            return [];
        }

        string temp = Path.Combine(Path.GetTempPath(), "rfscout", "rtl433");
        Directory.CreateDirectory(temp);
        string cs8 = Path.Combine(temp, $"cap_{context.CenterHz}_{context.SampleRateHz}_{Guid.NewGuid():N}.cs8");

        try
        {
            File.WriteAllBytes(cs8, context.Iq.Span);
            string json = Run(cs8, context.CenterHz, context.SampleRateHz);
            return ParseJsonLines(json, context.CenterHz);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            return [];
        }
        finally
        {
            TryDelete(cs8);
        }
    }

    IReadOnlyList<DeviceIdentification> IDecoder.Decode(DecoderContext context) => Decode(context);

    /// <summary>Run rtl_433 on the file; returns stdout (capped). Throws TimeoutException past the limit.</summary>
    internal string Run(string cs8Path, long centerHz, int sampleRateHz)
    {
        if (_exePath is null)
        {
            throw new InvalidOperationException("rtl_433 binary is not installed");
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = string.Format(
                CultureInfo.InvariantCulture,
                "-f {0:F6}M -s {1} -r \"{2}\" -F json -q",
                centerHz / 1e6,
                sampleRateHz,
                cs8Path),
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start rtl_433");

        var stdoutTask = ReadCappedAsync(process.StandardOutput);
        var stderrTask = ReadCappedAsync(process.StandardError);
        if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException($"rtl_433 ran past {RunTimeout.TotalSeconds:F0} s");
        }

        // Streams can outlive WaitForExit on Windows; bound the drain too.
        if (!stdoutTask.Wait(RunTimeout) || !stderrTask.Wait(RunTimeout))
        {
            throw new TimeoutException("rtl_433 output did not drain");
        }

        return stdoutTask.Result;
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var sb = new System.Text.StringBuilder();
        int total = 0;
        char[] chunk = new char[8192];
        while (total < MaxOutputBytes)
        {
            int n = await reader.ReadAsync(chunk.AsMemory(0, chunk.Length)).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
            sb.Append(chunk, 0, n);
        }

        return sb.ToString();
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Parse rtl_433 -F json output: one JSON object per line (plus banner lines to skip).
    /// Untrusted: every field is capped, unknown fields are ignored, and a line that is not an
    /// object is skipped rather than throwing. Extracts model, id, channel (and any other short
    /// scalar fields as public extras).
    /// </summary>
    internal static IReadOnlyList<DeviceIdentification> ParseJsonLines(string output, long centerHz)
    {
        var found = new List<DeviceIdentification>();
        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (found.Count >= MaxIdentifications)
            {
                break;
            }

            if (!raw.StartsWith('{'))
            {
                continue;
            }

            DeviceIdentification? id = ParseOne(raw, centerHz);
            if (id is not null)
            {
                found.Add(id);
            }
        }

        return found;
    }

    private static DeviceIdentification? ParseOne(string line, long centerHz)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(line, new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 16,
        });

        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        string? model = GetString(doc.RootElement, "model");
        if (model is null)
        {
            return null; // a time/frequency-only record, not a device report
        }

        string id = GetString(doc.RootElement, "id") ?? GetString(doc.RootElement, "protocol") ?? "unknown id";
        string? channel = GetString(doc.RootElement, "channel");

        var extra = new Dictionary<string, string>();
        if (channel is not null)
        {
            extra["channel"] = channel;
        }

        foreach (System.Text.Json.JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (extra.Count >= 12)
            {
                break;
            }

            if (prop.Name is "time" or "model" or "id" or "channel" or "freq" or "freq1" or "freq2")
            {
                continue;
            }

            if (prop.Value.ValueKind is System.Text.Json.JsonValueKind.String
                or System.Text.Json.JsonValueKind.Number
                or System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False)
            {
                extra[Cap(prop.Name, 32)] = Cap(prop.Value.ToString(), MaxFieldLength);
            }
        }

        extra["center_MHz"] = (centerHz / 1e6).ToString("F4", CultureInfo.InvariantCulture);
        extra["decoded"] = "true";

        return new DeviceIdentification(
            "rtl_433",
            Cap(model, MaxFieldLength),
            Cap(id, MaxFieldLength),
            extra,
            0.85,
            "rtl_433");
    }

    private static string? GetString(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out System.Text.Json.JsonElement el))
        {
            return null;
        }

        string? text = el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => el.GetString(),
            System.Text.Json.JsonValueKind.Number => el.ToString(),
            _ => null,
        };

        return text is null ? null : Cap(text, MaxFieldLength);
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Where the bridge looks for rtl_433.exe, so tests and the app agree on one rule.</summary>
internal static class Rtl433Paths
{
    /// <summary>Candidate paths, best first: repo tools\rtl_433 relative to the assembly and to the current directory.</summary>
    public static IEnumerable<string> Candidates(string baseDirectory)
    {
        yield return Path.Combine(baseDirectory, "tools", "rtl_433", "rtl_433.exe");
        string? probe = TryFindRepoRoot(baseDirectory);
        if (probe is not null)
        {
            yield return Path.Combine(probe, "tools", "rtl_433", "rtl_433.exe");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "rtl_433", "rtl_433.exe");
    }

    private static string? TryFindRepoRoot(string start)
    {
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RfScout.sln"))
                    || Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}

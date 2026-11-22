using RfScout.Core.Analysis;
using RfScout.Core.Spectrum;

namespace RfScout.Core.Decoders;

/// <summary>
/// M4 decoder selection and folding: given the band-plan entries for a peak and the M3
/// classification, run the decoders the band plan names (when the classifier agrees the signal
/// is of a kind they can handle), then fall back to a classification-only label (hard rule 3:
/// everything else is classified, never decoded).
///
/// The result feeds the peak table's "best guess" column (the most confident identification) and
/// the detail pane (all identifications). Decoders are injected so tests can pin behaviour and
/// the app can register the real ones; <see cref="Default"/> builds the shipped set.
/// </summary>
public sealed class DecoderPipeline
{
    private readonly IReadOnlyList<IDecoder> _decoders;

    public DecoderPipeline(IReadOnlyList<IDecoder> decoders)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        _decoders = decoders;
    }

    /// <summary>The decoders this pipeline will try, in registration order.</summary>
    public IReadOnlyList<IDecoder> Decoders => _decoders;

    /// <summary>
    /// The shipped pipeline: rtl_433 bridge first for ISM bands (it subsumes the OOK fallback),
    /// then the in-tree protocol decoders. The OOK pulse decoder is registered last and only
    /// reached when rtl_433 is missing or found nothing (Rtl433Bridge reports that honestly).
    /// </summary>
    public static DecoderPipeline Default(Rtl433Bridge? rtl433 = null) => new(
    [
        rtl433 ?? new Rtl433Bridge(),
        new ModeSDecoder(),
        new RdsDecoder(),
        new AisDecoder(),
        new OokPulseDecoder(),
    ]);

    /// <summary>
    /// Which decoders apply to this context: a band-plan decoder key must be named by the band
    /// plan AND the decoder's own <see cref="IDecoder.Supports"/> must accept the capture. The
    /// OOK fallback only applies when no other decoder was named for the band, matching the
    /// brief's "if rtl_433 is missing or finds nothing, fall back to OokPulseDecoder".
    /// </summary>
    public IReadOnlyList<IDecoder> SelectFor(DecoderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var named = new List<IDecoder>();
        var fallbacks = new List<IDecoder>();
        foreach (IDecoder decoder in _decoders)
        {
            if (decoder.Key == OokPulseDecoder.Key)
            {
                fallbacks.Add(decoder);
                continue;
            }

            if (context.BandWants(decoder.Key) && decoder.Supports(context))
            {
                named.Add(decoder);
            }
        }

        if (named.Count > 0)
        {
            // rtl_433 already covers OOK ISM protocols; the pulse decoder only adds timing when
            // rtl_433 produced nothing, which Identify() decides after running the named set.
            return named;
        }

        // No protocol was named: OOK/FSK ISM peaks still get the timing-only fallback.
        foreach (IDecoder decoder in fallbacks)
        {
            if (decoder.Supports(context))
            {
                return [decoder];
            }
        }

        return [];
    }

    /// <summary>
    /// Run the selected decoders and fold in the classification-only label. Identifications come
    /// back most confident first. When a protocol decoder named by the band plan produced
    /// nothing, the OOK timing fallback is tried next for OOK/ASK captures, so an ISM burst
    /// without a known protocol still reports "OOK PWM, x bit/s, unknown protocol".
    /// </summary>
    public IReadOnlyList<DeviceIdentification> Identify(DecoderContext context)
    {
        var found = new List<DeviceIdentification>();
        foreach (IDecoder decoder in SelectFor(context))
        {
            IReadOnlyList<DeviceIdentification> part = SafeDecode(decoder, context);
            found.AddRange(part);
        }

        if (found.Count == 0 && !AnyDecoderRan(context))
        {
            // Nothing protocol-level applied: still try the timing-only OOK fallback for a
            // band-named capture whose decode came up empty (rtl_433 missing or silent).
            foreach (IDecoder decoder in _decoders)
            {
                if (decoder.Key == OokPulseDecoder.Key && decoder.Supports(context))
                {
                    found.AddRange(SafeDecode(decoder, context));
                    break;
                }
            }
        }

        if (found.Count == 0)
        {
            found.Add(ClassificationLabel.For(context));
        }

        found.Sort(static (a, b) => b.Confidence.CompareTo(a.Confidence));
        return found;
    }

    private static IReadOnlyList<DeviceIdentification> SafeDecode(IDecoder decoder, DecoderContext context)
    {
        try
        {
            IReadOnlyList<DeviceIdentification>? part = decoder.Decode(context);
            return part ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A decoder must not throw on arbitrary RF data; if one does, log-and-skip rather
            // than crash the sweep. The detail pane still shows the classification label.
            System.Diagnostics.Debug.WriteLine($"decoder {decoder.Key} failed: {ex.Message}");
            return [];
        }
    }

    private bool AnyDecoderRan(DecoderContext context) => SelectFor(context).Count > 0;
}

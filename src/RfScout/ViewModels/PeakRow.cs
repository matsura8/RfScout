using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RfScout.Core.Spectrum;

namespace RfScout.ViewModels;

/// <summary>
/// One row of the peak table: a tracked peak plus the band-plan and classification columns the
/// brief asks for (ID, frequency, power, SNR, bandwidth, duty, type, band, best guess,
/// confidence, last seen). A mutable, notification-raising row rather than an immutable record
/// so a DataGrid can sort and filter it and the cell updates in place as the sweep refreshes.
/// </summary>
public sealed class PeakRow : INotifyPropertyChanged
{
    private int _id;
    private double _centerHz;
    private double _powerDb;
    private double _snrDb;
    private double _bandwidthHz;
    private double _dutyCycle;
    private string _activity = string.Empty;
    private string _band = "unallocated";
    private string _bestGuess = string.Empty;
    private double _confidence;
    private DateTimeOffset _lastSeenUtc = DateTimeOffset.UnixEpoch;
    private string _bandwidthClass = string.Empty;
    private bool _isSelected;
    private bool _isMuted;
    private IReadOnlyList<RfScout.Core.Decoders.DeviceIdentification> _identifications = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable tracker ID, shown first so the operator can follow one emitter.</summary>
    public int Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    /// <summary>Centre frequency in Hz; the raw value so numeric sort is exact.</summary>
    public double CenterHz
    {
        get => _centerHz;
        set
        {
            if (SetField(ref _centerHz, value))
            {
                OnPropertyChanged(nameof(FrequencyText));
            }
        }
    }

    public double PowerDb
    {
        get => _powerDb;
        set
        {
            if (SetField(ref _powerDb, value))
            {
                OnPropertyChanged(nameof(PowerText));
            }
        }
    }

    public double SnrDb
    {
        get => _snrDb;
        set
        {
            if (SetField(ref _snrDb, value))
            {
                OnPropertyChanged(nameof(SnrText));
            }
        }
    }

    public double BandwidthHz
    {
        get => _bandwidthHz;
        set
        {
            if (SetField(ref _bandwidthHz, value))
            {
                OnPropertyChanged(nameof(BandwidthText));
            }
        }
    }

    public double DutyCycle
    {
        get => _dutyCycle;
        set
        {
            if (SetField(ref _dutyCycle, value))
            {
                OnPropertyChanged(nameof(DutyText));
            }
        }
    }

    public string Activity
    {
        get => _activity;
        set => SetField(ref _activity, value);
    }

    public string Band
    {
        get => _band;
        set => SetField(ref _band, value);
    }

    public string BestGuess
    {
        get => _bestGuess;
        set => SetField(ref _bestGuess, value);
    }

    public double Confidence
    {
        get => _confidence;
        set
        {
            if (SetField(ref _confidence, value))
            {
                OnPropertyChanged(nameof(ConfidenceText));
            }
        }
    }

    public DateTimeOffset LastSeenUtc
    {
        get => _lastSeenUtc;
        set
        {
            if (SetField(ref _lastSeenUtc, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public string BandwidthClass
    {
        get => _bandwidthClass;
        set => SetField(ref _bandwidthClass, value);
    }

    /// <summary>M4: identifications (decodes or the classification label) from the last analyze of this peak, most confident first.</summary>
    public IReadOnlyList<RfScout.Core.Decoders.DeviceIdentification> Identifications
    {
        get => _identifications;
        set
        {
            if (ReferenceEquals(_identifications, value))
            {
                return;
            }

            _identifications = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Identifications)));
        }
    }

    /// <summary>True when this row is the selected peak; drives the marker highlight.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>
    /// Muted emitters are kept in the table but greyed out, and the locate panel ignores
    /// them, so the operator can silence a known pager or broadcast repeater without
    /// losing its row.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => SetField(ref _isMuted, value);
    }

    public string FrequencyText => Core.Display.FrequencyFormat.MHz(_centerHz);
    public string PowerText => Core.Display.FrequencyFormat.Db(_powerDb);
    public string SnrText => _snrDb.ToString("F1", CultureInfo.InvariantCulture) + " dB";
    public string BandwidthText => Core.Display.FrequencyFormat.BinWidth(Math.Max(1, _bandwidthHz));
    public string DutyText => (_dutyCycle * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
    public string ConfidenceText => (_confidence * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
    public string LastSeenText => _lastSeenUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Automation name for the row: the whole row in one sentence for a screen reader.</summary>
    public string AutomationName => string.Format(
        CultureInfo.InvariantCulture,
        "peak {0}, {1}, {2}, SNR {3}, duty {4}, {5}, band {6}",
        _id,
        FrequencyText,
        PowerText,
        SnrText,
        DutyText,
        _activity,
        _band);

    /// <summary>Fold a tracked peak into this row, raising change notifications once per column.</summary>
    public void Apply(TrackedPeak peak, string band, string bestGuess, double confidence)
    {
        Id = peak.Id;
        CenterHz = peak.CenterHz;
        PowerDb = peak.PeakDb;
        SnrDb = peak.SnrDb;
        BandwidthHz = peak.BandwidthHz3Db;
        DutyCycle = peak.DutyCycle;
        Activity = peak.Activity.ToString();
        BandwidthClass = peak.BandwidthClass.ToString();
        Band = band;
        BestGuess = bestGuess;
        Confidence = confidence;
        LastSeenUtc = peak.LastSeenUtc;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

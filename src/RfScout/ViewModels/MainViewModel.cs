using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RfScout.ViewModels;

/// <summary>
/// State for the main window. Milestone 1 is the DSP core, so the window only reports that the
/// core is present and healthy; the sweep controls arrive in milestone 2 and bind here.
/// Logic stays out of the code-behind: the view model and Core hold it so it can be unit tested.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; } =
        $"RfScout {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]}";

    private string _statusText =
        "Core ready: sweep framing, FFT and bin mapping are in. Sweep controls and the HackRF stream arrive next.";

    /// <summary>One-line status shown until the sweep UI lands (M2).</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

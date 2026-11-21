using System.Windows;
using RfScout.Core;

namespace RfScout;

/// <summary>Application entry. `--self-test` checks core logic and exits without UI.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(SelfTest.Run() ? 0 : 1);
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}

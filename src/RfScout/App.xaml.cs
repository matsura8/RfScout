using System.IO;
using System.Windows;
using RfScout.Core;

namespace RfScout;

/// <summary>
/// Application entry. <c>--self-test</c> checks core maths and framing and exits without a UI;
/// <c>--gpu-test</c> does the same for the optional CUDA path (see <see cref="GpuSelfTest"/>).
/// </summary>
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

        if (e.Args.Contains("--gpu-test", StringComparer.OrdinalIgnoreCase))
        {
            // The report goes to stdout, which is what native\cuda\build-cuda.ps1 captures after
            // it compiles RfScoutCuda.dll. Exit code 2 means "no CUDA device here", not failure.
            TextWriter output = Console.Out;
            int code = GpuSelfTest.Run(output);
            output.Flush();
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}

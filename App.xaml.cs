using System;
using System.Linq;
using System.Windows;

namespace GeminiBar;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                SelfTest.Run();
                Environment.ExitCode = 0;
            }
            catch
            {
                Environment.ExitCode = 1;
            }

            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

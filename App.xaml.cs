using System;
using System.Linq;
using System.Threading.Tasks;
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
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                System.Diagnostics.Trace.WriteLine(ex);
            }

            Shutdown();
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--ui-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            var testWindow = new MainWindow(uiSmokeTest: true);
            MainWindow = testWindow;
            testWindow.Loaded += async (_, _) =>
            {
                try
                {
                    await testWindow.RunUiSmokeTestAsync();
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 1;
                    System.Diagnostics.Trace.WriteLine(ex);
                }
                finally
                {
                    Shutdown();
                }
            };
            testWindow.Show();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

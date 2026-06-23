using System.Windows;

namespace CardProgram
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, ex) =>
            {
                ex.Handled = true;
                // Errors show in the status bar, not as dialogs
            };
        }
    }
}

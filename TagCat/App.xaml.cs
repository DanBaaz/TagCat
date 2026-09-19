using System;
using System.Windows;
using System.Windows.Threading;

namespace MediaTagger
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Without these, an exception during startup (e.g. a bad XAML resource in a
            // single-file publish, or a COM failure) just kills the process silently
            // because this is a windowed app with no console attached.
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                MessageBox.Show(
                    $"TagCat failed to start or crashed:\n\n{ex}",
                    "TagCat - Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(
                    $"TagCat hit an unhandled error:\n\n{args.Exception}",
                    "TagCat - Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true; // keep the app alive instead of dying silently
            };

            base.OnStartup(e);
        }
    }
}

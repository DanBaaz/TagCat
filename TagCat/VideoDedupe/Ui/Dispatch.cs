using System;
using System.Windows;
using System.Windows.Threading;

namespace VideoDedupe.Ui;

/// <summary>
/// WPF only allows UI objects to be touched from the thread that created them.
/// The scanner deliberately runs on background threads, so anything that ends up
/// changing the interface has to be routed back through here first.
/// </summary>
public static class Dispatch
{
    private static Dispatcher UiDispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>
    /// Runs the action on the UI thread. If already on it, runs immediately so
    /// there is no needless queuing.
    /// </summary>
    public static void OnUi(Action action)
    {
        var dispatcher = UiDispatcher;

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    /// <summary>Queues the action without waiting for it. Use for frequent progress updates.</summary>
    public static void PostToUi(Action action)
    {
        var dispatcher = UiDispatcher;

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }
}

using System.Threading;
using System.Windows;

namespace SunChat;

public partial class App : System.Windows.Application
{
    private const string MutexName = "SunChat.SingleInstance.Mutex";
    private const string ShowEventName = "SunChat.SingleInstance.Show";

    private Mutex _mutex;
    private EventWaitHandle _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!isFirst)
        {
            // Another instance already runs: signal it to surface, then exit.
            _showEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // Background waiter: when a second launch signals, show the existing window.
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.Invoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true };
        t.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}

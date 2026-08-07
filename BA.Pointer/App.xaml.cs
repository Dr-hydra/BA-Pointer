using System.Threading;
using System.Windows;

namespace BA.Pointer;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, "Local\\BA.Pointer.Singleton", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("BA Pointer 已经在运行。", "BA Pointer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

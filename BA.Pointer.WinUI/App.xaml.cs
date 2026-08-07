using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using BA.Pointer.Services;
using Microsoft.UI.Xaml;

namespace BA.Pointer;

public partial class App : Application
{
    private Mutex? _mutex;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = new SettingsStore().Load();
        if (settings.RunAsAdministrator && !IsRunningAsAdministrator())
        {
            try
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Exit();
                return;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                // The user declined the UAC prompt; continue without elevation.
            }
            catch (Exception exception)
            {
                ErrorLog.Write(exception);
            }
        }

        _mutex = new Mutex(true, "Local\\BA.Pointer.Singleton.WinUI", out var createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        _window = new MainWindow();
        if (!settings.SilentStart) _window.Activate();
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Shutdown()
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _mutex = null;
        Exit();
    }
}

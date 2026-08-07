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
        UnhandledException += (_, eventArgs) =>
            ErrorLog.Write(eventArgs.Exception, "App.UnhandledException");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            ErrorLog.Write(eventArgs.ExceptionObject as Exception ??
                           new InvalidOperationException($"Unhandled non-exception object: {eventArgs.ExceptionObject}"),
                "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            ErrorLog.Write(eventArgs.Exception, "TaskScheduler.UnobservedTaskException");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = new SettingsStore().Load();
        var isAdministrator = IsRunningAsAdministrator();
        ErrorLog.WriteInfo("App", $"Launch requested. version={GetType().Assembly.GetName().Version}, " +
                                  $"administrator={isAdministrator}, administratorRequested={settings.RunAsAdministrator}, " +
                                  $"silent={settings.SilentStart}, enabled={settings.Enabled}");
        if (settings.RunAsAdministrator && !isAdministrator)
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
                ErrorLog.WriteInfo("App", "Elevated child process started; exiting bootstrap process.");
                Exit();
                return;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                // The user declined the UAC prompt; continue without elevation.
                ErrorLog.WriteWarning("App", "UAC elevation was cancelled; continuing without elevation.");
            }
            catch (Exception exception)
            {
                ErrorLog.Write(exception, "App.Elevation");
            }
        }

        _mutex = new Mutex(true, "Local\\BA.Pointer.Singleton.WinUI", out var createdNew);
        if (!createdNew)
        {
            ErrorLog.WriteWarning("App", "Duplicate instance detected; exiting new process.");
            Exit();
            return;
        }

        _window = new MainWindow();
        ErrorLog.WriteInfo("App", $"Main window created. silent={settings.SilentStart}");
        if (!settings.SilentStart) _window.Activate();
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Shutdown()
    {
        ErrorLog.WriteInfo("App", "Application shutdown requested.");
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _mutex = null;
        Exit();
    }
}

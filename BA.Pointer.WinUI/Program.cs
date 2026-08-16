using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using BA.Pointer.Services;

namespace BA.Pointer;

/// <summary>
/// Explicit WinUI entry point. The generated XAML entry point starts WinUI too
/// early for a framework-dependent single-file deployment: Windows App SDK needs
/// the extracted application directory in MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY
/// before Application.Start is called.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if BA_POINTER_FRAMEWORK_DEPENDENT_ENTRYPOINT
        // The framework-dependent single-file host does not set this variable
        // itself. The self-contained host does, using its extraction directory;
        // overwriting it there would make WinUI unable to find ThemeResources.
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);
#endif

        // Elevate and repair single-file resources before WinUI/XAML is initialized.
        if (TryStartElevated(args)) return;
        EnsureSingleFileResourceAlias();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
        });
    }

    private static bool TryStartElevated(string[] args)
    {
        var settings = new SettingsStore().Load();
        var isAdministrator = IsRunningAsAdministrator();
        ErrorLog.WriteInfo("App",
            $"Bootstrap requested. administrator={isAdministrator}, " +
            $"administratorRequested={settings.RunAsAdministrator}, args={args.Length}");
        if (!settings.RunAsAdministrator || isAdministrator) return false;

        try
        {
            var executable = Environment.ProcessPath ??
                             throw new InvalidOperationException("无法确定程序路径。");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
            Process.Start(startInfo);
            ErrorLog.WriteInfo("App", "Elevated child started before WinUI initialization.");
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            ErrorLog.WriteWarning("App", "UAC elevation was cancelled; continuing without elevation.");
            return false;
        }
        catch (Exception exception)
        {
            ErrorLog.Write(exception, "App.Elevation");
            return false;
        }
    }

    internal static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void EnsureSingleFileResourceAlias()
    {
        var extractionDirectory = Environment.GetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY");
        var executablePath = Environment.ProcessPath;
        var assemblyName = typeof(Program).Assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(extractionDirectory) ||
            string.IsNullOrWhiteSpace(executablePath) ||
            string.IsNullOrWhiteSpace(assemblyName) ||
            !Directory.Exists(extractionDirectory))
            return;

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.Equals(executableName, assemblyName, StringComparison.OrdinalIgnoreCase)) return;

        var source = Path.Combine(extractionDirectory, $"{assemblyName}.pri");
        var target = Path.Combine(extractionDirectory, $"{executableName}.pri");
        if (!File.Exists(source) || File.Exists(target)) return;

        try
        {
            File.Copy(source, target);
            ErrorLog.WriteInfo("App", $"Created single-file PRI alias. target={Path.GetFileName(target)}");
        }
        catch (IOException)
        {
            // Another process may have created the alias concurrently.
        }
        catch (Exception exception)
        {
            ErrorLog.Write(exception, "App.SingleFileResourceAlias");
        }
    }
}

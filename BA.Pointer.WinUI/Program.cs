using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
        });
    }
}

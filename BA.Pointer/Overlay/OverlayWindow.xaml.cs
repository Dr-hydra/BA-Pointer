using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BA.Pointer.Interop;
using BA.Pointer.Models;
using Point = System.Windows.Point;

namespace BA.Pointer.Overlay;

public partial class OverlayWindow : Window
{
    private IntPtr _hwnd;

    public OverlayWindow(PointerSettings settings, string cursorImagePath)
    {
        InitializeComponent();
        Surface.Configure(settings, cursorImagePath);
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WindowHook);
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    public void Start() => Surface.Start();
    public void Stop() => Surface.Stop();
    public void ApplySettings(PointerSettings settings, string cursorImagePath) => Surface.Configure(settings, cursorImagePath);

    public Point ScreenToLocal(int screenX, int screenY)
    {
        if (_hwnd == IntPtr.Zero) return new Point(screenX, screenY);
        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        NativeMethods.ScreenToClient(_hwnd, ref point);
        var dpi = VisualTreeHelper.GetDpi(Surface);
        return new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY);
    }

    protected override void OnClosed(EventArgs e)
    {
        Surface.Stop();
        base.OnClosed(e);
    }
}

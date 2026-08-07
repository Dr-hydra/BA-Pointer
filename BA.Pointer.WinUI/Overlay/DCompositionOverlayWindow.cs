using System.ComponentModel;
using System.Runtime.InteropServices;
using BA.Pointer.Interop;
using BA.Pointer.Models;
using BA.Pointer.Services;
using Microsoft.UI.Dispatching;

namespace BA.Pointer.Overlay;

public sealed class DCompositionOverlayWindow : IDisposable
{
    private const string WindowClassName = "BA.Pointer.DCompOverlay";
    private static readonly object RegistrationLock = new();
    private static readonly NativeMethods.WindowProc WindowProcedure = WndProc;
    private static ushort _windowClass;

    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher;
    private readonly DCompositionRenderer _renderer;
    private PointerSettings _settings;
    private IntPtr _hwnd;
    private long _inputEvents;
    private DateTime _nextHeartbeatUtc = DateTime.UtcNow;
    private bool _tickFaulted;

    public DCompositionOverlayWindow(DispatcherQueue dispatcher, PointerSettings settings)
    {
        EnsureWindowClass();
        _dispatcher = dispatcher;
        _settings = settings;
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_NOREDIRECTIONBITMAP | NativeMethods.WS_EX_LAYERED,
            WindowClassName, "BA Pointer Effects", NativeMethods.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 DirectComposition 覆盖窗口。");
        if (!NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置覆盖窗口的点击穿透属性。");

        _renderer = new DCompositionRenderer(_hwnd);
        _renderer.Configure(settings);
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        UpdateTimerInterval();
    }

    public bool IsRunning => _renderer.IsRunning;

    public void Start()
    {
        _tickFaulted = false;
        _renderer.Start();
        UpdateTimerInterval();
        _timer.Start();
        _nextHeartbeatUtc = DateTime.UtcNow;
        ErrorLog.WriteInfo("Overlay", $"Started. hwnd=0x{_hwnd.ToInt64():X}, intervalMs={_timer.Interval.TotalMilliseconds:0.###}");
    }

    public void Stop()
    {
        _timer.Stop();
        _renderer.Stop();
        ErrorLog.WriteInfo("Overlay", $"Stopped. tickFaulted={_tickFaulted}, inputEvents={Interlocked.Read(ref _inputEvents)}");
    }

    public void Configure(PointerSettings settings)
    {
        _settings = settings;
        _renderer.Configure(settings);
        UpdateTimerInterval();
    }

    public void SetPointerState(PointerMouseButton button, bool isDown, int x, int y)
    {
        Interlocked.Increment(ref _inputEvents);
        if (!_dispatcher.TryEnqueue(() => _renderer.SetPointerState(button, isDown, x, y)))
            ErrorLog.WriteWarning("Overlay", "Dispatcher rejected a pointer event.");
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            _renderer.Tick();
            if (DateTime.UtcNow < _nextHeartbeatUtc) return;
            _nextHeartbeatUtc = DateTime.UtcNow.AddMinutes(1);
            var cloakResult = NativeMethods.DwmGetWindowAttributeInt(
                _hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
            ErrorLog.WriteInfo("Overlay", $"Heartbeat. hwndValid={NativeMethods.IsWindow(_hwnd)}, " +
                                          $"visible={NativeMethods.IsWindowVisible(_hwnd)}, cloaked={cloaked}, " +
                                          $"cloakResult=0x{unchecked((uint)cloakResult):X8}, timerRunning={sender.IsRunning}, " +
                                          $"inputEvents={Interlocked.Read(ref _inputEvents)}, " +
                                          _renderer.GetDiagnosticState());
        }
        catch (Exception exception)
        {
            sender.Stop();
            _tickFaulted = true;
            ErrorLog.Write(exception, "Overlay.Tick");
        }
    }

    private void UpdateTimerInterval() =>
        _timer.Interval = TimeSpan.FromMilliseconds(1000d / Math.Clamp(_settings.FrameRate, 30, 240));

    private static void EnsureWindowClass()
    {
        if (_windowClass != 0) return;
        lock (RegistrationLock)
        {
            if (_windowClass != 0) return;
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = WindowClassName
            };
            _windowClass = NativeMethods.RegisterClassEx(ref windowClass);
            if (_windowClass == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册覆盖窗口类。");
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_NCHITTEST) return new IntPtr(NativeMethods.HTTRANSPARENT);
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _renderer.Dispose();
        if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }
}

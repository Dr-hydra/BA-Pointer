using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<IntPtr, WeakReference<DCompositionOverlayWindow>> WindowOwners = new();
    private static ushort _windowClass;

    private readonly record struct MonitorDescriptor(
        IntPtr Handle, int Left, int Top, int Width, int Height, bool IsPrimary)
    {
        public string Key => $"{Handle.ToInt64():X}:{Left},{Top},{Width}x{Height}:{IsPrimary}";
        public override string ToString() =>
            $"0x{Handle.ToInt64():X}@{Left},{Top},{Width}x{Height}{(IsPrimary ? ":primary" : string.Empty)}";
    }

    private sealed class MonitorSurface
    {
        public required MonitorDescriptor Monitor { get; init; }
        public required IntPtr Hwnd { get; init; }
        public required uint Dpi { get; init; }
        public required DCompositionRenderer Renderer { get; init; }
    }

    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<MonitorSurface> _surfaces = new();
    private PointerSettings _settings;
    private long _inputEvents;
    private DateTime _nextHeartbeatUtc = DateTime.UtcNow;
    private DateTime _nextTopologyCheckUtc = DateTime.MinValue;
    private string _topologyKey = string.Empty;
    private bool _topologyDirty;
    private bool _tickFaulted;
    private bool _running;
    private bool _disposed;

    public DCompositionOverlayWindow(DispatcherQueue dispatcher, PointerSettings settings)
    {
        EnsureWindowClass();
        _dispatcher = dispatcher;
        _settings = settings;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        UpdateTimerInterval();

        var monitors = EnumerateMonitors();
        RebuildSurfaces(monitors, "initialization");
    }

    public bool IsRunning => _running && _surfaces.Any(surface => surface.Renderer.IsRunning);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tickFaulted = false;
        _running = true;
        EnsureCurrentTopology("start");
        foreach (var surface in _surfaces) surface.Renderer.Start();
        UpdateTimerInterval();
        _timer.Start();
        _nextHeartbeatUtc = DateTime.UtcNow;
        _nextTopologyCheckUtc = DateTime.UtcNow.AddSeconds(1);
        ErrorLog.WriteInfo("Overlay",
            $"Started. monitorCount={_surfaces.Count}, intervalMs={_timer.Interval.TotalMilliseconds:0.###}");
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
        foreach (var surface in _surfaces) surface.Renderer.Stop();
        ErrorLog.WriteInfo("Overlay",
            $"Stopped. monitorCount={_surfaces.Count}, tickFaulted={_tickFaulted}, " +
            $"inputEvents={Interlocked.Read(ref _inputEvents)}");
    }

    public void Configure(PointerSettings settings)
    {
        _settings = settings;
        foreach (var surface in _surfaces) surface.Renderer.Configure(settings);
        UpdateTimerInterval();
    }

    public void SetPointerState(PointerMouseButton button, bool isDown, int x, int y)
    {
        var eventSequence = Interlocked.Increment(ref _inputEvents);
        if (!_dispatcher.TryEnqueue(() =>
            {
                foreach (var surface in _surfaces)
                    surface.Renderer.SetPointerState(button, isDown, x, y, eventSequence);
            }))
            ErrorLog.WriteWarning("Overlay", "Dispatcher rejected a pointer event.");
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            if (_topologyDirty || utcNow >= _nextTopologyCheckUtc)
            {
                _nextTopologyCheckUtc = utcNow.AddSeconds(1);
                EnsureCurrentTopology(_topologyDirty ? "display notification" : "topology poll");
            }

            foreach (var surface in _surfaces) surface.Renderer.Tick();
            if (utcNow < _nextHeartbeatUtc) return;
            _nextHeartbeatUtc = utcNow.AddMinutes(1);
            WriteHeartbeat(sender);
        }
        catch (Exception exception)
        {
            sender.Stop();
            _tickFaulted = true;
            ErrorLog.Write(exception, "Overlay.Tick");
        }
    }

    private void EnsureCurrentTopology(string reason)
    {
        var monitors = EnumerateMonitors();
        var topologyKey = GetTopologyKey(monitors);
        var dpiChanged = _surfaces.Any(surface =>
        {
            var dpi = NativeMethods.GetDpiForWindow(surface.Hwnd);
            return dpi != 0 && dpi != surface.Dpi;
        });
        if (!_topologyDirty && !dpiChanged && topologyKey == _topologyKey) return;

        RebuildSurfaces(monitors, dpiChanged ? $"{reason}, dpi changed" : reason);
    }

    private void RebuildSurfaces(IReadOnlyList<MonitorDescriptor> monitors, string reason)
    {
        var wasRunning = _running;
        foreach (var surface in _surfaces) DisposeSurface(surface);
        _surfaces.Clear();

        var randomSeed = Random.Shared.Next();
        try
        {
            foreach (var monitor in monitors)
            {
                var surface = CreateSurface(monitor, randomSeed);
                _surfaces.Add(surface);
                if (wasRunning) surface.Renderer.Start();
            }

            _topologyKey = GetTopologyKey(monitors);
            _topologyDirty = false;
            ErrorLog.WriteInfo("Overlay",
                $"Display surfaces rebuilt. reason={reason}, count={_surfaces.Count}, " +
                $"monitors=[{string.Join("; ", _surfaces.Select(DescribeSurface))}]");
        }
        catch (Exception exception)
        {
            foreach (var surface in _surfaces) DisposeSurface(surface);
            _surfaces.Clear();
            _topologyKey = string.Empty;
            _topologyDirty = true;
            _nextTopologyCheckUtc = DateTime.UtcNow.AddSeconds(5);
            ErrorLog.Write(exception, "Overlay.RebuildSurfaces");
            if (!wasRunning) throw;
        }
    }

    private MonitorSurface CreateSurface(MonitorDescriptor monitor, int randomSeed)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_NOREDIRECTIONBITMAP | NativeMethods.WS_EX_LAYERED,
            WindowClassName, "BA Pointer Effects", NativeMethods.WS_POPUP,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 DirectComposition 覆盖窗口。");

        WindowOwners[hwnd] = new WeakReference<DCompositionOverlayWindow>(this);
        DCompositionRenderer? renderer = null;
        try
        {
            if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置覆盖窗口的点击穿透属性。");

            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            renderer = new DCompositionRenderer(hwnd, monitor.Left, monitor.Top,
                monitor.Width, monitor.Height, dpi, randomSeed);
            renderer.Configure(_settings);
            return new MonitorSurface { Monitor = monitor, Hwnd = hwnd, Dpi = dpi, Renderer = renderer };
        }
        catch
        {
            renderer?.Dispose();
            WindowOwners.TryRemove(hwnd, out _);
            NativeMethods.DestroyWindow(hwnd);
            throw;
        }
    }

    private static IReadOnlyList<MonitorDescriptor> EnumerateMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        NativeMethods.MonitorEnumProc callback = (
            IntPtr monitor, IntPtr monitorDc, ref NativeMethods.RECT monitorRect, IntPtr data) =>
        {
            var monitorInfo = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            var bounds = NativeMethods.GetMonitorInfo(monitor, ref monitorInfo)
                ? monitorInfo.rcMonitor
                : monitorRect;
            monitors.Add(new MonitorDescriptor(
                monitor,
                bounds.Left,
                bounds.Top,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top),
                (monitorInfo.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || monitors.Count == 0)
        {
            var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            monitors.Add(new MonitorDescriptor(
                IntPtr.Zero,
                left,
                top,
                Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN)),
                Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)),
                true));
        }

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Top)
            .ThenBy(monitor => monitor.Left)
            .ToArray();
    }

    private void WriteHeartbeat(DispatcherQueueTimer timer)
    {
        var surfaceStates = new List<string>(_surfaces.Count);
        foreach (var surface in _surfaces)
        {
            var cloakResult = NativeMethods.DwmGetWindowAttributeInt(
                surface.Hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
            surfaceStates.Add(
                $"hwnd=0x{surface.Hwnd.ToInt64():X}, valid={NativeMethods.IsWindow(surface.Hwnd)}, " +
                $"visible={NativeMethods.IsWindowVisible(surface.Hwnd)}, cloaked={cloaked}, " +
                $"cloakResult=0x{unchecked((uint)cloakResult):X8}, {surface.Renderer.GetDiagnosticState()}");
        }

        ErrorLog.WriteInfo("Overlay",
            $"Heartbeat. timerRunning={timer.IsRunning}, inputEvents={Interlocked.Read(ref _inputEvents)}, " +
            $"monitorCount={_surfaces.Count}, surfaces=[{string.Join("; ", surfaceStates)}]");
    }

    private static string DescribeSurface(MonitorSurface surface) =>
        $"{surface.Monitor},dpi={surface.Dpi},hwnd=0x{surface.Hwnd.ToInt64():X}";

    private static string GetTopologyKey(IEnumerable<MonitorDescriptor> monitors) =>
        string.Join("|", monitors.Select(monitor => monitor.Key));

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
            if (_windowClass == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册覆盖窗口类。");
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_NCHITTEST) return new IntPtr(NativeMethods.HTTRANSPARENT);
        if ((message == NativeMethods.WM_DISPLAYCHANGE || message == NativeMethods.WM_DPICHANGED) &&
            WindowOwners.TryGetValue(hwnd, out var ownerReference) &&
            ownerReference.TryGetTarget(out var owner))
            owner._topologyDirty = true;
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void DisposeSurface(MonitorSurface surface)
    {
        WindowOwners.TryRemove(surface.Hwnd, out _);
        surface.Renderer.Dispose();
        if (surface.Hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(surface.Hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _timer.Stop();
        _timer.Tick -= OnTick;
        foreach (var surface in _surfaces) DisposeSurface(surface);
        _surfaces.Clear();
        GC.SuppressFinalize(this);
    }
}

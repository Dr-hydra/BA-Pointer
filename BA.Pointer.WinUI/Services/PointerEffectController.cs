using BA.Pointer.Models;
using BA.Pointer.Overlay;
using Microsoft.UI.Dispatching;

namespace BA.Pointer.Services;

public sealed class PointerEffectController : IDisposable
{
    private readonly CursorInstaller _cursorInstaller;
    private readonly DispatcherQueue _dispatcher;
    private DCompositionOverlayWindow? _overlay;
    private MouseHook? _mouseHook;
    private bool _cursorApplied;
    private bool _pauseWhenCursorHidden = true;
    private readonly HashSet<PointerMouseButton> _forwardedButtons = new();
    private long _hiddenCursorPresses;

    public PointerEffectController(CursorInstaller cursorInstaller, DispatcherQueue dispatcher)
    {
        _cursorInstaller = cursorInstaller;
        _dispatcher = dispatcher;
    }

    public bool IsRunning => _overlay?.IsRunning == true;
    public event Action<bool>? StateChanged;

    public void Start(PointerSettings settings, string cursorImagePath)
    {
        if (IsRunning)
        {
            ErrorLog.WriteInfo("Controller", "Start requested while running; applying settings to existing pipeline.");
            ApplySettings(settings, cursorImagePath);
            return;
        }
        try
        {
            _pauseWhenCursorHidden = settings.PauseWhenCursorHidden;
            Interlocked.Exchange(ref _hiddenCursorPresses, 0);
            ErrorLog.WriteInfo("Controller", $"Starting. reuseOverlay={_overlay is not null}, frameRate={settings.FrameRate}, target={settings.Target}");
            if (settings.UseSystemCursor)
            {
                _cursorInstaller.Install(cursorImagePath);
                _cursorApplied = true;
            }
            else
            {
                // Clear a cursor left behind by an interrupted previous run,
                // even though this process never marked it as applied.
                _cursorInstaller.Restore();
                _cursorApplied = false;
            }
            _overlay ??= new DCompositionOverlayWindow(_dispatcher, settings);
            _overlay.Configure(settings);
            _overlay.Start();
            _mouseHook ??= new MouseHook();
            _mouseHook.MouseButtonChanged -= OnMouseButtonChanged;
            _mouseHook.MouseButtonChanged += OnMouseButtonChanged;
            _mouseHook.Start();
            ErrorLog.WriteInfo("Controller", "Started successfully.");
            StateChanged?.Invoke(true);
        }
        catch (Exception exception)
        {
            ErrorLog.Write(exception, "Controller.Start");
            Stop();
            throw;
        }
    }

    public void ApplySettings(PointerSettings settings, string cursorImagePath)
    {
        _pauseWhenCursorHidden = settings.PauseWhenCursorHidden;
        _overlay?.Configure(settings);
        if (settings.UseSystemCursor)
        {
            _cursorInstaller.Install(cursorImagePath);
            _cursorApplied = true;
        }
        else
        {
            if (!_cursorInstaller.Restore())
                throw new InvalidOperationException("无法恢复系统光标，请重试。");
            _cursorApplied = false;
        }
    }

    public void Stop()
    {
        ErrorLog.WriteInfo("Controller", $"Stopping. overlayExists={_overlay is not null}, " +
                                         $"hookEvents={_mouseHook?.EventCount ?? 0}, " +
                                         $"hiddenCursorPresses={Interlocked.Read(ref _hiddenCursorPresses)}");
        if (_mouseHook is not null)
        {
            _mouseHook.MouseButtonChanged -= OnMouseButtonChanged;
            _mouseHook.Dispose();
            _mouseHook = null;
        }
        _forwardedButtons.Clear();
        var overlay = _overlay;
        _overlay = null;
        overlay?.Dispose();
        if (_cursorApplied) { _cursorInstaller.Restore(); _cursorApplied = false; }
        StateChanged?.Invoke(false);
    }

    private void OnMouseButtonChanged(PointerMouseButton button, bool isDown, int x, int y)
    {
        if (isDown)
        {
            if (_pauseWhenCursorHidden && !CursorVisibility.IsVisibleOrUnknown())
            {
                Interlocked.Increment(ref _hiddenCursorPresses);
                if (_forwardedButtons.Remove(button))
                    _overlay?.SetPointerState(button, false, x, y);
                return;
            }
            _forwardedButtons.Add(button);
            _overlay?.SetPointerState(button, true, x, y);
            return;
        }

        if (_forwardedButtons.Remove(button))
            _overlay?.SetPointerState(button, false, x, y);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

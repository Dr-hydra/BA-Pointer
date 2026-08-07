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

    public PointerEffectController(CursorInstaller cursorInstaller, DispatcherQueue dispatcher)
    {
        _cursorInstaller = cursorInstaller;
        _dispatcher = dispatcher;
    }

    public bool IsRunning => _overlay?.IsRunning == true;
    public event Action<bool>? StateChanged;

    public void Start(PointerSettings settings, string cursorImagePath)
    {
        if (IsRunning) { ApplySettings(settings, cursorImagePath); return; }
        try
        {
            if (settings.UseSystemCursor) { _cursorInstaller.Install(cursorImagePath); _cursorApplied = true; }
            _overlay ??= new DCompositionOverlayWindow(_dispatcher, settings);
            _overlay.Configure(settings);
            _overlay.Start();
            _mouseHook ??= new MouseHook();
            _mouseHook.MouseButtonChanged -= OnMouseButtonChanged;
            _mouseHook.MouseButtonChanged += OnMouseButtonChanged;
            _mouseHook.Start();
            StateChanged?.Invoke(true);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void ApplySettings(PointerSettings settings, string cursorImagePath)
    {
        _overlay?.Configure(settings);
        if (settings.UseSystemCursor) { _cursorInstaller.Install(cursorImagePath); _cursorApplied = true; }
        else if (_cursorApplied) { _cursorInstaller.Restore(); _cursorApplied = false; }
    }

    public void Stop()
    {
        if (_mouseHook is not null)
        {
            _mouseHook.MouseButtonChanged -= OnMouseButtonChanged;
            _mouseHook.Dispose();
            _mouseHook = null;
        }
        _overlay?.Stop();
        if (_cursorApplied) { _cursorInstaller.Restore(); _cursorApplied = false; }
        StateChanged?.Invoke(false);
    }

    private void OnMouseButtonChanged(PointerMouseButton button, bool isDown, int x, int y) =>
        _overlay?.SetPointerState(button, isDown, x, y);

    public void Dispose()
    {
        Stop();
        _overlay?.Dispose();
        _overlay = null;
        GC.SuppressFinalize(this);
    }
}

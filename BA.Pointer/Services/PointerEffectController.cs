using BA.Pointer.Models;
using BA.Pointer.Overlay;

namespace BA.Pointer.Services;

public sealed class PointerEffectController : IDisposable
{
    private readonly CursorInstaller _cursorInstaller;
    private OverlayWindow? _overlay;
    private MouseHook? _mouseHook;
    private bool _cursorApplied;

    public bool IsRunning => _overlay is not null;
    public event Action<bool>? StateChanged;

    public PointerEffectController(CursorInstaller cursorInstaller) => _cursorInstaller = cursorInstaller;

    public void Start(PointerSettings settings, string cursorImagePath)
    {
        if (IsRunning)
        {
            ApplySettings(settings, cursorImagePath);
            return;
        }
        try
        {
            if (settings.UseSystemCursor)
            {
                _cursorInstaller.Install(cursorImagePath);
                _cursorApplied = true;
            }
            _overlay = new OverlayWindow(settings, cursorImagePath);
            _overlay.Show();
            _overlay.Start();
            _mouseHook = new MouseHook();
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
        if (!IsRunning) return;
        _overlay!.ApplySettings(settings, cursorImagePath);
        if (settings.UseSystemCursor)
        {
            _cursorInstaller.Install(cursorImagePath);
            _cursorApplied = true;
        }
        else if (_cursorApplied)
        {
            _cursorInstaller.Restore();
            _cursorApplied = false;
        }
    }

    public void Stop()
    {
        if (_mouseHook is not null)
        {
            _mouseHook.MouseButtonChanged -= OnMouseButtonChanged;
            _mouseHook.Dispose();
            _mouseHook = null;
        }
        if (_overlay is not null)
        {
            _overlay.Stop();
            _overlay.Close();
            _overlay = null;
        }
        if (_cursorApplied)
        {
            _cursorInstaller.Restore();
            _cursorApplied = false;
        }
        StateChanged?.Invoke(false);
    }

    private void OnMouseButtonChanged(PointerMouseButton button, bool isDown, int x, int y) =>
        _overlay?.Surface.SetPointerState(button, isDown, x, y);

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

using System.Runtime.InteropServices;
using BA.Pointer.Interop;

namespace BA.Pointer.Services;

public enum PointerMouseButton { Left, Right, Middle }

public sealed class MouseHook : IDisposable
{
    private NativeMethods.LowLevelMouseProc? _callback;
    private IntPtr _hook;
    public event Action<PointerMouseButton, bool, int, int>? MouseButtonChanged;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("无法安装全局鼠标监听。");
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            (PointerMouseButton Button, bool IsDown)? change = wParam.ToInt32() switch
            {
                NativeMethods.WM_LBUTTONDOWN => (PointerMouseButton.Left, true),
                NativeMethods.WM_LBUTTONUP => (PointerMouseButton.Left, false),
                NativeMethods.WM_RBUTTONDOWN => (PointerMouseButton.Right, true),
                NativeMethods.WM_RBUTTONUP => (PointerMouseButton.Right, false),
                NativeMethods.WM_MBUTTONDOWN => (PointerMouseButton.Middle, true),
                NativeMethods.WM_MBUTTONUP => (PointerMouseButton.Middle, false),
                _ => null
            };
            if (change is not null)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                MouseButtonChanged?.Invoke(change.Value.Button, change.Value.IsDown, data.pt.X, data.pt.Y);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _callback = null;
        GC.SuppressFinalize(this);
    }
}

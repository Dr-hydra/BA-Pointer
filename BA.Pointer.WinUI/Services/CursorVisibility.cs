using System.Runtime.InteropServices;
using BA.Pointer.Interop;

namespace BA.Pointer.Services;

internal static class CursorVisibility
{
    public static bool IsVisibleOrUnknown()
    {
        var info = new NativeMethods.CURSORINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.CURSORINFO>()
        };
        return !NativeMethods.GetCursorInfo(ref info) ||
               (info.flags & NativeMethods.CURSOR_SHOWING) != 0;
    }
}

using System.Text.Json;
using System.Windows.Media.Imaging;
using BA.Pointer.Interop;

namespace BA.Pointer.Services;

public sealed class CursorInstaller
{
    private readonly SettingsStore _store;

    public CursorInstaller(SettingsStore store) => _store = store;

    public string EnsureCursorFile(string pngPath)
    {
        Directory.CreateDirectory(_store.DataDirectory);
        if (!File.Exists(pngPath)) throw new FileNotFoundException("找不到光标素材", pngPath);
        CreateCursorFile(pngPath, _store.CursorPath, 2, 2);
        return _store.CursorPath;
    }

    public void Install(string pngPath)
    {
        var cursorPath = EnsureCursorFile(pngPath);
        var keyPath = @"Control Panel\Cursors";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("无法访问当前用户光标设置。");
        if (!File.Exists(_store.CursorBackupPath))
        {
            var original = key.GetValue("Arrow") as string ?? string.Empty;
            if (IsManagedCursorPath(original)) original = string.Empty;
            File.WriteAllText(_store.CursorBackupPath, JsonSerializer.Serialize(new CursorBackup(original)));
        }
        key.SetValue("Arrow", cursorPath);
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE))
            throw new InvalidOperationException("无法应用系统光标设置。");
    }

    public bool Restore()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", writable: true);
            if (key is null) return false;

            var current = key.GetValue("Arrow") as string ?? string.Empty;
            var backupExists = File.Exists(_store.CursorBackupPath);
            string? restorePath = null;

            if (backupExists)
            {
                var backup = JsonSerializer.Deserialize<CursorBackup>(File.ReadAllText(_store.CursorBackupPath));
                if (backup is null) return false;
                restorePath = backup.ArrowPath ?? string.Empty;
                if (IsManagedCursorPath(restorePath)) restorePath = string.Empty;
            }
            else if (IsManagedCursorPath(current))
            {
                restorePath = string.Empty;
            }
            else
            {
                return true;
            }

            key.SetValue("Arrow", restorePath);
            if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE))
                return false;

            if (backupExists) File.Delete(_store.CursorBackupPath);
            return true;
        }
        catch
        {
            // Restore is best effort during shutdown; explicit apply paths can
            // inspect the return value and report a failure to the user.
            return false;
        }
    }

    private bool IsManagedCursorPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(path.Trim(), _store.CursorPath, StringComparison.OrdinalIgnoreCase);

    private static void CreateCursorFile(string pngPath, string outputPath, int hotspotX, int hotspotY)
    {
        var bytes = File.ReadAllBytes(pngPath);
        var bitmap = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width > 256 || height > 256) throw new InvalidOperationException("光标素材尺寸超过 Windows 光标上限。");
        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((byte)(width == 256 ? 0 : width));
        writer.Write((byte)(height == 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)Math.Clamp(hotspotX, 0, width - 1));
        writer.Write((ushort)Math.Clamp(hotspotY, 0, height - 1));
        writer.Write((uint)bytes.Length);
        writer.Write((uint)22);
        writer.Write(bytes);
    }

    private sealed record CursorBackup(string ArrowPath);
}

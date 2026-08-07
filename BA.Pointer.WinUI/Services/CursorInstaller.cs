using System.Buffers.Binary;
using System.Text.Json;
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
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", writable: true)
            ?? throw new InvalidOperationException("无法访问当前用户光标设置。");
        if (!File.Exists(_store.CursorBackupPath))
        {
            var original = key.GetValue("Arrow") as string ?? string.Empty;
            File.WriteAllText(_store.CursorBackupPath, JsonSerializer.Serialize(new CursorBackup(original)));
        }
        key.SetValue("Arrow", cursorPath);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
    }

    public void Restore()
    {
        if (!File.Exists(_store.CursorBackupPath)) return;
        try
        {
            var backup = JsonSerializer.Deserialize<CursorBackup>(File.ReadAllText(_store.CursorBackupPath));
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", writable: true);
            if (key is null || backup is null) return;
            key.SetValue("Arrow", backup.ArrowPath ?? string.Empty);
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
            File.Delete(_store.CursorBackupPath);
        }
        catch
        {
            // Restoring is best effort during shutdown.
        }
    }

    private static void CreateCursorFile(string pngPath, string outputPath, int hotspotX, int hotspotY)
    {
        var bytes = File.ReadAllBytes(pngPath);
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4e || bytes[3] != 0x47)
            throw new InvalidOperationException("光标素材不是有效的 PNG 文件。");
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
        if (width is < 1 or > 256 || height is < 1 or > 256)
            throw new InvalidOperationException("光标素材尺寸超过 Windows 光标上限。");

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

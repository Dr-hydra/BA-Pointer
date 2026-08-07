using System.Text.Json;
using BA.Pointer.Models;

namespace BA.Pointer.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BA.Pointer");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string CursorPath => Path.Combine(DataDirectory, "BlueArchivePointer.cur");
    public string CursorBackupPath => Path.Combine(DataDirectory, "cursor-backup.json");

    public PointerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<PointerSettings>(File.ReadAllText(SettingsPath), Options) ??
                               new PointerSettings();
                if (settings.SettingsVersion < 3)
                {
                    settings.EffectScale = 0.5;
                    settings.SettingsVersion = 3;
                }
                return settings;
            }
        }
        catch
        {
            // A damaged preference file must not prevent the settings window from opening.
        }
        return new PointerSettings();
    }

    public void Save(PointerSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}

using System.Text.Json.Serialization;

namespace BA.Pointer.Models;

public enum TargetScope
{
    AllDesktop,
    PauseWhenFullscreen
}

public sealed class PointerSettings
{
    public int SettingsVersion { get; set; } = 3;
    public bool Enabled { get; set; }
    public bool UseSystemCursor { get; set; } = true;
    public TargetScope Target { get; set; } = TargetScope.AllDesktop;
    public bool ExcludeEffectsFromCapture { get; set; }
    public int FrameRate { get; set; } = 120;
    public double EffectScale { get; set; } = 0.5;
    public double EffectOpacity { get; set; } = 1.0;
    public double EffectDurationScale { get; set; } = 1.0;
    public double FragmentScale { get; set; } = 1.2;
    public double FragmentTransitionScale { get; set; } = 1.0;
    public double TrailWidthScale { get; set; } = 1.0;
    public double TrailDurationMs { get; set; } = 300.0;
    public bool PersistentTrail { get; set; }
    public double DistanceEmissionScale { get; set; } = 1.0;
    public double BloomRadius { get; set; } = 7.5;
    public double BloomStrength { get; set; } = 0.72;
    public string CursorImagePath { get; set; } = string.Empty;
    public bool StartWithWindows { get; set; }
    public bool SilentStart { get; set; }
    public bool RunAsAdministrator { get; set; }

    [JsonIgnore]
    public string DisplayTarget => Target == TargetScope.AllDesktop ? "全部" : "有应用全屏时暂停";
}

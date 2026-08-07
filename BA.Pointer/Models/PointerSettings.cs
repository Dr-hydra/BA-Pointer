using System.Text.Json.Serialization;

namespace BA.Pointer.Models;

public enum TargetScope
{
    AllDesktop,
    BlueArchiveOnly
}

public sealed class PointerSettings
{
    public bool Enabled { get; set; }
    public bool UseSystemCursor { get; set; } = true;
    public TargetScope Target { get; set; } = TargetScope.AllDesktop;
    public int FrameRate { get; set; } = 120;
    public double EffectScale { get; set; } = 1.0;
    public double EffectOpacity { get; set; } = 1.0;
    public double EffectDurationScale { get; set; } = 1.0;
    public double TrailWidthScale { get; set; } = 1.0;
    public double TrailDurationMs { get; set; } = 300.0;
    public double DistanceEmissionScale { get; set; } = 1.0;
    public string CursorImagePath { get; set; } = string.Empty;
    public bool StartWithWindows { get; set; }

    [JsonIgnore]
    public string DisplayTarget => Target == TargetScope.AllDesktop ? "所有桌面" : "仅 Blue Archive";
}

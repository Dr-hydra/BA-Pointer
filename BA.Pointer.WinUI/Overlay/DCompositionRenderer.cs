using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using BA.Pointer.Interop;
using BA.Pointer.Models;
using BA.Pointer.Services;
using Vortice.Mathematics;
using Color4 = Vortice.Mathematics.Color4;

namespace BA.Pointer.Overlay;

public sealed class DCompositionRenderer : IDisposable
{
    private const double OriginalRingHdrIntensity = 5.992156982421875;
    private const double ReferencePixelsPerUnityUnit = 720;
    private const double GraphicsMaintenanceIntervalMs = 30_000;
    private const double GraphicsRecoveryCooldownMs = 5_000;
    private const int MaximumTrailPoints = 320;

    private sealed record TrailPoint(Vector2 Position, double Time);

    private sealed class Touch
    {
        public required PointerMouseButton Button;
        public bool IsPersistent;
        public Vector2 Position;
        public Vector2 LastPosition;
        public double StartedAt;
        public double? ReleasedAt;
        public double EmissionCarry;
        public List<TrailPoint> Trail { get; } = new();
    }

    private sealed class MeshParticle
    {
        public double StartSize;
        public double InitialRotation;
        public double RotationBlend;
    }

    private sealed class TriangleParticle
    {
        public Vector2 Origin;
        public Vector2 ShapeOffset;
        public Vector2 Direction;
        public double StartedAt;
        public double Lifetime;
        public double Speed;
        public double Size;
        public bool AlternateFrame;
    }

    private sealed class ClickEffect
    {
        public required Vector2 Position;
        public required double StartedAt;
        public List<MeshParticle> MeshParticles { get; } = new();
        public List<TriangleParticle> Triangles { get; } = new();
    }

    private readonly IntPtr _hwnd;
    private readonly string _assetDirectory;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random;
    private readonly Dictionary<PointerMouseButton, Touch> _activeTouches = new();
    private readonly List<Touch> _touches = new();
    private readonly List<ClickEffect> _clickEffects = new();
    private readonly List<TriangleParticle> _moveParticles = new();
    private Touch? _persistentTrail;
    private PointerSettings _settings = new();
    private D3D11EffectPipeline? _pipeline;
    private bool _running;
    private bool _targetActive = true;
    private bool _initialized;
    private bool _lastFrameHadContent = true;
    private double _lastForegroundCheck;
    private double _nextGraphicsMaintenanceAt;
    private double _nextGraphicsRecoveryAt;
    private int _graphicsRecoveryCount;
    private string _lastRecoveryReason = "none";
    private int _originX;
    private int _originY;
    private int _width;
    private int _height;
    private readonly uint _dpi;

    public DCompositionRenderer(IntPtr hwnd, int originX, int originY, int width, int height, uint dpi, int randomSeed)
    {
        _hwnd = hwnd;
        _assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        _originX = originX;
        _originY = originY;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _dpi = dpi == 0 ? 96 : dpi;
        _random = new Random(randomSeed);
        RefreshPlacement();
    }

    public bool IsRunning => _running;

    public string GetDiagnosticState()
    {
        var graphicsState = _pipeline?.GetDiagnosticState() ?? "pipeline=unavailable";
        return $"rendererRunning={_running}, initialized={_initialized}, targetActive={_targetActive}, " +
               $"persistentEnabled={_settings.PersistentTrail}, persistentActive={_persistentTrail is not null}, " +
               $"activeTouches={_activeTouches.Count}, touches={_touches.Count}, " +
               $"clickEffects={_clickEffects.Count}, moveParticles={_moveParticles.Count}, " +
               $"bounds={_originX},{_originY},{_width}x{_height}, dpi={_dpi}, recoveries={_graphicsRecoveryCount}, " +
               $"lastRecovery={_lastRecoveryReason}, {graphicsState}";
    }

    public void Configure(PointerSettings settings)
    {
        _settings = settings;
        if (!_initialized)
        {
            InitializeGraphics();
        }
    }

    public void Start()
    {
        if (!_initialized) InitializeGraphics();
        _running = true;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public void Stop()
    {
        _running = false;
        _activeTouches.Clear();
        _touches.Clear();
        _clickEffects.Clear();
        _moveParticles.Clear();
        _persistentTrail = null;
        if (_initialized)
        {
            _lastFrameHadContent = true;
            RenderFrame(_clock.Elapsed.TotalMilliseconds);
        }
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    public void SetPointerState(PointerMouseButton button, bool isDown, int screenX, int screenY, long eventSequence)
    {
        if (!_running) return;
        var position = new Vector2(screenX - _originX, screenY - _originY);
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_settings.PersistentTrail)
        {
            if (isDown)
                SpawnClickEffect(position, now,
                    new Random(CreateClickSeed(eventSequence, screenX, screenY, button)));
            return;
        }
        if (isDown)
        {
            if (_activeTouches.TryGetValue(button, out var previous)) ReleaseTouch(previous, now, position);
            var touch = new Touch
            {
                Button = button,
                Position = position,
                LastPosition = position,
                StartedAt = now
            };
            touch.Trail.Add(new TrailPoint(position, now));
            _activeTouches[button] = touch;
            _touches.Add(touch);
            SpawnClickEffect(position, now, new Random(CreateClickSeed(eventSequence, screenX, screenY, button)));
        }
        else if (_activeTouches.Remove(button, out var released))
        {
            UpdateTouchPosition(released, position, now);
            ReleaseTouch(released, now, position);
        }
    }

    public void Tick()
    {
        if (!_running) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        if (!_initialized && !TryRecoverGraphics("pipeline unavailable", null, now)) return;
        MaintainGraphics(now);
        if (!_initialized) return;

        UpdateTargetState(now);
        if (!_targetActive)
        {
            _activeTouches.Clear();
            _touches.Clear();
            _clickEffects.Clear();
            _moveParticles.Clear();
            _persistentTrail = null;
            RenderFrameSafely(now);
            return;
        }

        if ((_settings.PersistentTrail || _activeTouches.Count > 0) && NativeMethods.GetCursorPos(out var cursor))
        {
            var position = new Vector2(cursor.X - _originX, cursor.Y - _originY);
            if (_settings.PersistentTrail)
            {
                _persistentTrail ??= CreatePersistentTrail(position, now);
                UpdateTouchPosition(_persistentTrail, position, now);
            }
            else if (_persistentTrail is not null)
            {
                ReleaseTouch(_persistentTrail, now, position);
                _persistentTrail = null;
            }

            foreach (var touch in _activeTouches.Values)
                UpdateTouchPosition(touch, position, now);
        }
        else if (!_settings.PersistentTrail && _persistentTrail is not null)
        {
            ReleaseTouch(_persistentTrail, now, _persistentTrail.Position);
            _persistentTrail = null;
        }
        PruneExpired(now);
        RenderFrameSafely(now);
    }

    private void InitializeGraphics()
    {
        RefreshPlacement();
        var pipeline = new D3D11EffectPipeline(_hwnd, _assetDirectory, _width, _height);
        try
        {
            _pipeline = pipeline;
            _initialized = true;
            _lastFrameHadContent = true;
            _nextGraphicsMaintenanceAt = _clock.Elapsed.TotalMilliseconds + GraphicsMaintenanceIntervalMs;
            RenderFrame(_clock.Elapsed.TotalMilliseconds);
        }
        catch
        {
            _pipeline = null;
            _initialized = false;
            try { pipeline.Dispose(); }
            catch { }
            throw;
        }
    }

    private void MaintainGraphics(double now)
    {
        if (now < _nextGraphicsMaintenanceAt || _pipeline is null) return;
        _nextGraphicsMaintenanceAt = now + GraphicsMaintenanceIntervalMs;

        try
        {
            if (!NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _originX, _originY, _width, _height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to refresh overlay window placement.");

            _pipeline.RefreshCompositionBinding();
        }
        catch (Exception exception)
        {
            TryRecoverGraphics("composition maintenance failed", exception, now);
        }
    }

    private void RenderFrameSafely(double now)
    {
        try
        {
            RenderFrame(now);
            if (_pipeline?.NeedsRecovery == true)
                throw new InvalidOperationException("DXGI Present remained occluded for three consecutive frames.");
        }
        catch (Exception exception)
        {
            TryRecoverGraphics("rendering failed", exception, now);
        }
    }

    private bool TryRecoverGraphics(string reason, Exception? cause, double now)
    {
        if (now < _nextGraphicsRecoveryAt) return false;
        _nextGraphicsRecoveryAt = now + GraphicsRecoveryCooldownMs;
        if (cause is not null) ErrorLog.Write(cause, "Renderer.RecoveryTrigger");
        ErrorLog.WriteWarning("Renderer", $"Rebuilding graphics pipeline. reason={reason}");

        var previousPipeline = _pipeline;
        _pipeline = null;
        _initialized = false;
        try
        {
            previousPipeline?.Dispose();
        }
        catch (Exception exception)
        {
            ErrorLog.Write(exception, "Renderer.DisposeFailedPipeline");
        }

        try
        {
            InitializeGraphics();
            _graphicsRecoveryCount++;
            _lastRecoveryReason = reason.Replace(' ', '_');
            ErrorLog.WriteInfo("Renderer", $"Graphics pipeline recovered. count={_graphicsRecoveryCount}, reason={reason}");
            return true;
        }
        catch (Exception exception)
        {
            _pipeline = null;
            _initialized = false;
            ErrorLog.Write(exception, "Renderer.RecoveryFailed");
            return false;
        }
    }

    private void RefreshPlacement()
    {
        if (!NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _originX, _originY, _width, _height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to place the monitor overlay window.");
    }

    private void RenderFrame(double now)
    {
        if (!_initialized || _pipeline is null) return;
        var hasContent = HasVisibleContent(now);
        if (!hasContent && !_lastFrameHadContent) return;

        _pipeline.BeginScene();
        DrawTrails(now);
        foreach (var effect in _clickEffects) DrawCentralFlash(effect, now);
        foreach (var effect in _clickEffects)
        {
            foreach (var particle in effect.MeshParticles) DrawMeshRing(effect, particle, now);
        }

        _pipeline.BeginForeground();
        foreach (var particle in _moveParticles) DrawTriangle(particle, now);
        foreach (var effect in _clickEffects)
        foreach (var triangle in effect.Triangles)
            DrawTriangle(triangle, now);

        _pipeline.Present(
            (float)Math.Clamp(_settings.BloomRadius, 0, 40),
            (float)Math.Clamp(_settings.BloomStrength, 0, 1.5),
            _clickEffects.Count > 0 || _touches.Any(touch => ShouldDrawTrail(touch) && touch.Trail.Count > 1));
        _lastFrameHadContent = hasContent;
    }

    private void DrawTrails(double now)
    {
        var opacity = Math.Clamp(_settings.EffectOpacity, 0, 1);
        var scale = Math.Clamp(_settings.EffectScale, 0.1, 5);
        var lifetime = Math.Clamp(_settings.TrailDurationMs, 20, 2000);
        var coreWidth = Math.Max(0.5,
            0.005 * GetPixelsPerUnityUnit() * scale * Math.Clamp(_settings.TrailWidthScale, 0.1, 10));
        Span<Vector2> path = stackalloc Vector2[MaximumTrailPoints];
        Span<float> ages = stackalloc float[MaximumTrailPoints];
        foreach (var touch in _touches)
        {
            if (!ShouldDrawTrail(touch)) continue;
            var count = Math.Min(touch.Trail.Count, MaximumTrailPoints);
            if (count < 2) continue;
            var start = touch.Trail.Count - count;
            for (var i = 0; i < count; i++)
            {
                var point = touch.Trail[start + i];
                path[i] = point.Position;
                ages[i] = (float)Math.Clamp((now - point.Time) / lifetime, 0, 1);
            }

            _pipeline!.DrawTrail(path[..count], ages[..count], (float)coreWidth,
                TrailColor(0), (float)opacity, 23.9686279f);
        }
    }

    private void DrawCentralFlash(ClickEffect effect, double now)
    {
        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5);
        var progress = (now - effect.StartedAt) / (200 * durationScale);
        if (progress is < 0 or > 1) return;

        var curve = RingSizeCurve(progress);
        var diameter = 0.12 * 2 * curve * GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5);
        var fade = progress <= 0.1088 ? 1 : 1 - (progress - 0.1088) / 0.8912;
        const double blueTime = 7903d / 65535d;
        var blue = new Color4(0.24056602f, 0.39061815f, 1, 1);
        var color = progress < blueTime
            ? LerpColor(new Color4(1, 1, 1, 1), blue, progress / blueTime)
            : blue;
        var opacity = fade * Math.Clamp(_settings.EffectOpacity, 0, 1);
        _pipeline!.DrawSprite(EffectTexture.Circle, effect.Position, (float)(diameter * 1.34),
            (float)(diameter * 1.34), 0, color, (float)opacity, emission: 2);
    }

    private void DrawMeshRing(ClickEffect effect, MeshParticle particle, double now)
    {
        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5);
        var progress = (now - effect.StartedAt) / (600 * durationScale);
        if (progress is < 0 or > 1) return;

        var scale = particle.StartSize * MeshTriSizeCurve(progress) * GetPixelsPerUnityUnit() *
                    Math.Clamp(_settings.EffectScale, 0.1, 5);
        var rotation = particle.InitialRotation - MeshTriRotationDelta(progress, particle.RotationBlend);
        var color = MeshTriColor(progress);
        var opacity = Math.Clamp(_settings.EffectOpacity * 0.92, 0, 1);
        var threshold = MeshTriDissolveThreshold(progress);
        _pipeline!.DrawRing(effect.Position, (float)scale, (float)rotation, color, (float)opacity,
            (float)threshold, (float)OriginalRingHdrIntensity);
    }

    private void DrawTriangle(TriangleParticle particle, double now)
    {
        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5);
        var simulatedAge = (now - particle.StartedAt) / 1000 / durationScale;
        var progress = simulatedAge / particle.Lifetime;
        if (progress is < 0 or > 1) return;

        var pixels = GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5);
        var position = GetTrianglePosition(particle, simulatedAge, pixels);
        var size = particle.Size * pixels * 0.3078824 * TriangleSizeCurve(progress) *
                   Math.Clamp(_settings.FragmentScale, 0.5, 2.5);
        var opacity = Math.Clamp(_settings.EffectOpacity * TriangleOpacity(progress), 0, 1);
        const float halfTexelX = 0.5f / 256f;
        const float halfTexelY = 0.5f / 128f;
        var uv = particle.AlternateFrame
            ? new Vector4(0.5f + halfTexelX, halfTexelY, 1 - halfTexelX, 1 - halfTexelY)
            : new Vector4(halfTexelX, halfTexelY, 0.5f - halfTexelX, 1 - halfTexelY);
        _pipeline!.DrawSprite(EffectTexture.Triangle, position, (float)size, (float)size, 0,
            TriangleColor(progress, _settings.FragmentTransitionScale), (float)opacity, uv, false, 1.86f);
    }

    private void SpawnClickEffect(Vector2 position, double now, Random random)
    {
        var effect = new ClickEffect { Position = position, StartedAt = now };
        for (var i = 0; i < 2; i++)
        {
            effect.MeshParticles.Add(new MeshParticle
            {
                StartSize = RandomRange(random, 0.12, 0.14),
                InitialRotation = RandomRange(random, 0, Math.PI * 2),
                RotationBlend = random.NextDouble()
            });
        }
        var density = Math.Clamp(_settings.DistanceEmissionScale, 0.25, 3);
        var triangleCount = Math.Clamp(
            (int)Math.Round(4 * density, MidpointRounding.AwayFromZero), 1, 12);
        for (var i = 0; i < triangleCount; i++)
            effect.Triangles.Add(CreateTriangle(position, now, false, random));
        _clickEffects.Add(effect);
    }

    private TriangleParticle CreateTriangle(Vector2 position, double now, bool movement, Random random)
    {
        var angle = RandomRange(random, 0, Math.PI * 2);
        var direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        return new TriangleParticle
        {
            Origin = position,
            ShapeOffset = movement
                ? RandomPointInTriangle(random, 0.15)
                : direction * (float)RandomRange(random, 0.09, 0.098),
            Direction = direction,
            StartedAt = now,
            Lifetime = movement ? RandomRange(random, 0.2, 0.4) : RandomRange(random, 0.6, 0.7),
            Speed = movement ? RandomRange(random, 0.067, 0.1) : RandomRange(random, 0.09, 0.13),
            Size = RandomRange(random, 0.1, 0.2),
            AlternateFrame = random.Next(2) == 1
        };
    }

    private Touch CreatePersistentTrail(Vector2 position, double now)
    {
        _touches.RemoveAll(touch => touch.IsPersistent);
        var touch = new Touch
        {
            Button = PointerMouseButton.Left,
            IsPersistent = true,
            Position = position,
            LastPosition = position,
            StartedAt = now
        };
        _touches.Add(touch);
        return touch;
    }

    private void UpdateTouchPosition(Touch touch, Vector2 position, double now)
    {
        touch.Position = position;
        var delta = position - touch.LastPosition;
        var distance = delta.Length();
        var pixels = GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5);
        if (distance < 0.01 * pixels) return;

        touch.Trail.Add(new TrailPoint(position, now));
        var expected = touch.EmissionCarry + distance / pixels * 5 *
            Math.Clamp(_settings.DistanceEmissionScale, 0.05, 10);
        var count = Math.Min((int)Math.Floor(expected), 64);
        touch.EmissionCarry = expected - Math.Floor(expected);
        for (var i = 0; i < count; i++)
        {
            var progress = (i + 1f) / (count + 1f);
            _moveParticles.Add(CreateTriangle(
                Vector2.Lerp(touch.LastPosition, position, progress), now, true, _random));
        }
        touch.LastPosition = position;
    }

    private static void ReleaseTouch(Touch touch, double now, Vector2 position)
    {
        touch.Position = position;
        touch.ReleasedAt = now;
    }

    private bool ShouldDrawTrail(Touch touch) => !_settings.PersistentTrail || touch.IsPersistent;

    private void PruneExpired(double now)
    {
        var cutoff = now - Math.Clamp(_settings.TrailDurationMs, 20, 2000);
        for (var i = _touches.Count - 1; i >= 0; i--)
        {
            var touch = _touches[i];
            while (touch.Trail.Count > 0 && touch.Trail[0].Time < cutoff) touch.Trail.RemoveAt(0);
            if (touch.Trail.Count > MaximumTrailPoints)
                touch.Trail.RemoveRange(0, touch.Trail.Count - MaximumTrailPoints);
            if (touch.ReleasedAt is not null && touch.Trail.Count == 0) _touches.RemoveAt(i);
        }

        var duration = Math.Clamp(_settings.EffectDurationScale, 0.1, 5);
        for (var i = _clickEffects.Count - 1; i >= 0; i--)
        {
            if (now - _clickEffects[i].StartedAt > 700 * duration) _clickEffects.RemoveAt(i);
        }
        for (var i = _moveParticles.Count - 1; i >= 0; i--)
        {
            if (now - _moveParticles[i].StartedAt > _moveParticles[i].Lifetime * 1000 * duration)
                _moveParticles.RemoveAt(i);
        }
    }

    private void UpdateTargetState(double now)
    {
        if (_settings.Target == TargetScope.AllDesktop)
        {
            _targetActive = true;
            return;
        }
        if (now - _lastForegroundCheck < 300) return;

        _lastForegroundCheck = now;
        _targetActive = !IsForegroundWindowFullscreen();
    }

    private static bool IsForegroundWindowFullscreen()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || window == NativeMethods.GetShellWindow() ||
            window == NativeMethods.GetDesktopWindow() || NativeMethods.IsIconic(window))
            return false;

        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo)) return false;

        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var windowBounds, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0 &&
            !NativeMethods.GetWindowRect(window, out windowBounds))
            return false;

        const int tolerance = 2;
        var monitorBounds = monitorInfo.rcMonitor;
        return windowBounds.Left <= monitorBounds.Left + tolerance &&
               windowBounds.Top <= monitorBounds.Top + tolerance &&
               windowBounds.Right >= monitorBounds.Right - tolerance &&
               windowBounds.Bottom >= monitorBounds.Bottom - tolerance;
    }

    private bool HasVisibleContent(double now)
    {
        var margin = (float)(0.5 * GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5));
        if (_clickEffects.Any(effect => IsNearViewport(effect.Position, margin))) return true;
        if (_touches.Any(touch => ShouldDrawTrail(touch) &&
                                  touch.Trail.Any(point => IsNearViewport(point.Position, margin)))) return true;

        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5);
        foreach (var particle in _moveParticles)
        {
            var simulatedAge = (now - particle.StartedAt) / 1000 / durationScale;
            if (simulatedAge < 0 || simulatedAge > particle.Lifetime) continue;
            var pixels = GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5);
            if (IsNearViewport(GetTrianglePosition(particle, simulatedAge, pixels), margin)) return true;
        }
        return false;
    }

    private static Vector2 GetTrianglePosition(TriangleParticle particle, double simulatedAge, double pixels) =>
        particle.Origin +
        (particle.ShapeOffset + particle.Direction * (float)(particle.Speed * simulatedAge)) * (float)pixels;

    private bool IsNearViewport(Vector2 position, float margin) =>
        position.X >= -margin && position.Y >= -margin &&
        position.X <= _width + margin && position.Y <= _height + margin;

    private double GetPixelsPerUnityUnit() =>
        Math.Max(1, ReferencePixelsPerUnityUnit * _dpi / 96d);

    private static Vector2 RandomPointInTriangle(Random random, double scale)
    {
        var root = Math.Sqrt(random.NextDouble());
        var a = 1 - root;
        var b = root * (1 - random.NextDouble());
        var c = 1 - a - b;
        return new Vector2(
            (float)((a * 0.099028 + b * -0.146066 + c * 0.099028) * scale),
            (float)((a * 0.134899 + c * -0.134899) * scale));
    }

    private static double RandomRange(Random random, double minimum, double maximum) =>
        minimum + random.NextDouble() * (maximum - minimum);

    private static int CreateClickSeed(
        long eventSequence, int screenX, int screenY, PointerMouseButton button)
    {
        unchecked
        {
            var hash = (int)(eventSequence ^ (eventSequence >> 32));
            hash = hash * 397 ^ screenX;
            hash = hash * 397 ^ screenY;
            return hash * 397 ^ (int)button;
        }
    }

    private static double Lerp(double from, double to, double progress) =>
        from + (to - from) * Math.Clamp(progress, 0, 1);

    private static Color4 LerpColor(Color4 from, Color4 to, double progress) => new(
        (float)Lerp(from.R, to.R, progress),
        (float)Lerp(from.G, to.G, progress),
        (float)Lerp(from.B, to.B, progress),
        (float)Lerp(from.A, to.A, progress));

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static double RingSizeCurve(double progress) => progress <= 0.2139
        ? CubicHermite(0.325836, 0.715977, 2.400473, 0.911574, progress / 0.2139, 0.2139)
        : CubicHermite(0.715977, 1, 0.911574, 0, (progress - 0.2139) / 0.7861, 0.7861);

    private static double MeshTriSizeCurve(double progress) => progress <= 0.00721
        ? 0.4205
        : progress <= 0.2139
            ? CubicHermite(0.420509, 0.715977, 2.400473, 0.911574,
                (progress - 0.00721) / 0.20669, 0.20669)
            : CubicHermite(0.715977, 1, 0.911574, 0,
                (progress - 0.2139) / 0.7861, 0.7861);

    private static double MeshTriAngularVelocity(double progress, double blend)
    {
        var first = progress <= 0.149 ? 1 : Lerp(1, 0.4556, SmoothStep((progress - 0.149) / 0.851));
        var second = progress <= 0.1587
            ? 0.7988
            : Lerp(0.7988, -0.06509, SmoothStep((progress - 0.1587) / 0.8413));
        return Lerp(first, second, blend) * 11.1701069;
    }

    private static double MeshTriRotationDelta(double progress, double blend)
    {
        var step = progress * 0.6 / 12;
        var sum = 0d;
        for (var i = 0; i < 12; i++)
            sum += MeshTriAngularVelocity(progress * (i + 0.5) / 12, blend) * step;
        return sum;
    }

    private static double MeshTriDissolveThreshold(double progress) => progress <= 0.2
        ? CubicHermite(1, 0, 0, 0, progress / 0.2, 0.2)
        : CubicHermite(0, 1, 2.4249368, 0.27735636, (progress - 0.2) / 0.8, 0.8);

    private static Color4 MeshTriColor(double progress) => progress <= 0.1118
        ? new Color4(1, 1, 1, 1)
        : progress <= 0.5
            ? LerpColor(new Color4(1, 1, 1, 1), new Color4(76 / 255f, 167 / 255f, 1, 1),
                (progress - 0.1118) / 0.3882)
            : new Color4(76 / 255f, 167 / 255f, 1, 1);

    private static double CubicHermite(double from, double to, double outgoing, double incoming,
        double normalizedTime, double duration)
    {
        var time = Math.Clamp(normalizedTime, 0, 1);
        var squared = time * time;
        var cubed = squared * time;
        return Math.Clamp(
            (2 * cubed - 3 * squared + 1) * from +
            (cubed - 2 * squared + time) * outgoing * duration +
            (-2 * cubed + 3 * squared) * to +
            (cubed - squared) * incoming * duration,
            0, 1);
    }

    private static double TriangleSizeCurve(double progress) => progress <= 0.15445
        ? SmoothStep(progress / 0.15445)
        : 1 - SmoothStep((progress - 0.15445) / 0.84555);

    private static double TriangleOpacity(double progress)
    {
        ReadOnlySpan<double> times = [0, 0.2882, 0.3647, 0.4706, 0.5734, 0.6676, 0.7561, 0.8529, 1];
        ReadOnlySpan<double> values = [1, 1, 0, 1, 0, 1, 0, 1, 1];
        for (var i = 1; i < times.Length; i++)
        {
            if (progress <= times[i])
                return Lerp(values[i - 1], values[i],
                    (progress - times[i - 1]) / (times[i] - times[i - 1]));
        }
        return 1;
    }

    private static Color4 TriangleColor(double progress, double transitionScale)
    {
        ReadOnlySpan<double> times =
        [
            11951d / 65535d,
            18504d / 65535d,
            30262d / 65535d,
            43369d / 65535d,
            54163d / 65535d
        ];
        ReadOnlySpan<Color4> colors =
        [
            new Color4(1, 1, 1, 1),
            new Color4(0.3726415f, 0.7731873f, 1, 1),
            new Color4(0.3725490f, 0.7725491f, 1, 1),
            new Color4(0.3529412f, 0.7294118f, 0.9450981f, 1),
            new Color4(0.3725490f, 0.7725491f, 1, 1)
        ];

        // The exported gradient reaches blue in roughly 10% of the particle
        // lifetime, which is only 60-70 ms for click fragments. Stretch that
        // first color transition while retaining the extracted gradient keys.
        const double defaultVisibleTransitionEnd = 0.55;
        var visibleTransitionEnd = Math.Min(0.98,
            times[0] + (defaultVisibleTransitionEnd - times[0]) * Math.Clamp(transitionScale, 0.25, 2));
        var sampledProgress = progress;
        if (progress > times[0] && progress <= visibleTransitionEnd)
        {
            sampledProgress = Lerp(times[0], times[1],
                (progress - times[0]) / (visibleTransitionEnd - times[0]));
        }
        else if (progress > visibleTransitionEnd)
        {
            sampledProgress = Lerp(times[1], 1,
                (progress - visibleTransitionEnd) / (1 - visibleTransitionEnd));
        }

        var gradient = colors[0];
        if (sampledProgress >= times[^1])
        {
            gradient = colors[^1];
        }
        else
        {
            for (var i = 1; i < times.Length; i++)
            {
                if (sampledProgress > times[i]) continue;
                gradient = LerpColor(colors[i - 1], colors[i],
                    (sampledProgress - times[i - 1]) / (times[i] - times[i - 1]));
                break;
            }
        }

        const float startColor = 0.53773582f;
        return new Color4(gradient.R * startColor, gradient.G * startColor,
            gradient.B * startColor, 1);
    }

    private static Color4 TrailColor(double progress)
    {
        const double firstTime = 1349d / 65535d;
        const double secondTime = 27563d / 65535d;
        var bright = new Color4(0, 0.39058137f, 1, 1);
        var dim = new Color4(0, 0.09486991f, 0.28235295f, 1);
        var black = new Color4(0, 0, 0, 1);
        if (progress <= firstTime) return bright;
        if (progress <= secondTime)
            return LerpColor(bright, dim, (progress - firstTime) / (secondTime - firstTime));
        return LerpColor(dim, black, (progress - secondTime) / (1 - secondTime));
    }

    public void Dispose()
    {
        Stop();
        _pipeline?.Dispose();
        _pipeline = null;
        _initialized = false;
        GC.SuppressFinalize(this);
    }
}

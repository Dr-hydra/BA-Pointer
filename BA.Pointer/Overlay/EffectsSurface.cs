using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BA.Pointer.Interop;
using BA.Pointer.Models;
using BA.Pointer.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;

namespace BA.Pointer.Overlay;

public sealed class EffectsSurface : FrameworkElement
{
    private const double OriginalTrailWidth = 0.005;
    private const double OriginalDistanceRate = 5.0;
    private const double ClickTriangleSpawnRadiusMin = 0.090;
    private const double ClickTriangleSpawnRadiusMax = 0.098;
    private const double TriangleParticleSystemScale = 0.3078824;

    private sealed record TrailPoint(Point Position, double Time);

    private sealed class TouchInstance
    {
        public required PointerMouseButton Button;
        public required Point Position;
        public required Point LastPosition;
        public required double StartedAt;
        public double? ReleasedAt;
        public double EmissionCarry;
        public List<TrailPoint> Trail { get; } = new();
    }

    private sealed class MeshTriParticle
    {
        public double StartSize;
        public double InitialRotation;
        public double RotationBlend;
    }

    private sealed class MeshRingTriangle
    {
        public required Point A;
        public required Point B;
        public required Point C;
        public required Point Ua;
        public required Point Ub;
        public required Point Uc;
        public required StreamGeometry Geometry;
    }

    private sealed class AlphaTexture
    {
        public required int Width;
        public required int Height;
        public required byte[] Alpha;

        public double Sample(double u, double v)
        {
            u -= Math.Floor(u);
            v = Math.Clamp(v, 0, 1);
            var x = u * (Width - 1);
            var y = (1 - v) * (Height - 1);
            var x0 = Math.Clamp((int)Math.Floor(x), 0, Width - 1);
            var y0 = Math.Clamp((int)Math.Floor(y), 0, Height - 1);
            var x1 = Math.Min(Width - 1, x0 + 1);
            var y1 = Math.Min(Height - 1, y0 + 1);
            var tx = x - x0;
            var ty = y - y0;
            var a00 = Alpha[y0 * Width + x0] / 255.0;
            var a10 = Alpha[y0 * Width + x1] / 255.0;
            var a01 = Alpha[y1 * Width + x0] / 255.0;
            var a11 = Alpha[y1 * Width + x1] / 255.0;
            return Lerp(Lerp(a00, a10, tx), Lerp(a01, a11, tx), ty);
        }
    }

    private sealed class TriangleParticle
    {
        public Point Origin;
        public Vector ShapeOffset;
        public Vector Direction;
        public double StartedAt;
        public double Lifetime;
        public double Speed;
        public double Size;
        public bool AlternateFrame;
        public bool IsMovementParticle;
    }

    private sealed class ClickEffect
    {
        public required Point Position;
        public required double StartedAt;
        public List<MeshTriParticle> MeshTriangles { get; } = new();
        public List<TriangleParticle> Triangles { get; } = new();
    }

    private readonly DispatcherTimer _timer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private readonly Dictionary<PointerMouseButton, TouchInstance> _activeTouches = new();
    private readonly List<TouchInstance> _touches = new();
    private readonly List<ClickEffect> _clickEffects = new();
    private readonly List<TriangleParticle> _moveParticles = new();

    private PointerSettings _settings = new();
    private OverlayWindow? _owner;
    private double _lastForegroundCheck;
    private bool _targetActive = true;
    private bool _hadVisuals;
    private ImageBrush? _circleMask;
    private ImageBrush? _triangleMaskA;
    private ImageBrush? _triangleMaskB;
    private ImageBrush? _trailMask;
    private AlphaTexture? _meshRingTexture;
    private MeshRingTriangle[] _meshRingTriangles = [];

    public EffectsSurface()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        _timer.Tick += OnTick;
    }

    public void Configure(PointerSettings settings, string _)
    {
        _settings = settings;
        _owner ??= Window.GetWindow(this) as OverlayWindow;
        LoadFxTouchTextures();
        SetTimerRate(HasVisuals());
        InvalidateVisual();
    }

    public void Start()
    {
        _owner ??= Window.GetWindow(this) as OverlayWindow;
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        ClearVisuals();
        InvalidateVisual();
    }

    public void SetPointerState(PointerMouseButton button, bool isDown, int screenX, int screenY)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _owner ??= Window.GetWindow(this) as OverlayWindow;
            if (_owner is null) return;

            var position = _owner.ScreenToLocal(screenX, screenY);
            if (isDown)
            {
                if (_targetActive) BeginTouch(button, position);
            }
            else
            {
                EndTouch(button, position);
            }
        });
    }

    private void BeginTouch(PointerMouseButton button, Point position)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_activeTouches.TryGetValue(button, out var previous)) ReleaseTouch(previous, now, position);

        var touch = new TouchInstance
        {
            Button = button,
            Position = position,
            LastPosition = position,
            StartedAt = now
        };
        touch.Trail.Add(new TrailPoint(position, now));
        _activeTouches[button] = touch;
        _touches.Add(touch);
        SpawnClickEffect(position, now);
        SetTimerRate(true);
        InvalidateVisual();
    }

    private void EndTouch(PointerMouseButton button, Point position)
    {
        if (!_activeTouches.Remove(button, out var touch)) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        UpdateTouchPosition(touch, position, now);
        ReleaseTouch(touch, now, position);
    }

    private static void ReleaseTouch(TouchInstance touch, double now, Point position)
    {
        touch.Position = position;
        touch.ReleasedAt = now;
    }

    private void SpawnClickEffect(Point position, double now)
    {
        var effect = new ClickEffect { Position = position, StartedAt = now };
        for (var i = 0; i < 2; i++)
        {
            effect.MeshTriangles.Add(new MeshTriParticle
            {
                StartSize = RandomRange(0.12, 0.14),
                InitialRotation = RandomRange(0, Math.PI * 2),
                RotationBlend = _random.NextDouble()
            });
        }

        for (var i = 0; i < 4; i++) effect.Triangles.Add(CreateTriangle(position, now, false));
        _clickEffects.Add(effect);
    }

    private TriangleParticle CreateTriangle(Point position, double now, bool movementParticle)
    {
        var angle = RandomRange(0, Math.PI * 2);
        var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
        return new TriangleParticle
        {
            Origin = position,
            ShapeOffset = movementParticle
                ? RandomPointInTriangle(0.15)
                : direction * RandomRange(ClickTriangleSpawnRadiusMin, ClickTriangleSpawnRadiusMax),
            Direction = direction,
            StartedAt = now,
            Lifetime = movementParticle ? RandomRange(0.2, 0.4) : RandomRange(0.6, 0.7),
            Speed = movementParticle ? RandomRange(0.067, 0.100) : RandomRange(0.090, 0.130),
            Size = RandomRange(0.1, 0.2),
            AlternateFrame = _random.Next(2) == 1,
            IsMovementParticle = movementParticle
        };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        UpdateTargetState(now);
        if (!_targetActive)
        {
            if (_hadVisuals || HasVisuals())
            {
                ClearVisuals();
                InvalidateVisual();
            }
            return;
        }

        if (_activeTouches.Count > 0 && NativeMethods.GetCursorPos(out var cursor) && _owner is not null)
        {
            var local = _owner.ScreenToLocal(cursor.X, cursor.Y);
            foreach (var touch in _activeTouches.Values) UpdateTouchPosition(touch, local, now);
        }

        PruneExpired(now);
        var hasVisuals = HasVisuals();
        SetTimerRate(hasVisuals);
        if (hasVisuals || hasVisuals != _hadVisuals) InvalidateVisual();
        _hadVisuals = hasVisuals;
    }

    private void UpdateTouchPosition(TouchInstance touch, Point position, double now)
    {
        touch.Position = position;
        var delta = position - touch.LastPosition;
        var distance = delta.Length;
        var pixelsPerUnit = GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5.0);
        if (distance < 0.01 * pixelsPerUnit) return;

        touch.Trail.Add(new TrailPoint(position, now));
        EmitMovementParticles(touch, touch.LastPosition, position, distance, pixelsPerUnit, now);
        touch.LastPosition = position;
    }

    private void EmitMovementParticles(
        TouchInstance touch,
        Point start,
        Point end,
        double distance,
        double pixelsPerUnit,
        double now)
    {
        var density = Math.Clamp(_settings.DistanceEmissionScale, 0.05, 10.0);
        var expected = touch.EmissionCarry + distance / pixelsPerUnit * OriginalDistanceRate * density;
        var count = Math.Min((int)Math.Floor(expected), 64);
        touch.EmissionCarry = expected - Math.Floor(expected);
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var t = (i + 1d) / (count + 1d);
            var position = new Point(
                start.X + (end.X - start.X) * t,
                start.Y + (end.Y - start.Y) * t);
            _moveParticles.Add(CreateTriangle(position, now, true));
        }
    }

    private void PruneExpired(double now)
    {
        var trailLifetime = Math.Clamp(_settings.TrailDurationMs, 20, 2000);
        var trailCutoff = now - trailLifetime;
        for (var i = _touches.Count - 1; i >= 0; i--)
        {
            var touch = _touches[i];
            while (touch.Trail.Count > 0 && touch.Trail[0].Time < trailCutoff) touch.Trail.RemoveAt(0);
            if (touch.Trail.Count > 320) touch.Trail.RemoveRange(0, touch.Trail.Count - 320);
            if (touch.ReleasedAt is not null && touch.Trail.Count == 0) _touches.RemoveAt(i);
        }

        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5.0);
        for (var i = _clickEffects.Count - 1; i >= 0; i--)
        {
            if (now - _clickEffects[i].StartedAt > 700 * durationScale)
                _clickEffects.RemoveAt(i);
        }
        for (var i = _moveParticles.Count - 1; i >= 0; i--)
        {
            if (now - _moveParticles[i].StartedAt > _moveParticles[i].Lifetime * 1000 * durationScale)
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
        var window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            _targetActive = process.ProcessName.Contains("BlueArchive", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _targetActive = false;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_circleMask is null || _triangleMaskA is null || _triangleMaskB is null ||
            _trailMask is null || _meshRingTexture is null || _meshRingTriangles.Length == 0) return;

        var now = _clock.Elapsed.TotalMilliseconds;
        DrawTrails(dc, now);
        foreach (var particle in _moveParticles) DrawTriangle(dc, particle, now);
        foreach (var effect in _clickEffects)
        {
            DrawCentralFlash(dc, effect, now);
            foreach (var particle in effect.MeshTriangles) DrawMeshTri(dc, effect, particle, now);
            foreach (var triangle in effect.Triangles) DrawTriangle(dc, triangle, now);
        }
    }

    private void DrawTrails(DrawingContext dc, double now)
    {
        if (_trailMask is null) return;
        var opacity = Math.Clamp(_settings.EffectOpacity, 0, 1);
        var scale = Math.Clamp(_settings.EffectScale, 0.1, 5.0);
        var lifetime = Math.Clamp(_settings.TrailDurationMs, 20, 2000);
        var coreWidth = Math.Max(0.5, OriginalTrailWidth * GetPixelsPerUnityUnit() * scale *
            Math.Clamp(_settings.TrailWidthScale, 0.1, 10));

        foreach (var touch in _touches)
        {
            for (var i = 1; i < touch.Trail.Count; i++)
            {
                var oldPoint = touch.Trail[i - 1];
                var newPoint = touch.Trail[i];
                var ageOpacity = 1 - Math.Clamp((now - newPoint.Time) / lifetime, 0, 1);
                if (ageOpacity <= 0) continue;

                var direction = oldPoint.Position - newPoint.Position;
                var length = direction.Length;
                if (length < 0.25) continue;
                var center = new Point(
                    (oldPoint.Position.X + newPoint.Position.X) * 0.5,
                    (oldPoint.Position.Y + newPoint.Position.Y) * 0.5);
                var angle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
                var positionT = i / (double)Math.Max(1, touch.Trail.Count - 1);
                var color = TrailGradient(positionT);
                var segmentOpacity = opacity * ageOpacity;

                DrawMaskedRect(dc, _trailMask, center, length + coreWidth * 4, coreWidth * 4,
                    angle, color, segmentOpacity * 0.18);
                DrawMaskedRect(dc, _trailMask, center, length + coreWidth, coreWidth,
                    angle, color, segmentOpacity * 0.82);
            }
        }
    }

    private void DrawCentralFlash(DrawingContext dc, ClickEffect effect, double now)
    {
        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5.0);
        var progress = (now - effect.StartedAt) / (200 * durationScale);
        if (progress < 0 || progress > 1 || _circleMask is null) return;

        var curve = RingSizeCurve(progress);
        var diameter = 0.12 * 2.0 * curve * GetPixelsPerUnityUnit() *
            Math.Clamp(_settings.EffectScale, 0.1, 5.0);
        var fade = progress <= 0.1088 ? 1.0 : 1 - (progress - 0.1088) / (1 - 0.1088);
        var opacity = Math.Clamp(_settings.EffectOpacity, 0, 1) * Math.Clamp(fade, 0, 1);
        var blue = Color.FromRgb(61, 100, 255);
        // Cross2 uses an HDR/additive material. With desktop alpha composition
        // the same dark-blue RGB at full opacity would make the backdrop look
        // much darker than the in-game pale cyan flash.
        var color = progress < 0.12
            ? LerpColor(Colors.White, blue, progress / 0.12)
            : LerpColor(blue, Colors.White, 0.58);

        DrawMaskedRect(dc, _circleMask, effect.Position, diameter * 1.34, diameter * 1.34, 0,
            LerpColor(color, Colors.White, 0.65), opacity * 0.08);
        DrawMaskedRect(dc, _circleMask, effect.Position, diameter, diameter, 0, color, opacity * 0.28);
    }

    private void DrawMeshTri(DrawingContext dc, ClickEffect effect, MeshTriParticle particle, double now)
    {
        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5.0);
        var progress = (now - effect.StartedAt) / (600 * durationScale);
        if (progress < 0 || progress > 1 || _meshRingTexture is null || _meshRingTriangles.Length == 0) return;

        var pixelsPerUnit = GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5.0);
        var meshScale = particle.StartSize * MeshTriSizeCurve(progress) * pixelsPerUnit;
        // WPF's screen Y axis points down; invert the Unity Z rotation so the
        // dissolve head travels counter-clockwise on screen like the original.
        var rotation = particle.InitialRotation - MeshTriRotationDelta(progress, particle.RotationBlend);
        var color = MeshTriColor(progress);
        var opacity = Math.Clamp(_settings.EffectOpacity, 0, 1);

        DrawGradientRing(dc, effect.Position, meshScale, rotation, color, opacity,
            MeshTriDissolveThreshold(progress));
    }

    private void DrawTriangle(DrawingContext dc, TriangleParticle particle, double now)
    {
        var durationScale = Math.Clamp(_settings.EffectDurationScale, 0.1, 5.0);
        var simulatedAge = (now - particle.StartedAt) / 1000 / durationScale;
        var progress = simulatedAge / particle.Lifetime;
        if (progress < 0 || progress > 1) return;

        var pixelsPerUnit = GetPixelsPerUnityUnit() * Math.Clamp(_settings.EffectScale, 0.1, 5.0);
        var position = particle.Origin +
            (particle.ShapeOffset + particle.Direction * (particle.Speed * simulatedAge)) * pixelsPerUnit;
        var size = particle.Size * pixelsPerUnit * TriangleParticleSystemScale * TriangleSizeCurve(progress);
        var opacity = Math.Clamp(_settings.EffectOpacity, 0, 1) * TriangleOpacity(progress);
        var color = TriangleColor(progress);
        var mask = particle.AlternateFrame ? _triangleMaskB : _triangleMaskA;
        if (mask is null || size <= 0.05 || opacity <= 0) return;

        DrawMaskedRect(dc, mask, position, size * 1.18, size * 1.18, 0, color, opacity * 0.20);
        DrawMaskedRect(dc, mask, position, size, size, 0, color, opacity * 0.88);
    }

    private void DrawGradientRing(
        DrawingContext dc,
        Point center,
        double meshScale,
        double rotation,
        Color color,
        double opacity,
        double dissolveThreshold)
    {
        if (_meshRingTexture is null) return;

        // The original renderer draws the extracted Cylinder002 mesh. Its
        // texture UVs carry the incomplete, soft ring shape; no geometric
        // sine-wave deformation is present in FXTouch or the shader.
        var brush = CreateBrush(color);
        brush.Freeze();
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(rotation * 180 / Math.PI));
        dc.PushTransform(new ScaleTransform(meshScale, meshScale));

        // Tri3 writes an HDR material color (5.992) and the game's URP Bloom
        // pass turns that energy into the soft cyan halo visible around the
        // ring. WPF has no HDR overlay, so approximate the bloom with two
        // expanded, low-opacity passes; geometry and alpha remain source-driven.
        var bloomBrush = CreateBrush(LerpColor(color, Colors.White, 0.75));
        dc.PushTransform(new ScaleTransform(1.34, 1.34));
        dc.PushOpacity(Math.Clamp(opacity * 0.06, 0, 1));
        DrawMeshRingPass(dc, bloomBrush, 1.0, dissolveThreshold);
        dc.Pop();
        dc.Pop();

        dc.PushTransform(new ScaleTransform(1.22, 1.22));
        dc.PushOpacity(Math.Clamp(opacity * 0.11, 0, 1));
        DrawMeshRingPass(dc, bloomBrush, 1.0, dissolveThreshold);
        dc.Pop();
        dc.Pop();

        dc.PushTransform(new ScaleTransform(1.10, 1.10));
        dc.PushOpacity(Math.Clamp(opacity * 0.18, 0, 1));
        DrawMeshRingPass(dc, bloomBrush, 1.0, dissolveThreshold);
        dc.Pop();
        dc.Pop();

        DrawMeshRingPass(dc, brush, opacity * 0.92, dissolveThreshold);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawMeshRingPass(
        DrawingContext dc,
        Brush brush,
        double opacity,
        double dissolveThreshold)
    {
        var texture = _meshRingTexture;
        if (texture is null) return;
        foreach (var triangle in _meshRingTriangles)
        {
            var alpha = (texture.Sample(triangle.Ua.X, triangle.Ua.Y) +
                texture.Sample(triangle.Ub.X, triangle.Ub.Y) +
                texture.Sample(triangle.Uc.X, triangle.Uc.Y)) / 3.0;
            // Shader 437 clips when (MainTex.a * particle.a) - CustomData.x < 0.
            if (alpha < dissolveThreshold) continue;

            dc.PushOpacity(Math.Clamp(opacity * alpha, 0, 1));
            dc.DrawGeometry(brush, null, triangle.Geometry);
            dc.Pop();
        }
    }

    private static void DrawMaskedRect(
        DrawingContext dc,
        Brush mask,
        Point center,
        double width,
        double height,
        double rotation,
        Color color,
        double opacity)
    {
        if (width <= 0 || height <= 0 || opacity <= 0) return;
        var rect = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
        dc.PushOpacity(Math.Clamp(opacity, 0, 1));
        dc.PushTransform(new RotateTransform(rotation, center.X, center.Y));
        dc.PushOpacityMask(mask);
        dc.DrawRectangle(CreateBrush(color), null, rect);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void LoadFxTouchTextures()
    {
        if (_circleMask is not null) return;
        var circle = LoadMask(AssetLocator.GetBundledAssetPath("FX_TEX_Circle_01.png"), true);
        var meshTri = LoadMask(AssetLocator.GetBundledAssetPath("FX_TEX_Grad_Ring3.png"), false);
        var triangles = LoadMask(AssetLocator.GetBundledAssetPath("FX_TEX_Triangle_02_1.png"), false);
        var trail = LoadMask(AssetLocator.GetBundledAssetPath("FX_TEX_Trail_03.png"), true);

        _circleMask = CreateImageBrush(circle);
        _meshRingTexture = CreateAlphaTexture(meshTri);
        _meshRingTriangles = LoadMeshRingTriangles(AssetLocator.GetBundledAssetPath("Cylinder002.obj"));
        _triangleMaskA = CreateImageBrush(new CroppedBitmap(triangles, new Int32Rect(0, 0, 128, 128)));
        _triangleMaskB = CreateImageBrush(new CroppedBitmap(triangles, new Int32Rect(128, 0, 128, 128)));
        _trailMask = CreateImageBrush(trail);
    }

    private static AlphaTexture CreateAlphaTexture(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var alpha = new byte[converted.PixelWidth * converted.PixelHeight];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];
        return new AlphaTexture
        {
            Width = converted.PixelWidth,
            Height = converted.PixelHeight,
            Alpha = alpha
        };
    }

    private static MeshRingTriangle[] LoadMeshRingTriangles(string path)
    {
        if (!File.Exists(path)) return [];

        var vertices = new List<Point>();
        var uvs = new List<Point>();
        var triangles = new List<MeshRingTriangle>();
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var y))
                    vertices.Add(new Point(x, y));
            }
            else if (line.StartsWith("vt ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var u) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    uvs.Add(new Point(u, v));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                var indices = new (int Vertex, int Uv)[3];
                var valid = true;
                for (var i = 0; i < 3; i++)
                {
                    var pair = parts[i + 1].Split('/');
                    if (pair.Length < 2 || !int.TryParse(pair[0], out var vertex) ||
                        !int.TryParse(pair[1], out var uv))
                    {
                        valid = false;
                        break;
                    }
                    indices[i] = (vertex - 1, uv - 1);
                }
                if (!valid || indices.Any(index => index.Vertex < 0 || index.Vertex >= vertices.Count ||
                        index.Uv < 0 || index.Uv >= uvs.Count)) continue;
                var a = vertices[indices[0].Vertex];
                var b = vertices[indices[1].Vertex];
                var c = vertices[indices[2].Vertex];
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(a, true, true);
                    context.LineTo(b, true, false);
                    context.LineTo(c, true, false);
                }
                geometry.Freeze();
                triangles.Add(new MeshRingTriangle
                {
                    A = a,
                    B = b,
                    C = c,
                    Ua = uvs[indices[0].Uv],
                    Ub = uvs[indices[1].Uv],
                    Uc = uvs[indices[2].Uv],
                    Geometry = geometry
                });
            }
        }
        return triangles.ToArray();
    }

    private static BitmapSource LoadMask(string path, bool luminanceAsAlpha)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(path, UriKind.Absolute);
        source.EndInit();
        source.Freeze();

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = luminanceAsAlpha
                ? Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]))
                : pixels[i + 3];
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = alpha;
        }

        var mask = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX > 0 ? converted.DpiX : 96,
            converted.DpiY > 0 ? converted.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        mask.Freeze();
        return mask;
    }

    private static ImageBrush CreateImageBrush(BitmapSource image)
    {
        if (image.CanFreeze) image.Freeze();
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        brush.Freeze();
        return brush;
    }

    private Vector RandomPointInTriangle(double shapeScale)
    {
        var root = Math.Sqrt(_random.NextDouble());
        var a = 1 - root;
        var b = root * (1 - _random.NextDouble());
        var c = 1 - a - b;
        var x = a * 0.099028 + b * -0.146066 + c * 0.099028;
        var y = a * 0.134899 + c * -0.134899;
        return new Vector(x * shapeScale, y * shapeScale);
    }

    private double RandomRange(double min, double max) => min + _random.NextDouble() * (max - min);

    private double GetPixelsPerUnityUnit()
    {
        var height = ActualHeight;
        if (!double.IsFinite(height) || height < 2) height = SystemParameters.PrimaryScreenHeight;
        return Math.Max(1, height / 2);
    }

    private static double MeshTriSizeCurve(double progress)
    {
        if (progress <= 0.00721) return 0.4205;
        if (progress <= 0.2139)
            return CubicHermite(0.420509, 0.715977, 2.400473, 0.911574,
                (progress - 0.00721) / (0.2139 - 0.00721), 0.2139 - 0.00721);
        return CubicHermite(0.715977, 1.0, 0.911574, 0.0,
            (progress - 0.2139) / (1 - 0.2139), 1 - 0.2139);
    }

    private static double RingSizeCurve(double progress)
    {
        if (progress <= 0.0) return 0.325836;
        if (progress <= 0.2139)
            return CubicHermite(0.325836, 0.715977, 2.400473, 0.911574,
                progress / 0.2139, 0.2139);
        return CubicHermite(0.715977, 1.0, 0.911574, 0.0,
            (progress - 0.2139) / (1 - 0.2139), 1 - 0.2139);
    }

    private static double MeshTriRotationDelta(double progress, double blend)
    {
        const int steps = 12;
        var secondsPerStep = progress * 0.6 / steps;
        var radians = 0.0;
        for (var i = 0; i < steps; i++)
        {
            var sampleProgress = progress * (i + 0.5) / steps;
            radians += MeshTriAngularVelocity(sampleProgress, blend) * secondsPerStep;
        }
        return radians;
    }

    private static double MeshTriAngularVelocity(double progress, double blend)
    {
        var firstCurve = progress <= 0.1490
            ? 1.0
            : Lerp(1.0, 0.4556, SmoothStep((progress - 0.1490) / (1 - 0.1490)));
        var secondCurve = progress <= 0.1587
            ? 0.7988
            : Lerp(0.7988, -0.06509, SmoothStep((progress - 0.1587) / (1 - 0.1587)));
        return Lerp(firstCurve, secondCurve, blend) * 11.1701069;
    }

    private static double MeshTriDissolveThreshold(double progress)
    {
        // CustomDataModule.vector0_0 from the original MeshTri particle:
        // (0, 1) -> (0.2, 0) -> (1, 1), with the exported tangents retained.
        if (progress <= 0.2)
            return CubicHermite(1.0, 0.0, 0.0, 0.0, progress / 0.2, 0.2);

        return CubicHermite(0.0, 1.0, 2.4249368, 0.27735636,
            (progress - 0.2) / 0.8, 0.8);
    }

    private static Color MeshTriColor(double progress)
    {
        if (progress <= 0.1118) return Colors.White;
        if (progress <= 0.5)
            return LerpColor(Colors.White, Color.FromRgb(76, 167, 255),
                (progress - 0.1118) / (0.5 - 0.1118));
        return Color.FromRgb(76, 167, 255);
    }

    private static double CubicHermite(
        double from,
        double to,
        double outgoingTangent,
        double incomingTangent,
        double normalizedTime,
        double duration)
    {
        var t = Math.Clamp(normalizedTime, 0, 1);
        var t2 = t * t;
        var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + t;
        var h01 = -2 * t3 + 3 * t2;
        var h11 = t3 - t2;
        return Math.Clamp(h00 * from + h10 * outgoingTangent * duration +
            h01 * to + h11 * incomingTangent * duration, 0, 1);
    }

    private static double TriangleSizeCurve(double progress)
    {
        if (progress <= 0.15445) return SmoothStep(progress / 0.15445);
        return 1 - SmoothStep((progress - 0.15445) / (1 - 0.15445));
    }

    private static double TriangleOpacity(double progress)
    {
        ReadOnlySpan<double> times = [0.0, 0.2882, 0.3647, 0.4706, 0.5734, 0.6676, 0.7561, 0.8529, 1.0];
        ReadOnlySpan<double> values = [1.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 1.0];
        for (var i = 1; i < times.Length; i++)
        {
            if (progress > times[i]) continue;
            var t = (progress - times[i - 1]) / (times[i] - times[i - 1]);
            return Lerp(values[i - 1], values[i], t);
        }
        return values[^1];
    }

    private static Color TriangleColor(double progress)
    {
        if (progress < 0.1824) return Colors.White;
        if (progress < 0.2824)
            return LerpColor(Colors.White, Color.FromRgb(95, 197, 255), (progress - 0.1824) / 0.1);
        return Color.FromRgb(95, 197, 255);
    }

    private static Color TrailGradient(double position)
    {
        var dark = Color.FromRgb(0, 24, 72);
        var bright = Color.FromRgb(0, 100, 255);
        return LerpColor(dark, bright, SmoothStep(position));
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(Lerp(from.A, to.A, amount)),
            (byte)Math.Round(Lerp(from.R, to.R, amount)),
            (byte)Math.Round(Lerp(from.G, to.G, amount)),
            (byte)Math.Round(Lerp(from.B, to.B, amount)));
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * Math.Clamp(amount, 0, 1);

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private bool HasVisuals() => _touches.Count > 0 || _clickEffects.Count > 0 || _moveParticles.Count > 0;

    private void SetTimerRate(bool active)
    {
        var milliseconds = active ? 1000d / Math.Clamp(_settings.FrameRate, 30, 240) : 100d;
        if (Math.Abs(_timer.Interval.TotalMilliseconds - milliseconds) > 0.01)
            _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
    }

    private void ClearVisuals()
    {
        _activeTouches.Clear();
        _touches.Clear();
        _clickEffects.Clear();
        _moveParticles.Clear();
        _hadVisuals = false;
        SetTimerRate(false);
    }
}

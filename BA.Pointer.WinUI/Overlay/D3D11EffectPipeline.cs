using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using BA.Pointer.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color4 = Vortice.Mathematics.Color4;
using D3DBlend = Vortice.Direct3D11.Blend;
using D3DMapFlags = Vortice.Direct3D11.MapFlags;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace BA.Pointer.Overlay;

internal enum EffectTexture
{
    Circle,
    Triangle,
    Trail
}

internal sealed class D3D11EffectPipeline : IDisposable
{
    private const int DxgiStatusOccluded = 0x087A0001;

    private const int MaximumBloomLevels = 6;
    private const int MaximumTrailPoints = 320;
    private const uint ShaderCompileFlags = (1u << 11) | (1u << 15);

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuVertex
    {
        public Vector2 Position;
        public Vector2 TexCoord;

        public GpuVertex(Vector2 position, Vector2 texCoord)
        {
            Position = position;
            TexCoord = texCoord;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrailVertex
    {
        public Vector2 Position;
        public Vector2 TexCoord;
        public float Age;

        public TrailVertex(Vector2 position, Vector2 texCoord, float age)
        {
            Position = position;
            TexCoord = texCoord;
            Age = age;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Vector2 ViewportSize;
        public Vector2 DrawPosition;
        public Vector2 DrawScale;
        public float DrawRotation;
        public float DissolveThreshold;
        public Vector4 UvRectangle;
        public Vector4 DrawColor;
        public float Emission;
        private Vector3 _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PostConstants
    {
        public Vector2 SourceTexelSize;
        public float BloomThreshold;
        public float BloomKnee;
        public float BloomScatter;
        public float BloomIntensity;
        private Vector2 _padding;
    }

    private sealed class TextureResource : IDisposable
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ID3D11ShaderResourceView View { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }

    private sealed class RenderTexture : IDisposable
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ID3D11RenderTargetView Target { get; init; }
        public required ID3D11ShaderResourceView View { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        public void Dispose()
        {
            View.Dispose();
            Target.Dispose();
            Texture.Dispose();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBlobPointerDelegate(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBlobSizeDelegate(IntPtr blob);

    private readonly IntPtr _hwnd;
    private readonly string _assetDirectory;
    private readonly string _shaderPath;
    private int _width;
    private int _height;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private IDCompositionDevice _compositionDevice = null!;
    private IDCompositionTarget _compositionTarget = null!;
    private IDCompositionVisual _compositionVisual = null!;

    private ID3D11VertexShader _sceneVertexShader = null!;
    private ID3D11PixelShader _scenePixelShader = null!;
    private ID3D11VertexShader _trailVertexShader = null!;
    private ID3D11PixelShader _trailPixelShader = null!;
    private ID3D11VertexShader _fullscreenVertexShader = null!;
    private ID3D11PixelShader _bloomPrefilterShader = null!;
    private ID3D11PixelShader _bloomDownsampleShader = null!;
    private ID3D11PixelShader _bloomUpsampleShader = null!;
    private ID3D11PixelShader _compositeShader = null!;
    private ID3D11InputLayout _sceneInputLayout = null!;
    private ID3D11InputLayout _trailInputLayout = null!;
    private ID3D11Buffer _sceneConstants = null!;
    private ID3D11Buffer _postConstants = null!;
    private ID3D11Buffer _quadVertexBuffer = null!;
    private ID3D11Buffer _quadIndexBuffer = null!;
    private ID3D11Buffer _ringVertexBuffer = null!;
    private ID3D11Buffer _ringIndexBuffer = null!;
    private ID3D11Buffer _trailVertexBuffer = null!;
    private ID3D11Buffer _trailIndexBuffer = null!;
    private uint _ringIndexCount;
    private ID3D11SamplerState _sceneSampler = null!;
    private ID3D11SamplerState _postSampler = null!;
    private ID3D11RasterizerState _noCullRasterizer = null!;
    private ID3D11BlendState _alphaBlend = null!;
    private ID3D11BlendState _additiveBlend = null!;
    private ID3D11BlendState _opaqueBlend = null!;
    private TextureResource _circleTexture = null!;
    private TextureResource _triangleTexture = null!;
    private TextureResource _trailTexture = null!;
    private TextureResource _ringTexture = null!;
    private TextureResource _blackTexture = null!;
    private readonly Vector2[] _trailPositions = new Vector2[MaximumTrailPoints];
    private readonly TrailVertex[] _trailVertices = new TrailVertex[MaximumTrailPoints * 2];

    private ID3D11RenderTargetView? _swapChainTarget;
    private RenderTexture? _scene;
    private RenderTexture? _foreground;
    private readonly List<RenderTexture> _bloomDown = new();
    private readonly List<RenderTexture> _bloomUp = new();
    private bool _disposed;
    private long _presentCount;
    private long _compositionCommitCount;
    private int _lastPresentCode;
    private int _lastCompositionCheckCode;
    private int _lastCompositionCommitCode;
    private int _occludedPresentStreak;
    private bool _compositionValid = true;

    public D3D11EffectPipeline(IntPtr hwnd, string assetDirectory, int width, int height)
    {
        _hwnd = hwnd;
        _assetDirectory = assetDirectory;
        _shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "PointerEffects.hlsl");
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        CreateDeviceAndComposition();
        CreateStaticResources();
        CreateSizeDependentResources();
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height) return;

        _width = width;
        _height = height;
        _context.ClearState();
        DisposeSizeDependentResources();
        _swapChain.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateSizeDependentResources();
        RefreshCompositionBinding();
    }

    public string GetDiagnosticState() =>
        $"device={_device.DeviceRemovedReason}, presentCount={_presentCount}, " +
        $"lastPresent={FormatResult(_lastPresentCode)}, occludedStreak={_occludedPresentStreak}, " +
        $"dcompValid={_compositionValid}, dcompCheck={FormatResult(_lastCompositionCheckCode)}, " +
        $"dcompCommit={FormatResult(_lastCompositionCommitCode)}, commitCount={_compositionCommitCount}";

    public bool NeedsRecovery => _occludedPresentStreak >= 3;

    public void RefreshCompositionBinding()
    {
        var deviceResult = _device.DeviceRemovedReason;
        if (deviceResult.Failure)
            throw new InvalidOperationException($"D3D11 device is unavailable: {deviceResult}");

        var checkResult = _compositionDevice.CheckDeviceState(out var compositionValid);
        _lastCompositionCheckCode = checkResult.Code;
        _compositionValid = compositionValid;
        if (checkResult.Failure || !_compositionValid)
            throw new InvalidOperationException(
                $"DirectComposition device is unavailable: check={checkResult}, valid={_compositionValid}");

        _compositionVisual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        var commitResult = _compositionDevice.Commit();
        _lastCompositionCommitCode = commitResult.Code;
        _compositionCommitCount++;
        commitResult.CheckError();
    }

    public void BeginScene()
    {
        if (_scene is null || _foreground is null) return;
        _context.ClearRenderTargetView(_scene.Target, new Color4(0, 0, 0, 0));
        _context.ClearRenderTargetView(_foreground.Target, new Color4(0, 0, 0, 0));
        BindEffectTarget(_scene);
    }

    public void BeginForeground()
    {
        if (_foreground is null) return;
        BindEffectTarget(_foreground);
    }

    private void BindEffectTarget(RenderTexture target)
    {
        _context.OMSetRenderTargets(target.Target, null!);
        _context.RSSetViewport(new Viewport(target.Width, target.Height));
        _context.OMSetBlendState(_alphaBlend);
        _context.RSSetState(_noCullRasterizer);
        _context.IASetInputLayout(_sceneInputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_sceneVertexShader);
        _context.VSSetConstantBuffer(0, _sceneConstants);
        _context.PSSetShader(_scenePixelShader);
        _context.PSSetConstantBuffer(0, _sceneConstants);
        _context.PSSetSampler(1, _sceneSampler);
    }

    public void DrawSprite(EffectTexture texture, Vector2 center, float width, float height, float rotation,
        Color4 color, float opacity, Vector4? uvRectangle = null, bool additive = false, float emission = 1)
    {
        if (opacity <= 0 || width <= 0 || height <= 0) return;
        var resource = texture switch
        {
            EffectTexture.Circle => _circleTexture,
            EffectTexture.Triangle => _triangleTexture,
            EffectTexture.Trail => _trailTexture,
            _ => throw new ArgumentOutOfRangeException(nameof(texture))
        };

        var constants = new SceneConstants
        {
            ViewportSize = new Vector2(_width, _height),
            DrawPosition = center,
            DrawScale = new Vector2(width, height),
            DrawRotation = rotation,
            DissolveThreshold = 0.0001f,
            UvRectangle = uvRectangle ?? new Vector4(0, 0, 1, 1),
            DrawColor = new Vector4(color.R, color.G, color.B, Math.Clamp(opacity, 0, 1)),
            Emission = Math.Max(0, emission)
        };
        _context.OMSetBlendState(additive ? _additiveBlend : _alphaBlend);
        DrawGeometry(_quadVertexBuffer, _quadIndexBuffer, 6, resource.View, ref constants);
    }

    public unsafe void DrawTrail(ReadOnlySpan<Vector2> path, ReadOnlySpan<float> ages, float width,
        Color4 color, float opacity, float emission)
    {
        if (path.Length < 2 || path.Length != ages.Length || width <= 0 || opacity <= 0) return;
        if (path.Length > MaximumTrailPoints)
        {
            path = path[^MaximumTrailPoints..];
            ages = ages[^MaximumTrailPoints..];
        }

        var pointCount = 0;
        Span<float> filteredAges = stackalloc float[MaximumTrailPoints];
        for (var i = 0; i < path.Length; i++)
        {
            var position = path[i];
            if (pointCount > 0 && Vector2.DistanceSquared(_trailPositions[pointCount - 1], position) < 0.0625f)
                continue;
            _trailPositions[pointCount] = position;
            filteredAges[pointCount] = Math.Clamp(ages[i], 0, 1);
            pointCount++;
        }
        if (pointCount < 2) return;

        var halfWidth = width * 0.5f;
        var halfTexelV = 0.5f / _trailTexture.Height;
        var profileU = Math.Clamp(128.5f / _trailTexture.Width, 0, 1);
        for (var i = 0; i < pointCount; i++)
        {
            var offset = CalculateTrailOffset(i, pointCount, halfWidth);
            _trailVertices[i * 2] = new TrailVertex(
                _trailPositions[i] + offset, new Vector2(profileU, halfTexelV), filteredAges[i]);
            _trailVertices[i * 2 + 1] = new TrailVertex(
                _trailPositions[i] - offset, new Vector2(profileU, 1 - halfTexelV), filteredAges[i]);
        }

        var vertexCount = pointCount * 2;
        var mapped = _context.Map(_trailVertexBuffer, 0, MapMode.WriteDiscard, D3DMapFlags.None);
        try
        {
            fixed (TrailVertex* source = _trailVertices)
            {
                var bytes = vertexCount * Marshal.SizeOf<TrailVertex>();
                Buffer.MemoryCopy(source, (void*)mapped.DataPointer, bytes, bytes);
            }
        }
        finally
        {
            _context.Unmap(_trailVertexBuffer, 0);
        }

        var constants = new SceneConstants
        {
            ViewportSize = new Vector2(_width, _height),
            DrawPosition = Vector2.Zero,
            DrawScale = Vector2.One,
            DrawRotation = 0,
            DissolveThreshold = 0.0001f,
            UvRectangle = new Vector4(0, 0, 1, 1),
            DrawColor = new Vector4(color.R, color.G, color.B, Math.Clamp(opacity, 0, 1)),
            Emission = Math.Max(0, emission)
        };
        UpdateConstantBuffer(_sceneConstants, ref constants);
        try
        {
            _context.OMSetBlendState(_additiveBlend);
            _context.IASetInputLayout(_trailInputLayout);
            _context.IASetVertexBuffer(0, _trailVertexBuffer, (uint)Marshal.SizeOf<TrailVertex>(), 0);
            _context.IASetIndexBuffer(_trailIndexBuffer, Format.R16_UInt, 0);
            _context.VSSetShader(_trailVertexShader);
            _context.PSSetShader(_trailPixelShader);
            _context.PSSetShaderResource(0, _trailTexture.View);
            _context.DrawIndexed((uint)((pointCount - 1) * 6), 0, 0);
        }
        finally
        {
            _context.IASetInputLayout(_sceneInputLayout);
            _context.VSSetShader(_sceneVertexShader);
            _context.PSSetShader(_scenePixelShader);
        }
    }

    private Vector2 CalculateTrailOffset(int index, int pointCount, float halfWidth)
    {
        var previousDirection = index > 0
            ? Vector2.Normalize(_trailPositions[index] - _trailPositions[index - 1])
            : Vector2.Normalize(_trailPositions[1] - _trailPositions[0]);
        var nextDirection = index < pointCount - 1
            ? Vector2.Normalize(_trailPositions[index + 1] - _trailPositions[index])
            : previousDirection;
        var previousNormal = new Vector2(-previousDirection.Y, previousDirection.X);
        var nextNormal = new Vector2(-nextDirection.Y, nextDirection.X);

        if (index == 0) return nextNormal * halfWidth;
        if (index == pointCount - 1) return previousNormal * halfWidth;

        var miter = previousNormal + nextNormal;
        if (miter.LengthSquared() < 0.0001f) return nextNormal * halfWidth;
        miter = Vector2.Normalize(miter);
        var denominator = Math.Max(0.35f, Math.Abs(Vector2.Dot(miter, nextNormal)));
        return miter * Math.Min(halfWidth / denominator, halfWidth * 2);
    }

    public void DrawRing(Vector2 center, float scale, float rotation, Color4 color, float opacity,
        float dissolveThreshold, float emission)
    {
        if (opacity <= 0 || scale <= 0 || _ringIndexCount == 0) return;
        var constants = new SceneConstants
        {
            ViewportSize = new Vector2(_width, _height),
            DrawPosition = center,
            DrawScale = new Vector2(scale, scale),
            DrawRotation = rotation,
            DissolveThreshold = Math.Clamp(dissolveThreshold, 0, 1),
            UvRectangle = new Vector4(0, 0, 1, 1),
            DrawColor = new Vector4(color.R, color.G, color.B, Math.Clamp(opacity, 0, 1)),
            Emission = Math.Max(0, emission)
        };
        _context.OMSetBlendState(_alphaBlend);
        DrawGeometry(_ringVertexBuffer, _ringIndexBuffer, _ringIndexCount, _ringTexture.View, ref constants);
    }

    public void Present(float bloomRadius, float bloomStrength, bool renderBloom)
    {
        if (_scene is null || _foreground is null || _swapChainTarget is null) return;
        ID3D11ShaderResourceView bloomView = _blackTexture.View;

        if (renderBloom && bloomStrength > 0.0001f && _bloomDown.Count > 0)
        {
            var activeLevels = _bloomDown.Count == 1
                ? 1
                : Math.Clamp(2 + (int)MathF.Round(Math.Clamp(bloomRadius, 0, 40) / 4f), 2, _bloomDown.Count);
            var scatter = Math.Clamp(0.48f + bloomRadius / 80f, 0.48f, 0.95f);
            RunFullscreenPass(_bloomDown[0], _bloomPrefilterShader, _scene.View, null,
                new PostConstants
                {
                    SourceTexelSize = new Vector2(1f / _scene.Width, 1f / _scene.Height),
                    BloomThreshold = 1,
                    BloomKnee = 0.5f
                });

            for (var i = 1; i < activeLevels; i++)
            {
                var source = _bloomDown[i - 1];
                RunFullscreenPass(_bloomDown[i], _bloomDownsampleShader, source.View, null,
                    new PostConstants { SourceTexelSize = new Vector2(1f / source.Width, 1f / source.Height) });
            }

            var lowView = _bloomDown[activeLevels - 1].View;
            var lowWidth = _bloomDown[activeLevels - 1].Width;
            var lowHeight = _bloomDown[activeLevels - 1].Height;
            for (var i = activeLevels - 2; i >= 0; i--)
            {
                RunFullscreenPass(_bloomUp[i], _bloomUpsampleShader, _bloomDown[i].View, lowView,
                    new PostConstants
                    {
                        SourceTexelSize = new Vector2(1f / lowWidth, 1f / lowHeight),
                        BloomScatter = scatter
                    });
                lowView = _bloomUp[i].View;
                lowWidth = _bloomUp[i].Width;
                lowHeight = _bloomUp[i].Height;
            }
            bloomView = lowView;
        }

        _context.OMSetRenderTargets(_swapChainTarget, null!);
        _context.RSSetViewport(new Viewport(_width, _height));
        _context.OMSetBlendState(_opaqueBlend);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullscreenVertexShader);
        _context.PSSetShader(_compositeShader);
        _context.PSSetSampler(0, _postSampler);
        _context.PSSetShaderResource(0, _scene.View);
        _context.PSSetShaderResource(1, bloomView);
        _context.PSSetShaderResource(2, _foreground.View);
        var finalConstants = new PostConstants { BloomIntensity = Math.Clamp(bloomStrength, 0, 1.5f) };
        UpdateConstantBuffer(_postConstants, ref finalConstants);
        _context.PSSetConstantBuffer(1, _postConstants);
        _context.Draw(3, 0);
        UnbindPostProcessTextures();
        var presentResult = _swapChain.Present(0, PresentFlags.None);
        _presentCount++;
        var previousPresentCode = _lastPresentCode;
        _lastPresentCode = presentResult.Code;
        _occludedPresentStreak = _lastPresentCode == DxgiStatusOccluded ? _occludedPresentStreak + 1 : 0;
        if (_lastPresentCode != previousPresentCode)
        {
            var message = $"Present status changed. result={presentResult}, count={_presentCount}, " +
                          $"occludedStreak={_occludedPresentStreak}";
            if (_lastPresentCode == 0) ErrorLog.WriteInfo("D3D11", message);
            else ErrorLog.WriteWarning("D3D11", message);
        }
        if (presentResult.Failure)
            throw new InvalidOperationException(
                $"DXGI Present failed: {presentResult}; deviceRemovedReason={_device.DeviceRemovedReason}");
    }

    private void CreateDeviceAndComposition()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        Vortice.Direct3D11.D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, levels, out _device, out _context).CheckError();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, description, null);

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _compositionDevice = Vortice.DirectComposition.DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(_hwnd, true, out _compositionTarget).CheckError();
        _compositionDevice.CreateVisual(out _compositionVisual).CheckError();
        RefreshCompositionBinding();
    }

    private static string FormatResult(int result) => $"0x{unchecked((uint)result):X8}";

    private void CreateStaticResources()
    {
        if (!File.Exists(_shaderPath)) throw new FileNotFoundException("软件本地缺少 Direct3D shader。", _shaderPath);
        var source = Encoding.UTF8.GetBytes(File.ReadAllText(_shaderPath));
        var sceneVertexCode = CompileShader(source, _shaderPath, "SceneVS", "vs_5_0");
        var scenePixelCode = CompileShader(source, _shaderPath, "ScenePS", "ps_5_0");
        var trailVertexCode = CompileShader(source, _shaderPath, "TrailVS", "vs_5_0");
        var trailPixelCode = CompileShader(source, _shaderPath, "TrailPS", "ps_5_0");
        var fullscreenVertexCode = CompileShader(source, _shaderPath, "FullscreenVS", "vs_5_0");
        var prefilterCode = CompileShader(source, _shaderPath, "BloomPrefilterPS", "ps_5_0");
        var downsampleCode = CompileShader(source, _shaderPath, "BloomDownsamplePS", "ps_5_0");
        var upsampleCode = CompileShader(source, _shaderPath, "BloomUpsamplePS", "ps_5_0");
        var compositeCode = CompileShader(source, _shaderPath, "CompositePS", "ps_5_0");

        _sceneVertexShader = _device.CreateVertexShader(sceneVertexCode, null);
        _scenePixelShader = _device.CreatePixelShader(scenePixelCode, null);
        _trailVertexShader = _device.CreateVertexShader(trailVertexCode, null);
        _trailPixelShader = _device.CreatePixelShader(trailPixelCode, null);
        _fullscreenVertexShader = _device.CreateVertexShader(fullscreenVertexCode, null);
        _bloomPrefilterShader = _device.CreatePixelShader(prefilterCode, null);
        _bloomDownsampleShader = _device.CreatePixelShader(downsampleCode, null);
        _bloomUpsampleShader = _device.CreatePixelShader(upsampleCode, null);
        _compositeShader = _device.CreatePixelShader(compositeCode, null);
        _sceneInputLayout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
        ], sceneVertexCode);
        _trailInputLayout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32_Float, 16, 0)
        ], trailVertexCode);

        _sceneConstants = _device.CreateBuffer((uint)Marshal.SizeOf<SceneConstants>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        _postConstants = _device.CreateBuffer((uint)Marshal.SizeOf<PostConstants>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        var quadVertices = new[]
        {
            new GpuVertex(new Vector2(-0.5f, -0.5f), new Vector2(0, 0)),
            new GpuVertex(new Vector2( 0.5f, -0.5f), new Vector2(1, 0)),
            new GpuVertex(new Vector2( 0.5f,  0.5f), new Vector2(1, 1)),
            new GpuVertex(new Vector2(-0.5f,  0.5f), new Vector2(0, 1))
        };
        _quadVertexBuffer = _device.CreateBuffer(quadVertices, BindFlags.VertexBuffer, ResourceUsage.Immutable);
        _quadIndexBuffer = _device.CreateBuffer(new ushort[] { 0, 1, 2, 0, 2, 3 }, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        _trailVertexBuffer = _device.CreateBuffer(
            (uint)(MaximumTrailPoints * 2 * Marshal.SizeOf<TrailVertex>()), BindFlags.VertexBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        var trailIndices = new ushort[(MaximumTrailPoints - 1) * 6];
        for (var i = 0; i < MaximumTrailPoints - 1; i++)
        {
            var vertex = (ushort)(i * 2);
            var index = i * 6;
            trailIndices[index] = vertex;
            trailIndices[index + 1] = (ushort)(vertex + 1);
            trailIndices[index + 2] = (ushort)(vertex + 2);
            trailIndices[index + 3] = (ushort)(vertex + 1);
            trailIndices[index + 4] = (ushort)(vertex + 3);
            trailIndices[index + 5] = (ushort)(vertex + 2);
        }
        _trailIndexBuffer = _device.CreateBuffer(trailIndices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        LoadRingMesh(Path.Combine(_assetDirectory, "Cylinder002.obj"));

        _sceneSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
            TextureAddressMode.Wrap, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1,
            ComparisonFunction.Never, 0, float.MaxValue));
        _postSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
            TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        _noCullRasterizer = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _alphaBlend = _device.CreateBlendState(new BlendDescription(D3DBlend.SourceAlpha, D3DBlend.InverseSourceAlpha,
            D3DBlend.One, D3DBlend.InverseSourceAlpha));
        _additiveBlend = _device.CreateBlendState(new BlendDescription(D3DBlend.SourceAlpha, D3DBlend.One,
            D3DBlend.One, D3DBlend.InverseSourceAlpha));
        _opaqueBlend = _device.CreateBlendState(BlendDescription.Opaque);

        _circleTexture = LoadMaskTexture(Path.Combine(_assetDirectory, "FX_TEX_Circle_01.png"), true);
        _triangleTexture = LoadMaskTexture(Path.Combine(_assetDirectory, "FX_TEX_Triangle_02_1.png"), false);
        _trailTexture = LoadMaskTexture(Path.Combine(_assetDirectory, "FX_TEX_Trail_03.png"), true);
        _ringTexture = LoadMaskTexture(Path.Combine(_assetDirectory, "FX_TEX_Grad_Ring3.png"), false);
        _blackTexture = CreateTexture([0, 0, 0, 0], 1, 1);
    }

    private void CreateSizeDependentResources()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _swapChainTarget = _device.CreateRenderTargetView(backBuffer, null);
        _scene = CreateRenderTexture(_width, _height);
        _foreground = CreateRenderTexture(_width, _height);

        var bloomWidth = Math.Max(1, _width / 2);
        var bloomHeight = Math.Max(1, _height / 2);
        for (var i = 0; i < MaximumBloomLevels; i++)
        {
            _bloomDown.Add(CreateRenderTexture(bloomWidth, bloomHeight));
            if (i < MaximumBloomLevels - 1) _bloomUp.Add(CreateRenderTexture(bloomWidth, bloomHeight));
            if (bloomWidth <= 2 && bloomHeight <= 2) break;
            bloomWidth = Math.Max(1, bloomWidth / 2);
            bloomHeight = Math.Max(1, bloomHeight / 2);
        }

        while (_bloomUp.Count >= _bloomDown.Count)
        {
            _bloomUp[^1].Dispose();
            _bloomUp.RemoveAt(_bloomUp.Count - 1);
        }
    }

    private RenderTexture CreateRenderTexture(int width, int height)
    {
        var description = new Texture2DDescription(Format.R16G16B16A16_Float, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
            ResourceOptionFlags.None);
        var texture = _device.CreateTexture2D(in description);
        return new RenderTexture
        {
            Texture = texture,
            Target = _device.CreateRenderTargetView(texture, null),
            View = _device.CreateShaderResourceView(texture, null),
            Width = width,
            Height = height
        };
    }

    private unsafe TextureResource LoadMaskTexture(string path, bool luminanceAsAlpha)
    {
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var stride = Math.Abs(data.Stride);
        var sourceBytes = new byte[stride * bitmap.Height];
        Marshal.Copy(data.Scan0, sourceBytes, 0, sourceBytes.Length);
        bitmap.UnlockBits(data);

        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var sourceIndex = y * stride + x * 4;
            var targetIndex = (y * bitmap.Width + x) * 4;
            var alpha = luminanceAsAlpha
                ? Math.Max(sourceBytes[sourceIndex], Math.Max(sourceBytes[sourceIndex + 1], sourceBytes[sourceIndex + 2]))
                : sourceBytes[sourceIndex + 3];
            pixels[targetIndex] = 255;
            pixels[targetIndex + 1] = 255;
            pixels[targetIndex + 2] = 255;
            pixels[targetIndex + 3] = alpha;
        }
        return CreateTexture(pixels, bitmap.Width, bitmap.Height);
    }

    private TextureResource CreateTexture(byte[] pixels, int width, int height)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            var initialData = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * 4), 0);
            var texture = _device.CreateTexture2D(in description, initialData);
            return new TextureResource
            {
                Texture = texture,
                View = _device.CreateShaderResourceView(texture, null),
                Width = width,
                Height = height
            };
        }
        finally
        {
            handle.Free();
        }
    }

    private void LoadRingMesh(string path)
    {
        var positions = new List<Vector2>();
        var textureCoordinates = new List<Vector2>();
        var vertices = new List<GpuVertex>();
        var indices = new List<ushort>();
        var mappedVertices = new Dictionary<(int Position, int TexCoord), ushort>();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (line.StartsWith("v ", StringComparison.Ordinal) && parts.Length >= 3 &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                positions.Add(new Vector2(x, y));
            }
            else if (line.StartsWith("vt ", StringComparison.Ordinal) && parts.Length >= 3 &&
                     float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) &&
                     float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                textureCoordinates.Add(new Vector2(u, 1 - v));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal) && parts.Length >= 4)
            {
                for (var i = 1; i <= 3; i++)
                {
                    var pair = parts[i].Split('/');
                    if (pair.Length < 2 || !int.TryParse(pair[0], out var positionIndex) ||
                        !int.TryParse(pair[1], out var textureIndex))
                        throw new InvalidDataException($"无法读取圆弧网格面：{line}");
                    positionIndex--;
                    textureIndex--;
                    if (positionIndex < 0 || positionIndex >= positions.Count ||
                        textureIndex < 0 || textureIndex >= textureCoordinates.Count)
                        throw new InvalidDataException($"圆弧网格索引越界：{line}");

                    var key = (positionIndex, textureIndex);
                    if (!mappedVertices.TryGetValue(key, out var index))
                    {
                        if (vertices.Count >= ushort.MaxValue) throw new InvalidDataException("圆弧网格顶点过多。");
                        index = (ushort)vertices.Count;
                        mappedVertices[key] = index;
                        vertices.Add(new GpuVertex(positions[positionIndex], textureCoordinates[textureIndex]));
                    }
                    indices.Add(index);
                }
            }
        }

        if (vertices.Count == 0 || indices.Count == 0) throw new InvalidDataException("圆弧 OBJ 中没有可渲染的面。");
        _ringVertexBuffer = _device.CreateBuffer(vertices.ToArray(), BindFlags.VertexBuffer, ResourceUsage.Immutable);
        _ringIndexBuffer = _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer, ResourceUsage.Immutable);
        _ringIndexCount = (uint)indices.Count;
    }

    private void DrawGeometry(ID3D11Buffer vertexBuffer, ID3D11Buffer indexBuffer, uint indexCount,
        ID3D11ShaderResourceView texture, ref SceneConstants constants)
    {
        UpdateConstantBuffer(_sceneConstants, ref constants);
        _context.IASetVertexBuffer(0, vertexBuffer, (uint)Marshal.SizeOf<GpuVertex>(), 0);
        _context.IASetIndexBuffer(indexBuffer, Format.R16_UInt, 0);
        _context.PSSetShaderResource(0, texture);
        _context.DrawIndexed(indexCount, 0, 0);
    }

    private void RunFullscreenPass(RenderTexture target, ID3D11PixelShader shader,
        ID3D11ShaderResourceView source, ID3D11ShaderResourceView? lowMip, PostConstants constants)
    {
        UnbindPostProcessTextures();
        _context.OMSetRenderTargets(target.Target, null!);
        _context.RSSetViewport(new Viewport(target.Width, target.Height));
        _context.OMSetBlendState(_opaqueBlend);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullscreenVertexShader);
        _context.PSSetShader(shader);
        _context.PSSetSampler(0, _postSampler);
        _context.PSSetShaderResource(0, source);
        _context.PSSetShaderResource(1, lowMip ?? _blackTexture.View);
        UpdateConstantBuffer(_postConstants, ref constants);
        _context.PSSetConstantBuffer(1, _postConstants);
        _context.Draw(3, 0);
    }

    private unsafe void UpdateConstantBuffer<T>(ID3D11Buffer buffer, ref T value) where T : unmanaged
    {
        var mapped = _context.Map(buffer, MapMode.WriteDiscard, D3DMapFlags.None);
        *(T*)mapped.DataPointer = value;
        _context.Unmap(buffer);
    }

    private void UnbindPostProcessTextures()
    {
        _context.PSSetShaderResource(0, null!);
        _context.PSSetShaderResource(1, null!);
        _context.PSSetShaderResource(2, null!);
    }

    private void DisposeSizeDependentResources()
    {
        _swapChainTarget?.Dispose();
        _swapChainTarget = null;
        _scene?.Dispose();
        _scene = null;
        _foreground?.Dispose();
        _foreground = null;
        foreach (var texture in _bloomDown) texture.Dispose();
        foreach (var texture in _bloomUp) texture.Dispose();
        _bloomDown.Clear();
        _bloomUp.Clear();
    }

    private static byte[] CompileShader(byte[] source, string sourceName, string entryPoint, string target)
    {
        var result = D3DCompile(source, (nuint)source.Length, sourceName, IntPtr.Zero, IntPtr.Zero,
            entryPoint, target, ShaderCompileFlags, 0, out var code, out var errors);
        try
        {
            if (result < 0)
            {
                var message = errors != IntPtr.Zero ? Encoding.UTF8.GetString(CopyBlob(errors)).TrimEnd('\0') :
                    $"HRESULT 0x{result:X8}";
                throw new InvalidOperationException($"Direct3D shader {entryPoint} 编译失败：{message}");
            }
            return CopyBlob(code);
        }
        finally
        {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }

    private static byte[] CopyBlob(IntPtr blob)
    {
        var table = Marshal.ReadIntPtr(blob);
        var getPointer = Marshal.GetDelegateForFunctionPointer<GetBlobPointerDelegate>(
            Marshal.ReadIntPtr(table, IntPtr.Size * 3));
        var getSize = Marshal.GetDelegateForFunctionPointer<GetBlobSizeDelegate>(
            Marshal.ReadIntPtr(table, IntPtr.Size * 4));
        var size = checked((int)getSize(blob));
        var bytes = new byte[size];
        Marshal.Copy(getPointer(blob), bytes, 0, size);
        return bytes;
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        byte[] sourceData,
        nuint sourceDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines,
        IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errors);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.ClearState();
        DisposeSizeDependentResources();

        _blackTexture.Dispose();
        _ringTexture.Dispose();
        _trailTexture.Dispose();
        _triangleTexture.Dispose();
        _circleTexture.Dispose();
        _opaqueBlend.Dispose();
        _additiveBlend.Dispose();
        _alphaBlend.Dispose();
        _noCullRasterizer.Dispose();
        _postSampler.Dispose();
        _sceneSampler.Dispose();
        _ringIndexBuffer.Dispose();
        _ringVertexBuffer.Dispose();
        _trailIndexBuffer.Dispose();
        _trailVertexBuffer.Dispose();
        _quadIndexBuffer.Dispose();
        _quadVertexBuffer.Dispose();
        _postConstants.Dispose();
        _sceneConstants.Dispose();
        _trailInputLayout.Dispose();
        _sceneInputLayout.Dispose();
        _compositeShader.Dispose();
        _bloomUpsampleShader.Dispose();
        _bloomDownsampleShader.Dispose();
        _bloomPrefilterShader.Dispose();
        _fullscreenVertexShader.Dispose();
        _trailPixelShader.Dispose();
        _trailVertexShader.Dispose();
        _scenePixelShader.Dispose();
        _sceneVertexShader.Dispose();
        _compositionVisual.Dispose();
        _compositionTarget.Dispose();
        _compositionDevice.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

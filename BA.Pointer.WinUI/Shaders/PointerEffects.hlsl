cbuffer SceneConstants : register(b0)
{
    float2 ViewportSize;
    float2 DrawPosition;
    float2 DrawScale;
    float DrawRotation;
    float DissolveThreshold;
    float4 UvRectangle;
    float4 DrawColor;
    float Emission;
    float3 ScenePadding;
};

cbuffer PostConstants : register(b1)
{
    float2 SourceTexelSize;
    float BloomThreshold;
    float BloomKnee;
    float BloomScatter;
    float BloomIntensity;
    float2 PostPadding;
};

Texture2D SourceTexture : register(t0);
Texture2D LowMipTexture : register(t1);
Texture2D ForegroundTexture : register(t2);
SamplerState LinearClampSampler : register(s0);
SamplerState LinearWrapSampler : register(s1);

struct SceneVertexInput
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct SceneVertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

SceneVertexOutput SceneVS(SceneVertexInput input)
{
    SceneVertexOutput output;
    float sine;
    float cosine;
    sincos(DrawRotation, sine, cosine);

    float2 local = input.Position * DrawScale;
    float2 rotated = float2(
        local.x * cosine - local.y * sine,
        local.x * sine + local.y * cosine);
    float2 screen = DrawPosition + rotated;

    output.Position = float4(
        screen.x / ViewportSize.x * 2.0 - 1.0,
        1.0 - screen.y / ViewportSize.y * 2.0,
        0.0,
        1.0);
    output.TexCoord = lerp(UvRectangle.xy, UvRectangle.zw, input.TexCoord);
    return output;
}

float4 ScenePS(SceneVertexOutput input) : SV_TARGET
{
    float4 sampleColor = SourceTexture.Sample(LinearWrapSampler, input.TexCoord);
    float alpha = sampleColor.a * DrawColor.a;
    clip(sampleColor.a - DissolveThreshold);

    // This is the exported Shader Graph forward pass: texture RGB, material
    // HDR color, and particle color are multiplied before alpha blending.
    float3 color = sampleColor.rgb * DrawColor.rgb * Emission;
    return float4(color, alpha);
}

struct TrailVertexInput
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
    float Age : TEXCOORD1;
};

struct TrailVertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float Age : TEXCOORD1;
};

TrailVertexOutput TrailVS(TrailVertexInput input)
{
    TrailVertexOutput output;
    output.Position = float4(
        input.Position.x / ViewportSize.x * 2.0 - 1.0,
        1.0 - input.Position.y / ViewportSize.y * 2.0,
        0.0,
        1.0);
    output.TexCoord = input.TexCoord;
    output.Age = input.Age;
    return output;
}

float4 TrailPS(TrailVertexOutput input) : SV_TARGET
{
    float4 sampleColor = SourceTexture.Sample(LinearWrapSampler, input.TexCoord);
    float lifetimeFade = saturate(1.0 - input.Age);
    lifetimeFade = lifetimeFade * lifetimeFade * (3.0 - 2.0 * lifetimeFade);
    float alpha = sampleColor.a * DrawColor.a * lifetimeFade;
    clip(alpha - 0.0001);
    float3 color = sampleColor.rgb * DrawColor.rgb * Emission;
    return float4(color, alpha);
}

struct FullscreenVertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

FullscreenVertexOutput FullscreenVS(uint vertexId : SV_VertexID)
{
    FullscreenVertexOutput output;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    output.TexCoord = uv;
    return output;
}

float3 SampleBox13(Texture2D textureToSample, float2 uv, float2 texelSize)
{
    float3 a = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-2.0, -2.0)).rgb;
    float3 b = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 0.0, -2.0)).rgb;
    float3 c = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 2.0, -2.0)).rgb;
    float3 d = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-1.0, -1.0)).rgb;
    float3 e = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 1.0, -1.0)).rgb;
    float3 f = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-2.0,  0.0)).rgb;
    float3 g = textureToSample.Sample(LinearClampSampler, uv).rgb;
    float3 h = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 2.0,  0.0)).rgb;
    float3 i = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-1.0,  1.0)).rgb;
    float3 j = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 1.0,  1.0)).rgb;
    float3 k = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-2.0,  2.0)).rgb;
    float3 l = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 0.0,  2.0)).rgb;
    float3 m = textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 2.0,  2.0)).rgb;

    float3 result = (d + e + i + j) * 0.125;
    result += (a + b + f + g) * 0.03125;
    result += (b + c + g + h) * 0.03125;
    result += (f + g + k + l) * 0.03125;
    result += (g + h + l + m) * 0.03125;
    return result;
}

float3 ApplyBloomThreshold(float3 color)
{
    float brightness = max(color.r, max(color.g, color.b));
    float knee = max(BloomKnee, 0.00001);
    float soft = clamp(brightness - BloomThreshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 0.00001);
    float contribution = max(brightness - BloomThreshold, soft) / max(brightness, 0.00001);
    return color * contribution;
}

float4 BloomPrefilterPS(FullscreenVertexOutput input) : SV_TARGET
{
    float3 color = SampleBox13(SourceTexture, input.TexCoord, SourceTexelSize);
    return float4(ApplyBloomThreshold(color), 1.0);
}

float4 BloomDownsamplePS(FullscreenVertexOutput input) : SV_TARGET
{
    return float4(SampleBox13(SourceTexture, input.TexCoord, SourceTexelSize), 1.0);
}

float3 SampleTent9(Texture2D textureToSample, float2 uv, float2 texelSize)
{
    float3 result = textureToSample.Sample(LinearClampSampler, uv).rgb * 4.0;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-1.0,  0.0)).rgb * 2.0;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 1.0,  0.0)).rgb * 2.0;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 0.0, -1.0)).rgb * 2.0;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 0.0,  1.0)).rgb * 2.0;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-1.0, -1.0)).rgb;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 1.0, -1.0)).rgb;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2(-1.0,  1.0)).rgb;
    result += textureToSample.Sample(LinearClampSampler, uv + texelSize * float2( 1.0,  1.0)).rgb;
    return result * (1.0 / 16.0);
}

float4 BloomUpsamplePS(FullscreenVertexOutput input) : SV_TARGET
{
    float3 highMip = SourceTexture.Sample(LinearClampSampler, input.TexCoord).rgb;
    float3 lowMip = SampleTent9(LowMipTexture, input.TexCoord, SourceTexelSize);
    return float4(lerp(highMip, highMip + lowMip, BloomScatter), 1.0);
}

float3 AcesTonemap(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

float3 LinearToSrgb(float3 color)
{
    float3 low = color * 12.92;
    float3 high = 1.055 * pow(max(color, 0.0), 1.0 / 2.4) - 0.055;
    return lerp(high, low, step(color, 0.0031308));
}

float4 CompositePS(FullscreenVertexOutput input) : SV_TARGET
{
    float4 scene = SourceTexture.Sample(LinearClampSampler, input.TexCoord);
    float3 bloom = LowMipTexture.Sample(LinearClampSampler, input.TexCoord).rgb * BloomIntensity;
    float4 foreground = ForegroundTexture.Sample(LinearClampSampler, input.TexCoord);

    float glowAlpha = saturate(max(bloom.r, max(bloom.g, bloom.b)));
    float backgroundAlpha = 1.0 - (1.0 - saturate(scene.a)) * (1.0 - glowAlpha);
    float foregroundAlpha = saturate(foreground.a);
    float alpha = foregroundAlpha + backgroundAlpha * (1.0 - foregroundAlpha);
    float3 backgroundLinear = max(scene.rgb + bloom, 0.0);
    float3 backgroundStraight = backgroundAlpha > 0.00001 ? backgroundLinear / backgroundAlpha : 0.0;
    float3 backgroundColor = LinearToSrgb(AcesTonemap(backgroundStraight)) * backgroundAlpha;

    // Foreground fragments are UI-color particles. They are intentionally
    // composited after Bloom and bypass HDR tone mapping so white-to-blue
    // particle colors keep their visible separation.
    float3 foregroundStraight = foregroundAlpha > 0.00001 ? foreground.rgb / foregroundAlpha : 0.0;
    float3 foregroundColor = LinearToSrgb(saturate(foregroundStraight)) * foregroundAlpha;
    float3 outputColor = foregroundColor + backgroundColor * (1.0 - foregroundAlpha);
    return float4(min(outputColor, alpha), alpha);
}

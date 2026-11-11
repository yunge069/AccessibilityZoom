struct VertexInput
{
    float2 Position : POSITION;
    float2 Texcoord : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 Texcoord : TEXCOORD0;
};

PixelInput VSMain(VertexInput input)
{
    PixelInput output;
    output.Position = float4(input.Position, 0.0f, 1.0f);
    output.Texcoord = input.Texcoord;
    return output;
}

Texture2D<float4> DesktopFrame : register(t0);
SamplerState DesktopSampler : register(s0);

cbuffer ColorFilter : register(b0)
{
    // Gamma, additive brightness, contrast, saturation.
    float4 Tone;
    // RGB balance and a black-preserving dark-detail lift.
    float4 ColorAndShadows;
    // Highlight protection strength; remaining components are reserved.
    float4 Visibility;
};

float4 PSMain(PixelInput input) : SV_TARGET
{
    float4 sample = DesktopFrame.Sample(DesktopSampler, input.Texcoord);
    float3 color = sample.rgb;

    // Zero at black and white, strongest in the dark/midtone range. Applying
    // the curve per channel also keeps the fallback close to AMD's 1D LUT.
    float3 visibilityShape = 2.0f * color * (1.0f - color);
    color += ColorAndShadows.w * visibilityShape;

    color = saturate(color + Tone.y);
    color = pow(max(color, 0.00001f), 1.0f / max(Tone.x, 0.05f));
    color = (color - 0.5f) * Tone.z + 0.5f;
    color = saturate(color);

    float3 highlightShape = 4.0f * color * color * (1.0f - color);
    color -= Visibility.x * highlightShape;

    float adjustedLuma = dot(color, float3(0.2126f, 0.7152f, 0.0722f));
    color = lerp(adjustedLuma.xxx, color, Tone.w);
    color *= ColorAndShadows.rgb;

    return float4(saturate(color), sample.a);
}

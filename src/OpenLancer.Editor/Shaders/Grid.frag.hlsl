// MIT License - Copyright (c) Callum McGing, Librelancer Contributors 2013-2026
// Vendored from Librelancer src/Editor/LancerEdit/Shaders/Grid.frag.hlsl at commit dcbcf8080dec4a10ebe1ef28b89a0a6d122fe415.
// docs/VENDORED.md has the Librelancer license text and the OpenLancer changes to this file.

// Each member starts on a 16-byte boundary or fills the rest of one, so the packing is the same in HLSL and in the
// C# struct of GridRenderer.
cbuffer GridParameters : register(b3, UNIFORM_SPACE)
{
    float4 GridColor;
    float4 AxisXColor;
    float4 AxisZColor;
    float4x4 ViewProjection;
    float3 CameraPosition;
    float FadeDistance;
    float Scale;
    float FineAlpha;
};

struct Input
{
    float3 nearPoint: TEXCOORD0;
    float3 farPoint: TEXCOORD1;
};

struct Output
{
    float4 color: SV_Target0;
    float depth: SV_Depth;
};

float4 grid(float3 fragPos3D, float scale)
{
    float2 coord = fragPos3D.xz * scale;
    float2 derivative = fwidth(coord);
    float2 grid = abs(frac(coord - 0.5) - 0.5) / derivative;
    float lineF = min(grid.x, grid.y);
    float4 color = float4(GridColor.rgb, (1.0 - min(lineF, 1.0)) * GridColor.a);
    // The axis lines are as wide as the grid lines: one pixel on each side of the axis.
    float2 axisWidth = fwidth(fragPos3D.xz);
    // z axis
    if (abs(fragPos3D.x) < axisWidth.x)
        color = float4(AxisZColor.rgb, (1.0 - min(lineF, 1.0)) * AxisZColor.a);
    // x axis
    if (abs(fragPos3D.z) < axisWidth.y)
        color = float4(AxisXColor.rgb, (1.0 - min(lineF, 1.0)) * AxisXColor.a);
    return color;
}

float computeDepth(float3 pos)
{
    float4 clipSpace = mul(float4(pos.xyz, 1.0), ViewProjection);
    return clipSpace.z / clipSpace.w;
}

#define DepthRange_near 0.0
#define DepthRange_far 1.0
#define DepthRange_diff (DepthRange_far - DepthRange_near)

Output main(Input input)
{
    float t = -input.nearPoint.y / (input.farPoint.y - input.nearPoint.y);
    float3 fragPos3D = input.nearPoint + t * (input.farPoint - input.nearPoint);

    float depth = ((DepthRange_diff * computeDepth(fragPos3D)) +
                DepthRange_near + DepthRange_far) / 2.0;
    // The grid fades out with the distance from the camera along the plane, so it ends before the horizon.
    float fading = 1.0 - smoothstep(0.3 * FadeDistance, FadeDistance, distance(fragPos3D.xz, CameraPosition.xz));

    // Two levels of lines. The fine lines fade in while the camera comes down. Each pixel takes the stronger level, so
    // a line keeps its alpha when the levels change and a coarse line becomes a fine line.
    float4 fine = grid(fragPos3D, Scale * 10.0);
    fine.a *= FineAlpha;
    float4 coarse = grid(fragPos3D, Scale);
    float4 outColor = (fine.a > coarse.a ? fine : coarse) * float(t > 0.0);
    outColor.a *= fading;

    Output output;
    output.color = outColor;
    output.depth = depth;
    return output;
}

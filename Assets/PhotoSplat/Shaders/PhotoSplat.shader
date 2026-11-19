// One shader for the whole effect: every Gaussian splat of the SHARP scene is also a particle.
//   _PS_Gather 0..1 : particles leave the photo and swirl round the pinch point (_PS_HandPos)
//   _PS_Expand 0..1 : particles fly from the hand to their place in the 3D scene and grow into real splats
// Splats are drawn as screen-space ellipses from their projected 3D covariance (as in 3DGS / aras-p's
// UnityGaussianSplatting), back to front (the C# side sorts the index buffer), premultiplied alpha so the
// result composites correctly over passthrough.
Shader "PhotoSplat/Splat"
{
    Properties
    {
        [Toggle] _PS_FlipAxisY ("Extra ellipse Y flip (toggle if splats look smeared diagonally)", Float) = 0
        _PS_Glow ("Glow while flying", Range(0, 3)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "PhotoSplat"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            float4x4 _PS_SceneToWorld;   // SHARP scene space (Unity axes) -> world
            float4x4 _PS_PhotoToWorld;   // unit quad of the photo panel -> world
            float3 _PS_HandPos;
            float3 _PS_ThrowDir;
            float2 _PS_GrabUV;
            float _PS_Gather;
            float _PS_Expand;
            float _PS_SrcSigma;          // world-space size of a splat while it is still a "pixel" of the photo
            float _PS_DotSigma;          // world-space size of a flying particle
            float _PS_HoldRadius;
            float _PS_VigInner;
            float _PS_VigOuter;
            float _PS_FlipAxisY;
            float _PS_Glow;

            struct Attributes
            {
                float3 center : POSITION;   // splat centre, scene space
                float4 color  : COLOR;      // sRGB colour + opacity
                float4 c0     : TEXCOORD0;  // cov xx xy xz yy
                float4 c1     : TEXCOORD1;  // cov yz zz, photo uv
                float4 r      : TEXCOORD2;  // 3 randoms, quad corner 0..3
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color       : COLOR;
                float2 quad       : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Ease(float t) { return t * t * (3.0 - 2.0 * t); }
            float3 Bezier(float3 a, float3 b, float3 c, float t) { float u = 1.0 - t; return u * u * a + 2.0 * u * t * b + t * t * c; }

            // 2D covariance of a 3D Gaussian seen from the current eye (EWA splatting, as in aras-p's renderer).
            float3 Covariance2D(float3 worldPos, float3x3 cov3)
            {
                float3 viewPos = mul(UNITY_MATRIX_V, float4(worldPos, 1)).xyz;
                float p00 = abs(UNITY_MATRIX_P._m00), p11 = abs(UNITY_MATRIX_P._m11);
                float aspect = p00 / p11;
                float tanFovX = rcp(p00);
                float tanFovY = rcp(p11 * aspect);
                float limX = 1.3 * tanFovX, limY = 1.3 * tanFovY;
                viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
                viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;
                float focal = _ScreenParams.x * p00 * 0.5;
                float z2 = viewPos.z * viewPos.z;
                float3x3 J = float3x3(
                    focal / viewPos.z, 0, -(focal * viewPos.x) / z2,
                    0, focal / viewPos.z, -(focal * viewPos.y) / z2,
                    0, 0, 0);
                float3x3 T = mul(J, (float3x3)UNITY_MATRIX_V);
                float3x3 c = mul(T, mul(cov3, transpose(T)));
                c._m00 += 0.3; c._m11 += 0.3;   // at least ~1 px
                return float3(c._m00, c._m01, c._m11);
            }

            void Axes(float3 cov2d, out float2 v1, out float2 v2)
            {
                float mid = 0.5 * (cov2d.x + cov2d.z);
                float radius = length(float2((cov2d.x - cov2d.z) * 0.5, cov2d.y));
                float l1 = mid + radius;
                float l2 = max(mid - radius, 0.1);
                float2 dir = normalize(float2(cov2d.y, l1 - cov2d.x));
                // J above flips both screen axes (view z is negative), which leaves the covariance unchanged.
                // A projection that flips only y (render-texture convention on Vulkan/D3D) mirrors it: undo that.
                bool flip = (UNITY_MATRIX_P._m11 < 0.0) != (_PS_FlipAxisY > 0.5);
                if (flip) dir.y = -dir.y;
                v1 = min(sqrt(2.0 * l1), 1024.0) * dir;
                v2 = min(sqrt(2.0 * l2), 1024.0) * float2(dir.y, -dir.x);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                ZERO_INITIALIZE(Varyings, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float2 uv = v.c1.zw;
                float3 rnd = v.r.xyz;
                float corner = v.r.w;
                float t = _Time.y;

                // ---- 1. on the photo -------------------------------------------------------------
                float3 src = mul(_PS_PhotoToWorld, float4(uv.x - 0.5, uv.y - 0.5, 0, 1)).xyz;
                float3 photoN = -normalize(_PS_PhotoToWorld._m02_m12_m22);   // towards the viewer

                // ---- 2. swirling round the fingertips ---------------------------------------------
                float ang = rnd.x * 6.2832 + t * (1.2 + rnd.y * 2.5);
                float elev = rnd.z * 2.0 - 1.0;
                float ring = sqrt(saturate(1.0 - elev * elev));
                float rr = _PS_HoldRadius * (0.25 + 0.75 * sqrt(rnd.y));
                float3 cluster = _PS_HandPos + rr * float3(cos(ang) * ring, elev * 0.7 + 0.15 * sin(t * 2.3 + rnd.x * 40.0), sin(ang) * ring);

                float delayG = saturate(distance(uv, _PS_GrabUV) * 0.8 + rnd.x * 0.25) * 0.6;
                float g = Ease(saturate((_PS_Gather - delayG) / 0.4));
                float3 ctrlG = lerp(src, cluster, 0.35) + photoN * (0.05 + 0.08 * rnd.y) + float3(0, 0.05 * rnd.z, 0);
                float3 pG = Bezier(src, ctrlG, cluster, g);

                // ---- 3. out into the scene ---------------------------------------------------------
                float3 target = mul(_PS_SceneToWorld, float4(v.center, 1)).xyz;
                float delayE = rnd.y * 0.35 + saturate(length(uv - 0.5) * 1.4) * 0.15;   // centre first
                float e = Ease(saturate((_PS_Expand - delayE) / 0.5));
                float3 ctrlE = lerp(pG, target, 0.45) + _PS_ThrowDir * 0.2 + float3(0, 0.15 + 0.25 * rnd.z, 0);
                float3 worldPos = Bezier(pG, ctrlE, target, e);

                // ---- size: photo pixel -> particle -> real gaussian --------------------------------
                float3x3 M = (float3x3)_PS_SceneToWorld;
                float3x3 S = float3x3(v.c0.x, v.c0.y, v.c0.z,
                                      v.c0.y, v.c0.w, v.c1.x,
                                      v.c0.z, v.c1.x, v.c1.y);
                float3x3 covWorld = mul(M, mul(S, transpose(M)));
                float sig = lerp(_PS_SrcSigma, _PS_DotSigma * (0.6 + 0.8 * rnd.z), smoothstep(0.0, 0.3, g));
                float3x3 iso = float3x3(sig * sig, 0, 0, 0, sig * sig, 0, 0, 0, sig * sig);
                float3x3 cov3 = lerp(iso, covWorld, e * e);

                // ---- colour / alpha ----------------------------------------------------------------
                float4 col = v.color;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                col.rgb = SRGBToLinear(col.rgb);
                #endif
                float vig = 1.0 - smoothstep(_PS_VigInner, _PS_VigOuter, length((uv - 0.5) * 2.0));
                float alpha = lerp(lerp(1.0, 0.85, g), col.a * vig, e);
                col.rgb *= 1.0 + _PS_Glow * g * (1.0 - e) * (0.5 + rnd.x);

                // ---- project ------------------------------------------------------------------------
                float4 centerCS = TransformWorldToHClip(worldPos);
                if (centerCS.w <= 0.0 || alpha < 0.004)
                {
                    o.positionCS = float4(2, 2, 2, 1);   // outside the clip volume: dropped
                    return o;
                }
                float2 a1, a2;
                Axes(Covariance2D(worldPos, cov3), a1, a2);
                float2 q = float2(fmod(corner, 2.0), floor(corner * 0.5)) * 2.0 - 1.0;
                q *= 2.0;
                float2 deltaScreen = (q.x * a1 + q.y * a2) * 2.0 / _ScreenParams.xy;
                o.positionCS = centerCS;
                o.positionCS.xy += deltaScreen * centerCS.w;
                o.quad = q;
                o.color = half4(col.rgb, alpha);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float a = exp(-dot(i.quad, i.quad)) * i.color.a;
                if (a < 1.0 / 255.0) discard;
                return half4(i.color.rgb * a, a);
            }
            ENDHLSL
        }
    }
}

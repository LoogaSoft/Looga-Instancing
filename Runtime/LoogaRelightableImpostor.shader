Shader "Looga/Instancing/Relightable Impostor"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo and opacity", 2D) = "white" {}
        _NormalAtlas("Object normals", 2D) = "bump" {}
        _MaterialAtlas("Metallic, roughness, occlusion, emission", 2D) = "black" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha cutoff", Range(0,1)) = 0.45
        _ViewGrid("View grid", Vector) = (1,1,0,0)
        _ImpostorMode("Mode", Float) = 0
        _BoundsMin("Bounds minimum", Vector) = (0,0,0,0)
        _BoundsSize("Bounds size", Vector) = (1,1,1,0)
        _Wind("Wind amplitude, speed, scale, phase", Vector) = (0,1,0.05,0)
        _WindParams("Wind amplitude, speed, height, base", Vector) = (0,1,1,0)
        _WindDirection("Wind direction", Vector) = (1,0,0,0)
        _BranchWind("Branch wind", Vector) = (0,1,1,0)
        _LeafWind("Leaf wind", Vector) = (0,1,0,0)
        _InteractionParams("Interaction", Vector) = (0,0,0,0)
        _GroundParams("Ground", Vector) = (0,0,0,1)
        [Toggle] _LoogaFixedLighting("Fixed lighting fast path", Float) = 0
        _LoogaFixedLightDirection("Fixed light direction and intensity", Vector) = (0,1,0,1)
        _LoogaFixedLightColor("Fixed light color", Color) = (1,1,1,1)
        _LoogaFixedAmbient("Fixed ambient", Color) = (0.22,0.22,0.22,1)
        [HideInInspector] _LoogaLightmapColor("Baked illumination", Color) = (1,1,1,1)
        [HideInInspector] _LoogaInstanceData("Instance data", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="TransparentCutout"
            "Queue"="AlphaTest"
            "UniversalMaterialType"="Lit"
            "LoogaInstanceDeformation"="Vegetation"
            "AlwaysRenderMotionVectors"="true"
        }
        Cull Off
        ZWrite On

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MotionVectorsCommon.hlsl"

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_NormalAtlas); SAMPLER(sampler_NormalAtlas);
        TEXTURE2D(_MaterialAtlas); SAMPLER(sampler_MaterialAtlas);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _ViewGrid;
            float4 _BoundsMin;
            float4 _BoundsSize;
            float4 _Wind;
            float4 _LoogaFixedLightDirection;
            float4 _LoogaFixedLightColor;
            float4 _LoogaFixedAmbient;
            float _Cutoff;
            float _ImpostorMode;
            float _LoogaFixedLighting;
        CBUFFER_END

        struct ImpostorAttributes
        {
            float3 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float2 view : TEXCOORD1;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct ImpostorVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
            float4 motionCurrent : TEXCOORD2;
            float4 motionPrevious : TEXCOORD3;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        float2 OctEncode(float3 direction)
        {
            direction /= abs(direction.x) + abs(direction.y) + abs(direction.z) + 0.00001;
            float2 value = direction.xz;
            if (direction.y < 0)
            {
                value = (1 - abs(value.yx)) * (value >= 0 ? 1 : -1);
            }
            return value * 0.5 + 0.5;
        }

        float2 AtlasUv(float2 sourceUv, float frame)
        {
            float2 grid = max(1, floor(_ViewGrid.xy + 0.5));
            float2 cell = float2(fmod(frame, grid.x), floor(frame / grid.x));
            return (sourceUv + cell) / grid;
        }

        float FrameFromCamera(float3 originWS, float fallback)
        {
            if (_ImpostorMode < 0.5) return fallback;
            float3 viewWS = normalize(_WorldSpaceCameraPos - originWS);
            float3 viewOS = normalize(TransformWorldToObjectDir(viewWS));
            float2 encoded = saturate(OctEncode(viewOS));
            float2 grid = max(1, floor(_ViewGrid.xy + 0.5));
            float2 cell = min(grid - 1, floor(encoded * grid));
            return cell.y * grid.x + cell.x;
        }

        float3 WindOffset(float3 originWS, float3 positionOS, float time)
        {
            float height = saturate((positionOS.y - _BoundsMin.y) / max(_BoundsSize.y, 0.001));
            float phase = dot(originWS.xz, float2(0.071, 0.113)) + _Wind.w;
            float sway = sin(time * _Wind.y + phase) * _Wind.x * height * height;
            return float3(sway, 0, sway * _Wind.z);
        }

        float3 ImpostorWorldPosition(float3 positionOS, float3 originWS, float time)
        {
            float3 positionWS;
            if (_ImpostorMode > 0.5)
            {
                float3 right = normalize(UNITY_MATRIX_I_V[0].xyz);
                float3 up = normalize(UNITY_MATRIX_I_V[1].xyz);
                float sx = length(UNITY_MATRIX_M[0].xyz);
                float sy = length(UNITY_MATRIX_M[1].xyz);
                positionWS = originWS + right * positionOS.x * sx + up * positionOS.y * sy;
            }
            else
            {
                positionWS = TransformObjectToWorld(positionOS);
            }
            return positionWS + WindOffset(originWS, positionOS, time);
        }

        ImpostorVaryings ImpostorVertex(ImpostorAttributes input)
        {
            ImpostorVaryings output = (ImpostorVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 originWS = TransformObjectToWorld(float3(0, 0, 0));
            float frame = FrameFromCamera(originWS, input.view.x);
            float3 positionWS = ImpostorWorldPosition(input.positionOS, originWS, _Time.y);
            output.positionWS = positionWS;
            output.positionCS = TransformWorldToHClip(positionWS);
            output.uv = AtlasUv(input.uv, frame);
            output.motionCurrent = mul(_NonJitteredViewProjMatrix, float4(positionWS, 1));
            float3 previousOrigin = mul(UNITY_PREV_MATRIX_M, float4(0, 0, 0, 1)).xyz;
            float3 previousPosition = mul(UNITY_PREV_MATRIX_M, float4(input.positionOS, 1)).xyz;
            previousPosition += WindOffset(previousOrigin, input.positionOS, _Time.y - unity_DeltaTime.x);
            output.motionPrevious = mul(_PrevViewProjMatrix, float4(previousPosition, 1));
            return output;
        }

        float4 SampleAlbedo(ImpostorVaryings input)
        {
            float4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            clip(albedo.a - _Cutoff);
            #if defined(LOD_FADE_CROSSFADE)
                LODFadeCrossFade(input.positionCS);
            #endif
            return albedo;
        }

        float3 SampleNormalWS(float2 uv)
        {
            float3 normalOS = normalize(SAMPLE_TEXTURE2D(_NormalAtlas, sampler_NormalAtlas, uv).xyz * 2 - 1);
            return normalize(TransformObjectToWorldNormal(normalOS));
        }

        half4 ForwardFragment(ImpostorVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            float4 albedo = SampleAlbedo(input);
            float4 mask = SAMPLE_TEXTURE2D(_MaterialAtlas, sampler_MaterialAtlas, input.uv);
            float3 normalWS = SampleNormalWS(input.uv);
            float3 color;
            if (_LoogaFixedLighting > 0.5)
            {
                float diffuse = saturate(dot(normalWS, normalize(_LoogaFixedLightDirection.xyz)));
                color = albedo.rgb * (_LoogaFixedAmbient.rgb * mask.b +
                    _LoogaFixedLightColor.rgb * diffuse * _LoogaFixedLightDirection.w);
            }
            else
            {
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float diffuse = saturate(dot(normalWS, mainLight.direction));
                float3 ambient = SampleSH(normalWS) * mask.b;
                color = albedo.rgb * (ambient + mainLight.color * diffuse * mainLight.shadowAttenuation);
            }
            color += albedo.rgb * mask.a;
            return half4(color, albedo.a);
        }

        half4 DepthFragment(ImpostorVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            SampleAlbedo(input);
            return 0;
        }

        half4 DepthNormalsFragment(ImpostorVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            SampleAlbedo(input);
            return half4(SampleNormalWS(input.uv) * 0.5 + 0.5, 0);
        }

        half4 MotionFragment(ImpostorVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            SampleAlbedo(input);
            return half4(CalcNdcMotionVectorFromCsPositions(input.motionCurrent, input.motionPrevious), 0, 0);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ImpostorVertex
            #pragma fragment ForwardFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ImpostorVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ImpostorVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ImpostorVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode"="MotionVectors" }
            ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ImpostorVertex
            #pragma fragment MotionFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}

Shader "Hidden/Looga/Instancing/Impostor Capture"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _MainTex("Main Texture", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _Color("Color", Color) = (1,1,1,1)
        _Cutoff("Cutoff", Range(0,1)) = 0.33
        _Metallic("Metallic", Range(0,1)) = 0
        _Smoothness("Smoothness", Range(0,1)) = 0.2
        _OcclusionStrength("Occlusion", Range(0,1)) = 1
        _EmissionColor("Emission", Color) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite On
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"

            sampler2D _BaseMap;
            sampler2D _MainTex;
            float4 _BaseMap_ST;
            float4 _MainTex_ST;
            float4 _BaseColor;
            float4 _Color;
            float _Cutoff;
            float _Metallic;
            float _Smoothness;
            float _OcclusionStrength;
            float4 _EmissionColor;
            int _LoogaImpostorCaptureMode;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalOS : TEXCOORD0;
                float2 uv0 : TEXCOORD1;
                float2 uv1 : TEXCOORD2;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.normalOS = normalize(UnityObjectToWorldNormal(input.normalOS));
                output.uv0 = TRANSFORM_TEX(input.uv, _BaseMap);
                output.uv1 = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            float4 Fragment(Varyings input) : SV_Target
            {
                float4 albedo = tex2D(_BaseMap, input.uv0) * tex2D(_MainTex, input.uv1) * _BaseColor * _Color;
                clip(albedo.a - _Cutoff);
                if (_LoogaImpostorCaptureMode == 1)
                {
                    return float4(normalize(input.normalOS) * 0.5 + 0.5, albedo.a);
                }
                if (_LoogaImpostorCaptureMode == 2)
                {
                    float emission = max(_EmissionColor.r, max(_EmissionColor.g, _EmissionColor.b));
                    return float4(_Metallic, 1 - _Smoothness, _OcclusionStrength, saturate(emission));
                }
                return albedo;
            }
            ENDHLSL
        }
    }
}

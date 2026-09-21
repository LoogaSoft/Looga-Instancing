using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Actual URP pass coverage, inspected before instance ownership.</summary>
    [Flags]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceShaderCapabilities")]
    public enum InstanceShaderCapabilities
    {
        None = 0, Surface = 1, Depth = 2, DepthNormals = 4, Shadow = 8, Motion = 16, Deferred = 32
    }

    /// <summary>Shader diagnostics without a shader-name allow-list.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceShaderInspection")]
    public static class InstanceShaderInspection
    {
        /// <summary>Read LightMode tags from the shader's active subshader.</summary>
        public static InstanceShaderCapabilities Inspect(Material material)
        {
            if (!material) return InstanceShaderCapabilities.None;
            var result = InstanceShaderCapabilities.None;
            int subshader = FindUrpSubshader(material.shader);
            if (subshader < 0) return result;
            for (int i = 0; i < material.shader.GetPassCountInSubshader(subshader); i++)
            {
                string mode = material.shader.FindPassTagValue(subshader, i, new ShaderTagId("LightMode")).name;
                if (!material.GetShaderPassEnabled(mode))
                {
                    continue;
                }
                switch (mode.ToLowerInvariant())
                {
                    case "": case "universalforward": case "universalforwardonly": case "srpdefaultunlit": result |= InstanceShaderCapabilities.Surface; break;
                    case "universalgbuffer": result |= InstanceShaderCapabilities.Surface | InstanceShaderCapabilities.Deferred; break;
                    case "depthonly": result |= InstanceShaderCapabilities.Depth; break;
                    case "depthnormals": case "depthnormalsonly": result |= InstanceShaderCapabilities.DepthNormals; break;
                    case "shadowcaster": result |= InstanceShaderCapabilities.Shadow; break;
                    case "motionvectors": result |= InstanceShaderCapabilities.Motion; break;
                }
            }
            return result;
        }
        /// <summary>Check standard DOTS variants and the caller's required passes. This does not change the material.</summary>
        /// <remarks>Editor checks include custom draw passes. Player checks cannot detect variants removed during a build.</remarks>
        public static bool TryValidate(Material material, InstanceShaderCapabilities required, out string reason)
        {
            if (!material || !material.shader || !material.shader.isSupported)
            {
                reason = "The material needs a supported shader.";
                return false;
            }
            if (material.renderQueue > 2500)
            {
                reason = "Transparent instance sorting is not supported.";
                return false;
            }
#if UNITY_EDITOR
            if (UnityEditor.ShaderUtil.ShaderHasError(material.shader))
            {
                reason = "The shader has compiler errors. Keep the source renderer native and resolve the shader diagnostics before conversion.";
                return false;
            }
#endif
            var missing = required & ~Inspect(material);
            if (missing != InstanceShaderCapabilities.None)
            {
                reason = "Missing enabled passes: " + missing;
                return false;
            }
            Shader shader = material.shader;
            if (!shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON").isValid)
            {
                reason = "The shader has no DOTS_INSTANCING_ON variant.";
                return false;
            }
#if UNITY_EDITOR
            var data = UnityEditor.ShaderUtil.GetShaderData(shader);
            int subshader = FindUrpSubshader(shader);
            if (subshader < 0)
            {
                reason = "The shader has no URP subshader.";
                return false;
            }
            for (int i = 0; i < material.shader.GetPassCountInSubshader(subshader); i++)
            {
                string mode = shader.FindPassTagValue(subshader, i, new ShaderTagId("LightMode")).name;
                string lower = mode.ToLowerInvariant();
                if (!material.GetShaderPassEnabled(mode) || lower == "meta" || lower == "sceneselectionpass" ||
                    lower == "picking" || lower == "universal2d" || lower == "xrmotionvectors")
                {
                    continue;
                }
                // GetPassKeywords emits "Snippet not found" for UsePass. Inspect Unity's resolved compiler declarations.
                bool hasDots = data.GetSubshader(subshader).GetPass(i).SourceCode.Contains("#define DOTS_INSTANCING_ON_KEYWORD_DECLARED 1");
                if (!hasDots)
                {
                    reason = "Pass '" + mode + "' has no DOTS_INSTANCING_ON variant.";
                    return false;
                }
            }
#endif
            reason = null;
            return true;
        }
        private static int FindUrpSubshader(Shader shader)
        {
            for (int i = 0; i < shader.subshaderCount; i++)
            {
                if (shader.FindSubshaderTagValue(i, new ShaderTagId("RenderPipeline")).name == "UniversalPipeline") return i;
            }
            return -1;
        }
    }
}

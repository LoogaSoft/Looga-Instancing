using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Choose direct, shared or two-level visibility selection.</summary>
    public enum InstanceVisibilityMode
    {
        /// <summary>Evaluate each draw part independently, with no shared selection storage.</summary>
        Direct,
        /// <summary>Evaluate prototype visibility once, then reuse it across draw parts.</summary>
        Shared,
        /// <summary>Reject conservative 64-slot clusters before individual bounds.</summary>
        Hierarchical,
        /// <summary>Reuse cluster bounds until source uploads invalidate them.</summary>
        CachedHierarchy
    }

    /// <summary>Override the source shadow mode for one prototype.</summary>
    public enum InstanceShadowMode
    {
        Inherit = 0,
        On = 1,
        Off = 2,
        TwoSided = 3
    }

    /// <summary>Explicit prototype quality policy. Decorative reductions never change collision residency.</summary>
    [Serializable]
    public struct InstanceQualitySettings
    {
        /// <summary>Allow visual population reduction for sources without gameplay colliders.</summary>
        public bool Decorative;
        /// <summary>Stable admitted fraction. One retains every instance.</summary>
        [Range(0, 1)] public float Density;
        /// <summary>Minimum projected bound diameter in pixels. Zero disables size rejection.</summary>
        [Min(0)] public float MinimumPixels;
        /// <summary>Camera distance where stable density thinning starts. Zero disables the falloff.</summary>
        [Min(0)] public float DensityFalloffDistance;
        /// <summary>Admitted fraction of the visible density at the view limit. The falloff is linear from its start.</summary>
        [Range(0, 1)] public float FarDensity;
        /// <summary>Maximum camera range in metres. Zero inherits renderer and native rules.</summary>
        [Min(0)] public float ViewDistance;
        /// <summary>Camera fade band before the view limit. Zero disables the fade.</summary>
        [Min(0)] public float ViewFadeDistance;
        /// <summary>Prototype LOD multiplier. Zero preserves the original multiplier.</summary>
        [Min(0)] public float LodBias;
        /// <summary>Shadow casting mode. Inherit keeps source renderer modes.</summary>
        public InstanceShadowMode ShadowMode;
        /// <summary>Maximum shadow range in metres. Zero inherits the renderer limit.</summary>
        [Min(0)] public float ShadowDistance;
        /// <summary>Distance band for stable stochastic whole-instance shadow reduction.</summary>
        [Min(0)] public float ShadowFadeDistance;
        /// <summary>Finest permitted shadow LOD, combined with renderer and quality limits.</summary>
        [Range(0, 7)] public int MinimumShadowLod;
        /// <summary>Number of admitted directional shadow cascades. Zero retains every cascade; point and spot projections are unchanged.</summary>
        [Range(0, 16)] public int ShadowSplits;
        /// <summary>
        /// Minimum bound diameter in shadow texels of each directional split, where the shadow renderer publishes
        /// split texel sizes. Zero inherits the renderer value.
        /// </summary>
        [Min(0)] public float MinimumShadowTexels;

        /// <summary>Full density with inherited renderer shadow limits.</summary>
        public static InstanceQualitySettings Default => new InstanceQualitySettings { Density = 1 };

        internal void Validate(bool hasColliders)
        {
            if (!float.IsFinite(Density) || Density < 0 || Density > 1 ||
                !float.IsFinite(MinimumPixels) || MinimumPixels < 0 ||
                !float.IsFinite(DensityFalloffDistance) || DensityFalloffDistance < 0 ||
                !float.IsFinite(FarDensity) || FarDensity < 0 || FarDensity > 1 ||
                !float.IsFinite(ViewDistance) || ViewDistance < 0 ||
                !float.IsFinite(ViewFadeDistance) || ViewFadeDistance < 0 ||
                !float.IsFinite(LodBias) || LodBias < 0 ||
                !float.IsFinite(ShadowDistance) || ShadowDistance < 0 ||
                !float.IsFinite(ShadowFadeDistance) || ShadowFadeDistance < 0 ||
                MinimumShadowLod < 0 || MinimumShadowLod > 7 || ShadowSplits < 0 || ShadowSplits > 16 ||
                !float.IsFinite(MinimumShadowTexels) || MinimumShadowTexels < 0 ||
                ShadowMode < InstanceShadowMode.Inherit || ShadowMode > InstanceShadowMode.TwoSided)
            {
                throw new ArgumentOutOfRangeException(nameof(InstanceQualitySettings));
            }
            if ((Density < 1 || MinimumPixels > 0 || DensityFalloffDistance > 0) && (!Decorative || hasColliders))
            {
                throw new InvalidOperationException("Density and small-object reductions require a decorative prototype without colliders.");
            }
        }
    }
}

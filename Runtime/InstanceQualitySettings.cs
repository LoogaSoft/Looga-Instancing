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
        /// <summary>Maximum shadow range in metres. Zero inherits the renderer limit.</summary>
        [Min(0)] public float ShadowDistance;
        /// <summary>Distance band for stable stochastic whole-instance shadow reduction.</summary>
        [Min(0)] public float ShadowFadeDistance;
        /// <summary>Finest permitted shadow LOD, combined with renderer and quality limits.</summary>
        [Range(0, 7)] public int MinimumShadowLod;
        /// <summary>Number of admitted directional shadow cascades. Zero retains every cascade; point and spot projections are unchanged.</summary>
        [Range(0, 16)] public int ShadowSplits;

        /// <summary>Full density with inherited renderer shadow limits.</summary>
        public static InstanceQualitySettings Default => new InstanceQualitySettings { Density = 1 };

        internal void Validate(bool hasColliders)
        {
            if (!float.IsFinite(Density) || Density < 0 || Density > 1 ||
                !float.IsFinite(MinimumPixels) || MinimumPixels < 0 ||
                !float.IsFinite(ShadowDistance) || ShadowDistance < 0 ||
                !float.IsFinite(ShadowFadeDistance) || ShadowFadeDistance < 0 ||
                MinimumShadowLod < 0 || MinimumShadowLod > 7 || ShadowSplits < 0 || ShadowSplits > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(InstanceQualitySettings));
            }
            if ((Density < 1 || MinimumPixels > 0) && (!Decorative || hasColliders))
            {
                throw new InvalidOperationException("Density and small-object reductions require a decorative prototype without colliders.");
            }
        }
    }
}

using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Deterministic mesh placement controls. Density uses world-space surface area.</summary>
    [Serializable]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "MeshScatterSettings")]
    public sealed class MeshScatterSettings
    {
        /// <summary>Seed for placement, acceptance, rotation and prototype selection.</summary>
        public int Seed = 1;
        /// <summary>Candidate count per square meter before filters and painting.</summary>
        [Min(0)] public float Density = 0.1f;
        /// <summary>Maximum candidates per rebuild, including candidates rejected by masks.</summary>
        [Min(1)] public int CandidateLimit = 100000;
        /// <summary>Allowed world heights, in meters.</summary>
        public Vector2 Height = new(-10000, 10000);
        /// <summary>Height filter fade distance inside each boundary.</summary>
        [Min(0)] public float HeightFalloff;
        /// <summary>Allowed surface slopes in degrees from world up.</summary>
        public Vector2 Slope = new(0, 60);
        /// <summary>Slope filter fade angle inside each boundary.</summary>
        [Min(0)] public float SlopeFalloff;
        /// <summary>Preferred normal heading, clockwise from world north.</summary>
        [Range(0, 360)] public float Aspect;
        /// <summary>Allowed angle from the preferred heading. 180 disables this filter.</summary>
        [Range(0, 180)] public float AspectRange = 180;
        /// <summary>Aspect filter fade angle inside its boundary.</summary>
        [Min(0)] public float AspectFalloff;
        /// <summary>Uniform world scale range.</summary>
        public Vector2 Scale = Vector2.one;
        /// <summary>Rotation range around the placement up axis.</summary>
        public Vector2 Yaw = new(0, 360);
        /// <summary>Offset range along the surface normal, in world meters.</summary>
        public Vector2 NormalOffset;
        /// <summary>Blend from world up to the sampled surface normal.</summary>
        [Range(0, 1)] public float NormalAlignment = 1;

        /// <summary>Return an independent settings copy.</summary>
        public MeshScatterSettings Copy() => (MeshScatterSettings)MemberwiseClone();

        /// <summary>Reject nonfinite, inverted or unsupported ranges before generation.</summary>
        public void Validate()
        {
            CheckRange(Height); CheckRange(Slope); CheckRange(Scale); CheckRange(Yaw); CheckRange(NormalOffset);
            foreach (float value in new[] { Density, HeightFalloff, SlopeFalloff, AspectFalloff, Aspect, AspectRange, NormalAlignment })
            {
                if (!float.IsFinite(value) || value < 0)
                {
                    throw new ArgumentException("Scatter values must be finite and nonnegative.");
                }
            }
            if (CandidateLimit < 1 || Scale.x <= 0 || Slope.x < 0 || Slope.y > 180 || Aspect > 360 ||
                AspectRange > 180 || NormalAlignment > 1)
            {
                throw new ArgumentException("Scatter ranges exceed their supported limits.");
            }
        }

        private static void CheckRange(Vector2 value)
        {
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || value.x > value.y)
            {
                throw new ArgumentException("Scatter ranges must be finite and ordered.");
            }
        }
    }
}

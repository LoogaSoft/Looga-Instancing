using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Near, middle, and far forest shadow policy shared by source trees and HLODs.</summary>
    [Serializable]
    public struct ForestShadowPolicy
    {
        [Min(0)] public float NearDistance;
        [Min(0)] public float MiddleDistance;
        [Min(0)] public float FarDistance;
        [Range(0, 7)] public int MiddleMinimumLod;
        [Range(0, 7)] public int FarMinimumLod;
        [Min(1)] public int MiddleUpdateInterval;
        [Min(1)] public int FarUpdateInterval;

        public static ForestShadowPolicy Default => new ForestShadowPolicy
        {
            NearDistance = 80, MiddleDistance = 220, FarDistance = 600,
            MiddleMinimumLod = 1, FarMinimumLod = 2,
            MiddleUpdateInterval = 2, FarUpdateInterval = 8
        };

        public void Validate()
        {
            if (!float.IsFinite(NearDistance) || !float.IsFinite(MiddleDistance) || !float.IsFinite(FarDistance) ||
                NearDistance < 0 || MiddleDistance < NearDistance || FarDistance < MiddleDistance ||
                MiddleUpdateInterval < 1 || FarUpdateInterval < MiddleUpdateInterval)
            {
                throw new ArgumentException("Forest shadow ranges and update intervals must increase from near to far.");
            }
        }

        public int MinimumLod(float distance)
        {
            if (distance <= NearDistance) return 0;
            if (distance <= MiddleDistance) return MiddleMinimumLod;
            return FarMinimumLod;
        }

        public int UpdateInterval(float distance)
        {
            if (distance <= NearDistance) return 1;
            if (distance <= MiddleDistance) return MiddleUpdateInterval;
            return FarUpdateInterval;
        }
    }
}

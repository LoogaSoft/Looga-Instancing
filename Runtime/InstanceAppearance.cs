using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Optional per-instance material channels. Default values do not allocate appearance streams.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceAppearance")]
    public readonly struct InstanceAppearance : IEquatable<InstanceAppearance>
    {
        /// <summary>Multiplier for the source material color.</summary>
        public Color Tint { get; }
        /// <summary>Multiplier for baked illumination in Looga vegetation materials.</summary>
        public Color LightmapColor { get; }
        /// <summary>Custom shader values. Use X for a stable random value when required.</summary>
        public Vector4 Custom { get; }
        /// <summary>Material defaults with no custom data.</summary>
        public static InstanceAppearance Default { get; } = new InstanceAppearance(Color.white, Color.white, Vector4.zero);

        /// <summary>Create finite material channels without changing source materials.</summary>
        public InstanceAppearance(Color tint, Color lightmapColor, Vector4 custom)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!float.IsFinite(tint[i]) || !float.IsFinite(lightmapColor[i]) || !float.IsFinite(custom[i]))
                {
                    throw new ArgumentException("Instance material channels must be finite.");
                }
            }
            Tint = tint;
            LightmapColor = lightmapColor;
            Custom = custom;
        }
        /// <summary>Compare material channels without boxing per-instance values.</summary>
        public bool Equals(InstanceAppearance other)
        {
            return Tint.Equals(other.Tint) && LightmapColor.Equals(other.LightmapColor) && Custom.Equals(other.Custom);
        }
        public override bool Equals(object other) => other is InstanceAppearance appearance && Equals(appearance);
        public override int GetHashCode() => HashCode.Combine(Tint, LightmapColor, Custom);
    }
}

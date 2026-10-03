using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>
    /// What a custom shadow renderer tells instancing about its lights' shadow culls. Texel sizes: the culling context
    /// carries each split's volume but not its resolution. With them, shadow culling measures casters in texels of
    /// each split: it skips casters below a minimum texel size and floors their LOD. Ignored culls: culls whose casters
    /// the shadow renderer never draws.
    /// </summary>
    public static class InstanceShadowSplits
    {
        /// <summary>Most splits a culling view can describe.</summary>
        public const int MaximumSplits = 16;

        private sealed class Entry
        {
            internal readonly float[] TexelSizes = new float[MaximumSplits];
            internal int Count;
            internal int Frame;
            internal int DynamicOnlySplits;
        }

        private static readonly Dictionary<int, Entry> Entries = new();
        private static readonly Dictionary<int, int> IgnoredLights = new();

        /// <summary>
        /// Changes whenever the static shadow casters of any instance renderer change: instances, visibility,
        /// quality, uploads, rendering state or disposal. A shadow renderer that caches static casters redraws
        /// them when this value changes.
        /// </summary>
        public static long StaticCasterRevision { get; private set; }

        internal static void NotifyStaticCastersChanged()
        {
            StaticCasterRevision++;
        }

        /// <summary>
        /// Publish the world-space texel size of each split for the light's next shadow cull, in split order.
        /// Call this from the main thread before culling. Values expire after the current frame, so a shadow
        /// renderer that stops publishing stops applying texel rules. Bit i of <paramref name="dynamicOnlySplits"/>
        /// marks split i as drawn without static shadow casters, which the shadow renderer caches: static
        /// renderers then skip that split.
        /// </summary>
        public static void SetTexelSizes(Light light, ReadOnlySpan<float> texelSizes, int dynamicOnlySplits = 0)
        {
            if (light == null) throw new ArgumentNullException(nameof(light));
            if (texelSizes.Length > MaximumSplits) throw new ArgumentOutOfRangeException(nameof(texelSizes));
            int id = light.GetInstanceID();
            if (!Entries.TryGetValue(id, out Entry entry))
            {
                entry = new Entry();
                Entries.Add(id, entry);
            }
            for (int i = 0; i < texelSizes.Length; i++)
            {
                float size = texelSizes[i];
                if (!float.IsFinite(size) || size <= 0) throw new ArgumentException("Texel sizes must be positive and finite.", nameof(texelSizes));
                entry.TexelSizes[i] = size;
            }
            entry.Count = texelSizes.Length;
            entry.Frame = Time.frameCount;
            entry.DynamicOnlySplits = dynamicOnlySplits;
        }

        /// <summary>Stop applying texel rules to the light's shadow culls.</summary>
        public static void Clear(Light light)
        {
            if (light != null) Entries.Remove(light.GetInstanceID());
        }

        /// <summary>
        /// Draw no instances into the light's shadow culls until <see cref="ResumeCulls"/>, at most for the current
        /// frame. A shadow renderer that replaces the pipeline's shadow map sets this around the pipeline's own cull,
        /// whose casters it never draws, so instancing skips that cull's compute work.
        /// </summary>
        public static void IgnoreCulls(Light light)
        {
            if (light == null) throw new ArgumentNullException(nameof(light));
            IgnoredLights[light.GetInstanceID()] = Time.frameCount;
        }

        /// <summary>Cull instances for the light's shadows again.</summary>
        public static void ResumeCulls(Light light)
        {
            if (light != null) IgnoredLights.Remove(light.GetInstanceID());
        }

        internal static bool IsIgnored(int lightInstanceId)
        {
            return IgnoredLights.TryGetValue(lightInstanceId, out int frame) && frame == Time.frameCount;
        }

        // The sizes must describe exactly the view's splits, and come from this frame.
        internal static bool TryGetTexelSizes(int lightInstanceId, int splitCount, out float[] texelSizes,
            out int dynamicOnlySplits)
        {
            texelSizes = null;
            dynamicOnlySplits = 0;
            if (!Entries.TryGetValue(lightInstanceId, out Entry entry) || entry.Count != splitCount ||
                entry.Frame != Time.frameCount) return false;
            texelSizes = entry.TexelSizes;
            dynamicOnlySplits = entry.DynamicOnlySplits;
            return true;
        }
    }
}

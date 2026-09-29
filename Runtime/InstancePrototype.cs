using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Immutable static-mesh draw data. Source assets remain unchanged.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstancePrototype")]
    public sealed class InstancePrototype
    {
        internal readonly Part[] Parts;
        internal readonly bool HasColliders;
        internal readonly float[] Thresholds;
        internal readonly float[] FadeWidths;
        internal readonly bool CrossFade;
        internal readonly bool AnimatedCrossFade;
        internal readonly int PercentageLods;
        internal readonly InstanceMaterialProfile Profile;
        internal readonly GameObject Source;
        internal readonly bool NativeLodDistance;
        internal readonly Bounds Bounds;
        internal readonly Vector3 LodCenter;
        internal readonly float LodSize;
        /// <summary>Per-prototype LOD multiplier. One follows the native quality setting.</summary>
        public float LodBias { get; private set; } = 1;
        internal bool LegacyProbes { get; private set; }
        internal bool HasLightmaps { get; private set; }

        internal readonly struct Part
        {
            internal readonly Mesh Mesh;
            internal readonly Material Material;
            internal readonly Matrix4x4 Local;
            internal readonly int Submesh;
            internal readonly int Lod;
            internal readonly ShadowCastingMode Shadows;
            internal readonly bool ReceiveShadows;
            internal readonly bool HasMotion;
            internal readonly uint RenderingLayers;
            internal readonly Color MaterialColor;
            internal readonly int MeshLevel;
            internal readonly int MeshLevels;
            internal readonly uint IndexStart;
            internal readonly uint IndexCount;
            internal readonly Vector4 MeshSelection;
            internal readonly ushort LightmapIndex;
            internal readonly Vector4 LightmapST;

            internal Part(Mesh mesh, Material material, Matrix4x4 local, int submesh, int lod, MeshRenderer renderer, int meshLevel = 0)
            {
                Mesh = mesh;
                MeshLevel = meshLevel;
                LightmapIndex = renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534 ? (ushort)renderer.lightmapIndex : ushort.MaxValue;
                LightmapST = renderer.lightmapScaleOffset;
                MeshLevels = Mathf.Max(1, mesh.lodCount);
                IndexStart = mesh.GetIndexStart(submesh);
                IndexCount = mesh.GetIndexCount(submesh);
                if (mesh.lodCount > 0)
                {
                    var range = mesh.GetLod(submesh, meshLevel);
                    // Mesh LOD offsets are relative to the containing submesh.
                    IndexStart += range.indexStart;
                    IndexCount = range.indexCount;
                }
                var curve = mesh.lodSelectionCurve;
                MeshSelection = new Vector4(curve.lodSlope, curve.lodBias, renderer.meshLodSelectionBias, renderer.forceMeshLod);
                Material = material;
                Local = local;
                Submesh = submesh;
                Lod = lod;
                Shadows = (InstanceShaderInspection.Inspect(material) & InstanceShaderCapabilities.Shadow) != 0 ? renderer.shadowCastingMode : ShadowCastingMode.Off;
                ReceiveShadows = renderer.receiveShadows;
                HasMotion = (InstanceShaderInspection.Inspect(material) & InstanceShaderCapabilities.Motion) != 0;
                RenderingLayers = renderer.renderingLayerMask;
                MaterialColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
            }
        }

        /// <summary>Number of mesh/submesh draw parts across all LODs.</summary>
        public int PartCount => Parts.Length;

        /// <summary>Combined local-space bounds for all captured mesh renderers.</summary>
        public Bounds LocalBounds => Bounds;

        /// <summary>Number of captured LOD levels.</summary>
        public int LodCount => Thresholds.Length;

        /// <summary>Deterministic structural identity used by the world registry.</summary>
        public InstancePrototypeId StableId => InstancePrototypeRegistry.GetId(this);

        private InstancePrototype(Part[] parts, float[] thresholds, Bounds bounds, Vector3 lodCenter, float lodSize,
            float[] fadeWidths, bool crossFade, int percentageLods, InstanceMaterialProfile profile, GameObject source,
            bool nativeLodDistance = false, bool? hasColliders = null, bool? animatedCrossFade = null)
        {
            Parts = parts;
            HasColliders = hasColliders ?? source && source.GetComponentsInChildren<Collider>(true).Length > 0;
            foreach (var part in parts)
            {
                HasLightmaps |= part.LightmapIndex != ushort.MaxValue;
            }
            Thresholds = thresholds;
            Bounds = bounds;
            LodCenter = lodCenter;
            LodSize = lodSize;
            FadeWidths = fadeWidths;
            CrossFade = crossFade;
            var group = source ? source.GetComponent<LODGroup>() : null;
            AnimatedCrossFade = animatedCrossFade ??
                group && group.fadeMode == LODFadeMode.CrossFade && group.animateCrossFading;
            PercentageLods = percentageLods;
            Profile = profile;
            Source = source;
            NativeLodDistance = nativeLodDistance;
        }

        /// <summary>Check whether two captured sources can share draw prototypes without changing their renderer state.</summary>
        public bool CanShareWith(InstancePrototype other)
        {
            if (other == null || HasColliders != other.HasColliders || LodBias != other.LodBias || LegacyProbes != other.LegacyProbes ||
                NativeLodDistance != other.NativeLodDistance || Profile || other.Profile || Parts.Length != other.Parts.Length || Thresholds.Length != other.Thresholds.Length ||
                Bounds != other.Bounds || LodCenter != other.LodCenter || LodSize != other.LodSize || CrossFade != other.CrossFade ||
                AnimatedCrossFade != other.AnimatedCrossFade || PercentageLods != other.PercentageLods) return false;
            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (Thresholds[i] != other.Thresholds[i] || FadeWidths[i] != other.FadeWidths[i]) return false;
            }
            for (int i = 0; i < Parts.Length; i++)
            {
                var a = Parts[i];
                var b = other.Parts[i];
                if (a.Mesh != b.Mesh || a.Material != b.Material || a.Local != b.Local || a.Submesh != b.Submesh || a.Lod != b.Lod ||
                    a.Shadows != b.Shadows || a.ReceiveShadows != b.ReceiveShadows || a.HasMotion != b.HasMotion || a.RenderingLayers != b.RenderingLayers ||
                    a.MeshLevel != b.MeshLevel || a.MeshLevels != b.MeshLevels || a.IndexStart != b.IndexStart || a.IndexCount != b.IndexCount ||
                    a.MeshSelection != b.MeshSelection || a.LightmapIndex != b.LightmapIndex || a.LightmapST != b.LightmapST) return false;
            }
            return true;
        }

        /// <summary>Read a prefab without taking ownership or changing its renderers.</summary>
        /// <remarks>Supports opaque and cutout mesh shaders with standard DOTS variants. Profiles add optional deformation data.</remarks>
        public static InstancePrototype FromPrefab(GameObject prefab, InstanceMaterialProfile profile = null,
            InstanceShaderCapabilities requiredPasses = InstanceShaderCapabilities.Surface | InstanceShaderCapabilities.Depth |
                InstanceShaderCapabilities.DepthNormals)
        {
            return InstancePrototypeCompiler.CompileOrThrow(prefab, profile, requiredPasses);
        }

        internal static InstancePrototype CompileUncached(GameObject prefab, InstanceMaterialProfile profile,
            InstanceShaderCapabilities requiredPasses)
        {
            if (prefab == null)
            {
                throw new ArgumentNullException(nameof(prefab));
            }
            if (prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
            {
                throw new NotSupportedException("Skinned meshes need a separate instance provider.");
            }
            var groups = prefab.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length > 1 || (groups.Length == 1 && groups[0].gameObject != prefab))
            {
                throw new NotSupportedException("Use one LODGroup on the prototype root.");
            }
            var group = groups.Length == 0 ? null : groups[0];
            var lods = group == null ? null : group.GetLODs();
            if (group != null && group.fadeMode == LODFadeMode.SpeedTree && !(profile && profile.SpeedTreeLod))
            {
                throw new NotSupportedException("SpeedTree fades require the explicit SpeedTree material profile.");
            }
            if (lods != null && (lods.Length == 0 || lods.Length > 8))
            {
                throw new NotSupportedException("A prototype supports one to eight LOD levels.");
            }
            var parts = new List<Part>();
            var included = new HashSet<Renderer>();
            var thresholds = new float[lods?.Length ?? 1];
            var fadeWidths = new float[thresholds.Length];
            Bounds bounds = default;
            bool hasBounds = false;
            for (int lod = 0; lod < thresholds.Length; lod++)
            {
                thresholds[lod] = lods == null ? 0 : lods[lod].screenRelativeTransitionHeight;
                fadeWidths[lod] = lods == null ? 0 : lods[lod].fadeTransitionWidth *
                    ((lod == 0 ? 1 : thresholds[lod - 1]) - thresholds[lod]);
                if (thresholds[lod] < 0 || float.IsNaN(thresholds[lod]) ||
                    thresholds[lod] >= (lod == 0 ? 1.0001f : thresholds[lod - 1]))
                {
                    throw new NotSupportedException("LOD thresholds must decrease from one to zero.");
                }
                Renderer[] renderers = lods == null ? prefab.GetComponentsInChildren<MeshRenderer>(true) : lods[lod].renderers;
                foreach (Renderer source in renderers)
                {
                    if (source == null || !source.enabled)
                    {
                        continue;
                    }
                    if (!(source is MeshRenderer renderer) || !source.transform.IsChildOf(prefab.transform))
                    {
                        throw new NotSupportedException("LOD renderers must be static children of the prototype.");
                    }
                    if (!IsActiveChild(source.transform, prefab.transform))
                    {
                        continue;
                    }
                    included.Add(source);
                    Mesh mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
                    Material[] materials = source.sharedMaterials;
                    if (mesh == null || materials.Length != mesh.subMeshCount)
                    {
                        throw new NotSupportedException("Each submesh needs one material and a valid MeshFilter.");
                    }
                    Matrix4x4 local = RelativeMatrix(prefab.transform, source.transform);
                    float windPadding = profile ? profile.BoundsPadding : 0;
                    if (!float.IsFinite(windPadding) || windPadding < 0)
                    {
                        throw new ArgumentException("Profile displacement must be finite and nonnegative.");
                    }
                    for (int submesh = 0; submesh < materials.Length; submesh++)
                    {
                        Material material = materials[submesh];
                        if (profile && !profile.Supports(material))
                        {
                            throw new NotSupportedException("The instance material profile does not support this source material.");
                        }
                        if (!InstanceShaderInspection.TryValidate(material, requiredPasses | InstanceShaderCapabilities.Surface, out string reason))
                        {
                            throw new NotSupportedException((material ? material.name : "Missing material") + ": " + reason);
                        }
                        if (material.GetTag("LoogaInstanceDeformation", false, "") == "Vegetation")
                        {
                            Vector4 wind = material.GetVector("_WindParams");
                            if (!Finite(wind) || !Finite(material.GetVector("_BranchWind")) || !Finite(material.GetVector("_LeafWind")) || !Finite(material.GetVector("_WindDirection")))
                            {
                                throw new NotSupportedException("Wind parameters must be finite.");
                            }
                            Vector4 interaction = material.GetVector("_InteractionParams");
                            Vector4 ground = material.GetVector("_GroundParams");
                            if (!Finite(interaction) || interaction.x < 0 || !Finite(ground) || ground.z < 0)
                            {
                                throw new NotSupportedException("Vegetation interaction displacement must be finite and nonnegative.");
                            }
                            windPadding = Mathf.Max(windPadding, Mathf.Abs(wind.x) + Mathf.Abs(material.GetVector("_BranchWind").x) +
                                Mathf.Abs(material.GetVector("_LeafWind").x) + interaction.x + ground.z);
                        }
                        string displacementProperty = material.GetTag("LoogaInstanceDisplacement", false, "");
                        if (!string.IsNullOrEmpty(displacementProperty))
                        {
                            if (!material.HasProperty(displacementProperty))
                            {
                                throw new NotSupportedException("The shader's displacement property is missing.");
                            }
                            float displacement = material.GetFloat(displacementProperty);
                            if (!float.IsFinite(displacement) || displacement < 0)
                            {
                                throw new NotSupportedException("Shader displacement must be finite and nonnegative.");
                            }
                            windPadding = Mathf.Max(windPadding, displacement);
                        }
                        if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        {
                            throw new NotSupportedException("Only triangle meshes are supported.");
                        }
                        for (int meshLevel = 0; meshLevel < Mathf.Max(1, mesh.lodCount); meshLevel++)
                        {
                            parts.Add(new Part(mesh, material, local, submesh, lod, renderer, meshLevel));
                        }
                    }
                    Bounds deformed = mesh.bounds;
                    deformed.Expand(windPadding * 2);
                    Bounds transformed = TransformBounds(deformed, local);
                    if (!hasBounds)
                    {
                        bounds = transformed;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(transformed);
                    }
                }
            }
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.enabled && IsActiveChild(renderer.transform, prefab.transform) && !included.Contains(renderer))
                {
                    throw new NotSupportedException("Every enabled renderer must belong to the root LODGroup.");
                }
            }
            if (parts.Count == 0)
            {
                throw new ArgumentException("The prototype has no enabled mesh parts.");
            }
            return new InstancePrototype(parts.ToArray(), thresholds, bounds,
                group == null ? bounds.center : group.localReferencePoint,
                group == null ? Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) : group.size,
                fadeWidths, group != null && group.fadeMode != LODFadeMode.None,
                group != null && group.fadeMode == LODFadeMode.SpeedTree ? thresholds.Length - 1 : 0, profile, prefab);
        }

        /// <summary>Return a prototype with an independent quality multiplier. Source assets remain unchanged.</summary>
        public InstancePrototype WithLodBias(float bias)
        {
            if (!float.IsFinite(bias) || bias <= 0) throw new ArgumentOutOfRangeException(nameof(bias));
            var result = new InstancePrototype(Parts, Thresholds, Bounds, LodCenter, LodSize, FadeWidths, CrossFade,
                PercentageLods, Profile, Source, NativeLodDistance);
            result.LodBias = bias;
            result.LegacyProbes = LegacyProbes;
            return result;
        }

        /// <summary>Sample legacy light probes for each uploaded instance. APV sampling stays in the shader.</summary>
        public InstancePrototype WithLegacyLightProbes(bool enabled = true)
        {
            var result = WithLodBias(LodBias);
            result.LegacyProbes = enabled;
            return result;
        }

        internal InstancePrototype WithLodCenter(Vector3 center)
        {
            return new InstancePrototype(Parts, Thresholds, Bounds, center, LodSize, FadeWidths, CrossFade,
                PercentageLods, Profile, Source, Source && Source.GetComponent<LODGroup>() != null);
        }

        internal InstancePrototype WithSource(GameObject source)
        {
            var result = new InstancePrototype(Parts, Thresholds, Bounds, LodCenter, LodSize, FadeWidths, CrossFade,
                PercentageLods, Profile, source, NativeLodDistance);
            result.LodBias = LodBias;
            result.LegacyProbes = LegacyProbes;
            return result;
        }

        internal InstancePrototype WithoutSource()
        {
            var result = new InstancePrototype(Parts, Thresholds, Bounds, LodCenter, LodSize, FadeWidths, CrossFade,
                PercentageLods, Profile, null, NativeLodDistance, HasColliders, AnimatedCrossFade);
            result.LodBias = LodBias;
            result.LegacyProbes = LegacyProbes;
            return result;
        }

        private static bool Finite(Vector4 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
        }

        private static bool IsActiveChild(Transform child, Transform root)
        {
            while (child != root)
            {
                if (!child.gameObject.activeSelf) return false;
                child = child.parent;
            }
            return true;
        }

        /// <summary>
        /// Returns the transform of a child relative to a root from the local transforms between them.
        /// </summary>
        /// <remarks>
        /// Copies of one hierarchy give identical matrices at any root position, rotation and scale, so they can share a
        /// prototype. A round trip through world space adds rounding errors from the root transform.
        /// </remarks>
        internal static Matrix4x4 RelativeMatrix(Transform root, Transform child)
        {
            Matrix4x4 local = Matrix4x4.identity;
            for (Transform current = child; current != root; current = current.parent)
            {
                local = Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale) * local;
            }
            return local;
        }

        internal static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 x = matrix.MultiplyVector(new Vector3(bounds.extents.x, 0, 0));
            Vector3 y = matrix.MultiplyVector(new Vector3(0, bounds.extents.y, 0));
            Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, bounds.extents.z));
            Vector3 extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center), extents * 2);
        }
    }
}

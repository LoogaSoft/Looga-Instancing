using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing
{
    /// <summary>Compiles ordinary prefab hierarchies into reusable Looga instance prototypes.</summary>
    /// <remarks>The compiler reads source objects only. Unsupported renderers stay on their native rendering path.</remarks>
    public static class InstancePrototypeCompiler
    {
        private const int MaximumCachedPrototypes = 256;
        private static readonly Dictionary<Hash128, CacheEntry> _cache = new Dictionary<Hash128, CacheEntry>();
        private static readonly Queue<Hash128> _cacheOrder = new Queue<Hash128>();

        /// <summary>Number of derived prototypes currently retained by the compiler.</summary>
        public static int CachedPrototypeCount => _cache.Count;

        private sealed class CacheEntry
        {
            internal InstancePrototype Prototype;
            internal int[] DependencyIds;
        }

        private sealed class Analysis
        {
            internal readonly List<InstancePrototypeDiagnostic> Diagnostics = new List<InstancePrototypeDiagnostic>();
            internal readonly HashSet<int> DependencyIds = new HashSet<int>();
            internal Hash128 Revision;
            internal int Renderers;
            internal int Meshes;
            internal int Materials;
            internal int ShadowCasters;
            internal int ShadowReceivers;
            internal int Lods;
            internal bool HasColliders;
            internal bool HasWind;
            internal bool UsesSpeedTree;
            internal bool HasErrors;
        }

        /// <summary>Compile source data or return a structured report that keeps the source on native rendering.</summary>
        public static InstancePrototypeCompilation Compile(GameObject source, InstanceMaterialProfile profile = null,
            InstanceShaderCapabilities requiredPasses = InstanceShaderCapabilities.Surface | InstanceShaderCapabilities.Depth |
                InstanceShaderCapabilities.DepthNormals)
        {
            if (!source)
            {
                var diagnostics = new List<InstancePrototypeDiagnostic>
                {
                    new InstancePrototypeDiagnostic("MissingSource", InstancePrototypeDiagnosticSeverity.Error,
                        string.Empty, "Assign a prefab or hierarchy root.")
                };
                return new InstancePrototypeCompilation(null, default, default, false, diagnostics);
            }

            Analysis analysis = Analyze(source, profile, requiredPasses);
            if (analysis.HasErrors)
            {
                return Result(null, source, analysis, false);
            }

            if (_cache.TryGetValue(analysis.Revision, out CacheEntry cached) && cached.Prototype != null)
            {
                return Result(cached.Prototype.WithSource(source), source, analysis, true);
            }

            try
            {
                InstancePrototype prototype = InstancePrototype.CompileUncached(source, profile, requiredPasses);
                analysis.Lods = prototype.LodCount;
                AddCache(analysis.Revision, prototype, analysis.DependencyIds);
                return Result(prototype, source, analysis, false);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException)
            {
                string code = exception is ArgumentException ? "InvalidSourceData" : "UnsupportedSource";
                analysis.Diagnostics.Add(new InstancePrototypeDiagnostic(code, InstancePrototypeDiagnosticSeverity.Error,
                    string.Empty, exception.Message));
                analysis.HasErrors = true;
                return Result(null, source, analysis, false);
            }
        }

        /// <summary>Remove derived data that references one changed mesh, material, shader, profile, or source asset.</summary>
        public static int InvalidateDependency(Object dependency)
        {
            if (!dependency) return 0;
            int id = dependency.GetInstanceID();
            var remove = new List<Hash128>();
            foreach (var pair in _cache)
            {
                if (Array.IndexOf(pair.Value.DependencyIds, id) >= 0)
                {
                    remove.Add(pair.Key);
                }
            }
            foreach (Hash128 revision in remove)
            {
                _cache.Remove(revision);
            }
            if (remove.Count > 0)
            {
                _cacheOrder.Clear();
                foreach (Hash128 revision in _cache.Keys) _cacheOrder.Enqueue(revision);
            }
            return remove.Count;
        }

        /// <summary>Release all session-derived prototype data.</summary>
        public static void ClearCache()
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }

        internal static InstancePrototype CompileOrThrow(GameObject source, InstanceMaterialProfile profile,
            InstanceShaderCapabilities requiredPasses)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            InstancePrototypeCompilation compilation = Compile(source, profile, requiredPasses);
            if (compilation.Succeeded) return compilation.Prototype;
            foreach (InstancePrototypeDiagnostic diagnostic in compilation.Diagnostics)
            {
                if (diagnostic.Code == "InvalidSourceData")
                {
                    throw new ArgumentException(compilation.Summary, nameof(source));
                }
            }
            throw new NotSupportedException(compilation.Summary);
        }

        private static Analysis Analyze(GameObject source, InstanceMaterialProfile profile,
            InstanceShaderCapabilities requiredPasses)
        {
            var analysis = new Analysis();
            var signature = new StringBuilder(1024);
            Append(signature, requiredPasses.GetHashCode());
            AddDependency(analysis, profile);
            Append(signature, profile ? profile.GetInstanceID() : 0);
            Append(signature, profile ? profile.BoundsPadding.GetHashCode() : 0);
            Append(signature, profile && profile.SpeedTreeLod ? 1 : 0);
            Append(signature, profile ? JsonUtility.ToJson(profile) : string.Empty);

            LODGroup[] groups = source.GetComponentsInChildren<LODGroup>(true);
            analysis.Lods = groups.Length == 1 ? groups[0].GetLODs().Length : 1;
            if (groups.Length > 1 || groups.Length == 1 && groups[0].gameObject != source)
            {
                AddError(analysis, "NestedLodGroup", Path(source.transform, groups[0].transform),
                    "Use one LODGroup on the prototype root.");
            }
            foreach (LODGroup group in groups)
            {
                Append(signature, SignaturePath(source.transform, group.transform));
                Append(signature, (int)group.fadeMode);
                Append(signature, group.animateCrossFading ? 1 : 0);
                Append(signature, group.localReferencePoint);
                Append(signature, group.size);
                LOD[] lods = group.GetLODs();
                Append(signature, lods.Length);
                for (int level = 0; level < lods.Length; level++)
                {
                    Append(signature, lods[level].screenRelativeTransitionHeight);
                    Append(signature, lods[level].fadeTransitionWidth);
                    foreach (Renderer renderer in lods[level].renderers)
                    {
                        Append(signature, renderer ? SignaturePath(source.transform, renderer.transform) : "<missing>");
                    }
                }
                analysis.UsesSpeedTree |= group.fadeMode == LODFadeMode.SpeedTree;
            }

            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            Renderer[] renderers = source.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                string path = Path(source.transform, renderer.transform);
                Append(signature, renderer.GetType().FullName);
                Append(signature, SignaturePath(source.transform, renderer.transform));
                Append(signature, renderer.enabled ? 1 : 0);
                Append(signature, IsActiveChild(renderer.transform, source.transform) ? 1 : 0);
                Append(signature, renderer.gameObject.layer);
                if (renderer is SkinnedMeshRenderer)
                {
                    AddError(analysis, "SkinnedMeshRenderer", path,
                        "Skinned meshes need a separate instance provider. Native rendering remains enabled.");
                    continue;
                }
                if (renderer is not MeshRenderer meshRenderer)
                {
                    analysis.Diagnostics.Add(new InstancePrototypeDiagnostic("NativeRenderer",
                        InstancePrototypeDiagnosticSeverity.Warning, path,
                        renderer.GetType().Name + " is not compiled and remains on its native rendering path."));
                    continue;
                }

                analysis.Renderers++;
                Mesh mesh = meshRenderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (meshRenderer.HasPropertyBlock())
                {
                    AddError(analysis, "MaterialPropertyBlock", path,
                        "MaterialPropertyBlock data needs an explicit instance-data adapter.");
                }
                if (meshRenderer.additionalVertexStreams)
                {
                    AddError(analysis, "AdditionalVertexStreams", path,
                        "Bake additional vertex streams into the source mesh before compilation.");
                }
                AddDependency(analysis, mesh);
                Append(signature, mesh ? mesh.GetInstanceID() : 0);
                if (mesh)
                {
                    meshes.Add(mesh);
                    Append(signature, mesh.vertexCount);
                    Append(signature, mesh.subMeshCount);
                    Append(signature, mesh.bounds.center);
                    Append(signature, mesh.bounds.size);
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        Append(signature, (int)mesh.GetTopology(submesh));
                        Append(signature, unchecked((int)mesh.GetIndexCount(submesh)));
                    }
                }
                Material[] sharedMaterials = meshRenderer.sharedMaterials;
                Append(signature, sharedMaterials.Length);
                foreach (Material material in sharedMaterials)
                {
                    AddDependency(analysis, material);
                    AddDependency(analysis, material ? material.shader : null);
                    Append(signature, material ? material.GetInstanceID() : 0);
                    Append(signature, material ? material.ComputeCRC() : 0);
                    if (material) materials.Add(material);
                    if (material && material.GetTag("LoogaInstanceDeformation", false, string.Empty) == "Vegetation")
                    {
                        analysis.HasWind = true;
                    }
                }
                Append(signature, source.transform.worldToLocalMatrix * meshRenderer.transform.localToWorldMatrix);
                Append(signature, (int)meshRenderer.shadowCastingMode);
                Append(signature, meshRenderer.receiveShadows ? 1 : 0);
                Append(signature, unchecked((int)meshRenderer.renderingLayerMask));
                Append(signature, meshRenderer.lightmapIndex);
                Append(signature, meshRenderer.lightmapScaleOffset);
                if (meshRenderer.shadowCastingMode != ShadowCastingMode.Off) analysis.ShadowCasters++;
                if (meshRenderer.receiveShadows) analysis.ShadowReceivers++;
            }

            analysis.Meshes = meshes.Count;
            analysis.Materials = materials.Count;
            analysis.HasColliders = source.GetComponentsInChildren<Collider>(true).Length > 0;
            analysis.HasWind |= profile && profile.BoundsPadding > 0;
            Append(signature, analysis.HasColliders ? 1 : 0);
            analysis.Revision = Hash128.Compute(signature.ToString());
            return analysis;
        }

        private static InstancePrototypeCompilation Result(InstancePrototype prototype, GameObject source,
            Analysis analysis, bool reused)
        {
            Bounds bounds = prototype == null ? default : prototype.LocalBounds;
            int partCount = prototype == null ? 0 : prototype.PartCount;
            int lodCount = prototype == null ? analysis.Lods : prototype.LodCount;
            var descriptor = new InstancePrototypeDescriptor(analysis.Renderers, analysis.Meshes,
                analysis.Materials, partCount, lodCount, analysis.ShadowCasters, analysis.ShadowReceivers,
                bounds, analysis.HasColliders, analysis.HasWind, analysis.UsesSpeedTree);
            return new InstancePrototypeCompilation(prototype, descriptor, analysis.Revision, reused,
                analysis.Diagnostics);
        }

        private static void AddCache(Hash128 revision, InstancePrototype prototype, HashSet<int> dependencies)
        {
            if (_cache.ContainsKey(revision)) return;
            while (_cache.Count >= MaximumCachedPrototypes && _cacheOrder.Count > 0)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }
            var ids = new int[dependencies.Count];
            dependencies.CopyTo(ids);
            _cache.Add(revision, new CacheEntry { Prototype = prototype.WithoutSource(), DependencyIds = ids });
            _cacheOrder.Enqueue(revision);
        }

        private static void AddDependency(Analysis analysis, Object dependency)
        {
            if (dependency) analysis.DependencyIds.Add(dependency.GetInstanceID());
        }

        private static void AddError(Analysis analysis, string code, string path, string message)
        {
            analysis.Diagnostics.Add(new InstancePrototypeDiagnostic(code, InstancePrototypeDiagnosticSeverity.Error,
                path, message));
            analysis.HasErrors = true;
        }

        private static string Path(Transform root, Transform child)
        {
            if (child == root) return root.name;
            var names = new Stack<string>();
            while (child && child != root)
            {
                names.Push(child.name);
                child = child.parent;
            }
            return root.name + "/" + string.Join("/", names);
        }

        private static string SignaturePath(Transform root, Transform child)
        {
            if (child == root) return "root";
            var indices = new Stack<int>();
            while (child && child != root)
            {
                indices.Push(child.GetSiblingIndex());
                child = child.parent;
            }
            return string.Join("/", indices);
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

        private static void Append(StringBuilder builder, string value)
        {
            builder.Append(value).Append('|');
        }

        private static void Append(StringBuilder builder, int value)
        {
            builder.Append(value).Append('|');
        }

        private static void Append(StringBuilder builder, float value)
        {
            builder.Append(value.GetHashCode()).Append('|');
        }

        private static void Append(StringBuilder builder, Vector3 value)
        {
            Append(builder, value.x);
            Append(builder, value.y);
            Append(builder, value.z);
        }

        private static void Append(StringBuilder builder, Vector4 value)
        {
            Append(builder, value.x);
            Append(builder, value.y);
            Append(builder, value.z);
            Append(builder, value.w);
        }

        private static void Append(StringBuilder builder, Matrix4x4 value)
        {
            for (int i = 0; i < 16; i++) Append(builder, value[i]);
        }
    }
}

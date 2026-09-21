using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Render explicit scene roots through BRG. Keep scripts, colliders and source materials intact.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "SceneInstanceProvider")]
    public sealed class SceneInstanceProvider : MonoBehaviour, IInstanceSourceAdapter
    {
        [SerializeField] private GameObject[] _sources = Array.Empty<GameObject>();
        [SerializeField] private InstanceMaterialProfile _materialProfile;
        [SerializeField, Min(1)] private float _maxDistance = 1000;
        [SerializeField] private string _worldSourceId = Guid.NewGuid().ToString("N");
        private readonly List<InstancePrototype> _prototypes = new List<InstancePrototype>();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<int, InstanceRenderer> _renderers = new Dictionary<int, InstanceRenderer>();
        private readonly List<MeshRenderer> _scan = new List<MeshRenderer>();
        private readonly List<Material> _materials = new List<Material>();
        private bool _rebuildRequested;
        private static readonly HashSet<MeshRenderer> _owners = new HashSet<MeshRenderer>();

        /// <summary>Number of scene roots currently submitted through BRG.</summary>
        public int ActiveSourceCount => _entries.Count;
        /// <summary>Number of distinct draw prototypes shared by this provider.</summary>
        public int PrototypeCount => _prototypes.Count;
        /// <summary>Reason a requested source stayed native, or null when all active sources are supported.</summary>
        public string Diagnostic { get; private set; }
        /// <summary>Warnings for renderers or components that remain on their native path.</summary>
        public string CompatibilityReport { get; private set; }
        /// <summary>Current mixed Looga/native ownership for configured active scene roots.</summary>
        public InstanceSourceAdapterStatus SourceStatus
        {
            get
            {
                int requested = 0;
                foreach (GameObject source in _sources)
                {
                    if (source && source.activeInHierarchy) requested++;
                }
                int owned = isActiveAndEnabled ? Mathf.Min(_entries.Count, requested) : 0;
                return new InstanceSourceAdapterStatus(requested, owned, requested - owned, false, Diagnostic);
            }
        }

        private sealed class Entry
        {
            internal GameObject Root;
            internal InstanceRenderer Renderer;
            internal InstanceHandle Handle;
            internal Matrix4x4 Matrix;
            internal Snapshot[] Parts;
            internal LODGroup Group;
            internal LOD[] Lods;
            internal LODFadeMode Fade;
            internal bool Animated;
            internal Vector3 Center;
            internal float Size;
        }

        private sealed class Snapshot
        {
            internal MeshRenderer Source;
            internal Mesh Mesh;
            internal Material[] Materials;
            internal Shader[] Shaders;
            internal int[] MaterialCrc;
            internal Matrix4x4 Local;
            internal bool Enabled;
            internal bool Active;
            internal bool Owned;
            internal int Layer;
            internal int Lightmap;
            internal Vector4 LightmapST;
            internal ShadowCastingMode Shadows;
            internal bool ReceiveShadows;
            internal uint RenderingLayers;
        }

        /// <summary>Set explicit scene roots. Nested roots and duplicate ownership are rejected.</summary>
        public void Configure(GameObject[] sources, InstanceMaterialProfile profile = null, float maxDistance = 1000)
        {
            if (!float.IsFinite(maxDistance) || maxDistance <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDistance));
            }
            Release();
            _sources = sources == null ? Array.Empty<GameObject>() : (GameObject[])sources.Clone();
            _materialProfile = profile;
            _maxDistance = maxDistance;
            if (isActiveAndEnabled)
            {
                Rebuild();
            }
        }

        private void OnEnable()
        {
            Rebuild();
        }

        private void OnDisable()
        {
            Release();
        }

        private void OnDestroy()
        {
            Release();
        }

        private void OnValidate()
        {
            _rebuildRequested = true;
        }

        private void LateUpdate()
        {
            Synchronize();
        }

        /// <summary>Apply root movement. Source structure changes rebuild the provider before its next draw.</summary>
        public void Synchronize()
        {
            if (!isActiveAndEnabled) return;
            if (_rebuildRequested)
            {
                Rebuild();
                return;
            }
            int active = 0;
            foreach (var root in _sources)
            {
                if (root && root.activeInHierarchy)
                {
                    active++;
                }
            }
            // Unsupported sources stay native until an explicit rebuild or configuration change.
            if (Diagnostic == null && active != _entries.Count)
            {
                Rebuild();
                return;
            }
            foreach (var entry in _entries)
            {
                if (Changed(entry))
                {
                    Rebuild();
                    return;
                }
                var matrix = entry.Root.transform.localToWorldMatrix;
                if (matrix != entry.Matrix)
                {
                    try
                    {
                        entry.Renderer.Update(entry.Handle, matrix);
                        entry.Matrix = matrix;
                    }
                    catch (ArgumentException)
                    {
                        Rebuild();
                        return;
                    }
                }
            }
            foreach (var renderer in _renderers.Values)
            {
                renderer.Flush();
            }
        }

        /// <summary>Re-read source hierarchy and material state. Unsupported roots retain native rendering.</summary>
        public void Rebuild()
        {
            Release();
            Diagnostic = null;
            CompatibilityReport = null;
            _rebuildRequested = false;
            if (!isActiveAndEnabled) return;
            foreach (var root in _sources)
            {
                if (!root || !root.activeInHierarchy)
                {
                    continue;
                }
                try
                {
                    ValidateRoot(root);
                    InstancePrototypeCompilation compilation = InstancePrototypeCompiler.Compile(root, _materialProfile);
                    if (!compilation.Succeeded)
                    {
                        throw new NotSupportedException(compilation.Summary);
                    }
                    foreach (InstancePrototypeDiagnostic diagnostic in compilation.Diagnostics)
                    {
                        if (diagnostic.Severity == InstancePrototypeDiagnosticSeverity.Error) continue;
                        CompatibilityReport = (CompatibilityReport == null ? string.Empty : CompatibilityReport + "\n") +
                            root.name + ": " + diagnostic;
                    }
                    var prototype = compilation.Prototype;
                    foreach (var existing in _prototypes)
                    {
                        if (existing.CanShareWith(prototype))
                        {
                            prototype = existing;
                            break;
                        }
                    }
                    if (!_prototypes.Contains(prototype))
                    {
                        _prototypes.Add(prototype);
                    }
                    var parts = Capture(root);
                    if (!_renderers.TryGetValue(root.layer, out var renderer))
                    {
                        renderer = new InstanceRenderer(layer: root.layer, worldSourceId: _worldSourceId + ":layer:" + root.layer,
                            worldContentKind: InstanceWorldContentKind.SceneObject) { MaxDistance = _maxDistance };
                        _renderers.Add(root.layer, renderer);
                    }
                    int index = renderer.Register(prototype);
                    var matrix = root.transform.localToWorldMatrix;
                    var handle = renderer.Add(index, matrix);
                    renderer.Flush();
                    var group = root.GetComponent<LODGroup>();
                    var entry = new Entry { Root = root, Renderer = renderer, Handle = handle, Matrix = matrix,
                        Parts = parts, Group = group, Lods = group ? group.GetLODs() : null,
                        Fade = group ? group.fadeMode : LODFadeMode.None, Animated = group && group.animateCrossFading,
                        Center = group ? group.localReferencePoint : Vector3.zero, Size = group ? group.size : 0 };
                    _entries.Add(entry);
                    foreach (var part in parts)
                    {
                        if (!part.Enabled || !part.Active)
                        {
                            continue;
                        }
                        _owners.Add(part.Source);
                        part.Owned = true;
                        part.Source.forceRenderingOff = true;
                    }
                }
                catch (Exception exception)
                {
                    Diagnostic = (Diagnostic == null ? "" : Diagnostic + "\n") + root.name + ": " + exception.Message;
                }
            }
        }

        private void ValidateRoot(GameObject root)
        {
#if UNITY_EDITOR
            if (UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(root) != null)
            {
                throw new NotSupportedException("Prefab editing keeps native rendering.");
            }
#endif
            if (!root.scene.IsValid() || !root.scene.isLoaded)
            {
                throw new NotSupportedException("Use a loaded scene object.");
            }
            foreach (var other in _sources)
            {
                if (other && other != root && (root.transform.IsChildOf(other.transform) || other.transform.IsChildOf(root.transform)))
                {
                    throw new NotSupportedException("Nested source roots overlap.");
                }
            }
            root.GetComponentsInChildren(true, _scan);
            foreach (var source in _scan)
            {
                if (_owners.Contains(source) || source.forceRenderingOff)
                {
                    throw new NotSupportedException("Another renderer owns this source.");
                }
                if (source.HasPropertyBlock())
                {
                    throw new NotSupportedException("MaterialPropertyBlock data needs an explicit instance-data adapter.");
                }
                if (source.gameObject.layer != root.layer)
                {
                    throw new NotSupportedException("All parts of a root must use the same layer.");
                }
                if (source.isPartOfStaticBatch || source.realtimeLightmapIndex >= 0 && source.realtimeLightmapIndex < 65534)
                {
                    throw new NotSupportedException("Static batches and realtime lightmaps need a separate source adapter.");
                }
            }
        }

        private Snapshot[] Capture(GameObject root)
        {
            var parts = new Snapshot[_scan.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                var source = _scan[i];
                var materials = source.sharedMaterials;
                var shaders = new Shader[materials.Length];
                var crc = new int[materials.Length];
                for (int m = 0; m < materials.Length; m++)
                {
                    shaders[m] = materials[m] ? materials[m].shader : null;
                    crc[m] = materials[m] ? materials[m].ComputeCRC() : 0;
                }
                parts[i] = new Snapshot { Source = source, Mesh = source.GetComponent<MeshFilter>()?.sharedMesh,
                    Materials = materials, Shaders = shaders, MaterialCrc = crc, Local = root.transform.worldToLocalMatrix * source.transform.localToWorldMatrix,
                    Enabled = source.enabled, Active = source.gameObject.activeInHierarchy, Layer = source.gameObject.layer,
                    Lightmap = source.lightmapIndex, LightmapST = source.lightmapScaleOffset, Shadows = source.shadowCastingMode,
                    ReceiveShadows = source.receiveShadows, RenderingLayers = source.renderingLayerMask };
            }
            return parts;
        }

        private bool Changed(Entry entry)
        {
            if (!entry.Root || !entry.Root.activeInHierarchy) return true;
            entry.Root.GetComponentsInChildren(true, _scan);
            if (_scan.Count != entry.Parts.Length || entry.Root.GetComponent<LODGroup>() != entry.Group) return true;
            if (entry.Group && (entry.Group.fadeMode != entry.Fade || entry.Group.animateCrossFading != entry.Animated ||
                entry.Group.localReferencePoint != entry.Center || entry.Group.size != entry.Size)) return true;
            if (entry.Group)
            {
                var lods = entry.Group.GetLODs();
                if (lods.Length != entry.Lods.Length) return true;
                for (int lod = 0; lod < lods.Length; lod++)
                {
                    var old = entry.Lods[lod];
                    var current = lods[lod];
                    if (current.screenRelativeTransitionHeight != old.screenRelativeTransitionHeight ||
                        current.fadeTransitionWidth != old.fadeTransitionWidth || current.renderers.Length != old.renderers.Length) return true;
                    for (int r = 0; r < current.renderers.Length; r++)
                    {
                        if (current.renderers[r] != old.renderers[r]) return true;
                    }
                }
            }
            for (int i = 0; i < _scan.Count; i++)
            {
                var source = _scan[i];
                var saved = entry.Parts[i];
                if (source != saved.Source || source.enabled != saved.Enabled || source.gameObject.activeInHierarchy != saved.Active ||
                    source.forceRenderingOff != saved.Owned || source.HasPropertyBlock() || source.gameObject.layer != saved.Layer ||
                    source.GetComponent<MeshFilter>()?.sharedMesh != saved.Mesh || source.lightmapIndex != saved.Lightmap ||
                    source.lightmapScaleOffset != saved.LightmapST || source.shadowCastingMode != saved.Shadows ||
                    source.receiveShadows != saved.ReceiveShadows || source.renderingLayerMask != saved.RenderingLayers) return true;
                // A moving root can change matrix roundoff. Compare the relative transform with a tolerance.
                var local = entry.Root.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                for (int element = 0; element < 16; element++)
                {
                    if (Mathf.Abs(local[element] - saved.Local[element]) > 0.0001f) return true;
                }
                source.GetSharedMaterials(_materials);
                if (_materials.Count != saved.Materials.Length) return true;
                for (int m = 0; m < _materials.Count; m++)
                {
                    if (_materials[m] != saved.Materials[m] || !_materials[m] || _materials[m].shader != saved.Shaders[m] ||
                        _materials[m].renderQueue > 2500 || _materials[m].ComputeCRC() != saved.MaterialCrc[m]) return true;
                }
            }
            return false;
        }

        private void Release()
        {
            foreach (var renderer in _renderers.Values)
            {
                renderer.Dispose();
            }
            _renderers.Clear();
            foreach (var entry in _entries)
            {
                foreach (var part in entry.Parts)
                {
                    if (!part.Owned)
                    {
                        continue;
                    }
                    _owners.Remove(part.Source);
                    if (part.Source)
                    {
                        part.Source.forceRenderingOff = false;
                    }
                }
            }
            _entries.Clear();
            _prototypes.Clear();
        }
    }
}

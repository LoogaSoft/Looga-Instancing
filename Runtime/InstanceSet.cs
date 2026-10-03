using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing
{
    /// <summary>
    /// Draws static placements of several prototypes through one renderer. Each entry has an owner object and a prototype.
    /// Placements have no identity, so a change replaces all placements of one entry.
    /// </summary>
    /// <remarks>
    /// One renderer runs one culling callback for each view, so many prototypes cost less than one container each.
    /// Moving the set rebuilds the renderer. Use InstanceContainer for per-placement edits, proxies or visibility claims.
    /// </remarks>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class InstanceSet : MonoBehaviour, IInstanceSourceAdapter, IAdaptiveQualityTarget
    {
        /// <summary>One local transform of an entry.</summary>
        [Serializable]
        public struct Placement
        {
            /// <summary>Local position in meters.</summary>
            public Vector3 Position;
            /// <summary>Local rotation in degrees.</summary>
            public Vector3 EulerAngles;
            /// <summary>Nonzero scale on each axis.</summary>
            public Vector3 Scale;
        }

        [Serializable]
        private sealed class Entry
        {
            public Object Owner;
            public GameObject Prototype;
            public Placement[] Placements = Array.Empty<Placement>();
        }

        [SerializeField] private List<Entry> _entries = new();
        [SerializeField] private string _sourceId = Guid.NewGuid().ToString("N");
        [SerializeField] private InstanceWorldContentKind _worldContentKind = InstanceWorldContentKind.SceneObject;
        [SerializeField] private InstanceMaterialProfile _materialProfile;
        [SerializeField] private InstanceQualitySettings _quality = InstanceQualitySettings.Default;
        [SerializeField] private InstanceVisibilityMode _visibilityMode;
        [SerializeField, Min(1)] private float _maxDistance = 1000;
        [SerializeField, Min(0)] private float _shadowDistance = 1000;
        [SerializeField, Range(0, 7)] private int _minimumShadowLod;
        private InstanceRenderer _renderer;
        private readonly HashSet<int> _registered = new();
        private readonly Dictionary<Entry, List<InstanceHandle>> _handles = new();
        private Matrix4x4 _worldMatrix;
        private int _layer;
        private bool _rebuildRequested = true;
        private float _adaptiveQualityScale = 1;

        /// <summary>Active renderer, or null while disabled or empty.</summary>
        public InstanceRenderer Renderer => _renderer;
        /// <summary>Number of owner and prototype entries.</summary>
        public int EntryCount => _entries.Count;
        /// <summary>Placements in all entries.</summary>
        public int PlacementCount
        {
            get
            {
                int count = 0;
                foreach (Entry entry in _entries)
                {
                    count += entry.Placements.Length;
                }
                return count;
            }
        }
        /// <summary>Last configuration or rendering failure, or null.</summary>
        public string Diagnostic { get; private set; }
        /// <summary>Each entry is one source population.</summary>
        public InstanceSourceAdapterStatus SourceStatus
        {
            get
            {
                int owned = _renderer != null && _renderer.RenderingEnabled ? _entries.Count : 0;
                return new InstanceSourceAdapterStatus(_entries.Count, owned, 0, false, Diagnostic);
            }
        }

        #region Built-in
        private void OnEnable()
        {
            _rebuildRequested = true;
            Synchronize();
        }

        private void OnValidate()
        {
            _rebuildRequested = true;
        }

        private void LateUpdate()
        {
            Synchronize();
        }

        private void OnDisable()
        {
            Release();
        }
        #endregion

        #region Entries
        /// <summary>
        /// Replace the placements of one owner and prototype. An empty list removes the entry.
        /// Invalid input throws and leaves the set unchanged.
        /// </summary>
        public void SetEntry(Object owner, GameObject prototype, IReadOnlyList<Placement> placements)
        {
            if (prototype == null)
            {
                throw new ArgumentNullException(nameof(prototype));
            }
            Placement[] copy = Copy(placements);
            int index = FindEntry(owner, prototype);
            if (copy.Length == 0)
            {
                if (index < 0) return;
                RemoveHandles(_entries[index]);
                _entries.RemoveAt(index);
                _renderer?.Flush();
                return;
            }
            // Compile first, so an unsupported prototype leaves the current entry unchanged.
            InstancePrototype compiled = InstancePrototype.FromPrefab(prototype, _materialProfile);
            Entry entry = index >= 0 ? _entries[index] : new Entry { Owner = owner, Prototype = prototype };
            if (index < 0)
            {
                _entries.Add(entry);
            }
            RemoveHandles(entry);
            entry.Placements = copy;
            if (_renderer != null && !_rebuildRequested)
            {
                AddHandles(entry, compiled, transform.localToWorldMatrix);
                _renderer.Flush();
                return;
            }
            _rebuildRequested = true;
            Synchronize();
        }

        /// <summary>Remove the entries of one owner whose prototype is not in the list.</summary>
        /// <param name="owner">Owner of the entries to check. Entries of other owners do not change.</param>
        /// <param name="keep">Prototypes that the owner keeps.</param>
        /// <returns>Number of removed entries.</returns>
        public int RemoveOtherPrototypes(Object owner, ICollection<GameObject> keep)
        {
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Owner != owner || (keep != null && entry.Prototype && keep.Contains(entry.Prototype))) continue;
                RemoveHandles(entry);
                _entries.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
            {
                _renderer?.Flush();
            }
            return removed;
        }

        /// <summary>Remove the entries whose owner is not in the list. Entries of destroyed owners are also removed.</summary>
        /// <param name="keep">Owners that keep their entries.</param>
        /// <returns>Number of removed entries.</returns>
        public int RemoveOtherOwners(ICollection<Object> keep)
        {
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Owner && keep != null && keep.Contains(entry.Owner)) continue;
                RemoveHandles(entry);
                _entries.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
            {
                _renderer?.Flush();
            }
            return removed;
        }

        /// <summary>Number of placements of one owner and prototype, or zero when the entry does not exist.</summary>
        public int GetPlacementCount(Object owner, GameObject prototype)
        {
            int index = FindEntry(owner, prototype);
            return index < 0 ? 0 : _entries[index].Placements.Length;
        }

        /// <summary>Return an independent copy of the placements of one entry.</summary>
        public Placement[] CopyPlacements(Object owner, GameObject prototype)
        {
            int index = FindEntry(owner, prototype);
            return index < 0 ? Array.Empty<Placement>() : (Placement[])_entries[index].Placements.Clone();
        }

        private int FindEntry(Object owner, GameObject prototype)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Owner == owner && _entries[i].Prototype == prototype) return i;
            }
            return -1;
        }
        #endregion

        #region Rendering
        /// <summary>Set the render settings of all entries. The set rebuilds its renderer once.</summary>
        /// <param name="kind">Spatial layout of the world visibility cells.</param>
        /// <param name="maxDistance">Maximum camera distance in meters. Use at least 1.</param>
        /// <param name="shadowDistance">Shadow distance in meters, before adaptive quality.</param>
        /// <param name="minimumShadowLod">Minimum LOD index for shadows, from 0 to 7.</param>
        /// <param name="quality">Quality controls for every prototype.</param>
        /// <param name="visibility">Visibility path of the renderer.</param>
        public void ConfigureRendering(InstanceWorldContentKind kind, float maxDistance, float shadowDistance,
            int minimumShadowLod, InstanceQualitySettings quality, InstanceVisibilityMode visibility)
        {
            if (!float.IsFinite(maxDistance) || maxDistance < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDistance));
            }
            if (!float.IsFinite(shadowDistance) || shadowDistance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(shadowDistance));
            }
            if (minimumShadowLod < 0 || minimumShadowLod > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumShadowLod));
            }
            quality.Validate(_entries.Exists(entry => entry.Prototype &&
                entry.Prototype.GetComponentsInChildren<Collider>(true).Length > 0));
            _worldContentKind = kind;
            _maxDistance = maxDistance;
            _shadowDistance = shadowDistance;
            _minimumShadowLod = minimumShadowLod;
            _quality = quality;
            _visibilityMode = visibility;
            _rebuildRequested = true;
            Synchronize();
        }

        /// <summary>Rebuild after a transform, layer or settings change. A static, unchanged set does nothing.</summary>
        public void Synchronize()
        {
            if (!isActiveAndEnabled) return;
            if (!_rebuildRequested && transform.localToWorldMatrix == _worldMatrix && gameObject.layer == _layer) return;
            try
            {
                Rebuild();
                Diagnostic = null;
            }
            catch (Exception exception)
            {
                // Keep the failure visible and retry after the next configuration change.
                Diagnostic = exception.Message;
                Release();
                _rebuildRequested = false;
            }
        }

        /// <summary>Apply runtime quality without changing the authored quality.</summary>
        public void ApplyAdaptiveQuality(AdaptiveQualityState state)
        {
            _adaptiveQualityScale = state.Scale;
            if (_renderer == null) return;
            foreach (int prototype in _registered)
            {
                _renderer.SetQuality(prototype, InstanceQualityScaler.Scale(_quality, _adaptiveQualityScale));
            }
            _renderer.ShadowDistance = _shadowDistance * Mathf.Lerp(0.55f, 1, _adaptiveQualityScale);
        }

        private void Rebuild()
        {
            Release();
            _rebuildRequested = false;
            _worldMatrix = transform.localToWorldMatrix;
            _layer = gameObject.layer;
            if (_entries.Count == 0) return;
            if (!float.IsFinite(_maxDistance) || _maxDistance <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(_maxDistance));
            }
            var candidate = new InstanceRenderer(null, _layer, _sourceId, _worldContentKind)
            {
                MaxDistance = _maxDistance,
                ShadowDistance = _shadowDistance * Mathf.Lerp(0.55f, 1, _adaptiveQualityScale),
                MinimumShadowLod = _minimumShadowLod,
                VisibilityMode = _visibilityMode
            };
            _renderer = candidate;
            foreach (Entry entry in _entries)
            {
                if (entry.Prototype == null || entry.Placements.Length == 0) continue;
                AddHandles(entry, InstancePrototype.FromPrefab(entry.Prototype, _materialProfile), _worldMatrix);
            }
            _renderer.Flush();
        }

        private void AddHandles(Entry entry, InstancePrototype prototype, Matrix4x4 world)
        {
            int index = _renderer.Register(prototype);
            if (_registered.Add(index))
            {
                _renderer.SetQuality(index, InstanceQualityScaler.Scale(_quality, _adaptiveQualityScale));
            }
            if (!_handles.TryGetValue(entry, out List<InstanceHandle> handles))
            {
                handles = new List<InstanceHandle>(entry.Placements.Length);
                _handles.Add(entry, handles);
            }
            foreach (Placement placement in entry.Placements)
            {
                handles.Add(_renderer.Add(index, world * LocalMatrix(placement)));
            }
        }

        private void RemoveHandles(Entry entry)
        {
            if (!_handles.Remove(entry, out List<InstanceHandle> handles) || _renderer == null) return;
            foreach (InstanceHandle handle in handles)
            {
                _renderer.Remove(handle);
            }
        }

        private void Release()
        {
            _renderer?.Dispose();
            _renderer = null;
            _registered.Clear();
            _handles.Clear();
        }
        #endregion

        #region Validation
        private static Placement[] Copy(IReadOnlyList<Placement> placements)
        {
            if (placements == null || placements.Count == 0) return Array.Empty<Placement>();
            var copy = new Placement[placements.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = placements[i];
                Matrix4x4 matrix = LocalMatrix(copy[i]);
                for (int element = 0; element < 16; element++)
                {
                    if (!float.IsFinite(matrix[element]))
                    {
                        throw new ArgumentException("Placement transforms must contain finite values.");
                    }
                }
                if (Mathf.Abs(matrix.determinant) < 0.000001f)
                {
                    throw new ArgumentException("Placement transforms must be invertible.");
                }
            }
            return copy;
        }

        private static Matrix4x4 LocalMatrix(Placement placement)
        {
            return Matrix4x4.TRS(placement.Position, Quaternion.Euler(placement.EulerAngles), placement.Scale);
        }
        #endregion
    }
}

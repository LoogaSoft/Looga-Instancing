using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Owns explicit placements in local space. Source objects and shared placement assets remain unchanged.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceContainer")]
    public sealed class InstanceContainer : MonoBehaviour, IInstanceSourceAdapter, IAdaptiveQualityTarget
    {
        /// <summary>One transform and persistent identity in a container placement set.</summary>
        [Serializable]
        public struct Placement
        {
            /// <summary>Identity within this placement set. Empty values receive an ID when imported.</summary>
            public string Id;
            /// <summary>Local position in meters.</summary>
            public Vector3 Position;
            /// <summary>Local rotation in degrees.</summary>
            public Vector3 EulerAngles;
            /// <summary>Nonzero scale on each axis. Negative values permit mirrored instances.</summary>
            public Vector3 Scale;
        }

        [SerializeField] private GameObject _prototype;
        [SerializeField] private string _sourceId = Guid.NewGuid().ToString("N");
        [SerializeField] private InstanceWorldContentKind _worldContentKind = InstanceWorldContentKind.PaintedInstance;
        private InstanceEditJournal _journal;
        private Placement[] _resolved = Array.Empty<Placement>();
        [SerializeField] private InstanceMaterialProfile _materialProfile;
        [SerializeField] private InstanceQualitySettings _quality = InstanceQualitySettings.Default;
        [SerializeField] private InstanceVisibilityMode _visibilityMode;
        [SerializeField] private Placement[] _placements = Array.Empty<Placement>();
        [SerializeField, Min(1)] private float _maxDistance = 1000;
        [SerializeField] private ComputeShader _cullingShader;
        [SerializeField, Min(0)] private float _shadowDistance = 1000;
        [SerializeField, Range(0, 7)] private int _minimumShadowLod;
        private InstanceRenderer _renderer;
        private readonly Dictionary<string, LivePlacement> _live = new(StringComparer.Ordinal);
        private readonly HashSet<string> _proxyOwned = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _visibilityClaims = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
        private readonly List<string> _removed = new();
        private Matrix4x4 _worldMatrix;
        private bool _dataDirty = true;
        private bool _indexDirty = true;
        private bool _rebuildRequested = true;
        private int _layer;
        private VegetationAssetLease _assetLease;
        private CancellationTokenSource _loadCancellation;
        private int _loadVersion;
        private float _adaptiveQualityScale = 1;

        private struct LivePlacement
        {
            internal InstanceHandle Handle;
            internal Matrix4x4 Matrix;
        }

        /// <summary>Active renderer, or null while disabled. Use container APIs to change its placements.</summary>
        public InstanceRenderer Renderer => _renderer;
        /// <summary>Persistent source identity. Assign a unique world key before attaching shared gameplay edits.</summary>
        public string SourceId => _sourceId;
        /// <summary>Prototype used by this container.</summary>
        public GameObject Prototype => _prototype;

        /// <summary>Attach game-owned edits. Reuse the same source key after unloading and reloading a cell.</summary>
        public void BindEdits(string sourceId, InstanceEditJournal journal)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                throw new ArgumentException("Assign a persistent, nonempty source ID.");
            }
            if (_journal != null)
            {
                _journal.Changed -= OnEditsChanged;
            }
            _sourceId = sourceId;
            _journal = journal;
            if (_journal != null)
            {
                _journal.Changed += OnEditsChanged;
            }
            OnEditsChanged(null);
            _rebuildRequested = true;
        }

        private void OnDestroy()
        {
            if (_journal != null)
            {
                _journal.Changed -= OnEditsChanged;
            }
        }

        private void OnEditsChanged(string source)
        {
            if (source != null && source != _sourceId) return;
            _indexDirty = true;
            _dataDirty = true;
        }

        /// <summary>Copy gameplay-resolved data, independent of render visibility and component state.</summary>
        public Placement[] CopyResolvedPlacements()
        {
            EnsureData();
            return (Placement[])_resolved.Clone();
        }

        /// <summary>Current gameplay-resolved data without a copy. Callers must not change the array.</summary>
        /// <remarks>Each placement change assigns a new array. Callers can compare references to detect changes.</remarks>
        internal Placement[] ResolvedPlacements
        {
            get
            {
                EnsureData();
                return _resolved;
            }
        }

        /// <summary>Find gameplay-resolved data. Removed instances return false.</summary>
        public bool TryGetResolvedPlacement(string id, out Placement placement)
        {
            if (!TryGetPlacement(id, out placement)) return false;
            return _journal == null || _journal.Resolve(_sourceId, placement, out placement);
        }
        /// <summary>Source placement count, independent of render visibility and component state.</summary>
        public int PlacementCount => _placements?.Length ?? 0;
        /// <summary>Last automatic synchronization failure. Explicit API calls throw validation errors.</summary>
        public string Diagnostic { get; private set; }
        /// <summary>Current ownership of this explicit painted or runtime placement population.</summary>
        public InstanceSourceAdapterStatus SourceStatus
        {
            get
            {
                int requested = _prototype && PlacementCount > 0 && isActiveAndEnabled ? 1 : 0;
                int owned = requested > 0 && _renderer != null && _renderer.RenderingEnabled ? 1 : 0;
                return new InstanceSourceAdapterStatus(requested, owned, 0, requested > owned && string.IsNullOrEmpty(Diagnostic), Diagnostic);
            }
        }

        #region Lifecycle
        private void OnEnable()
        {
            _dataDirty = true;
            _indexDirty = true;
            _rebuildRequested = true;
            SynchronizeAutomatic();
        }

        private void OnValidate()
        {
            _dataDirty = true;
            _indexDirty = true;
            _rebuildRequested = true;
        }

        private void LateUpdate()
        {
            SynchronizeAutomatic();
        }

        private void OnDisable()
        {
            CancelLoad();
            ReleaseRenderer();
            if (_assetLease != null)
            {
                _prototype = null;
                _assetLease.Dispose();
                _assetLease = null;
            }
        }

        private void SynchronizeAutomatic()
        {
            try
            {
                Synchronize();
                Diagnostic = null;
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                // Keep invalid transforms out of the render buffers. Retry after the author corrects the source.
                if (_renderer != null)
                {
                    _renderer.RenderingEnabled = false;
                }
            }
        }
        #endregion

        #region Configuration
        /// <summary>Replace the prototype and placement set. Empty IDs receive persistent values in the copied data.</summary>
        public void Configure(GameObject prototype, Placement[] placements, float maxDistance = 1000)
        {
            var copy = CopyValidated(placements);
            ValidateDistance(maxDistance);
            Replace(prototype, copy, maxDistance);
            CancelLoad();
            _assetLease?.Dispose();
            _assetLease = null;
        }

        /// <summary>Choose the spatial layout used by future world visibility passes.</summary>
        public void ConfigureWorldCells(InstanceWorldContentKind kind)
        {
            if (_worldContentKind == kind) return;
            _worldContentKind = kind;
            _rebuildRequested = true;
            if (isActiveAndEnabled) Rebuild();
        }

        /// <summary>Load a streamed prototype. Failed or cancelled replacements keep the previous population and lease.</summary>
        public async Task ConfigureAsync(IVegetationAssetSource source, string key, Placement[] placements,
            float maxDistance = 1000, CancellationToken cancellation = default)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (!isActiveAndEnabled)
            {
                throw new InvalidOperationException("Enable the container before streaming an asset.");
            }
            var copy = CopyValidated(placements);
            ValidateDistance(maxDistance);
            CancelLoad();
            int version = _loadVersion;
            _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var token = _loadCancellation.Token;
            VegetationAssetLease lease = null;
            try
            {
                lease = await source.AcquireAsync(key, token);
                token.ThrowIfCancellationRequested();
                if (version != _loadVersion || !this || !isActiveAndEnabled)
                {
                    throw new OperationCanceledException();
                }
                Replace(lease.Prefab, copy, maxDistance);
                _assetLease?.Dispose();
                _assetLease = lease;
                lease = null;
            }
            finally
            {
                lease?.Dispose();
                if (version == _loadVersion)
                {
                    _loadCancellation?.Dispose();
                    _loadCancellation = null;
                }
            }
        }

        /// <summary>Set explicit quality controls for this population and retain its placement IDs.</summary>
        public void ConfigureQuality(InstanceQualitySettings quality, InstanceVisibilityMode visibility)
        {
            quality.Validate(_prototype && _prototype.GetComponentsInChildren<Collider>(true).Length > 0);
            if (_renderer != null)
            {
                _renderer.SetQuality(0, InstanceQualityScaler.Scale(quality, _adaptiveQualityScale));
                _renderer.VisibilityMode = visibility;
            }
            _quality = quality;
            _visibilityMode = visibility;
        }

        /// <summary>Apply runtime quality without changing authored placements or quality values.</summary>
        public void ApplyAdaptiveQuality(AdaptiveQualityState state)
        {
            _adaptiveQualityScale = state.Scale;
            if (_renderer == null)
            {
                return;
            }
            _renderer.SetQuality(0, InstanceQualityScaler.Scale(_quality, state.Scale));
            _renderer.ShadowDistance = _shadowDistance * Mathf.Lerp(0.55f, 1, state.Scale);
        }

        private static uint VisibilityKey(string id)
        {
            uint hash = 2166136261;
            foreach (char character in id)
            {
                hash = unchecked((hash ^ character) * 16777619);
            }
            return hash;
        }

        /// <summary>Rebuild prototype buffers after mesh, material or render settings change. Placement IDs remain unchanged.</summary>
        public void Rebuild()
        {
            ValidateDistance(_maxDistance);
            Replace(_prototype, CopyValidated(_placements), _maxDistance);
        }

        private void Replace(GameObject source, Placement[] placements, float distance)
        {
            InstanceRenderer candidate = null;
            var handles = new Dictionary<string, LivePlacement>(StringComparer.Ordinal);
            Matrix4x4 world = transform.localToWorldMatrix;
            try
            {
                if (isActiveAndEnabled && source != null)
                {
                    var visible = Resolve(placements);
                    ValidateWorld(visible, world);
                    var prototype = InstancePrototype.FromPrefab(source, _materialProfile);
                    candidate = new InstanceRenderer(_cullingShader, gameObject.layer, _sourceId, _worldContentKind)
                    {
                        MaxDistance = distance,
                        ShadowDistance = _shadowDistance * Mathf.Lerp(0.55f, 1, _adaptiveQualityScale),
                        MinimumShadowLod = _minimumShadowLod,
                        VisibilityMode = _visibilityMode
                    };
                    int index = candidate.Register(prototype);
                    candidate.SetQuality(index, InstanceQualityScaler.Scale(_quality, _adaptiveQualityScale));
                    foreach (var placement in visible)
                    {
                        Matrix4x4 matrix = world * LocalMatrix(placement);
                        var handle = candidate.Add(index, matrix);
                        candidate.SetVisibilityKey(handle, VisibilityKey(placement.Id));
                        if (IsVisibilityClaimed(placement.Id)) candidate.SetVisible(handle, false);
                        handles.Add(placement.Id, new LivePlacement { Handle = handle, Matrix = matrix });
                    }
                    candidate.Flush();
                }
            }
            catch
            {
                candidate?.Dispose();
                throw;
            }
            ReleaseRenderer();
            _renderer = candidate;
            foreach (var pair in handles)
            {
                _live.Add(pair.Key, pair.Value);
            }
            _prototype = source;
            _placements = placements;
            _maxDistance = distance;
            _worldMatrix = world;
            _layer = gameObject.layer;
            IndexPlacements();
            _dataDirty = false;
            _rebuildRequested = false;
            Diagnostic = null;
        }
        #endregion

        #region Placements
        /// <summary>Return an independent snapshot. Editing the result does not change this container.</summary>
        public Placement[] CopyPlacements()
        {
            EnsureData();
            return (Placement[])_placements.Clone();
        }

        /// <summary>Replace placements without rebuilding the renderer. Existing IDs keep their live handles.</summary>
        public void SetPlacements(Placement[] placements)
        {
            var copy = CopyValidated(placements);
            if (_renderer != null)
            {
                ValidateWorld(copy, transform.localToWorldMatrix);
            }
            _placements = copy;
            IndexPlacements();
            _dataDirty = true;
            Synchronize();
        }

        /// <summary>Append placements and return their IDs. The caller's array is not changed.</summary>
        public string[] AddPlacements(Placement[] placements)
        {
            EnsureData();
            var added = CopyValidated(placements);
            var result = new Placement[_placements.Length + added.Length];
            Array.Copy(_placements, result, _placements.Length);
            Array.Copy(added, 0, result, _placements.Length, added.Length);
            var ids = new string[added.Length];
            for (int i = 0; i < added.Length; i++)
            {
                ids[i] = added[i].Id;
            }
            SetPlacements(result);
            return ids;
        }

        /// <summary>Apply ID-based additions, updates and removals together. Invalid batches leave source data unchanged.</summary>
        public void ApplyChanges(Placement[] upserts, string[] removals = null)
        {
            EnsureData();
            var updates = new Dictionary<string, Placement>(StringComparer.Ordinal);
            foreach (var placement in upserts ?? Array.Empty<Placement>())
            {
                if (string.IsNullOrWhiteSpace(placement.Id) || !updates.TryAdd(placement.Id, placement))
                {
                    throw new ArgumentException("Each update must have a unique, nonempty placement ID.");
                }
            }
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in removals ?? Array.Empty<string>())
            {
                if (id == null || !_indices.ContainsKey(id) || !removed.Add(id) || updates.ContainsKey(id))
                {
                    throw new ArgumentException("Removal IDs must exist, be unique and have no update in the same batch.");
                }
            }
            var next = new List<Placement>(_placements.Length + updates.Count);
            foreach (var placement in _placements)
            {
                if (removed.Contains(placement.Id))
                {
                    continue;
                }
                next.Add(updates.TryGetValue(placement.Id, out var replacement) ? replacement : placement);
            }
            foreach (var pair in updates)
            {
                if (!_indices.ContainsKey(pair.Key))
                {
                    next.Add(pair.Value);
                }
            }
            SetPlacements(next.ToArray());
        }

        /// <summary>Find source data by persistent ID, including when rendering is disabled.</summary>
        public bool TryGetPlacement(string id, out Placement placement)
        {
            EnsureData();
            placement = default;
            if (id == null || !_indices.TryGetValue(id, out int index)) return false;
            placement = _placements[index];
            return true;
        }

        /// <summary>Resolve a persistent ID to its current renderer handle. Handles expire when the renderer is rebuilt.</summary>
        public bool TryGetHandle(string id, out InstanceHandle handle)
        {
            handle = default;
            if (id == null || !_live.TryGetValue(id, out var live)) return false;
            handle = live.Handle;
            return true;
        }

        /// <summary>Transfer one placement between the GPU renderer and an interactive proxy.</summary>
        public bool SetProxyOwned(string id, bool proxyOwned)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Placement ID must be nonempty.", nameof(id));
            if (!TryGetResolvedPlacement(id, out _)) return false;
            if (proxyOwned)
            {
                _proxyOwned.Add(id);
            }
            else
            {
                _proxyOwned.Remove(id);
            }
            if (_renderer == null || !_live.TryGetValue(id, out var live)) return true;
            bool changed = _renderer.SetVisible(live.Handle, !IsVisibilityClaimed(id));
            _renderer.Flush();
            return changed;
        }

        /// <summary>Transfer several placements and flush renderer visibility once.</summary>
        public int SetProxyOwnership(IReadOnlyList<string> ids, bool proxyOwned)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            int changed = 0;
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id) || !TryGetResolvedPlacement(id, out _)) continue;
                if (proxyOwned) _proxyOwned.Add(id);
                else _proxyOwned.Remove(id);
                if (_renderer != null && _live.TryGetValue(id, out var live) &&
                    _renderer.SetVisible(live.Handle, !IsVisibilityClaimed(id))) changed++;
            }
            if (changed > 0) _renderer.Flush();
            return changed;
        }

        /// <summary>Claim or release source visibility for one derived owner without disturbing other owners.</summary>
        public int SetVisibilityClaim(string ownerId, IReadOnlyList<string> ids, bool claimed)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Owner ID must be nonempty.", nameof(ownerId));
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (!_visibilityClaims.TryGetValue(ownerId, out HashSet<string> owned))
            {
                owned = new HashSet<string>(StringComparer.Ordinal);
                if (claimed) _visibilityClaims.Add(ownerId, owned);
            }
            int changed = 0;
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id) || !TryGetResolvedPlacement(id, out _)) continue;
                bool before = IsVisibilityClaimed(id);
                if (claimed) owned.Add(id); else owned.Remove(id);
                bool after = IsVisibilityClaimed(id);
                if (before != after && _renderer != null && _live.TryGetValue(id, out var live) &&
                    _renderer.SetVisible(live.Handle, !after)) changed++;
            }
            if (!claimed && owned.Count == 0) _visibilityClaims.Remove(ownerId);
            if (changed > 0) _renderer.Flush();
            return changed;
        }

        private bool IsVisibilityClaimed(string id)
        {
            if (_proxyOwned.Contains(id)) return true;
            foreach (HashSet<string> claim in _visibilityClaims.Values)
                if (claim.Contains(id)) return true;
            return false;
        }

        /// <summary>Apply a proxy transform to the gameplay source while preserving its placement ID.</summary>
        public bool CommitProxyTransform(string id, Transform proxy)
        {
            if (!TryCreateProxyPlacement(id, proxy, out var placement)) return false;
            CommitProxyPlacements(new[] { placement });
            return true;
        }

        /// <summary>Convert one proxy world transform into a placement with the same persistent ID.</summary>
        public bool TryCreateProxyPlacement(string id, Transform proxy, out Placement placement)
        {
            placement = default;
            if (!proxy || !TryGetResolvedPlacement(id, out placement)) return false;
            Matrix4x4 local = transform.worldToLocalMatrix * proxy.localToWorldMatrix;
            placement.Position = local.GetPosition();
            placement.EulerAngles = local.rotation.eulerAngles;
            placement.Scale = local.lossyScale;
            return true;
        }

        /// <summary>Apply several returned proxy transforms in one source transaction.</summary>
        public void CommitProxyPlacements(Placement[] placements)
        {
            if (placements == null || placements.Length == 0) return;
            if (_journal != null) _journal.OverrideMany(_sourceId, placements);
            else ApplyChanges(placements);
        }

        /// <summary>Remove one placement after gameplay destruction.</summary>
        public bool DestroyProxyOwned(string id)
        {
            if (!TryGetResolvedPlacement(id, out _)) return false;
            _proxyOwned.Remove(id);
            if (_journal != null)
            {
                _journal.Remove(_sourceId, id);
            }
            else
            {
                ApplyChanges(null, new[] { id });
            }
            return true;
        }

        /// <summary>Append IDs whose placement origins are inside a world-space box. This linear query ignores render visibility.</summary>
        public int QueryOrigins(Bounds worldBounds, List<string> results)
        {
            if (results == null)
            {
                throw new ArgumentNullException(nameof(results));
            }
            EnsureData();
            int before = results.Count;
            Matrix4x4 world = transform.localToWorldMatrix;
            foreach (var placement in _resolved)
            {
                if (worldBounds.Contains(world.MultiplyPoint3x4(placement.Position)))
                {
                    results.Add(placement.Id);
                }
            }
            return results.Count - before;
        }
        #endregion

        /// <summary>Apply changed placements or container transforms. A stationary, unchanged container does not upload source data.</summary>
        public void Synchronize()
        {
            if (!isActiveAndEnabled) return;
            if (_rebuildRequested || (_renderer != null && _layer != gameObject.layer))
            {
                Rebuild();
                return;
            }
            if (_renderer == null) return;
            Matrix4x4 world = transform.localToWorldMatrix;
            if (!_dataDirty && world == _worldMatrix && _renderer.RenderingEnabled) return;
            EnsureData();
            ValidateWorld(_resolved, world);
            _removed.Clear();
            foreach (var pair in _live)
            {
                if (!TryGetResolvedPlacement(pair.Key, out _))
                {
                    _removed.Add(pair.Key);
                }
            }
            foreach (string id in _removed)
            {
                _renderer.Remove(_live[id].Handle);
                _live.Remove(id);
            }
            foreach (var placement in _resolved)
            {
                Matrix4x4 matrix = world * LocalMatrix(placement);
                if (_live.TryGetValue(placement.Id, out var live))
                {
                    if (matrix == live.Matrix)
                    {
                        continue;
                    }
                    _renderer.Update(live.Handle, matrix);
                    live.Matrix = matrix;
                }
                else
                {
                    live = new LivePlacement { Handle = _renderer.Add(0, matrix), Matrix = matrix };
                    _renderer.SetVisibilityKey(live.Handle, VisibilityKey(placement.Id));
                    if (IsVisibilityClaimed(placement.Id)) _renderer.SetVisible(live.Handle, false);
                }
                _live[placement.Id] = live;
            }
            _renderer.Flush();
            _renderer.RenderingEnabled = true;
            _worldMatrix = world;
            _dataDirty = false;
            Diagnostic = null;
        }

        #region Validation
        internal static Placement[] CopyValidated(Placement[] placements)
        {
            var copy = placements == null ? Array.Empty<Placement>() : (Placement[])placements.Clone();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < copy.Length; i++)
            {
                ValidateMatrix(LocalMatrix(copy[i]));
                if (string.IsNullOrWhiteSpace(copy[i].Id))
                {
                    copy[i].Id = Guid.NewGuid().ToString("N");
                }
                if (!ids.Add(copy[i].Id))
                {
                    throw new ArgumentException("Placement IDs must be unique within a container.");
                }
            }
            return copy;
        }

        private void EnsureData()
        {
            if (!_indexDirty) return;
            _placements = CopyValidated(_placements);
            IndexPlacements();
        }

        private void IndexPlacements()
        {
            _indexDirty = false;
            _resolved = Resolve(_placements);
            _indices.Clear();
            for (int i = 0; i < _placements.Length; i++)
            {
                _indices.Add(_placements[i].Id, i);
            }
        }

        private Placement[] Resolve(Placement[] source)
        {
            if (_journal == null) return source;
            var result = new List<Placement>(source.Length);
            foreach (var placement in source)
            {
                if (_journal.Resolve(_sourceId, placement, out var resolved))
                {
                    result.Add(resolved);
                }
            }
            return result.ToArray();
        }

        private static Matrix4x4 LocalMatrix(Placement placement)
        {
            return Matrix4x4.TRS(placement.Position, Quaternion.Euler(placement.EulerAngles), placement.Scale);
        }

        private static void ValidateWorld(Placement[] placements, Matrix4x4 world)
        {
            ValidateMatrix(world);
            foreach (var placement in placements)
            {
                ValidateMatrix(world * LocalMatrix(placement));
            }
        }

        private static void ValidateMatrix(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; i++)
            {
                if (!float.IsFinite(matrix[i]))
                {
                    throw new ArgumentException("Placement transforms must contain finite values.");
                }
            }
            if (!float.IsFinite(matrix.determinant) || Mathf.Abs(matrix.determinant) < 0.000001f)
            {
                throw new ArgumentException("Placement transforms must be invertible.");
            }
        }

        private static void ValidateDistance(float distance)
        {
            if (!float.IsFinite(distance) || distance <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }
        }
        #endregion

        private void ReleaseRenderer()
        {
            _renderer?.Dispose();
            _renderer = null;
            _live.Clear();
        }

        private void CancelLoad()
        {
            _loadVersion++;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        }
    }
}

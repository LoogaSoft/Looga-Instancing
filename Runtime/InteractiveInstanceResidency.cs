using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Stable gameplay identity for one promoted instance.</summary>
    public readonly struct InstanceProxyKey : IEquatable<InstanceProxyKey>
    {
        public readonly string SourceId;
        public readonly string PlacementId;

        public InstanceProxyKey(string sourceId, string placementId)
        {
            SourceId = sourceId;
            PlacementId = placementId;
        }

        public bool Equals(InstanceProxyKey other)
        {
            return string.Equals(SourceId, other.SourceId, StringComparison.Ordinal) &&
                string.Equals(PlacementId, other.PlacementId, StringComparison.Ordinal);
        }

        public override bool Equals(object value) => value is InstanceProxyKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SourceId, PlacementId);
        public override string ToString() => SourceId + ":" + PlacementId;
    }

    /// <summary>Context passed to proxy lifecycle and network hooks.</summary>
    public readonly struct InstanceProxyContext
    {
        public readonly InstanceProxyKey Key;
        public readonly InstanceContainer Source;
        public readonly GameObject Proxy;

        internal InstanceProxyContext(InstanceProxyKey key, InstanceContainer source, GameObject proxy)
        {
            Key = key;
            Source = source;
            Proxy = proxy;
        }
    }

    /// <summary>Reason that a GameObject proxy leaves near-field ownership.</summary>
    public enum InstanceProxyReleaseReason
    {
        Distance,
        Requested,
        Destroyed,
        ResidencyDisabled
    }

    /// <summary>Receives promotion and release notifications.</summary>
    public interface IInstanceProxyLifecycle
    {
        void OnInstancePromoted(InstanceProxyContext context);
        void OnInstanceReleased(InstanceProxyContext context, InstanceProxyReleaseReason reason);
    }

    /// <summary>Stores game-specific state as an opaque save payload.</summary>
    public interface IInstanceProxyStateAdapter
    {
        string CaptureInstanceState();
        void RestoreInstanceState(string payload);
    }

    /// <summary>Receives promotion events without a dependency on one network package.</summary>
    public interface IInstanceProxyNetworkBridge
    {
        void OnInstanceProxyPromoted(InstanceProxyContext context);
        void OnInstanceProxyReleased(InstanceProxyContext context, InstanceProxyReleaseReason reason);
    }

    /// <summary>Handles one harvest request. Return true when harvesting destroys the instance.</summary>
    public interface IInstanceHarvestHandler
    {
        bool HarvestInstance(float amount);
    }

    /// <summary>Serializable proxy state for save systems and streamed-cell reloads.</summary>
    [Serializable]
    public struct InstanceProxyStateRecord
    {
        public string SourceId;
        public string PlacementId;
        public string Payload;
        public int Revision;
    }

    /// <summary>Stores opaque gameplay state by stable source and placement identity.</summary>
    [Serializable]
    public sealed class InstanceProxyStateStore : ISerializationCallbackReceiver
    {
        [SerializeField] private List<InstanceProxyStateRecord> _records = new();
        private Dictionary<InstanceProxyKey, InstanceProxyStateRecord> _index;

        public bool TryGet(InstanceProxyKey key, out InstanceProxyStateRecord record)
        {
            Index();
            return _index.TryGetValue(key, out record);
        }

        public void Set(InstanceProxyStateRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.SourceId) || string.IsNullOrWhiteSpace(record.PlacementId))
            {
                throw new ArgumentException("Proxy state requires source and placement IDs.");
            }
            Index();
            var key = new InstanceProxyKey(record.SourceId, record.PlacementId);
            record.Revision = _index.TryGetValue(key, out var previous) ? previous.Revision + 1 : 1;
            _index[key] = record;
            int index = _records.FindIndex(item => item.SourceId == record.SourceId && item.PlacementId == record.PlacementId);
            if (index < 0) _records.Add(record);
            else _records[index] = record;
        }

        public InstanceProxyStateRecord[] Export() => _records.ToArray();

        public void Import(InstanceProxyStateRecord[] records)
        {
            var next = new Dictionary<InstanceProxyKey, InstanceProxyStateRecord>();
            foreach (var record in records ?? Array.Empty<InstanceProxyStateRecord>())
            {
                if (string.IsNullOrWhiteSpace(record.SourceId) || string.IsNullOrWhiteSpace(record.PlacementId))
                {
                    throw new ArgumentException("Proxy state requires source and placement IDs.");
                }
                if (!next.TryAdd(new InstanceProxyKey(record.SourceId, record.PlacementId), record))
                {
                    throw new ArgumentException("Proxy state identities must be unique.");
                }
            }
            _records = new List<InstanceProxyStateRecord>(next.Values);
            _index = next;
        }

        public void OnBeforeSerialize() { }
        public void OnAfterDeserialize() => _index = null;

        private void Index()
        {
            if (_index != null) return;
            _index = new Dictionary<InstanceProxyKey, InstanceProxyStateRecord>();
            foreach (var record in _records)
            {
                _index.Add(new InstanceProxyKey(record.SourceId, record.PlacementId), record);
            }
        }
    }

    /// <summary>Connects gameplay calls to the residency owner of a promoted proxy.</summary>
    [DisallowMultipleComponent]
    public sealed class InstanceProxyIdentity : MonoBehaviour
    {
        private InteractiveInstanceResidency _owner;
        public InstanceProxyKey Key { get; private set; }
        public InstanceContainer Source { get; private set; }

        internal void Initialize(InteractiveInstanceResidency owner, InstanceContainer source, string placementId)
        {
            _owner = owner;
            Source = source;
            Key = new InstanceProxyKey(source.SourceId, placementId);
        }

        /// <summary>Send a harvest request to all handlers on this proxy.</summary>
        public bool Harvest(float amount)
        {
            if (!float.IsFinite(amount) || amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            bool destroy = false;
            foreach (var behaviour in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IInstanceHarvestHandler handler) destroy |= handler.HarvestInstance(amount);
            }
            if (destroy) DestroyInstance();
            return destroy;
        }

        /// <summary>Remove this identity from the gameplay source and GPU renderer.</summary>
        public void DestroyInstance() => _owner?.DestroyPromoted(Key.PlacementId);

        /// <summary>Return this proxy to pooled storage and restore GPU rendering.</summary>
        public void ReturnToGpu() => _owner?.ReleasePromoted(Key.PlacementId);
    }

    /// <summary>Promotes nearby GPU instances into pooled GameObjects for gameplay.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class InteractiveInstanceResidency : MonoBehaviour
    {
        [SerializeField] private InstanceContainer _source;
        [SerializeField] private GameObject _proxyPrototype;
        [SerializeField] private VegetationInterest[] _interests = Array.Empty<VegetationInterest>();
        [SerializeField, Min(1)] private int _maximumProxies = 128;
        [SerializeField, Min(1)] private int _promotionBudget = 8;
        [SerializeField, Min(0)] private float _releaseMargin = 8;
        [SerializeField] private InstanceProxyStateStore _state = new();

        private readonly Dictionary<string, GameObject> _active = new(StringComparer.Ordinal);
        private readonly Stack<GameObject> _pool = new();
        private readonly List<Candidate> _wanted = new();
        private readonly List<string> _release = new();
        private readonly List<string> _promoted = new();
        private readonly List<InstanceContainer.Placement> _commits = new();
        private readonly HashSet<string> _retained = new(StringComparer.Ordinal);
        private GameObject _owner;

        private struct Candidate
        {
            internal InstanceContainer.Placement Placement;
            internal float Distance;
        }

        public int ActiveCount => _active.Count;
        public int PooledCount => _pool.Count;
        public int PendingCount { get; private set; }
        public string Diagnostic { get; private set; }
        public InstanceProxyStateStore StateStore => _state;

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            Synchronize();
        }

        private void OnDisable() => ReleaseAll(InstanceProxyReleaseReason.ResidencyDisabled);

        /// <summary>Configure opt-in promotion without changing the source prefab or placement data.</summary>
        public void Configure(InstanceContainer source, GameObject proxyPrototype, VegetationInterest[] interests,
            int maximumProxies = 128, int promotionBudget = 8, float releaseMargin = 8)
        {
            if (maximumProxies < 1 || promotionBudget < 1 || !float.IsFinite(releaseMargin) || releaseMargin < 0)
            {
                throw new ArgumentException("Use positive budgets and a finite, nonnegative release margin.");
            }
            ReleaseAll(InstanceProxyReleaseReason.ResidencyDisabled);
            _source = source;
            _proxyPrototype = proxyPrototype;
            _interests = interests == null ? Array.Empty<VegetationInterest>() : (VegetationInterest[])interests.Clone();
            _maximumProxies = maximumProxies;
            _promotionBudget = promotionBudget;
            _releaseMargin = releaseMargin;
        }

        /// <summary>Use a save-owned state store across residency or streamed-cell lifetimes.</summary>
        public void BindStateStore(InstanceProxyStateStore state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>Update near-field ownership with bounded promotion work.</summary>
        public void Synchronize()
        {
            if (!_source || !_proxyPrototype || !isActiveAndEnabled)
            {
                ReleaseAll(InstanceProxyReleaseReason.ResidencyDisabled);
                return;
            }
            _wanted.Clear();
            _retained.Clear();
            foreach (var placement in _source.CopyResolvedPlacements())
            {
                Vector3 position = _source.transform.TransformPoint(placement.Position);
                float nearest = float.PositiveInfinity;
                foreach (var interest in _interests)
                {
                    if (!interest || !interest.isActiveAndEnabled) continue;
                    float distance = Vector3.Distance(position, interest.transform.position);
                    float radius = interest.Radius + (_active.ContainsKey(placement.Id) ? _releaseMargin : 0);
                    if (distance <= radius) nearest = Mathf.Min(nearest, distance);
                }
                if (!float.IsPositiveInfinity(nearest))
                {
                    _wanted.Add(new Candidate { Placement = placement, Distance = nearest });
                }
            }
            _wanted.Sort((a, b) =>
            {
                int distance = a.Distance.CompareTo(b.Distance);
                return distance != 0 ? distance : string.CompareOrdinal(a.Placement.Id, b.Placement.Id);
            });
            int wantedCount = Mathf.Min(_wanted.Count, _maximumProxies);
            for (int i = 0; i < wantedCount; i++) _retained.Add(_wanted[i].Placement.Id);
            _release.Clear();
            foreach (var pair in _active)
            {
                if (!_retained.Contains(pair.Key)) _release.Add(pair.Key);
            }
            ReleaseBatch(_release, InstanceProxyReleaseReason.Distance);

            int promoted = 0;
            _promoted.Clear();
            for (int i = 0; i < wantedCount && promoted < _promotionBudget; i++)
            {
                var placement = _wanted[i].Placement;
                if (_active.ContainsKey(placement.Id)) continue;
                PreparePromotion(placement);
                _promoted.Add(placement.Id);
                promoted++;
            }
            if (_promoted.Count > 0)
            {
                _source.SetProxyOwnership(_promoted, true);
                foreach (string id in _promoted)
                {
                    GameObject proxy = _active[id];
                    proxy.SetActive(true);
                    NotifyPromotion(proxy, Context(id, proxy));
                }
            }
            PendingCount = wantedCount - _active.Count;
            Diagnostic = _wanted.Count > _maximumProxies ?
                "Interactive proxy demand exceeds capacity. Increase Maximum Proxies or reduce interest regions." : null;
        }

        /// <summary>Return one active proxy to the GPU representation.</summary>
        public bool ReleasePromoted(string id) => Release(id, InstanceProxyReleaseReason.Requested);

        /// <summary>Remove one active proxy and its persistent source placement.</summary>
        public bool DestroyPromoted(string id)
        {
            if (!_active.TryGetValue(id, out var proxy)) return false;
            var context = Context(id, proxy);
            NotifyRelease(proxy, context, InstanceProxyReleaseReason.Destroyed);
            CaptureState(context);
            _active.Remove(id);
            proxy.SetActive(false);
            _pool.Push(proxy);
            _source.DestroyProxyOwned(id);
            return true;
        }

        private void PreparePromotion(InstanceContainer.Placement placement)
        {
            EnsureOwner();
            GameObject proxy = _pool.Count > 0 ? _pool.Pop() : Instantiate(_proxyPrototype, _owner.transform);
            proxy.name = _proxyPrototype.name + " [" + placement.Id + "]";
            proxy.transform.SetParent(_owner.transform, true);
            proxy.transform.SetPositionAndRotation(_source.transform.TransformPoint(placement.Position),
                _source.transform.rotation * Quaternion.Euler(placement.EulerAngles));
            proxy.transform.localScale = Vector3.Scale(_source.transform.lossyScale, placement.Scale);
            var identity = proxy.GetComponent<InstanceProxyIdentity>() ?? proxy.AddComponent<InstanceProxyIdentity>();
            identity.Initialize(this, _source, placement.Id);
            _active.Add(placement.Id, proxy);
            var context = Context(placement.Id, proxy);
            RestoreState(context);
        }

        private bool Release(string id, InstanceProxyReleaseReason reason)
        {
            if (!_active.TryGetValue(id, out var proxy)) return false;
            var context = Context(id, proxy);
            NotifyRelease(proxy, context, reason);
            CaptureState(context);
            _source.CommitProxyTransform(id, proxy.transform);
            _source.SetProxyOwned(id, false);
            _active.Remove(id);
            proxy.SetActive(false);
            _pool.Push(proxy);
            return true;
        }

        private void ReleaseBatch(List<string> ids, InstanceProxyReleaseReason reason)
        {
            if (ids.Count == 0 || !_source) return;
            _commits.Clear();
            foreach (string id in ids)
            {
                if (!_active.TryGetValue(id, out var proxy)) continue;
                var context = Context(id, proxy);
                NotifyRelease(proxy, context, reason);
                CaptureState(context);
                if (_source.TryCreateProxyPlacement(id, proxy.transform, out var placement)) _commits.Add(placement);
                _active.Remove(id);
                proxy.SetActive(false);
                _pool.Push(proxy);
            }
            if (_commits.Count > 0) _source.CommitProxyPlacements(_commits.ToArray());
            _source.SetProxyOwnership(ids, false);
        }

        private void CaptureState(InstanceProxyContext context)
        {
            string payload = null;
            foreach (var behaviour in context.Proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IInstanceProxyStateAdapter adapter) payload = adapter.CaptureInstanceState();
            }
            _state.Set(new InstanceProxyStateRecord
            {
                SourceId = context.Key.SourceId,
                PlacementId = context.Key.PlacementId,
                Payload = payload
            });
        }

        private void RestoreState(InstanceProxyContext context)
        {
            _state.TryGet(context.Key, out var state);
            foreach (var behaviour in context.Proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IInstanceProxyStateAdapter adapter) adapter.RestoreInstanceState(state.Payload);
            }
        }

        private static void NotifyPromotion(GameObject proxy, InstanceProxyContext context)
        {
            foreach (var behaviour in proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IInstanceProxyLifecycle lifecycle) lifecycle.OnInstancePromoted(context);
                if (behaviour is IInstanceProxyNetworkBridge network) network.OnInstanceProxyPromoted(context);
            }
        }

        private static void NotifyRelease(GameObject proxy, InstanceProxyContext context, InstanceProxyReleaseReason reason)
        {
            foreach (var behaviour in proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IInstanceProxyLifecycle lifecycle) lifecycle.OnInstanceReleased(context, reason);
                if (behaviour is IInstanceProxyNetworkBridge network) network.OnInstanceProxyReleased(context, reason);
            }
        }

        private InstanceProxyContext Context(string id, GameObject proxy)
        {
            return new InstanceProxyContext(new InstanceProxyKey(_source.SourceId, id), _source, proxy);
        }

        private void EnsureOwner()
        {
            if (_owner) return;
            _owner = new GameObject("Looga interactive proxies") { hideFlags = HideFlags.HideAndDontSave };
        }

        private void ReleaseAll(InstanceProxyReleaseReason reason)
        {
            _release.Clear();
            foreach (var pair in _active) _release.Add(pair.Key);
            if (_source)
            {
                ReleaseBatch(_release, reason);
            }
            else
            {
                foreach (string id in _release)
                {
                    GameObject proxy = _active[id];
                    proxy.SetActive(false);
                    _pool.Push(proxy);
                }
                _active.Clear();
            }
            while (_pool.Count > 0) DestroyObject(_pool.Pop());
            DestroyObject(_owner);
            _owner = null;
            PendingCount = 0;
        }

        private static void DestroyObject(GameObject value)
        {
            if (!value) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}

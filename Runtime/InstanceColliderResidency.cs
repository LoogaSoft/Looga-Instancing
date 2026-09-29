using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Streams collider-only proxies around explicit gameplay interests. Camera visibility does not control physics.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceColliderResidency")]
    public sealed class InstanceColliderResidency : MonoBehaviour
    {
        [SerializeField] private InstanceContainer _source;
        [SerializeField] private GameObject _colliderPrototype;
        [SerializeField] private VegetationInterest[] _interests = Array.Empty<VegetationInterest>();
        [SerializeField, Min(1)] private int _maximumProxies = 256;
        [SerializeField, Min(1)] private int _creationBudget = 16;
        [SerializeField, Min(0)] private float _releaseMargin = 8;
        private readonly Dictionary<string, GameObject> _proxies = new(StringComparer.Ordinal);
        private readonly List<string> _remove = new();
        private readonly List<Candidate> _wanted = new();
        private readonly HashSet<string> _retained = new(StringComparer.Ordinal);
        private GameObject _owner;
        private bool _validated;
        private InstanceContainer.Placement[] _boundsSource;
        private Matrix4x4 _boundsMatrix;
        private Bounds _pivotBounds;
        private struct Candidate
        {
            internal InstanceContainer.Placement Placement;
            internal float Distance;
        }

        /// <summary>Number of live collider roots.</summary>
        public int ResidentCount => _proxies.Count;
        /// <summary>Required roots waiting for capacity or the creation budget.</summary>
        public int PendingCount { get; private set; }
        /// <summary>Last invalid configuration or exhausted capacity diagnostic.</summary>
        public string Diagnostic { get; private set; }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            try
            {
                Synchronize();
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                Release();
            }
        }

        private void OnDisable() => Release();
        private void OnValidate() => _validated = false;

        /// <summary>Configure a collider-only source. Prototype scripts and renderers are never instantiated.</summary>
        public void Configure(InstanceContainer source, GameObject colliderPrototype, VegetationInterest[] interests,
            int maximumProxies = 256, int creationBudget = 16, float releaseMargin = 8)
        {
            if (maximumProxies < 1 || creationBudget < 1 || !float.IsFinite(releaseMargin) || releaseMargin < 0)
            {
                throw new ArgumentException("Use positive proxy budgets and a finite, nonnegative release margin.");
            }
            Release();
            _source = source;
            _colliderPrototype = colliderPrototype;
            _interests = interests == null ? Array.Empty<VegetationInterest>() : (VegetationInterest[])interests.Clone();
            _maximumProxies = maximumProxies;
            _creationBudget = creationBudget;
            _releaseMargin = releaseMargin;
            _validated = false;
        }

        /// <summary>Update physics residency without requiring an enabled container or a rendering camera.</summary>
        public void Synchronize()
        {
            if (!_source || !_colliderPrototype || !isActiveAndEnabled)
            {
                Release();
                _validated = false;
                return;
            }
            if (!_validated)
            {
                Release();
                ValidatePrototype();
                _validated = true;
            }
            InstanceContainer.Placement[] placements = _source.ResolvedPlacements;
            // Skip the per-placement scan when no interest can reach this container and no proxy needs release.
            if (_proxies.Count == 0 && !InterestReachesPlacements(placements))
            {
                PendingCount = 0;
                Diagnostic = null;
                return;
            }
            _wanted.Clear();
            _retained.Clear();
            foreach (var placement in placements)
            {
                Vector3 position = _source.transform.TransformPoint(placement.Position);
                float nearest = float.PositiveInfinity;
                foreach (var interest in _interests)
                {
                    if (!interest || !interest.isActiveAndEnabled)
                    {
                        continue;
                    }
                    float distance = Vector3.Distance(position, interest.transform.position);
                    float radius = interest.Radius + (_proxies.ContainsKey(placement.Id) ? _releaseMargin : 0);
                    if (distance <= radius)
                    {
                        nearest = Mathf.Min(nearest, distance);
                    }
                }
                if (float.IsPositiveInfinity(nearest))
                {
                    continue;
                }
                _wanted.Add(new Candidate { Placement = placement, Distance = nearest });
            }
            _wanted.Sort((a, b) =>
            {
                int comparison = a.Distance.CompareTo(b.Distance);
                return comparison != 0 ? comparison : string.CompareOrdinal(a.Placement.Id, b.Placement.Id);
            });
            int count = Mathf.Min(_wanted.Count, _maximumProxies);
            for (int i = 0; i < count; i++)
            {
                _retained.Add(_wanted[i].Placement.Id);
            }
            _remove.Clear();
            foreach (var pair in _proxies)
            {
                if (!_retained.Contains(pair.Key))
                {
                    _remove.Add(pair.Key);
                }
            }
            foreach (string id in _remove)
            {
                DestroyProxy(_proxies[id]);
                _proxies.Remove(id);
            }
            int created = 0;
            for (int i = 0; i < count; i++)
            {
                var placement = _wanted[i].Placement;
                if (!_proxies.TryGetValue(placement.Id, out var proxy))
                {
                    if (created >= _creationBudget)
                    {
                        continue;
                    }
                    EnsureOwner();
                    proxy = CreateProxy(placement.Id);
                    _proxies.Add(placement.Id, proxy);
                    created++;
                }
                proxy.transform.localPosition = placement.Position;
                proxy.transform.localRotation = Quaternion.Euler(placement.EulerAngles);
                proxy.transform.localScale = placement.Scale;
            }
            PendingCount = _wanted.Count - _proxies.Count;
            Diagnostic = _wanted.Count > _maximumProxies ? "Gameplay collider demand exceeds capacity. Increase Maximum Proxies or reduce the interest regions." : null;
        }

        private bool InterestReachesPlacements(InstanceContainer.Placement[] placements)
        {
            if (placements.Length == 0) return false;
            Matrix4x4 world = _source.transform.localToWorldMatrix;
            if (placements != _boundsSource || world != _boundsMatrix)
            {
                // The residency test uses pivot distance, so pivot bounds give an exact rejection.
                _pivotBounds = new Bounds(world.MultiplyPoint3x4(placements[0].Position), Vector3.zero);
                for (int i = 1; i < placements.Length; i++)
                {
                    _pivotBounds.Encapsulate(world.MultiplyPoint3x4(placements[i].Position));
                }
                _boundsSource = placements;
                _boundsMatrix = world;
            }
            foreach (var interest in _interests)
            {
                if (!interest || !interest.isActiveAndEnabled)
                {
                    continue;
                }
                if (_pivotBounds.SqrDistance(interest.transform.position) <= interest.Radius * interest.Radius)
                {
                    return true;
                }
            }
            return false;
        }

        private void ValidatePrototype()
        {
            int count = 0;
            foreach (var collider in _colliderPrototype.GetComponentsInChildren<Collider>(true))
            {
                if (collider is not BoxCollider && collider is not SphereCollider && collider is not CapsuleCollider && collider is not MeshCollider)
                {
                    throw new NotSupportedException("Collider proxies support box, sphere, capsule and mesh colliders.");
                }
                count++;
            }
            if (count == 0)
            {
                throw new ArgumentException("Assign a prototype that contains a supported collider.");
            }
        }

        private void EnsureOwner()
        {
            if (_owner) return;
            _owner = new GameObject("Looga collider residency") { hideFlags = HideFlags.HideAndDontSave };
            _owner.transform.SetParent(_source.transform, false);
        }

        private GameObject CreateProxy(string id)
        {
            var root = new GameObject("Collider " + id) { hideFlags = HideFlags.HideAndDontSave };
            root.transform.SetParent(_owner.transform, false);
            var identity = root.AddComponent<InstanceColliderIdentity>();
            identity.Initialize(_source, id);
            try
            {
                CopyNode(_colliderPrototype.transform, root.transform, true);
                return root;
            }
            catch
            {
                DestroyProxy(root);
                throw;
            }
        }

        private static void CopyNode(Transform source, Transform destination, bool root)
        {
            destination.gameObject.layer = source.gameObject.layer;
            if (!root)
            {
                destination.localPosition = source.localPosition;
                destination.localRotation = source.localRotation;
                destination.localScale = source.localScale;
                destination.gameObject.SetActive(source.gameObject.activeSelf);
            }
            foreach (var original in source.GetComponents<Collider>())
            {
                Collider copy;
                if (original is BoxCollider box)
                {
                    var value = destination.gameObject.AddComponent<BoxCollider>();
                    value.center = box.center;
                    value.size = box.size;
                    copy = value;
                }
                else if (original is SphereCollider sphere)
                {
                    var value = destination.gameObject.AddComponent<SphereCollider>();
                    value.center = sphere.center;
                    value.radius = sphere.radius;
                    copy = value;
                }
                else if (original is CapsuleCollider capsule)
                {
                    var value = destination.gameObject.AddComponent<CapsuleCollider>();
                    value.center = capsule.center;
                    value.radius = capsule.radius;
                    value.height = capsule.height;
                    value.direction = capsule.direction;
                    copy = value;
                }
                else
                {
                    var mesh = (MeshCollider)original;
                    var value = destination.gameObject.AddComponent<MeshCollider>();
                    value.cookingOptions = mesh.cookingOptions;
                    value.convex = mesh.convex;
                    value.sharedMesh = mesh.sharedMesh;
                    copy = value;
                }
                copy.sharedMaterial = original.sharedMaterial;
                copy.isTrigger = original.isTrigger;
                copy.contactOffset = original.contactOffset;
                copy.enabled = original.enabled;
                copy.includeLayers = original.includeLayers;
                copy.excludeLayers = original.excludeLayers;
                copy.layerOverridePriority = original.layerOverridePriority;
            }
            foreach (Transform child in source)
            {
                var node = new GameObject(child.name) { hideFlags = HideFlags.HideAndDontSave };
                node.transform.SetParent(destination, false);
                CopyNode(child, node.transform, false);
            }
        }

        private void Release()
        {
            foreach (var pair in _proxies)
            {
                DestroyProxy(pair.Value);
            }
            _proxies.Clear();
            DestroyProxy(_owner);
            _owner = null;
            PendingCount = 0;
        }

        private static void DestroyProxy(GameObject proxy)
        {
            if (!proxy) return;
            proxy.SetActive(false);
            if (Application.isPlaying)
            {
                Destroy(proxy);
            }
            else
            {
                DestroyImmediate(proxy);
            }
        }
    }

    /// <summary>Resolves a physics hit to a persistent instance ID.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceColliderIdentity")]
    public sealed class InstanceColliderIdentity : MonoBehaviour
    {
        /// <summary>Source container, independent of its render residency.</summary>
        public InstanceContainer Source { get; private set; }
        /// <summary>Persistent placement ID within the source.</summary>
        public string PlacementId { get; private set; }
        internal void Initialize(InstanceContainer source, string id)
        {
            Source = source;
            PlacementId = id;
        }
    }
}

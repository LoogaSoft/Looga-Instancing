using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Switches derived forest-cell HLODs without changing source placements or interactive proxy ownership.</summary>
    [DisallowMultipleComponent]
    public sealed class ForestHlodController : MonoBehaviour
    {
        [SerializeField] private InstanceContainer _source;
        [SerializeField] private ForestHlodAsset _asset;
        [SerializeField] private Transform[] _observers = Array.Empty<Transform>();
        [SerializeField, Min(0)] private float _hysteresis = 30;
        [SerializeField] private ForestShadowPolicy _shadowPolicy = default;
        private InstanceRenderer _renderer;
        private readonly List<InstanceHandle> _handles = new();
        private readonly HashSet<int> _active = new();
        private readonly HashSet<int> _next = new();
        private int _lastShadowPolicyFrame = -1000;
        private const string ClaimOwner = "Looga.ForestHlod";

        public int ActiveCellCount => _active.Count;
        public string Diagnostic { get; private set; }

        public void Configure(InstanceContainer source, ForestHlodAsset asset, Transform[] observers)
        {
            _source = source;
            _asset = asset;
            _observers = observers == null ? Array.Empty<Transform>() : (Transform[])observers.Clone();
            Rebuild();
        }

        private void OnEnable() => Rebuild();
        private void OnDisable() => Release();
        private void LateUpdate() => Synchronize();

        public void Rebuild()
        {
            Release();
            if (!_source || !_asset) return;
            if (!_asset.TryValidate(out string reason))
            {
                Diagnostic = reason;
                return;
            }
            if (_asset.SourceId != _source.SourceId)
            {
                Diagnostic = "Forest HLOD source ID does not match the container.";
                return;
            }
            _shadowPolicy = _shadowPolicy.FarDistance > 0 ? _shadowPolicy : ForestShadowPolicy.Default;
            _shadowPolicy.Validate();
            try
            {
                _renderer = new InstanceRenderer(layer: gameObject.layer, worldSourceId: _source.SourceId + ":hlod",
                    worldContentKind: InstanceWorldContentKind.Hlod);
                foreach (ForestHlodAsset.Cell cell in _asset.Cells)
                {
                    int prototype = _renderer.Register(InstancePrototype.FromPrefab(cell.Prefab));
                    _handles.Add(_renderer.Add(prototype, Matrix4x4.identity));
                    _renderer.SetVisible(_handles[_handles.Count - 1], false);
                }
                _renderer.ShadowDistance = _shadowPolicy.FarDistance;
                _renderer.Flush();
                Diagnostic = null;
            }
            catch (Exception exception)
            {
                Release();
                Diagnostic = exception.Message;
            }
        }

        public void Synchronize()
        {
            if (_renderer == null || !_source || !_asset) return;
            if (_asset.FixedLighting && !LightingMatches(_asset.LightingSignature))
            {
                RestoreSources();
                _renderer.RenderingEnabled = false;
                Diagnostic = "Fixed forest lighting changed. Source trees remain active until the HLOD is rebaked.";
                return;
            }
            _renderer.RenderingEnabled = true;
            _next.Clear();
            float closestActive = float.PositiveInfinity;
            for (int i = 0; i < _asset.Cells.Length; i++)
            {
                Bounds bounds = _asset.Cells[i].Bounds;
                float distance = ClosestDistance(bounds);
                bool wasActive = _active.Contains(i);
                float threshold = _asset.TransitionDistance + (wasActive ? -_hysteresis : _hysteresis);
                if (distance >= Mathf.Max(0, threshold))
                {
                    _next.Add(i);
                    closestActive = Mathf.Min(closestActive, distance);
                }
            }
            for (int i = 0; i < _asset.Cells.Length; i++)
            {
                bool next = _next.Contains(i);
                bool current = _active.Contains(i);
                if (next == current) continue;
                _renderer.SetVisible(_handles[i], next);
                _source.SetVisibilityClaim(ClaimOwner + ":" + _asset.Cells[i].Id, _asset.Cells[i].PlacementIds, next);
            }
            _active.Clear();
            foreach (int cell in _next) _active.Add(cell);
            if (!float.IsPositiveInfinity(closestActive))
            {
                int interval = _shadowPolicy.UpdateInterval(closestActive);
                if (Time.frameCount - _lastShadowPolicyFrame >= interval)
                {
                    _renderer.MinimumShadowLod = _shadowPolicy.MinimumLod(closestActive);
                    _lastShadowPolicyFrame = Time.frameCount;
                }
            }
            _renderer.Flush();
            Diagnostic = null;
        }

        private float ClosestDistance(Bounds bounds)
        {
            float closest = float.PositiveInfinity;
            foreach (Transform observer in _observers)
            {
                if (observer) closest = Mathf.Min(closest, Mathf.Sqrt(bounds.SqrDistance(observer.position)));
            }
            if (float.IsPositiveInfinity(closest) && Camera.main) closest = Mathf.Sqrt(bounds.SqrDistance(Camera.main.transform.position));
            return closest;
        }

        private static bool LightingMatches(Vector4 signature)
        {
            Light sun = RenderSettings.sun;
            if (!sun) return signature == Vector4.zero;
            Vector3 direction = -sun.transform.forward;
            var current = new Vector4(direction.x, direction.y, direction.z, sun.intensity * sun.color.maxColorComponent);
            return (current - signature).sqrMagnitude < 0.0025f;
        }

        private void RestoreSources()
        {
            if (!_source || !_asset) return;
            foreach (ForestHlodAsset.Cell cell in _asset.Cells)
                _source.SetVisibilityClaim(ClaimOwner + ":" + cell.Id, cell.PlacementIds, false);
            _active.Clear();
        }

        private void Release()
        {
            RestoreSources();
            _renderer?.Dispose();
            _renderer = null;
            _handles.Clear();
            _active.Clear();
            _next.Clear();
        }
    }
}

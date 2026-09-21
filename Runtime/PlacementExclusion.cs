using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>A local polyline corridor or generated surface mask. Height limits preserve vegetation below bridges.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "PlacementExclusion")]
    public sealed class PlacementExclusion : MonoBehaviour
    {
        [SerializeField] private Vector3[] _points = Array.Empty<Vector3>();
        [SerializeField, Min(0)] private float _radius = 3;
        [SerializeField, Min(0)] private float _falloff = 1;
        [SerializeField, Min(0)] private float _below = 1;
        [SerializeField, Min(0)] private float _above = 3;
        [SerializeField] private MeshFilter _generatedSurface;
        private MeshScatterSurface _surface;
        private Bounds _bounds;
        private Matrix4x4 _matrix;
        private Matrix4x4 _generatedMatrix;
        private Mesh _generatedMesh;
        private bool _dirty = true;
        private bool _published;
        private Bounds? _pendingNotification;

        /// <summary>Raised with the union of old and new regions. Listeners can regenerate only intersecting placements.</summary>
        public event Action<Bounds> Changed;
        /// <summary>Last captured world bounds.</summary>
        public Bounds WorldBounds => _bounds;
        /// <summary>Reports unavailable generated geometry without repeating exceptions every frame.</summary>
        public string Diagnostic { get; private set; }

        private void OnEnable() => _dirty = true;
        private void OnValidate() => _dirty = true;
        private bool SourceChanged => _dirty || _matrix != transform.localToWorldMatrix ||
            (_generatedSurface && (_generatedMatrix != _generatedSurface.transform.localToWorldMatrix || _generatedMesh != _generatedSurface.sharedMesh)) ||
            (!_generatedSurface && _generatedMesh);

        private void LateUpdate()
        {
            if (SourceChanged)
            {
                Refresh();
            }
            else
            {
                PublishChange();
            }
        }
        private void OnDisable()
        {
            if (_published)
            {
                Changed?.Invoke(_bounds);
            }
        }

        /// <summary>Set a local-space corridor. Radius and height controls use world meters.</summary>
        public void Configure(Vector3[] points, float radius, float falloff = 1, float below = 1, float above = 3)
        {
            if (points == null || points.Length < 2 || !Valid(radius) || !Valid(falloff) || !Valid(below) || !Valid(above))
            {
                throw new ArgumentException("Supply at least two points and finite nonnegative mask distances.");
            }
            foreach (var point in points)
            {
                if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
                {
                    throw new ArgumentException("Corridor points must be finite.");
                }
            }
            _points = (Vector3[])points.Clone();
            _generatedSurface = null;
            _radius = radius;
            _falloff = falloff;
            _below = below;
            _above = above;
            Refresh();
        }

        /// <summary>Use a public generated road or water mesh. Connect generation-finished events to Refresh.</summary>
        public void ConfigureSurface(MeshFilter surface, float below = 1, float above = 3)
        {
            if (!surface || !surface.sharedMesh || !surface.sharedMesh.isReadable || !Valid(below) || !Valid(above))
            {
                throw new ArgumentException("Assign a readable generated surface and valid height distances.");
            }
            _generatedSurface = surface;
            _points = Array.Empty<Vector3>();
            _below = below;
            _above = above;
            Refresh();
        }

        /// <summary>Recapture public geometry after a road or RAM regeneration. No terrain or source mesh is modified.</summary>
        public void Refresh() => Capture(true);

        private void Capture(bool notify)
        {
            Bounds old = _bounds;
            bool hadOld = _published;
            _matrix = transform.localToWorldMatrix;
            _surface = null;
            _generatedMesh = _generatedSurface ? _generatedSurface.sharedMesh : null;
            _generatedMatrix = _generatedSurface ? _generatedSurface.transform.localToWorldMatrix : Matrix4x4.identity;
            Diagnostic = null;
            if (_generatedMesh && !_generatedMesh.isReadable)
            {
                Diagnostic = "The generated mask mesh must enable Read/Write. The last captured bounds remain excluded.";
                _dirty = false;
                return;
            }
            if (_generatedSurface && _generatedSurface.sharedMesh)
            {
                _surface = new MeshScatterSurface(_generatedSurface.sharedMesh, _generatedSurface.transform.localToWorldMatrix);
                _bounds = _surface.TriangleCount > 0 ? _surface.GetBounds(0) : new Bounds(transform.position, Vector3.zero);
                for (int i = 1; i < _surface.TriangleCount; i++)
                {
                    _bounds.Encapsulate(_surface.GetBounds(i));
                }
            }
            else
            {
                _bounds = new Bounds(_points.Length == 0 ? transform.position : transform.TransformPoint(_points[0]), Vector3.zero);
                foreach (var point in _points)
                {
                    _bounds.Encapsulate(transform.TransformPoint(point));
                }
                _bounds.Expand(new Vector3((_radius + _falloff) * 2, 0, (_radius + _falloff) * 2));
            }
            _bounds.SetMinMax(_bounds.min - Vector3.up * _below, _bounds.max + Vector3.up * _above);
            _dirty = false;
            _published = true;
            var changed = _bounds;
            if (hadOld)
            {
                changed.Encapsulate(old);
            }
            if (_pendingNotification.HasValue)
            {
                changed.Encapsulate(_pendingNotification.Value);
            }
            _pendingNotification = changed;
            if (notify)
            {
                PublishChange();
            }
        }

        private void PublishChange()
        {
            if (!_pendingNotification.HasValue) return;
            Bounds change = _pendingNotification.Value;
            _pendingNotification = null;
            Changed?.Invoke(change);
        }

        /// <summary>Remove this mask after its generated source is unloaded. Source geometry remains unchanged.</summary>
        public void Clear()
        {
            _points = Array.Empty<Vector3>();
            _generatedSurface = null;
            Refresh();
        }

        /// <summary>Return allowed placement weight, where one keeps the placement and zero removes it.</summary>
        public float Sample(Vector3 worldPosition)
        {
            if (!isActiveAndEnabled) return 1;
            if (SourceChanged) Capture(false);
            if (!_bounds.Contains(worldPosition)) return 1;
            if (Diagnostic != null) return 0;
            if (_surface != null)
            {
                var ray = new Ray(new Vector3(worldPosition.x, _bounds.max.y + 1, worldPosition.z), Vector3.down);
                if (!_surface.Raycast(ray, out var point, out _)) return 1;
                return worldPosition.y >= point.y - _below && worldPosition.y <= point.y + _above ? 0 : 1;
            }
            float allowed = 1;
            for (int i = 1; i < _points.Length; i++)
            {
                Vector3 a = transform.TransformPoint(_points[i - 1]);
                Vector3 b = transform.TransformPoint(_points[i]);
                Vector2 delta = new Vector2(b.x - a.x, b.z - a.z);
                Vector2 offset = new Vector2(worldPosition.x - a.x, worldPosition.z - a.z);
                float t = delta.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(offset, delta) / delta.sqrMagnitude) : 0;
                float height = Mathf.Lerp(a.y, b.y, t);
                if (worldPosition.y < height - _below || worldPosition.y > height + _above)
                {
                    continue;
                }
                float distance = (offset - delta * t).magnitude;
                allowed = Mathf.Min(allowed, _falloff > 0 ? Mathf.SmoothStep(0, 1, (distance - _radius) / _falloff) : distance > _radius ? 1 : 0);
            }
            return allowed;
        }

        private static bool Valid(float value) => float.IsFinite(value) && value >= 0;
    }
}

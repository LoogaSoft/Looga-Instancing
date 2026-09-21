using System;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Instancing.Splines
{
    /// <summary>Converts a public Unity spline into a local placement exclusion corridor.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class SplinePlacementBridge : MonoBehaviour
    {
        [SerializeField] private SplineContainer _source;
        [SerializeField] private PlacementExclusion _mask;
        [SerializeField, Min(0)] private int _splineIndex;
        [SerializeField, Min(0)] private float _radius = 3;
        [SerializeField, Min(0)] private float _falloff = 1;
        [SerializeField, Min(0)] private float _below = 1;
        [SerializeField, Min(0)] private float _above = 3;
        [SerializeField, Min(0.1f)] private float _spacing = 1;
        private bool _dirty = true;
        private Matrix4x4 _sourceMatrix;
        private Matrix4x4 _maskMatrix;
        private bool _hadSource;
        private Spline _capturedSpline;
        /// <summary>Last unavailable or invalid source diagnostic.</summary>
        public string Diagnostic { get; private set; }

        private void OnEnable()
        {
            Spline.Changed += Changed;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed += RequestRefresh;
#endif
            _dirty = true;
        }
        private void OnDisable()
        {
            Spline.Changed -= Changed;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed -= RequestRefresh;
#endif
            if (_mask)
            {
                _mask.Clear();
            }
        }
        private void OnValidate() => _dirty = true;
        private void LateUpdate() => ProcessChanges();
        private void Changed(Spline spline, int knot, SplineModification change)
        {
            if (!_source || _splineIndex >= _source.Splines.Count) return;
            if (ReferenceEquals(spline, _source.Splines[_splineIndex]))
            {
                _dirty = true;
            }
        }
        /// <summary>Queue a public source edit or replacement for the next update.</summary>
        public void RequestRefresh() => _dirty = true;

        /// <summary>Assign a spline, dedicated mask and world-space corridor dimensions.</summary>
        public void Configure(SplineContainer source, PlacementExclusion mask, float radius,
            float falloff = 1, float below = 1, float above = 3, int splineIndex = 0)
        {
            if (!source || !mask || splineIndex < 0 || !Valid(radius) || !Valid(falloff) || !Valid(below) || !Valid(above))
            {
                throw new ArgumentException("Assign a spline, mask and finite nonnegative corridor dimensions.");
            }
            if (_mask && _mask != mask)
            {
                _mask.Clear();
            }
            _source = source;
            _mask = mask;
            _radius = radius;
            _falloff = falloff;
            _below = below;
            _above = above;
            _splineIndex = splineIndex;
            _dirty = true;
            ProcessChanges();
        }

        /// <summary>Refresh the corridor after spline edits, transforms, Undo or source loss.</summary>
        public void ProcessChanges()
        {
            if (!isActiveAndEnabled) return;
            Spline selected = _source && _splineIndex < _source.Splines.Count ? _source.Splines[_splineIndex] : null;
            if (!_dirty && ReferenceEquals(selected, _capturedSpline) && !(_hadSource && !_source) && _source && _mask &&
                _sourceMatrix == _source.transform.localToWorldMatrix && _maskMatrix == _mask.transform.localToWorldMatrix) return;
            _dirty = false;
            _hadSource = _source;
            _capturedSpline = selected;
            if (!_source || !_mask || _splineIndex >= _source.Splines.Count || _source.Splines[_splineIndex].Count < 2)
            {
                if (_mask)
                {
                    _mask.Clear();
                }
                Diagnostic = "The placement corridor needs a loaded spline with at least two knots.";
                return;
            }
            float length = _source.CalculateLength(_splineIndex);
            if (!float.IsFinite(length) || !float.IsFinite(_spacing) || _spacing <= 0)
            {
                _mask.Clear();
                Diagnostic = "The spline length and sampling distance must be finite.";
                return;
            }
            int segments = Mathf.Clamp(Mathf.CeilToInt(length / _spacing), 1, 4095);
            var points = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                points[i] = _mask.transform.InverseTransformPoint((Vector3)_source.EvaluatePosition(_splineIndex, i / (float)segments));
            }
            _mask.Configure(points, _radius, _falloff, _below, _above);
            _sourceMatrix = _source.transform.localToWorldMatrix;
            _maskMatrix = _mask.transform.localToWorldMatrix;
            Diagnostic = null;
        }
        private static bool Valid(float value) => float.IsFinite(value) && value >= 0;
    }
}

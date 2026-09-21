using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>A gameplay residency region. It does not change camera culling or create collider proxies.</summary>
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "VegetationInterest")]
    public sealed class VegetationInterest : MonoBehaviour
    {
        [SerializeField, Min(0)] private float _radius = 64;
        [SerializeField] private bool _useExplicitVelocity;
        [SerializeField] private Vector3 _explicitVelocity;
        private Vector3 _lastPosition;
        private Vector3 _measuredVelocity;
        private bool _hasPosition;
        /// <summary>Required placement radius in meters.</summary>
        public float Radius
        {
            get => _radius;
            set
            {
                if (!float.IsFinite(value) || value < 0) throw new System.ArgumentOutOfRangeException(nameof(value));
                _radius = value;
            }
        }

        /// <summary>Current explicit or measured world-space velocity.</summary>
        public Vector3 Velocity => _useExplicitVelocity ? _explicitVelocity : _measuredVelocity;

        /// <summary>Use a game-owned velocity. Disable this mode to measure Transform motion.</summary>
        public void SetVelocity(Vector3 velocity, bool explicitVelocity = true)
        {
            if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
                throw new System.ArgumentException("Velocity must be finite.", nameof(velocity));
            _explicitVelocity = velocity;
            _useExplicitVelocity = explicitVelocity;
        }

        public Vector3 PredictPosition(float seconds) => transform.position + Velocity * Mathf.Max(0, seconds);

        private void OnEnable() { _lastPosition = transform.position; _hasPosition = true; _measuredVelocity = Vector3.zero; }
        private void LateUpdate()
        {
            Vector3 current = transform.position;
            float delta = Time.unscaledDeltaTime;
            if (_hasPosition && delta > 0.00001f) _measuredVelocity = (current - _lastPosition) / delta;
            _lastPosition = current;
            _hasPosition = true;
        }
    }
}

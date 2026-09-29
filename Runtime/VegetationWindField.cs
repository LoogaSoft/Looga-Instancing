using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>One shared wind field for Looga vegetation. Material amplitudes remain the displacement limits.</summary>
    [ExecuteAlways]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "VegetationWindField")]
    public sealed class VegetationWindField : MonoBehaviour
    {
        /// <summary>A bounded local direction override. The transform supplies its position and forward direction.</summary>
        [Serializable]
        public struct Volume
        {
            public Transform Transform;
            [Min(0.01f)] public float Radius;
            [Range(0, 1)] public float Influence;
        }

        [SerializeField] private Vector3 _direction = Vector3.right;
        [SerializeField, Range(0, 1)] private float _strength = 1;
        [SerializeField, Range(0, 1)] private float _gustAmplitude = 0.25f;
        [SerializeField, Min(0)] private float _gustFrequency = 0.5f;
        [SerializeField, Min(1), Tooltip("Distance between gust fronts in meters. The fronts travel downwind at " +
            "Gust Frequency x Gust Wavelength / 2 pi meters per second.")]
        private float _gustWavelength = 24;
        [SerializeField, Range(0, 1), Tooltip("Part of the trunk amplitude that bends steadily downwind. The rest sways.")]
        private float _lean = 0.5f;
        [SerializeField] private Volume[] _volumes = Array.Empty<Volume>();
        private static VegetationWindField _owner;
        private static bool _subscribed;
        private static int _frame = -1;
        private static Vector4 _currentDirection;
        private static Vector4 _currentGust;
        private static Vector4 _currentWave;
        private static Vector4 _previousDirection;
        private static Vector4 _previousGust;
        private static Vector4 _previousWave;
        private static int _count;
        private static int _previousCount;
        private static readonly Vector4[] _positions = new Vector4[8];
        private static readonly Vector4[] _directions = new Vector4[8];
        private static readonly Vector4[] _oldPositions = new Vector4[8];
        private static readonly Vector4[] _oldDirections = new Vector4[8];

        private void OnEnable()
        {
            if (_owner && _owner != this)
            {
                Debug.LogError("Only one Looga vegetation wind field can be active.", this);
                return;
            }
            _owner = this;
            if (!_subscribed)
            {
                RenderPipelineManager.beginCameraRendering += BeforeCamera;
                _subscribed = true;
            }
            Publish();
            CopyHistory();
            Upload();
        }

        private void OnDisable()
        {
            if (_owner != this) return;
            _owner = null;
            Publish();
        }

        private void LateUpdate()
        {
            if (!_owner)
            {
                OnEnable();
            }
            if (_owner == this)
            {
                Publish();
            }
        }

        /// <summary>Set world direction and bounded gust controls. This does not change source materials.</summary>
        public void Configure(Vector3 direction, float strength, float gustAmplitude, float gustFrequency, Volume[] volumes = null)
        {
            if (!Finite(direction) || !float.IsFinite(strength) || !float.IsFinite(gustAmplitude) || !float.IsFinite(gustFrequency))
            {
                throw new ArgumentException("Wind parameters must be finite.");
            }
            if (volumes != null && volumes.Length > 8)
            {
                throw new ArgumentException("A wind field supports at most eight local volumes.");
            }
            if (volumes != null)
            {
                foreach (var volume in volumes)
                {
                    if (!float.IsFinite(volume.Radius) || volume.Radius <= 0 || !float.IsFinite(volume.Influence))
                    {
                        throw new ArgumentException("Wind volume radius and influence must be finite.");
                    }
                }
            }
            _direction = direction;
            _strength = Mathf.Clamp01(strength);
            _gustAmplitude = Mathf.Clamp01(gustAmplitude);
            _gustFrequency = Mathf.Max(0, gustFrequency);
            _volumes = volumes == null ? Array.Empty<Volume>() : (Volume[])volumes.Clone();
            if (_owner == this)
            {
                Publish();
            }
        }

        /// <summary>Set the distance between gust fronts in meters and the steady downwind part of the trunk bend.</summary>
        public void ConfigureWaves(float gustWavelength, float lean)
        {
            if (!float.IsFinite(gustWavelength) || !float.IsFinite(lean))
            {
                throw new ArgumentException("Wave parameters must be finite.");
            }
            _gustWavelength = Mathf.Max(1, gustWavelength);
            _lean = Mathf.Clamp01(lean);
            if (_owner == this)
            {
                Publish();
            }
        }

        private static void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            Publish();
            if (!_owner && _previousDirection.w == 0)
            {
                RenderPipelineManager.beginCameraRendering -= BeforeCamera;
                _subscribed = false;
            }
        }

        private static void CopyHistory()
        {
            _previousDirection = _currentDirection;
            _previousGust = _currentGust;
            _previousWave = _currentWave;
            _previousCount = _count;
            Array.Copy(_positions, _oldPositions, 8);
            Array.Copy(_directions, _oldDirections, 8);
        }

        private static void Publish()
        {
            if (_frame != Time.frameCount)
            {
                CopyHistory();
                _frame = Time.frameCount;
            }
            _count = 0;
            _currentDirection = Vector4.zero;
            _currentGust = Vector4.zero;
            _currentWave = Vector4.zero;
            if (_owner)
            {
                var direction = Finite(_owner._direction) ? _owner._direction.normalized : Vector3.zero;
                _currentDirection = new Vector4(direction.x, direction.y, direction.z, 1);
                _currentGust = new Vector4(SafeUnit(_owner._strength), SafeUnit(_owner._gustAmplitude),
                    float.IsFinite(_owner._gustFrequency) ? Mathf.Max(0, _owner._gustFrequency) : 0, 0);
                _currentWave = new Vector4(float.IsFinite(_owner._gustWavelength) ? Mathf.Max(1, _owner._gustWavelength) : 24,
                    SafeUnit(_owner._lean), 0, 0);
                foreach (var volume in _owner._volumes ?? Array.Empty<Volume>())
                {
                    if (_count == 8)
                    {
                        break;
                    }
                    if (!volume.Transform || !float.IsFinite(volume.Radius) || volume.Radius <= 0)
                    {
                        continue;
                    }
                    Vector3 position = volume.Transform.position;
                    Vector3 forward = volume.Transform.forward;
                    if (!Finite(position) || !Finite(forward))
                    {
                        continue;
                    }
                    _positions[_count] = new Vector4(position.x, position.y, position.z, volume.Radius);
                    _directions[_count++] = new Vector4(forward.x, forward.y, forward.z, SafeUnit(volume.Influence));
                }
            }
            Upload();
        }

        private static void Upload()
        {
            Shader.SetGlobalVector("_LoogaFieldDirection", _currentDirection);
            Shader.SetGlobalVector("_LoogaFieldGust", _currentGust);
            Shader.SetGlobalVector("_LoogaFieldPreviousDirection", _previousDirection);
            Shader.SetGlobalVector("_LoogaFieldPreviousGust", _previousGust);
            Shader.SetGlobalVector("_LoogaFieldWave", _currentWave);
            Shader.SetGlobalVector("_LoogaFieldPreviousWave", _previousWave);
            Shader.SetGlobalInteger("_LoogaFieldVolumeCount", _count);
            Shader.SetGlobalInteger("_LoogaFieldPreviousVolumeCount", _previousCount);
            Shader.SetGlobalVectorArray("_LoogaFieldPositions", _positions);
            Shader.SetGlobalVectorArray("_LoogaFieldDirections", _directions);
            Shader.SetGlobalVectorArray("_LoogaFieldPreviousPositions", _oldPositions);
            Shader.SetGlobalVectorArray("_LoogaFieldPreviousDirections", _oldDirections);
        }

        private static float SafeUnit(float value) => float.IsFinite(value) ? Mathf.Clamp01(value) : 0;
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            _owner = null;
            _subscribed = false;
            _frame = -1;
            _count = _previousCount = 0;
            _currentDirection = _previousDirection = _currentGust = _previousGust = _currentWave = _previousWave = Vector4.zero;
            Upload();
        }
    }
}

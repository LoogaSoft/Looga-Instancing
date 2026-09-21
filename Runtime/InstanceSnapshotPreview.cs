using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Switches a static placement snapshot and its original renderers for reversible migration review.</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(InstanceContainer))]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceSnapshotPreview")]
    public sealed class InstanceSnapshotPreview : MonoBehaviour
    {
        [SerializeField] private GameObject[] _sources = Array.Empty<GameObject>();
        [SerializeField] private bool _useSnapshot;
        private InstanceContainer _container;
        private readonly List<MeshRenderer> _owned = new();
        private bool _applied;

        /// <summary>True after the snapshot acquires submission and suppresses its original renderers.</summary>
        public bool IsSnapshotActive => _applied;
        /// <summary>Last activation failure. Original renderers remain available after failure.</summary>
        public string Diagnostic { get; private set; }

        private void OnEnable()
        {
            _container = GetComponent<InstanceContainer>();
            if (_useSnapshot)
            {
                TryApply();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.update -= ResumeAfterSceneLoad;
                UnityEditor.EditorApplication.update += ResumeAfterSceneLoad;
#endif
            }
            else if (_container)
            {
                _container.enabled = false;
            }
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= ResumeAfterSceneLoad;
#endif
            Restore();
        }

#if UNITY_EDITOR
        private void ResumeAfterSceneLoad()
        {
            if (!this)
            {
                UnityEditor.EditorApplication.update -= ResumeAfterSceneLoad;
                return;
            }

            if (isActiveAndEnabled && _useSnapshot && !_applied && Diagnostic == null)
            {
                TryApply();
            }
            if (!isActiveAndEnabled || !_useSnapshot || _applied || Diagnostic != null)
            {
                UnityEditor.EditorApplication.update -= ResumeAfterSceneLoad;
            }
        }
#endif

        private void LateUpdate()
        {
            if (_applied && (!_container || !_container.isActiveAndEnabled || _container.Renderer == null))
            {
                Diagnostic = "The snapshot renderer is unavailable. Original rendering was restored.";
                Restore();
            }
            else if (_useSnapshot != _applied && Diagnostic == null)
            {
                if (_useSnapshot)
                {
                    TryApply();
                }
                else
                {
                    Restore();
                }
            }
        }

        private void OnValidate() => Diagnostic = null;

        /// <summary>Assign original roots for a static snapshot. Source scripts, colliders and active states remain unchanged.</summary>
        public void Configure(GameObject[] sources)
        {
            if (sources == null || sources.Length == 0)
            {
                throw new ArgumentException("Assign the original roots for this snapshot.");
            }
            var unique = new HashSet<GameObject>();
            foreach (var root in sources)
            {
                if (!root || !root.scene.IsValid() || root.scene != gameObject.scene || !unique.Add(root) ||
                    transform.IsChildOf(root.transform) || root.transform.IsChildOf(transform))
                {
                    throw new ArgumentException("Assign distinct source roots in the same scene, outside the snapshot hierarchy.");
                }
                foreach (var other in sources)
                {
                    if (other && other != root && root.transform.IsChildOf(other.transform))
                    {
                        throw new ArgumentException("Snapshot roots must not overlap.");
                    }
                }
            }
            Restore();
            _sources = (GameObject[])sources.Clone();
            _useSnapshot = false;
            Diagnostic = null;
            _container = GetComponent<InstanceContainer>();
            _container.enabled = false;
        }

        /// <summary>Activate a reviewed static snapshot, or restore its originals. This method does not delete source objects.</summary>
        public bool SetSnapshotActive(bool active)
        {
            Diagnostic = null;
            _useSnapshot = active;
            if (!active)
            {
                Restore();
                return true;
            }
            if (!isActiveAndEnabled) return false;
            return _applied || TryApply();
        }

        private bool TryApply()
        {
            try
            {
                if (!_container || _sources.Length == 0)
                {
                    throw new InvalidOperationException("Configure the snapshot and its original sources before activation.");
                }
                var candidates = new List<MeshRenderer>();
                foreach (var root in _sources)
                {
                    if (!root || !root.activeInHierarchy)
                    {
                        throw new InvalidOperationException("Every original root must be loaded and active for migration review.");
                    }
                    foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (renderer.forceRenderingOff)
                        {
                            throw new InvalidOperationException("Another renderer owns a snapshot source. Disable that owner before review.");
                        }
                        if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                        {
                            candidates.Add(renderer);
                        }
                    }
                }
                _container.enabled = true;
                // Scene deserialization can enable this component before the container becomes active.
                if (!_container.isActiveAndEnabled) return false;
                _container.Synchronize();
                if (_container.Renderer == null || _container.Diagnostic != null)
                {
                    throw new InvalidOperationException(_container.Diagnostic ?? "The snapshot renderer could not start.");
                }
                foreach (var renderer in candidates)
                {
                    _owned.Add(renderer);
                    renderer.forceRenderingOff = true;
                }
                _applied = true;
                return true;
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                Restore();
                return false;
            }
        }

        private void Restore()
        {
            if (_container)
            {
                _container.enabled = false;
            }
            foreach (var renderer in _owned)
            {
                if (renderer)
                {
                    renderer.forceRenderingOff = false;
                }
            }
            _owned.Clear();
            _applied = false;
        }
    }
}

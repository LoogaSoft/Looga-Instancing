using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>An atomic runtime placement snapshot. The adapter copies placement data before applying it.</summary>
    public readonly struct RuntimeInstanceSnapshot
    {
        public GameObject Prototype { get; }
        public InstanceContainer.Placement[] Placements { get; }
        public float MaxDistance { get; }

        public RuntimeInstanceSnapshot(GameObject prototype, InstanceContainer.Placement[] placements, float maxDistance = 1000)
        {
            Prototype = prototype;
            Placements = placements;
            MaxDistance = maxDistance;
        }
    }

    /// <summary>Runtime systems implement this without inheriting a Looga component or changing their data model.</summary>
    public interface IRuntimeInstanceSource
    {
        event Action Changed;
        /// <summary>Return one complete revision. False leaves the last complete Looga revision active.</summary>
        bool TryCapture(out RuntimeInstanceSnapshot snapshot, out string diagnostic);
    }

    /// <summary>Copies an application-owned runtime source into a dedicated InstanceContainer transactionally.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class RuntimeInstanceSourceAdapter : MonoBehaviour, IInstanceSourceAdapter
    {
        [SerializeField] private InstanceContainer _target;
        private IRuntimeInstanceSource _source;
        private bool _dirty;
        private int _capturedSources;

        public string Diagnostic { get; private set; }
        public InstanceSourceAdapterStatus SourceStatus => new(_source == null ? 0 : 1,
            _capturedSources, 0, _dirty, Diagnostic);

        /// <summary>Bind a runtime source and a dedicated target. Existing target data changes only after a valid capture.</summary>
        public void Configure(IRuntimeInstanceSource source, InstanceContainer target)
        {
            Unsubscribe();
            if (_target && _target != target)
            {
                _target.Configure(null, null);
            }
            _source = source;
            _target = target;
            if (_target) _target.ConfigureWorldCells(InstanceWorldContentKind.RuntimeObject);
            Subscribe();
            _dirty = true;
            if (isActiveAndEnabled)
            {
                ProcessChanges();
            }
        }

        private void OnEnable()
        {
            Subscribe();
            _dirty = true;
            ProcessChanges();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ClearTarget();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            ClearTarget();
        }

        private void LateUpdate() => ProcessChanges();

        /// <summary>Apply a complete pending source revision. Invalid revisions keep the previous complete draw population.</summary>
        public bool ProcessChanges()
        {
            if (!isActiveAndEnabled || !_dirty || _source == null || !_target) return false;
            _dirty = false;
            try
            {
                if (!_source.TryCapture(out RuntimeInstanceSnapshot snapshot, out string diagnostic))
                {
                    Diagnostic = string.IsNullOrEmpty(diagnostic) ? "The runtime source did not provide a complete revision." : diagnostic;
                    return false;
                }
                var placements = snapshot.Placements == null ? Array.Empty<InstanceContainer.Placement>() :
                    (InstanceContainer.Placement[])snapshot.Placements.Clone();
                _target.Configure(snapshot.Prototype, placements, snapshot.MaxDistance);
                _capturedSources = 1;
                Diagnostic = null;
                return true;
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                return false;
            }
        }

        private void Changed() => _dirty = true;

        private void Subscribe()
        {
            if (_source != null)
            {
                _source.Changed -= Changed;
                _source.Changed += Changed;
            }
        }

        private void Unsubscribe()
        {
            if (_source != null)
            {
                _source.Changed -= Changed;
            }
        }

        private void ClearTarget()
        {
            if (_target)
            {
                _target.Configure(null, null);
            }
            _capturedSources = 0;
            _dirty = _source != null;
        }
    }
}

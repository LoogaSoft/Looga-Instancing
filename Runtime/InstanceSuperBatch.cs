using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>One BRG owner shared by compatible logical sources.</summary>
    public sealed class InstanceSuperBatch : IDisposable
    {
        private readonly InstanceRenderer _renderer;
        private readonly HashSet<InstanceSuperBatchSource> _sources = new();
        private bool _disposed;

        public InstanceRenderer Renderer => _renderer;
        public int SourceCount => _sources.Count;
        public int InstanceCount => _renderer.InstanceCount;

        public InstanceSuperBatch(int layer = 0,
            InstanceWorldContentKind contentKind = InstanceWorldContentKind.Generic,
            string worldSourceId = null)
        {
            _renderer = new InstanceRenderer(layer: layer,
                worldSourceId: string.IsNullOrWhiteSpace(worldSourceId) ? "super-batch" : worldSourceId,
                worldContentKind: contentKind);
        }

        /// <summary>Add a logical source. Compatible prototypes use one population and draw submission.</summary>
        public InstanceSuperBatchSource AddSource(string sourceId)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InstanceSuperBatch));
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A stable source ID is required.", nameof(sourceId));
            var source = new InstanceSuperBatchSource(this, sourceId.Trim());
            _sources.Add(source);
            return source;
        }

        internal int Register(InstancePrototype prototype) => _renderer.Register(prototype);
        internal InstanceHandle Add(int prototype, Matrix4x4 transform, InstanceAppearance appearance) =>
            _renderer.Add(prototype, transform, appearance);
        internal bool Remove(InstanceHandle handle) => _renderer.Remove(handle);
        internal void Release(InstanceSuperBatchSource source) => _sources.Remove(source);

        public void Flush() => _renderer.Flush();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var source in new List<InstanceSuperBatchSource>(_sources)) source.Dispose();
            _renderer.Dispose();
            _sources.Clear();
        }
    }

    /// <summary>Logical ownership inside one compatible super-batch.</summary>
    public sealed class InstanceSuperBatchSource : IDisposable
    {
        private readonly InstanceSuperBatch _owner;
        private readonly List<InstanceHandle> _handles = new();
        private bool _disposed;

        public string SourceId { get; }
        public int InstanceCount => _handles.Count;

        internal InstanceSuperBatchSource(InstanceSuperBatch owner, string sourceId)
        {
            _owner = owner;
            SourceId = sourceId;
        }

        public int Register(InstancePrototype prototype)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InstanceSuperBatchSource));
            return _owner.Register(prototype);
        }

        public InstanceHandle Add(int prototype, Matrix4x4 transform)
        {
            return Add(prototype, transform, InstanceAppearance.Default);
        }

        public InstanceHandle Add(int prototype, Matrix4x4 transform,
            InstanceAppearance appearance)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InstanceSuperBatchSource));
            InstanceHandle handle = _owner.Add(prototype, transform, appearance);
            _handles.Add(handle);
            return handle;
        }

        public bool Remove(InstanceHandle handle)
        {
            if (_disposed || !_owner.Remove(handle)) return false;
            _handles.Remove(handle);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (InstanceHandle handle in _handles) _owner.Remove(handle);
            _handles.Clear();
            _owner.Release(this);
        }
    }
}

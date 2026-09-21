using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>An optional asset-loading boundary. Addressables adapters can implement this contract.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "IVegetationAssetSource")]
    public interface IVegetationAssetSource
    {
        /// <summary>Acquire a prefab on the Unity main thread. Each successful acquisition owns one lease.</summary>
        Task<VegetationAssetLease> AcquireAsync(string key, CancellationToken cancellation);
    }

    /// <summary>Keep an asset and its dependencies resident until the renderer has released them.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "VegetationAssetLease")]
    public sealed class VegetationAssetLease : IDisposable
    {
        private Action _release;
        /// <summary>The source prefab. Do not change it.</summary>
        public GameObject Prefab { get; }
        /// <summary>Create a lease. Release must run on the Unity main thread and release one reference.</summary>
        public VegetationAssetLease(GameObject prefab, Action release)
        {
            if (!prefab || release == null) throw new ArgumentException("A prefab and release callback are required.");
            Prefab = prefab;
            _release = release;
        }
        public void Dispose()
        {
            var release = _release;
            _release = null;
            release?.Invoke();
        }
    }

    /// <summary>Reference-counted asynchronous AssetBundle loading without an Addressables dependency.</summary>
    /// <remarks>The caller keeps dependency bundles resident. Cancellation waits for Unity's non-cancellable IO
    /// before releasing its reservation. Dispose rejects new loads but does not invalidate existing leases.</remarks>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "AssetBundleVegetationSource")]
    public sealed class AssetBundleVegetationSource : IVegetationAssetSource, IDisposable
    {
        private readonly string _path;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private Task<AssetBundle> _loading;
        private AssetBundle _bundle;
        private int _references;
        private bool _disposed;
        /// <summary>Number of pending acquisitions and live leases.</summary>
        public int References => _references;
        /// <summary>Create a lazy source for a bundle built for the current player platform.</summary>
        public AssetBundleVegetationSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A bundle path is required.");
            _path = System.IO.Path.GetFullPath(path);
        }
        public async Task<VegetationAssetLease> AcquireAsync(string key, CancellationToken cancellation)
        {
            CheckThread();
            if (_disposed) throw new ObjectDisposedException(nameof(AssetBundleVegetationSource));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An asset name is required.");
            cancellation.ThrowIfCancellationRequested();
            _references++;
            bool transferred = false;
            try
            {
                _loading ??= LoadBundle();
                _bundle = await _loading;
                cancellation.ThrowIfCancellationRequested();
                var request = _bundle.LoadAssetAsync<GameObject>(key);
                var completion = new TaskCompletionSource<GameObject>();
                request.completed += _ => completion.TrySetResult(request.asset as GameObject);
                if (request.isDone) completion.TrySetResult(request.asset as GameObject);
                var prefab = await completion.Task;
                cancellation.ThrowIfCancellationRequested();
                if (!prefab) throw new InvalidOperationException("The vegetation bundle has no prefab named " + key);
                var lease = new VegetationAssetLease(prefab, Release);
                transferred = true;
                return lease;
            }
            finally
            {
                if (!transferred)
                {
                    Release();
                }
            }
        }
        private async Task<AssetBundle> LoadBundle()
        {
            var request = AssetBundle.LoadFromFileAsync(_path);
            var completion = new TaskCompletionSource<AssetBundle>();
            request.completed += _ => completion.TrySetResult(request.assetBundle);
            if (request.isDone) completion.TrySetResult(request.assetBundle);
            var bundle = await completion.Task;
            if (!bundle) throw new InvalidOperationException("Cannot load vegetation bundle " + _path);
            return bundle;
        }
        private void Release()
        {
            CheckThread();
            if (--_references != 0) return;
            if (_bundle)
            {
                _bundle.Unload(true);
            }
            _bundle = null;
            _loading = null;
        }
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Asset leases must be used on their Unity main thread.");
        }
        public void Dispose()
        {
            CheckThread();
            _disposed = true;
        }
    }
}

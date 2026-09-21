using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Stable identity within one renderer lifetime. Buffer growth does not change this handle.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceHandle")]
    public readonly struct InstanceHandle
    {
        internal readonly int Owner;
        internal readonly int Prototype;
        internal readonly int Slot;
        internal readonly uint Generation;

        internal InstanceHandle(int owner, int prototype, int slot, uint generation)
        {
            Owner = owner;
            Prototype = prototype;
            Slot = slot;
            Generation = generation;
        }
    }

    /// <summary>Explicit BRG owner for static instance populations. Call mutation methods on the main thread.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceRenderer")]
    public sealed class InstanceRenderer : IDisposable
    {
        private static int _nextOwner;
        private readonly int _owner = Interlocked.Increment(ref _nextOwner);
        private readonly BatchRendererGroup _group;
        private readonly ComputeShader _shader;
        private readonly List<Population> _populations = new List<Population>();
        private readonly Dictionary<InstancePrototypeId, int> _registry = new Dictionary<InstancePrototypeId, int>();
        private readonly List<InstancePrototypeRegistration> _prototypeRegistrations = new();
        private readonly Vector4[] _planes = new Vector4[64];
        private readonly Vector4[] _splits = new Vector4[16];
        private readonly Matrix4x4[] _bakedOccluders = new Matrix4x4[BakedInstanceOcclusion.MaximumBoxes];
        private readonly int _reset;
        private readonly int _resetAll;
        private readonly int _resetOcclusionStats;
        private readonly int _cull;
        private readonly int _cullParts;
        private readonly int _selectVisibility;
        private readonly int _selectVisibilityOcclusion;
        private readonly int _updateLod;
        private readonly int _updateMeshLod;
        private readonly int _patchRaw;
        private readonly GraphicsBuffer _disabledHistory;
        private readonly int _layer;
        private readonly string _worldSourceId;
        private readonly InstanceWorldContentKind _worldContentKind;
        private bool _worldCellsDirty;
        private InstanceWorldHierarchy _worldHierarchy;
        private InstanceHierarchySettings _hierarchySettings = InstanceHierarchySettings.Default;
        private bool _disposed;
        /// <summary>Suspend GPU drawing while a streaming owner uses native fallback.</summary>
        public bool RenderingEnabled { get; set; } = true;
        /// <summary>Visibility path. Direct remains the default until workload measurements select another policy.</summary>
        public InstanceVisibilityMode VisibilityMode { get; set; }

        /// <summary>Choose direct, shared, or hierarchical selection from the visible source cost.</summary>
        public bool AdaptiveHierarchy { get; set; } = true;

        /// <summary>Last coarse visibility decision. Use this for diagnostics only.</summary>
        public InstanceHierarchyDecision LastHierarchyDecision { get; private set; }

        private int _viewCursor;
        private bool _boundsDirty;
        private Bounds _worldBounds;
        private ScriptableRenderContext _renderContext;
        private bool _hasRenderContext;
        private readonly Stack<ScriptableRenderContext> _cameraContexts = new Stack<ScriptableRenderContext>();

        /// <summary>Maximum center distance, expanded by each instance bound. Infinite distance disables this limit.</summary>
        public float MaxDistance { get; set; } = 1000;

        /// <summary>Shadow distance in meters. The camera distance remains an upper limit.</summary>
        public float ShadowDistance { get; set; } = float.PositiveInfinity;

        /// <summary>Minimum LOD index for shadows. Zero keeps camera LOD selection.</summary>
        public int MinimumShadowLod { get; set; }

        /// <summary>Current live source instance count, before camera and shadow culling.</summary>
        public int InstanceCount { get; private set; }

        /// <summary>Canonical prototypes registered by this renderer.</summary>
        public int RegisteredPrototypeCount => _prototypeRegistrations.Count;

        /// <summary>Spatial-registry identity for this renderer lifetime.</summary>
        public string WorldSourceId => _worldSourceId;

        /// <summary>Change hierarchy thresholds. The current source layout is rebuilt after its next upload.</summary>
        public void ConfigureHierarchy(InstanceHierarchySettings settings)
        {
            CheckDisposed();
            settings.Validate();
            _hierarchySettings = settings;
            _worldCellsDirty = true;
        }

        /// <summary>Total bytes uploaded by source changes since construction.</summary>
        public long UploadedBytes { get; private set; }

        /// <summary>Dirty bytes written through Unity persistent mapped buffers.</summary>
        public long MappedUploadedBytes
        {
            get { long value = 0; foreach (Population population in _populations) value += population.MappedUploadedBytes; return value; }
        }

        /// <summary>Dirty bytes that used the safe SetData fallback.</summary>
        public long FallbackUploadedBytes
        {
            get { long value = 0; foreach (Population population in _populations) value += population.FallbackUploadedBytes; return value; }
        }

        /// <summary>Coalesced destination ranges written since construction.</summary>
        public long UploadRangeCount
        {
            get { long value = 0; foreach (Population population in _populations) value += population.UploadRangeCount; return value; }
        }

        /// <summary>Allocated GPU bytes, including pooled camera and shadow outputs.</summary>
        public long GpuBytes
        {
            get
            {
                long result = 0;
                foreach (Population population in _populations)
                {
                    result += population.Bytes;
                }
                return result;
            }
        }

        /// <summary>Reserve source storage and view buffers before a streamed population allocates them.</summary>
        public long ProjectedGpuBytes(int reservedViews = 8)
        {
            if (reservedViews < 1) throw new ArgumentOutOfRangeException(nameof(reservedViews));
            long total = 0;
            foreach (var population in _populations)
            {
                total += population.ProjectedBytes(reservedViews);
            }
            return total;
        }

        /// <summary>Create a renderer. No scene objects or native terrain settings are changed.</summary>
        public InstanceRenderer(ComputeShader cullingShader = null, int layer = 0, string worldSourceId = null,
            InstanceWorldContentKind worldContentKind = InstanceWorldContentKind.Generic)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                throw new NotSupportedException("The first instance renderer target is Windows Direct3D 12.");
            }
            if (layer < 0 || layer > 31)
            {
                throw new ArgumentOutOfRangeException(nameof(layer));
            }
            _layer = layer;
            string source = string.IsNullOrWhiteSpace(worldSourceId) ? "renderer" : worldSourceId.Trim();
            _worldSourceId = source + ":" + _owner;
            _worldContentKind = worldContentKind;
            _shader = cullingShader != null ? cullingShader : Resources.Load<ComputeShader>("LoogaInstanceCulling");
            if (_shader == null)
            {
                throw new InvalidOperationException("Assign the Looga instance culling compute shader.");
            }
            _reset = _shader.FindKernel("Reset");
            _resetAll = _shader.FindKernel("ResetAll");
            _resetOcclusionStats = _shader.FindKernel("ResetOcclusionStats");
            _cull = _shader.FindKernel("Cull");
            _cullParts = _shader.FindKernel("CullParts");
            _selectVisibility = _shader.FindKernel("SelectVisibility");
            _selectVisibilityOcclusion = _shader.FindKernel("SelectVisibilityOcclusion");
            _updateLod = _shader.FindKernel("UpdateLod");
            _updateMeshLod = _shader.FindKernel("UpdateMeshLod");
            _patchRaw = _shader.FindKernel("PatchRawBuffer");
            _disabledHistory = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            _group = new BatchRendererGroup(Cull, IntPtr.Zero);
            _group.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera, BatchCullingViewType.Light });
            _group.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one));
            RenderPipelineManager.endContextRendering += EndContext;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endCameraRendering += EndCamera;
            InstanceOcclusion.Add(this);
        }

        #region Sources
        /// <summary>Register immutable prototype data once. Repeated registration returns the existing index.</summary>
        public int Register(InstancePrototype prototype)
        {
            CheckDisposed();
            if (prototype == null)
            {
                throw new ArgumentNullException(nameof(prototype));
            }
            InstancePrototypeRegistration registration = InstancePrototypeRegistry.Acquire(prototype);
            if (_registry.TryGetValue(registration.Id, out int existing))
            {
                registration.Dispose();
                return existing;
            }
            int index = _populations.Count;
            Population population;
            try
            {
                population = new Population(registration.Prototype, _group, _shader, _patchRaw);
            }
            catch
            {
                registration.Dispose();
                throw;
            }
            _populations.Add(population);
            _prototypeRegistrations.Add(registration);
            _registry.Add(registration.Id, index);
            _worldCellsDirty = true;
            return index;
        }

        /// <summary>Return the global stable identity for one renderer-local prototype index.</summary>
        public InstancePrototypeId GetPrototypeId(int prototype)
        {
            CheckDisposed();
            if ((uint)prototype >= (uint)_prototypeRegistrations.Count) throw new ArgumentOutOfRangeException(nameof(prototype));
            return _prototypeRegistrations[prototype].Id;
        }

        /// <summary>Apply quality to one registered prototype without rebuilding source buffers.</summary>
        public void SetQuality(int prototype, InstanceQualitySettings quality)
        {
            CheckDisposed();
            if ((uint)prototype >= (uint)_populations.Count) throw new ArgumentOutOfRangeException(nameof(prototype));
            quality.Validate(_populations[prototype].Prototype.HasColliders);
            _populations[prototype].Quality = quality;
        }

        /// <summary>Set a persistent density key. Preserve this key when a source unloads and returns.</summary>
        public bool SetVisibilityKey(InstanceHandle handle, uint key)
        {
            if (!TryResolve(handle, out Population population)) return false;
            population.VisibilityKeys[handle.Slot] = key;
            population.Dirty.Add(handle.Slot);
            return true;
        }

        /// <summary>Show or hide a live instance without changing its handle or world registration.</summary>
        public bool SetVisible(InstanceHandle handle, bool visible)
        {
            CheckDisposed();
            if (!TryResolve(handle, out Population population)) return false;
            if (population.Visible[handle.Slot] == visible) return true;
            population.Visible[handle.Slot] = visible;
            population.Dirty.Add(handle.Slot);
            return true;
        }

        /// <summary>Read the source visibility of a live instance.</summary>
        public bool TryGetVisible(InstanceHandle handle, out bool visible)
        {
            CheckDisposed();
            visible = false;
            if (!TryResolve(handle, out Population population)) return false;
            visible = population.Visible[handle.Slot];
            return true;
        }

        /// <summary>Add an instance with an affine, invertible transform.</summary>
        public InstanceHandle Add(int prototype, Matrix4x4 transform)
        {
            CheckDisposed();
            ValidateTransform(transform);
            if (prototype < 0 || prototype >= _populations.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(prototype));
            }
            Population population = _populations[prototype];
            population.EnsureCapacity(population.Free.Count > 0 ? population.HighWater : population.HighWater + 1);
            int slot = population.Free.Count > 0 ? population.Free.Pop() : population.HighWater++;

            population.Active[slot] = true;
            population.Visible[slot] = true;
            if (population.Generations[slot] == 0)
            {
                population.Generations[slot] = 1;
            }
            population.InitializeSlot(slot, transform);
            population.Dirty.Add(slot);
            IncludeBounds(population.Prototype, transform);
            _worldCellsDirty = true;
            InstanceCount++;
            return new InstanceHandle(_owner, prototype, slot, population.Generations[slot]);
        }

        /// <summary>Add an instance with optional material data.</summary>
        public InstanceHandle Add(int prototype, Matrix4x4 transform, InstanceAppearance appearance)
        {
            CheckDisposed();
            if (prototype < 0 || prototype >= _populations.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(prototype));
            }
            _populations[prototype].ValidateAppearance(appearance);
            var handle = Add(prototype, transform);
            _populations[prototype].SetAppearance(handle.Slot, appearance);
            return handle;
        }

        /// <summary>Change material data without changing the source material or instance transform.</summary>
        public bool UpdateAppearance(InstanceHandle handle, InstanceAppearance appearance)
        {
            CheckDisposed();
            if (!TryResolve(handle, out Population population)) return false;
            population.ValidateAppearance(appearance);
            population.SetAppearance(handle.Slot, appearance);
            return true;
        }

        /// <summary>Change a live instance. Stale or foreign handles return false.</summary>
        public bool Update(InstanceHandle handle, Matrix4x4 transform)
        {
            CheckDisposed();
            if (!TryResolve(handle, out Population population)) return false;
            ValidateTransform(transform);
            population.SetTransform(handle.Slot, transform);
            population.Dirty.Add(handle.Slot);
            IncludeBounds(population.Prototype, transform);
            _worldCellsDirty = true;
            return true;
        }

        /// <summary>Remove a live instance. Reusing its slot does not reactivate the old handle.</summary>
        public bool Remove(InstanceHandle handle)
        {
            CheckDisposed();
            if (!TryResolve(handle, out Population population)) return false;
            population.Active[handle.Slot] = false;
            population.Visible[handle.Slot] = false;
            population.Generations[handle.Slot]++;
            population.Free.Push(handle.Slot);
            population.Dirty.Add(handle.Slot);
            population.MarkSpatialOrderDirty();
            _boundsDirty = true;
            _worldCellsDirty = true;
            InstanceCount--;
            return true;
        }

        /// <summary>True until all changed slots reach GPU storage.</summary>
        public bool HasPendingUploads
        {
            get
            {
                foreach (var population in _populations)
                {
                    if (population.Dirty.Count > 0) return true;
                }
                return false;
            }
        }

        /// <summary>Upload all changed slots before rendering.</summary>
        public void Flush()
        {
            FlushBudget(long.MaxValue);
        }

        /// <summary>Upload at most the specified source bytes. Suspend rendering until this method returns true.</summary>
        public bool FlushBudget(long byteBudget)
        {
            CheckDisposed();
            if (byteBudget < 64)
            {
                throw new ArgumentOutOfRangeException(nameof(byteBudget), "Allow at least the 64-byte default header.");
            }
            long remaining = byteBudget;
            foreach (Population population in _populations)
            {
                long uploaded = population.Upload(remaining);
                UploadedBytes += uploaded;
                remaining -= uploaded;
                population.PrepareSpatialOrder();
            }
            if (_boundsDirty)
            {
                _group.SetGlobalBounds(InstanceCount == 0 ? new Bounds(Vector3.zero, Vector3.one) : _worldBounds);
                _boundsDirty = false;
            }
            bool complete = !HasPendingUploads;
            if (complete && _worldCellsDirty)
            {
                RefreshWorldCells();
            }
            return complete;
        }

        /// <summary>Release empty population storage and shrink unused trailing capacity without changing live handles.</summary>
        public void TrimExcess()
        {
            CheckDisposed();
            foreach (var population in _populations)
            {
                population.TrimExcess();
            }
        }

        private void IncludeBounds(InstancePrototype prototype, Matrix4x4 transform)
        {
            Bounds bounds = InstancePrototype.TransformBounds(prototype.Bounds, transform);
            if (InstanceCount == 0)
            {
                _worldBounds = bounds;
            }
            else
            {
                _worldBounds.Encapsulate(bounds);
            }
            _boundsDirty = true;
        }

        /// <summary>Release all batches and buffers. Repeated disposal is safe.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            InstanceOcclusion.Remove(this);
            RenderPipelineManager.endContextRendering -= EndContext;
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.endCameraRendering -= EndCamera;
            foreach (Population population in _populations)
            {
                population.Dispose();
            }
            InstanceWorldCells.RemoveSource(_worldSourceId);
            foreach (InstancePrototypeRegistration registration in _prototypeRegistrations)
            {
                registration.Dispose();
            }
            _group.Dispose();
            _disabledHistory.Dispose();
            _registry.Clear();
            _prototypeRegistrations.Clear();
            _populations.Clear();
            InstanceCount = 0;
        }

        private void RefreshWorldCells()
        {
            using InstanceWorldCellUpdate update = InstanceWorldCells.BeginUpdate(_worldSourceId, _worldContentKind);
            for (int prototype = 0; prototype < _populations.Count; prototype++)
            {
                Population population = _populations[prototype];
                InstancePrototypeId id = _prototypeRegistrations[prototype].Id;
                for (int slot = 0; slot < population.HighWater; slot++)
                {
                    if (!population.Active[slot]) continue;
                    Matrix4x4 matrix = population.Transforms[slot];
                    update.Add(new Vector3(matrix.m03, matrix.m13, matrix.m23), id);
                }
            }
            update.Commit(InstanceCellResidencyState.Resident);
            _worldHierarchy = InstanceWorldHierarchy.Build(_worldSourceId, _hierarchySettings);
            _worldCellsDirty = false;
        }
        #endregion

        /// <summary>Read one pooled view for validation. This method stalls the GPU and must not run during normal rendering.</summary>
        public uint[] ReadDrawCountsForDiagnostics(int viewIndex = 0)
        {
            CheckDisposed();
            var counts = new List<uint>();
            foreach (Population population in _populations)
            {
                if (!population.TryGetView(viewIndex, out ViewBuffers view))
                {
                    continue;
                }
                var data = new uint[view.Arguments.count];
                view.Arguments.GetData(data);
                for (int i = 1; i < data.Length; i += 5)
                {
                    counts.Add(data[i]);
                }
            }
            return counts.ToArray();
        }
        /// <summary>Read this camera's current draw counts. This diagnostic stalls the GPU and allocates result storage.</summary>
        public uint[] ReadCameraDrawCounts(Camera camera)
        {
            CheckDisposed();
            var counts = new List<uint>();
            foreach (var population in _populations)
            {
                foreach (var view in population.Views)
                {
                    if (view.Camera != camera || view.Frame != Time.frameCount) continue;
                    var arguments = new uint[view.Arguments.count];
                    view.Arguments.GetData(arguments);
                    for (int i = 1; i < arguments.Length; i += 5)
                    {
                        counts.Add(arguments[i]);
                    }
                }
            }
            return counts.ToArray();
        }

        private bool TryResolve(InstanceHandle handle, out Population population)
        {
            population = null;
            if (handle.Owner != _owner || handle.Prototype < 0 || handle.Prototype >= _populations.Count) return false;
            population = _populations[handle.Prototype];
            return handle.Slot >= 0 && handle.Slot < population.HighWater && population.Active[handle.Slot] &&
                population.Generations[handle.Slot] == handle.Generation;
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InstanceRenderer));
            }
        }

        private static void ValidateTransform(Matrix4x4 transform)
        {
            for (int i = 0; i < 16; i++)
            {
                if (float.IsNaN(transform[i]) || float.IsInfinity(transform[i]))
                {
                    throw new ArgumentException("Instance transforms must contain finite values.");
                }
            }
            if (Mathf.Abs(transform.determinant) < 0.000001f || transform.m30 != 0 || transform.m31 != 0 ||
                transform.m32 != 0 || transform.m33 != 1)
            {
                throw new ArgumentException("Instance transforms must be affine and invertible.");
            }
        }

        private readonly Stack<Camera> _cameraViews = new();

        private void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            foreach (Population population in _populations)
            {
                population.AdvanceFrame(Time.frameCount);
                if (RenderingEnabled)
                {
                    UploadedBytes += population.Upload(long.MaxValue);
                }
                UploadedBytes += population.UpdateWind();
            }
            _cameraContexts.Push(context);
            _cameraViews.Push(camera);
            _renderContext = context;
            _hasRenderContext = true;
        }

        private void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (_cameraContexts.Count > 0)
            {
                _cameraContexts.Pop();
                _cameraViews.Pop();
            }
            _hasRenderContext = _cameraContexts.Count > 0;
            if (_hasRenderContext)
            {
                _renderContext = _cameraContexts.Peek();
            }
        }
        private void EndContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            // Each callback owns an output slot until all cameras in the context have submitted.
            _viewCursor = 0;
        }

        private unsafe JobHandle Cull(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output, IntPtr userContext)
        {
            if (!RenderingEnabled) return default;
            if (_disposed || InstanceCount == 0 || (context.cullingLayerMask & (1u << _layer)) == 0 ||
                (context.viewType != BatchCullingViewType.Camera && context.viewType != BatchCullingViewType.Light)) return default;
            int commandCount = 0;
            int rangeCount = 0;
            foreach (Population population in _populations)
            {
                if (population.Buffer != null && population.HighWater > 0)
                {
                    commandCount += population.Prototype.PartCount * population.BucketsPerPart;
                    rangeCount += population.Prototype.PartCount;
                }
            }
            if (commandCount == 0) return default;
            var result = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            *result = default;
            result->indirectDrawCommands = Allocate<BatchDrawCommandIndirect>(commandCount);
            result->drawRanges = Allocate<BatchDrawRange>(rangeCount);
            int planeCount = context.cullingPlanes.Length;
            int splitCount = context.cullingSplits.Length;
            // Fail visible when a view exceeds the fixed plane budget.
            if (planeCount > _planes.Length || splitCount > _splits.Length)
            {
                planeCount = 0;
                splitCount = 0;
            }
            for (int i = 0; i < planeCount; i++)
            {
                Plane plane = context.cullingPlanes[i];
                _planes[i] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
            }
            for (int i = 0; i < splitCount; i++)
            {
                _splits[i] = new Vector4(context.cullingSplits[i].cullingPlaneOffset, context.cullingSplits[i].cullingPlaneCount, 0, 0);
            }
            var lod = context.lodParameters;
            float scale = QualitySettings.lodBias / (2 * (lod.isOrthographic ? Mathf.Max(lod.orthoSize, 0.001f) :
                Mathf.Tan(lod.fieldOfView * Mathf.Deg2Rad * 0.5f)));
            var cmd = CommandBufferPool.Get("Looga Instances.Cull");
            try
            {
                cmd.SetComputeIntParam(_shader, "_PlaneCount", planeCount);
                cmd.SetComputeIntParam(_shader, "_SplitCount", splitCount);
                cmd.SetComputeVectorArrayParam(_shader, "_Planes", _planes);
                cmd.SetComputeVectorArrayParam(_shader, "_Splits", _splits);
                cmd.SetComputeVectorParam(_shader, "_Camera", lod.cameraPosition);
                bool shadow = context.viewType == BatchCullingViewType.Light;
                Camera camera = !shadow && _cameraViews.Count > 0 ? _cameraViews.Peek() : null;
                bool hasOcclusion = InstanceVisibility.TryGet(camera,
                    out InstanceVisibilityContext visibility);
                InstanceVisibilityMode adaptiveMode = VisibilityMode;
                if (_worldHierarchy != null)
                {
                    float hierarchyDistance = shadow ? Mathf.Min(MaxDistance, ShadowDistance) : MaxDistance;
                    LastHierarchyDecision = _worldHierarchy.Evaluate(_planes, planeCount, lod.cameraPosition,
                        hierarchyDistance, hasOcclusion, VisibilityMode);
                    if (!LastHierarchyDecision.Visible)
                    {
                        _viewCursor++;
                        return default;
                    }
                    if (AdaptiveHierarchy)
                    {
                        adaptiveMode = LastHierarchyDecision.VisibilityMode;
                    }
                }
                int boxes = !shadow && !lod.isOrthographic ? BakedInstanceOcclusion.CopyActiveBoxes(_bakedOccluders, context.cullingLayerMask) : 0;
                cmd.SetComputeIntParam(_shader, "_BakedOccluderCount", boxes);
                if (boxes > 0)
                {
                    cmd.SetComputeMatrixArrayParam(_shader, "_BakedOccluders", _bakedOccluders);
                }
                foreach (Population population in _populations)
                {
                    if (population.Buffer == null || population.HighWater == 0)
                    {
                        continue;
                    }
                    float distance = population.Prototype.NativeLodDistance ? float.MaxValue : MaxDistance;
                    var quality = population.Quality;
                    float shadowLimit = Mathf.Min(distance, ShadowDistance);
                    if (quality.ShadowDistance > 0)
                    {
                        shadowLimit = Mathf.Min(shadowLimit, quality.ShadowDistance);
                    }
                    cmd.SetComputeVectorParam(_shader, "_Lod", new Vector4(scale * population.Prototype.LodBias, lod.isOrthographic ? 1 : 0,
                        shadow ? shadowLimit : distance,
                        Mathf.Clamp(Mathf.Max(QualitySettings.maximumLODLevel, shadow ? Mathf.Max(MinimumShadowLod, quality.MinimumShadowLod) : 0), 0, 7)));
                    bool limitSplits = shadow && context.projectionType == BatchCullingProjectionType.Orthographic && quality.ShadowSplits > 0;
                    int admittedSplits = limitSplits ? Mathf.Min(splitCount, quality.ShadowSplits) : splitCount;
                    cmd.SetComputeIntParam(_shader, "_SplitCount", admittedSplits);
                    cmd.SetComputeVectorParam(_shader, "_Quality", new Vector4(quality.Density, shadow ? 0 : quality.MinimumPixels,
                        shadow ? quality.ShadowFadeDistance : 0, Mathf.Max(1, lod.cameraPixelHeight) / population.Prototype.LodBias));

                    ViewBuffers view = population.GetView(_viewCursor);
                    cmd.SetComputeIntParam(_shader, "_Count", population.HighWater);
                    cmd.SetComputeIntParam(_shader, "_Capacity", population.Capacity);
                    cmd.SetComputeIntParam(_shader, "_BucketStride", population.BucketsPerPart);
                    InstanceVisibilityMode populationMode = AdaptiveHierarchy && _worldHierarchy != null ?
                        _worldHierarchy.ChooseVisibility(population.HighWater, adaptiveMode) : adaptiveMode;
                    bool populationOcclusion = hasOcclusion && (!AdaptiveHierarchy || _worldHierarchy == null ||
                        _worldHierarchy.ShouldUseOcclusion(population.HighWater));
                    int selectionMode = Mathf.Clamp((int)populationMode, 0, 3);
                    if (population.Prototype.PartCount > 1)
                    {
                        selectionMode = Mathf.Max(1, selectionMode);
                    }
                    if (populationOcclusion)
                    {
                        selectionMode = Mathf.Max(1, selectionMode);
                    }
                    view.Camera = camera;
                    view.Frame = Time.frameCount;
                    view.Parameters = lod;
                    view.LodScale = scale;
                    population.EnsureClusters(selectionMode >= 2 ? (population.Capacity + 63) / 64 : 1);
                    if (selectionMode == 3 && population.ClusterDirty) selectionMode = 2;
                    view.SelectionMode = selectionMode;
                    cmd.SetComputeIntParam(_shader, "_SelectionMode", selectionMode);
                    cmd.SetComputeIntParam(_shader, "_UseSpatialOrder", selectionMode >= 2 ? 1 : 0);
                    view.EnsureSelection(selectionMode > 0 ? population.Capacity : 1);
                    cmd.SetComputeBufferParam(_shader, _cull, "_Selection", view.Selection);
                    if (selectionMode > 0)
                    {
                        int selectKernel = populationOcclusion ? _selectVisibilityOcclusion : _selectVisibility;
                        cmd.SetComputeBufferParam(_shader, selectKernel, "_Selection", view.Selection);
                        cmd.SetComputeBufferParam(_shader, selectKernel, "_Clusters", population.Clusters);
                        cmd.SetComputeBufferParam(_shader, selectKernel, "_Bounds", population.BoundsBuffer);
                        cmd.SetComputeBufferParam(_shader, selectKernel, "_SpatialOrder", population.SpatialOrder);
                        cmd.SetComputeBufferParam(_shader, selectKernel, "_OcclusionStatistics", view.OcclusionStatistics);
                        cmd.SetComputeIntParam(_shader, "_MeasureOcclusion",
                            populationOcclusion && visibility.Measure ? 1 : 0);
                        if (populationOcclusion)
                        {
                            if (visibility.Measure)
                            {
                                cmd.SetComputeBufferParam(_shader, _resetOcclusionStats,
                                    "_OcclusionStatistics", view.OcclusionStatistics);
                                cmd.DispatchCompute(_shader, _resetOcclusionStats, 1, 1, 1);
                            }
                            cmd.SetComputeTextureParam(_shader, selectKernel,
                                "_OcclusionDepth", visibility.DepthPyramid);
                            cmd.SetComputeMatrixParam(_shader, "_OcclusionViewProjection",
                                visibility.ViewProjection);
                            cmd.SetComputeVectorParam(_shader, "_OcclusionSize",
                                new Vector4(visibility.Width, visibility.Height,
                                    visibility.Levels,
                                    SystemInfo.usesReversedZBuffer ? 1 : 0));
                            cmd.SetComputeFloatParam(_shader, "_OcclusionBias",
                                visibility.Bias);
                            cmd.SetComputeIntParam(_shader, "_OcclusionFlipY", SystemInfo.graphicsUVStartsAtTop ? 1 : 0);
                        }
                        cmd.DispatchCompute(_shader, selectKernel, (population.HighWater + 63) / 64, 1, 1);
                        if (populationOcclusion && visibility.Measure)
                        {
                            Camera sampledCamera = camera;
                            int sampleId = visibility.SampleId;
                            cmd.RequestAsyncReadback(view.OcclusionStatistics, request =>
                            {
                                if (request.hasError || !sampledCamera)
                                {
                                    return;
                                }
                                var values = request.GetData<uint>();
                                if (values.Length >= 2)
                                {
                                    InstanceVisibility.Report(sampledCamera, sampleId,
                                        values[0], values[1]);
                                }
                            });
                        }
                        if (selectionMode == 2) population.ClusterDirty = false;
                    }
                    cmd.SetComputeIntParam(_shader, "_LodCount", population.Prototype.Thresholds.Length);
                    cmd.SetComputeVectorArrayParam(_shader, "_Thresholds", population.ThresholdVectors);
                    cmd.SetComputeVectorArrayParam(_shader, "_FadeWidths", population.FadeVectors);
                    cmd.SetComputeIntParam(_shader, "_CrossFade", population.Prototype.CrossFade ? 1 : 0);
                    cmd.SetComputeIntParam(_shader, "_PercentageLods", population.Prototype.PercentageLods);
                    bool animated = population.Prototype.AnimatedCrossFade;
                    cmd.SetComputeIntParam(_shader, "_AnimatedFade", animated ? 1 : 0);
                    cmd.SetComputeVectorParam(_shader, "_TransitionTime", new Vector4(Time.time, Mathf.Max(0.001f, LODGroup.crossFadeAnimationDuration), 0, 0));
                    var history = animated ? population.GetHistory(context.viewID, lod, _cameraViews.Count > 0 && _cameraViews.Peek() ? _cameraViews.Peek().transform.forward : Vector3.forward) : _disabledHistory;
                    view.LodHistory = history;
                    cmd.SetComputeBufferParam(_shader, _cull, "_LodHistory", history);
                    if (animated)
                    {
                        cmd.SetComputeBufferParam(_shader, _updateLod, "_LodHistory", history);
                        cmd.SetComputeBufferParam(_shader, _updateLod, "_Bounds", population.BoundsBuffer);
                        cmd.DispatchCompute(_shader, _updateLod, (population.HighWater + 63) / 64, 1, 1);
                    }
                    cmd.SetComputeBufferParam(_shader, _reset, "_Arguments", view.Arguments);
                    cmd.SetComputeBufferParam(_shader, _cull, "_Arguments", view.Arguments);
                    cmd.SetComputeBufferParam(_shader, _cull, "_Visible", view.Visible);
                    cmd.SetComputeBufferParam(_shader, _cull, "_Bounds", population.BoundsBuffer);
                    cmd.SetComputeBufferParam(_shader, _cull, "_InstanceData", population.Buffer);
                    float meshMetric = 2 * (lod.isOrthographic ? lod.orthoSize : Mathf.Tan(lod.fieldOfView * Mathf.Deg2Rad * 0.5f));
                    cmd.SetComputeFloatParam(_shader, "_MeshMetric", QualitySettings.meshLodThreshold * meshMetric /
                        (Mathf.Max(1, lod.cameraPixelHeight) * population.Prototype.LodBias));
                    bool fusedParts = !QualitySettings.enableLODCrossFade || !population.HasMeshLod;
                    if (fusedParts)
                    {
                        cmd.SetComputeIntParam(_shader, "_PartCount", population.Prototype.PartCount);
                        cmd.SetComputeBufferParam(_shader, _resetAll, "_Arguments", view.Arguments);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_Arguments", view.Arguments);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_Visible", view.Visible);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_Bounds", population.BoundsBuffer);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_InstanceData", population.Buffer);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_Selection", view.Selection);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_LodHistory", history);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_MeshHistory", _disabledHistory);
                        cmd.SetComputeBufferParam(_shader, _cullParts, "_PartParameters", population.PartParameters);
                        cmd.DispatchCompute(_shader, _resetAll,
                            (population.Prototype.PartCount * population.BucketsPerPart + 63) / 64, 1, 1);
                        cmd.DispatchCompute(_shader, _cullParts, (population.HighWater + 63) / 64,
                            population.Prototype.PartCount, 1);
                    }
                    for (int partIndex = 0; partIndex < population.Prototype.PartCount; partIndex++)
                    {
                        InstancePrototype.Part part = population.Prototype.Parts[partIndex];
                        cmd.SetComputeIntParam(_shader, "_Part", partIndex);
                        cmd.SetComputeIntParam(_shader, "_PartLod", part.Lod);
                        cmd.SetComputeIntParam(_shader, "_PartFlip", part.Local.determinant < 0 ? 1 : 0);
                        cmd.SetComputeIntParam(_shader, "_MeshLevel", part.MeshLevel);
                        cmd.SetComputeIntParam(_shader, "_MeshLevels", part.MeshLevels);
                        cmd.SetComputeVectorParam(_shader, "_MeshSelection", part.MeshSelection);
                        cmd.SetComputeVectorParam(_shader, "_MeshCenter", part.Mesh.bounds.center);
                        cmd.SetComputeVectorParam(_shader, "_MeshExtents", part.Mesh.bounds.extents);
                        bool meshFade = !fusedParts && part.MeshLevels > 1 && QualitySettings.enableLODCrossFade;
                        cmd.SetComputeIntParam(_shader, "_MeshFade", meshFade ? 1 : 0);
                        var meshHistory = meshFade ? population.GetHistory(context.viewID, lod,
                            _cameraViews.Count > 0 && _cameraViews.Peek() ? _cameraViews.Peek().transform.forward : Vector3.forward, partIndex) : _disabledHistory;
                        view.MeshHistories[partIndex] = meshHistory;
                        cmd.SetComputeBufferParam(_shader, _cull, "_MeshHistory", meshHistory);
                        if (meshFade)
                        {
                            cmd.SetComputeBufferParam(_shader, _updateMeshLod, "_LodHistory", meshHistory);
                            cmd.SetComputeBufferParam(_shader, _updateMeshLod, "_Bounds", population.BoundsBuffer);
                            cmd.SetComputeBufferParam(_shader, _updateMeshLod, "_InstanceData", population.Buffer);
                            cmd.DispatchCompute(_shader, _updateMeshLod, (population.HighWater + 63) / 64, 1, 1);
                        }
                        if (!fusedParts)
                        {
                            cmd.DispatchCompute(_shader, _reset, 1, 1, 1);
                            cmd.DispatchCompute(_shader, _cull, (population.HighWater + 63) / 64, 1, 1);
                        }
                        int begin = result->indirectDrawCommandCount;
                        for (int entry = 0; entry < population.BucketsPerPart; entry++)
                        {
                            int bucket = partIndex * population.BucketsPerPart + entry;
                            bool packedFade = entry >= 2;
                            result->indirectDrawCommands[result->indirectDrawCommandCount++] = new BatchDrawCommandIndirect
                            {
                                batchID = population.Batches[partIndex],
                                meshID = population.Meshes[partIndex],
                                materialID = population.Materials[partIndex],
                                flags = (part.HasMotion ? BatchDrawCommandFlags.HasMotion : BatchDrawCommandFlags.None) |
                                    (part.LightmapIndex != ushort.MaxValue ? BatchDrawCommandFlags.IsLightMapped : BatchDrawCommandFlags.None) |
                                    BatchDrawCommandFlags.UseLegacyLightmapsKeyword |
                                    ((entry & 1) == 0 ? BatchDrawCommandFlags.None : BatchDrawCommandFlags.FlipWinding) |
                                    (packedFade ? (part.Lod < population.Prototype.PercentageLods ?
                                        BatchDrawCommandFlags.LODCrossFadeValuePacked : BatchDrawCommandFlags.LODCrossFade) :
                                        BatchDrawCommandFlags.None),
                                splitVisibilityMask = limitSplits ? (ushort)((1u << quality.ShadowSplits) - 1) : ushort.MaxValue,
                                lightmapIndex = part.LightmapIndex,
                                topology = MeshTopology.Triangles,
                                visibleOffset = (uint)(bucket * population.Capacity),
                                visibleInstancesBufferHandle = view.Visible.bufferHandle,
                                visibleInstancesBufferWindowSizeBytes = 0,
                                indirectArgsBufferHandle = view.Arguments.bufferHandle,
                                indirectArgsBufferOffset = (uint)(bucket * GraphicsBuffer.IndirectDrawIndexedArgs.size)
                            };
                        }
                        result->drawRanges[result->drawRangeCount++] = new BatchDrawRange
                        {
                            drawCommandsType = BatchDrawCommandType.Indirect,
                            drawCommandsBegin = (uint)begin,
                            drawCommandsCount = (uint)population.BucketsPerPart,
                            filterSettings = new BatchFilterSettings
                            {
                                layer = (byte)_layer,
                                renderingLayerMask = part.RenderingLayers,
                                sceneCullingMask = ulong.MaxValue,
                                shadowCastingMode = part.Shadows,
                                receiveShadows = part.ReceiveShadows,
                                motionMode = part.HasMotion ? MotionVectorGenerationMode.Object : MotionVectorGenerationMode.Camera
                            }
                        };
                    }
                }
                if (_hasRenderContext)
                {
                    _renderContext.ExecuteCommandBuffer(cmd);
                }
                else
                {
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                _viewCursor++;
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
            return default;
        }

        private static unsafe T* Allocate<T>(int count) where T : unmanaged
        {
            return (T*)UnsafeUtility.Malloc(count * UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>(), Allocator.TempJob);
        }

        private sealed class ViewBuffers : IDisposable
        {
            internal readonly GraphicsBuffer Visible;
            internal readonly GraphicsBuffer Arguments;
            internal readonly GraphicsBuffer OcclusionStatistics;
            internal GraphicsBuffer Selection;
            internal Camera Camera;
            internal int Frame;
            internal LODParameters Parameters;
            internal float LodScale;
            internal int SelectionMode;
            internal GraphicsBuffer LodHistory;
            internal readonly GraphicsBuffer[] MeshHistories;

            internal void EnsureSelection(int capacity)
            {
                if (Selection == null || Selection.count < capacity)
                {
                    Selection?.Dispose();
                    Selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16);
                }
            }

            internal ViewBuffers(Population population)
            {
                MeshHistories = new GraphicsBuffer[population.Prototype.PartCount];
                int count = population.Prototype.PartCount * population.BucketsPerPart;
                Visible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count * population.Capacity, 4);
                Arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, count * 5, 4);
                OcclusionStatistics = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 2, 4);
                var arguments = new uint[count * 5];
                for (int i = 0; i < count; i++)
                {
                    var part = population.Prototype.Parts[i / population.BucketsPerPart];
                    arguments[i * 5] = part.IndexCount;
                    arguments[i * 5 + 2] = part.IndexStart;
                    arguments[i * 5 + 3] = part.Mesh.GetBaseVertex(part.Submesh);
                }
                Arguments.SetData(arguments);
            }
            public void Dispose()
            {
                Visible.Dispose();
                Arguments.Dispose();
                OcclusionStatistics.Dispose();
                Selection?.Dispose();
            }
        }

        private struct PartCullData
        {
            internal Vector4 Header;
            internal Vector4 Selection;
            internal Vector4 Center;
            internal Vector4 Extents;
        }

        private sealed class Population : IDisposable
        {
            internal readonly InstancePrototype Prototype;
            internal readonly int BucketsPerPart;
            internal InstanceQualitySettings Quality = InstanceQualitySettings.Default;
            internal uint[] VisibilityKeys = Array.Empty<uint>();
            internal bool[] Visible = Array.Empty<bool>();
            internal readonly Stack<int> Free = new Stack<int>();
            internal readonly SortedSet<int> Dirty = new SortedSet<int>();
            internal readonly Vector4[] ThresholdVectors = new Vector4[2];
            internal readonly Vector4[] FadeVectors = new Vector4[2];
            internal readonly BatchMeshID[] Meshes;
            internal readonly BatchMaterialID[] Materials;
            internal readonly BatchID[] Batches;
            internal Matrix4x4[] Transforms = Array.Empty<Matrix4x4>();
            internal uint[] Generations = Array.Empty<uint>();
            internal bool[] Active = Array.Empty<bool>();
            internal int HighWater;
            internal int Capacity;
            internal GraphicsBuffer Buffer;
            internal GraphicsBuffer BoundsBuffer;
            internal GraphicsBuffer Clusters;
            internal GraphicsBuffer SpatialOrder;
            internal long MappedUploadedBytes;
            internal long FallbackUploadedBytes;
            internal long UploadRangeCount;
            internal readonly GraphicsBuffer PartParameters;
            internal readonly bool HasMeshLod;
            internal bool ClusterDirty = true;

            internal void EnsureClusters(int count)
            {
                if (Clusters != null && Clusters.count >= count) return;
                Clusters?.Dispose();
                Clusters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
                ClusterDirty = true;
            }

            private readonly BatchRendererGroup _group;
            internal readonly List<ViewBuffers> Views = new List<ViewBuffers>();
            private readonly Dictionary<(BatchPackedCullingViewID View, int Part), TransitionHistory> _histories = new();
            private readonly List<(BatchPackedCullingViewID View, int Part)> _expiredHistories = new();
            private sealed class TransitionHistory
            {
                internal GraphicsBuffer Buffer;
                internal int LastFrame;
                internal LODParameters Parameters;
                internal Vector3 Forward;
            }
            private float[] _packed;
            private GpuBounds[] _bounds;
            private uint[] _spatialOrder = Array.Empty<uint>();
            private SpatialEntry[] _spatialEntries = Array.Empty<SpatialEntry>();
            private bool _spatialOrderDirty = true;
            private int _lastSpatialSortFrame = -1000;
            private bool _resize;
            private Matrix4x4[] _previous;
            private InstanceAppearance[] _appearance;
            private readonly HashSet<int> _moving = new();
            private readonly List<int> _uploadSlots = new();
            private readonly List<InstanceUploadSegment> _uploadSegments = new();
            private readonly InstanceUploadRing _uploadRing;
            private int _sourceFrame = -1;
            private int _appearanceOffset;
            private int _lightingOffset;
            private int LightingFloats => (Prototype.LegacyProbes ? 32 : 0) + (Prototype.HasLightmaps ? 4 : 0);
            private readonly Vector3[] _probePositions = new Vector3[1];
            private readonly SphericalHarmonicsL2[] _probeSH = new SphericalHarmonicsL2[1];
            private readonly Vector4[] _probeOcclusion = new Vector4[1];
            private int MatrixArrays => _previous == null ? 2 : 4;

            private InstanceWindSource _wind;
            private NativeArray<Vector4> _windData;
            private int _windOffset;
            private int _windFrame = -1;
            private int _registeredMeshes;
            private int _registeredMaterials;
            private readonly List<Material> _lightmapMaterials = new();
            private readonly List<InstanceMaterialBinding> _materialBindings = new();

            internal long Bytes
            {
                get
                {
                    if (Buffer == null) return 0;
                    long bytes = (long)Buffer.count * 4 + (long)BoundsBuffer.count * 48 +
                        (long)Views.Count * Prototype.PartCount * BucketsPerPart * (Capacity * 4L + 20) + (long)_histories.Count * Capacity * 16;
                    foreach (var view in Views)
                    {
                        bytes += view.Selection == null ? 0 : view.Selection.count * 16L;
                    }
                    return bytes + PartParameters.count * 64L +
                        (Clusters == null ? 0 : Clusters.count * 16L) +
                        (SpatialOrder == null ? 0 : SpatialOrder.count * 4L);
                }
            }

            internal long ProjectedBytes(int reservedViews)
            {
                if (Capacity == 0) return 0;
                int views = Mathf.Max(reservedViews, Views.Count);
                long source = 64 + Capacity * (48L + Prototype.PartCount * (MatrixArrays * 48L +
                    (_appearance == null ? 0 : 48L) + LightingFloats * 4L)) + (_wind == null ? 0 : _wind.PropertyIds.Length * 16L);
                long visibility = views * (Prototype.PartCount * (long)BucketsPerPart * (Capacity * 4L + 20) + Capacity * 16L);
                int streams = Prototype.AnimatedCrossFade ? 1 : 0;
                foreach (var part in Prototype.Parts)
                {
                    if (part.MeshLevels > 1) streams++;
                }
                long history = Mathf.Max(views * streams, _histories.Count) * Capacity * 16L;
                return Math.Max(Bytes, source + visibility + history + Prototype.PartCount * 64L +
                    ((Capacity + 63L) / 64) * 16 + Capacity * 4L);
            }

            internal Population(InstancePrototype prototype, BatchRendererGroup group, ComputeShader shader, int patchKernel)
            {
                Prototype = prototype;
                // Fully visible draws must not run the shader dither variant.
                BucketsPerPart = prototype.CrossFade ? 4 : 2;
                foreach (var part in prototype.Parts)
                {
                    HasMeshLod |= part.MeshLevels > 1;
                    if (part.MeshLevels > 1) BucketsPerPart = 4;
                    if (part.HasMotion && _previous == null)
                    {
                        // BRG metadata is immutable for the lifetime of a batch. Reserve
                        // transform history before the first upload so the first moving
                        // frame does not rebuild the batch after culling has started.
                        _previous = Array.Empty<Matrix4x4>();
                    }
                }
                _group = group;
                _uploadRing = new InstanceUploadRing(shader, patchKernel);
                var partData = new PartCullData[prototype.PartCount];
                for (int index = 0; index < partData.Length; index++)
                {
                    InstancePrototype.Part part = prototype.Parts[index];
                    partData[index] = new PartCullData
                    {
                        Header = new Vector4(part.Lod, part.Local.determinant < 0 ? 1 : 0,
                            part.MeshLevel, part.MeshLevels),
                        Selection = part.MeshSelection,
                        Center = part.Mesh.bounds.center,
                        Extents = part.Mesh.bounds.extents
                    };
                }
                PartParameters = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.Max(1, partData.Length), 64);
                PartParameters.SetData(partData);
                Meshes = new BatchMeshID[prototype.PartCount];
                Materials = new BatchMaterialID[prototype.PartCount];
                Batches = new BatchID[prototype.PartCount];
                try
                {
                    _wind = prototype.Profile ? prototype.Profile.CreateWind(prototype.Source) : null;
                    if (_wind != null)
                    {
                        _windData = new NativeArray<Vector4>(_wind.PropertyIds.Length, Allocator.Persistent);
                    }
                    for (int i = 0; i < prototype.PartCount; i++)
                    {
                        Meshes[i] = group.RegisterMesh(prototype.Parts[i].Mesh);
                        _registeredMeshes++;
                        var sourcePart = prototype.Parts[i];
                        Material drawMaterial = sourcePart.Material;
                        if (prototype.Profile)
                        {
                            var binding = prototype.Profile.CreateMaterial(drawMaterial);
                            if (binding != null)
                            {
                                _materialBindings.Add(binding);
                                drawMaterial = binding.Material;
                                if (!drawMaterial || drawMaterial.shader != sourcePart.Material.shader)
                                {
                                    throw new InvalidOperationException("A material binding must preserve the validated shader.");
                                }
                            }
                        }
                        if (sourcePart.LightmapIndex != ushort.MaxValue)
                        {
                            var maps = LightmapSettings.lightmaps;
                            if (sourcePart.LightmapIndex >= maps.Length || maps[sourcePart.LightmapIndex].lightmapColor == null)
                            {
                                throw new InvalidOperationException("The prototype references an unavailable baked lightmap.");
                            }
                            var map = maps[sourcePart.LightmapIndex];
                            drawMaterial = new Material(drawMaterial) { hideFlags = HideFlags.HideAndDontSave, name = drawMaterial.name + " (Looga lightmap)" };
                            _lightmapMaterials.Add(drawMaterial);
                            drawMaterial.SetTexture("unity_Lightmap", map.lightmapColor);
                            drawMaterial.SetTexture("unity_LightmapInd", map.lightmapDir);
                            drawMaterial.SetTexture("unity_ShadowMask", map.shadowMask);
                            drawMaterial.EnableKeyword("LIGHTMAP_ON");
                            drawMaterial.EnableKeyword("USE_LEGACY_LIGHTMAPS");
                            if (map.lightmapDir) drawMaterial.EnableKeyword("DIRLIGHTMAP_COMBINED");
                            else drawMaterial.DisableKeyword("DIRLIGHTMAP_COMBINED");
                            if (map.shadowMask) drawMaterial.EnableKeyword("SHADOWS_SHADOWMASK");
                        }
                        Materials[i] = group.RegisterMaterial(drawMaterial);
                        _registeredMaterials++;
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
                for (int i = 0; i < prototype.Thresholds.Length; i++)
                {
                    ThresholdVectors[i / 4][i % 4] = prototype.Thresholds[i];
                    FadeVectors[i / 4][i % 4] = prototype.FadeWidths[i];
                }
            }

            internal void EnsureCapacity(int size)
            {
                if (BucketsPerPart > 2 && size > 0x1000000)
                {
                    throw new InvalidOperationException("Packed LOD visibility supports at most 16777216 slots per prototype.");
                }
                if (size <= Capacity) return;
                Capacity = Mathf.NextPowerOfTwo(Mathf.Max(64, size));
                int cpuCapacity = Math.Max(Capacity, Transforms.Length);
                Array.Resize(ref Transforms, cpuCapacity);
                Array.Resize(ref Generations, cpuCapacity);
                Array.Resize(ref VisibilityKeys, cpuCapacity);
                Array.Resize(ref Visible, cpuCapacity);
                Array.Resize(ref Active, cpuCapacity);
                int oldSpatialSize = _spatialOrder.Length;
                Array.Resize(ref _spatialOrder, cpuCapacity);
                for (int index = oldSpatialSize; index < cpuCapacity; index++)
                {
                    _spatialOrder[index] = (uint)index;
                }
                _spatialOrderDirty = true;
                if (_previous != null)
                {
                    Array.Resize(ref _previous, cpuCapacity);
                }
                if (_appearance != null)
                {
                    int oldSize = _appearance.Length;
                    Array.Resize(ref _appearance, cpuCapacity);
                    for (int i = oldSize; i < cpuCapacity; i++)
                    {
                        _appearance[i] = InstanceAppearance.Default;
                    }
                }
                _resize = true;
            }

            internal void TrimExcess()
            {
                while (HighWater > 0 && !Active[HighWater - 1])
                {
                    Dirty.Remove(--HighWater);
                    _moving.Remove(HighWater);
                }
                Free.Clear();
                _spatialOrderDirty = true;
                for (int i = 0; i < HighWater; i++)
                {
                    if (!Active[i])
                    {
                        Free.Push(i);
                    }
                }
                int capacity = HighWater == 0 ? 0 : Mathf.NextPowerOfTwo(Mathf.Max(64, HighWater));
                if (capacity >= Capacity) return;
                Capacity = capacity;
                if (capacity == 0)
                {
                    ReleaseBuffers();
                    Dirty.Clear();
                    _resize = false;
                    _packed = null;
                    _bounds = null;
                }
                else
                {
                    _resize = true;
                    for (int i = 0; i < HighWater; i++)
                    {
                        Dirty.Add(i);
                    }
                }
                // Generation records remain valid when capacity grows again.
            }

            internal void InitializeSlot(int slot, Matrix4x4 transform)
            {
                Transforms[slot] = transform;
                unchecked
                {
                    uint x = (uint)BitConverter.SingleToInt32Bits(transform.m03);
                    uint y = (uint)BitConverter.SingleToInt32Bits(transform.m13);
                    uint z = (uint)BitConverter.SingleToInt32Bits(transform.m23);
                    VisibilityKeys[slot] = x * 73856093u ^ y * 19349663u ^ z * 83492791u;
                }
                if (_previous != null)
                {
                    _previous[slot] = transform;
                }
                if (_appearance != null)
                {
                    _appearance[slot] = InstanceAppearance.Default;
                }
                _moving.Remove(slot);
                _spatialOrderDirty = true;
            }

            internal void SetTransform(int slot, Matrix4x4 transform)
            {
                AdvanceFrame(Time.frameCount);
                if (_previous != null && _moving.Add(slot))
                {
                    _previous[slot] = Transforms[slot];
                }
                Transforms[slot] = transform;
                _spatialOrderDirty = true;
            }

            internal void MarkSpatialOrderDirty()
            {
                _spatialOrderDirty = true;
            }

            internal void AdvanceFrame(int frame)
            {
                if (_sourceFrame == frame) return;
                foreach (int slot in _moving)
                {
                    _previous[slot] = Transforms[slot];
                    Dirty.Add(slot);
                }
                _moving.Clear();
                _sourceFrame = frame;
            }

            internal void ValidateAppearance(InstanceAppearance value)
            {
                if (value.Equals(InstanceAppearance.Default)) return;
                foreach (var part in Prototype.Parts)
                {
                    string shader = part.Material.shader.name;
                    bool own = part.Material.GetTag("LoogaInstanceAppearance", false, "") == "Vegetation";
                    if (!own && shader != "Universal Render Pipeline/Lit" && shader != "Universal Render Pipeline/Unlit")
                    {
                        throw new NotSupportedException("This shader has no qualified instance appearance contract.");
                    }
                    if (!own && (value.LightmapColor != Color.white || value.Custom != Vector4.zero))
                    {
                        throw new NotSupportedException("Lightmap color and custom data require Looga Vegetation Lit.");
                    }
                }
            }

            internal void SetAppearance(int slot, InstanceAppearance value)
            {
                if (_appearance == null)
                {
                    if (value.Equals(InstanceAppearance.Default)) return;
                    _appearance = new InstanceAppearance[Capacity];
                    Array.Fill(_appearance, InstanceAppearance.Default);
                    _resize = true;
                }
                _appearance[slot] = value;
                Dirty.Add(slot);
            }

            internal GraphicsBuffer GetHistory(BatchPackedCullingViewID id, LODParameters parameters, Vector3 forward, int part = -1)
            {
                var keyId = (id, part);
                _expiredHistories.Clear();
                foreach (var entry in _histories)
                {
                    if (Time.frameCount - entry.Value.LastFrame > 120)
                    {
                        _expiredHistories.Add(entry.Key);
                    }
                }
                foreach (var key in _expiredHistories)
                {
                    _histories[key].Buffer.Dispose();
                    _histories.Remove(key);
                }
                if (!_histories.TryGetValue(keyId, out var history))
                {
                    history = new TransitionHistory { Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, 16) };
                    history.Buffer.SetData(new Vector4[Capacity]);
                    _histories.Add(keyId, history);
                }
                bool cut = history.LastFrame != 0 && (Time.frameCount - history.LastFrame > 1 ||
                    (parameters.cameraPosition - history.Parameters.cameraPosition).sqrMagnitude > 2500 ||
                    parameters.isOrthographic != history.Parameters.isOrthographic ||
                    Mathf.Abs(parameters.fieldOfView - history.Parameters.fieldOfView) > 0.1f ||
                    Mathf.Abs(parameters.orthoSize - history.Parameters.orthoSize) > 0.1f ||
                    Vector3.Dot(forward.normalized, history.Forward.normalized) < 0.5f);
                if (cut)
                {
                    history.Buffer.SetData(new Vector4[Capacity]);
                }
                history.Parameters = parameters;
                history.Forward = forward;
                history.LastFrame = Time.frameCount;
                return history.Buffer;
            }

            internal bool TryGetView(int index, out ViewBuffers view)
            {
                view = index >= 0 && index < Views.Count ? Views[index] : null;
                return view != null;
            }

            internal ViewBuffers GetView(int index)
            {
                while (Views.Count <= index)
                {
                    Views.Add(new ViewBuffers(this));
                }
                return Views[index];
            }

            internal long Upload(long budget)
            {
                if (HighWater == 0 || budget < 64) return 0;
                long bytes = 0;
                if (_resize)
                {
                    ReleaseBuffers();
                    int matrixFloats = Prototype.PartCount * Capacity * 12;
                    _appearanceOffset = 16 + matrixFloats * MatrixArrays;
                    _lightingOffset = _appearanceOffset + (_appearance == null ? 0 : Prototype.PartCount * Capacity * 12);
                    _windOffset = _lightingOffset + Prototype.PartCount * Capacity * LightingFloats;
                    _packed = new float[_windOffset + (_wind == null ? 0 : _wind.PropertyIds.Length * 4)];
                    _windFrame = -1;
                    _bounds = new GpuBounds[Capacity];
                    Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, _packed.Length, 4);
                    BoundsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.LockBufferForWrite, Capacity, 48);
                    SpatialOrder = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, 4);
                    SpatialOrder.SetData(_spatialOrder, 0, 0, Capacity);
                    Buffer.SetData(_packed, 0, 0, 16);
                    bytes += 64;
                    for (int part = 0; part < Prototype.PartCount; part++)
                    {
                        var metadata = new NativeArray<MetadataValue>(4 + (Prototype.LegacyProbes ? 1 : 0) + (Prototype.HasLightmaps ? 1 : 0) + (_appearance == null ? 0 : 3) + (_wind == null ? 0 : _wind.PropertyIds.Length), Allocator.Temp);
                        metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"),
                            Value = 0x80000000u | (uint)((16 + part * Capacity * 12) * 4) };
                        metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"),
                            Value = 0x80000000u | (uint)((16 + matrixFloats + part * Capacity * 12) * 4) };
                        metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("unity_MatrixPreviousM"),
                            Value = 0x80000000u | (uint)((16 + (_previous == null ? 0 : matrixFloats * 2) + part * Capacity * 12) * 4) };
                        metadata[3] = new MetadataValue { NameID = Shader.PropertyToID("unity_MatrixPreviousMI"),
                            Value = 0x80000000u | (uint)((16 + (_previous == null ? matrixFloats : matrixFloats * 3) + part * Capacity * 12) * 4) };
                        if (_appearance != null)
                        {
                            string[] names = { "_BaseColor", "_LoogaLightmapColor", "_LoogaInstanceData" };
                            for (int channel = 0; channel < names.Length; channel++)
                            {
                                metadata[4 + channel] = new MetadataValue { NameID = Shader.PropertyToID(names[channel]),
                                    Value = 0x80000000u | (uint)((_appearanceOffset + (part * 3 + channel) * Capacity * 4) * 4) };
                            }
                        }
                        if (_wind != null)
                        {
                            for (int i = 0; i < _wind.PropertyIds.Length; i++)
                            {
                                metadata[4 + (_appearance == null ? 0 : 3) + i] = new MetadataValue { NameID = _wind.PropertyIds[i], Value = (uint)(_windOffset * 4 + i * 16) };
                            }
                        }
                        int lightingMetadata = metadata.Length - (Prototype.LegacyProbes ? 1 : 0) - (Prototype.HasLightmaps ? 1 : 0);
                        if (Prototype.LegacyProbes)
                        {
                            metadata[lightingMetadata++] = new MetadataValue { NameID = Shader.PropertyToID("unity_SHCoefficients"),
                                Value = 0x80000000u | (uint)((_lightingOffset + part * Capacity * LightingFloats) * 4) };
                        }
                        if (Prototype.HasLightmaps)
                        {
                            metadata[lightingMetadata] = new MetadataValue { NameID = Shader.PropertyToID("unity_LightmapST"),
                                Value = 0x80000000u | (uint)((_lightingOffset + part * Capacity * LightingFloats + (Prototype.LegacyProbes ? Capacity * 32 : 0)) * 4) };
                        }
                        Batches[part] = _group.AddBatch(metadata, Buffer.bufferHandle);
                        metadata.Dispose();
                    }
                    for (int slot = 0; slot < HighWater; slot++)
                    {
                        Dirty.Add(slot);
                    }
                    _resize = false;
                }
                long bytesPerSlot = 48L + Prototype.PartCount * (MatrixArrays * 48L + (_appearance == null ? 0 : 48L) + LightingFloats * 4L);
                int limit = (int)Math.Min(Dirty.Count, (budget - bytes) / bytesPerSlot);
                _uploadSlots.Clear();
                foreach (int slot in Dirty)
                {
                    if (_uploadSlots.Count >= limit)
                    {
                        break;
                    }
                    _uploadSlots.Add(slot);
                }
                foreach (int slot in _uploadSlots)
                {
                    Matrix4x4 transform = Transforms[slot];
                    Bounds bounds = InstancePrototype.TransformBounds(Prototype.Bounds, transform);
                    Vector3 lodCenter = transform.MultiplyPoint3x4(Prototype.LodCenter);
                    float scale = Mathf.Max(transform.GetColumn(0).magnitude, transform.GetColumn(1).magnitude, transform.GetColumn(2).magnitude);
                    _bounds[slot] = new GpuBounds
                    {
                        Sphere = new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, bounds.extents.magnitude),
                        Lod = new Vector4(lodCenter.x, lodCenter.y, lodCenter.z, Prototype.LodSize * scale),
                        State = new Vector4(Active[slot] && Visible[slot] ? 1 : 0, transform.determinant < 0 ? 1 : 0, Generations[slot], StableFraction(VisibilityKeys[slot]))
                    };
                    for (int part = 0; part < Prototype.PartCount; part++)
                    {
                        Matrix4x4 world = transform * Prototype.Parts[part].Local;
                        Pack(world, 16 + (part * Capacity + slot) * 12);
                        Pack(world.inverse, 16 + (Prototype.PartCount * Capacity + part * Capacity + slot) * 12);
                        if (_previous != null)
                        {
                            Matrix4x4 previous = _previous[slot] * Prototype.Parts[part].Local;
                            Pack(previous, 16 + (Prototype.PartCount * Capacity * 2 + part * Capacity + slot) * 12);
                            Pack(previous.inverse, 16 + (Prototype.PartCount * Capacity * 3 + part * Capacity + slot) * 12);
                        }
                        int lighting = _lightingOffset + part * Capacity * LightingFloats;
                        if (Prototype.LegacyProbes)
                        {
                            _probePositions[0] = world.MultiplyPoint3x4(Prototype.Parts[part].Mesh.bounds.center);
                            LightProbes.CalculateInterpolatedLightAndOcclusionProbes(_probePositions, _probeSH, _probeOcclusion);
                            var sh = new SHCoefficients(_probeSH[0], _probeOcclusion[0]);
                            int offset = lighting + slot * 32;
                            PackVector(sh.SHAr, offset); PackVector(sh.SHAg, offset + 4); PackVector(sh.SHAb, offset + 8);
                            PackVector(sh.SHBr, offset + 12); PackVector(sh.SHBg, offset + 16); PackVector(sh.SHBb, offset + 20);
                            PackVector(sh.SHC, offset + 24); PackVector(sh.ProbesOcclusion, offset + 28);
                        }
                        if (Prototype.HasLightmaps)
                        {
                            PackVector(Prototype.Parts[part].LightmapST, lighting + (Prototype.LegacyProbes ? Capacity * 32 : 0) + slot * 4);
                        }
                        if (_appearance != null)
                        {
                            var value = _appearance[slot];
                            Color color = Prototype.Parts[part].MaterialColor * value.Tint;
                            PackVector(QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color, _appearanceOffset + (part * 3 * Capacity + slot) * 4);
                            PackVector(QualitySettings.activeColorSpace == ColorSpace.Linear ? value.LightmapColor.linear : value.LightmapColor, _appearanceOffset + ((part * 3 + 1) * Capacity + slot) * 4);
                            PackVector(value.Custom, _appearanceOffset + ((part * 3 + 2) * Capacity + slot) * 4);
                        }
                    }
                }
                if (_uploadSlots.Count > 0) ClusterDirty = true;
                int start = -1;
                int end = -1;
                foreach (int slot in _uploadSlots)
                {
                    if (start >= 0 && slot != end + 1)
                    {
                        bytes += UploadRange(start, end - start + 1);
                        start = -1;
                    }
                    if (start < 0)
                    {
                        start = slot;
                    }
                    end = slot;
                }
                if (start >= 0)
                {
                    bytes += UploadRange(start, end - start + 1);
                }
                foreach (int slot in _uploadSlots)
                {
                    Dirty.Remove(slot);
                }
                return bytes;
            }

            internal void PrepareSpatialOrder()
            {
                if (!_spatialOrderDirty || Dirty.Count > 0 || HighWater == 0 || SpatialOrder == null)
                {
                    return;
                }
                if (Application.isPlaying && _lastSpatialSortFrame >= 0 &&
                    Time.frameCount - _lastSpatialSortFrame < 30)
                {
                    return;
                }
                Vector3 minimum = Vector3.positiveInfinity;
                Vector3 maximum = Vector3.negativeInfinity;
                for (int slot = 0; slot < HighWater; slot++)
                {
                    if (!Active[slot]) continue;
                    Vector3 center = _bounds[slot].Sphere;
                    minimum = Vector3.Min(minimum, center);
                    maximum = Vector3.Max(maximum, center);
                }
                if (!float.IsFinite(minimum.x))
                {
                    return;
                }
                Array.Resize(ref _spatialEntries, HighWater);
                Vector3 extent = maximum - minimum;
                int entry = 0;
                for (int slot = 0; slot < HighWater; slot++)
                {
                    if (!Active[slot]) continue;
                    Vector3 normalized = new Vector3(
                        extent.x > 0.0001f ? (_bounds[slot].Sphere.x - minimum.x) / extent.x : 0.5f,
                        extent.y > 0.0001f ? (_bounds[slot].Sphere.y - minimum.y) / extent.y : 0.5f,
                        extent.z > 0.0001f ? (_bounds[slot].Sphere.z - minimum.z) / extent.z : 0.5f);
                    _spatialEntries[entry++] = new SpatialEntry
                    {
                        Slot = (uint)slot,
                        Code = Morton(normalized)
                    };
                }
                for (int slot = 0; slot < HighWater; slot++)
                {
                    if (Active[slot]) continue;
                    _spatialEntries[entry++] = new SpatialEntry { Slot = (uint)slot, Code = uint.MaxValue };
                }
                Array.Sort(_spatialEntries, 0, HighWater, SpatialEntryComparer.Instance);
                for (int index = 0; index < HighWater; index++)
                {
                    _spatialOrder[index] = _spatialEntries[index].Slot;
                }
                SpatialOrder.SetData(_spatialOrder, 0, 0, HighWater);
                ClusterDirty = true;
                _spatialOrderDirty = false;
                _lastSpatialSortFrame = Time.frameCount;
            }

            private static uint Morton(Vector3 normalized)
            {
                uint x = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.x * 1023f), 0, 1023);
                uint y = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.y * 1023f), 0, 1023);
                uint z = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.z * 1023f), 0, 1023);
                return SpreadBits(x) | SpreadBits(y) << 1 | SpreadBits(z) << 2;
            }

            private static uint SpreadBits(uint value)
            {
                value &= 0x000003ff;
                value = (value | value << 16) & 0x030000ff;
                value = (value | value << 8) & 0x0300f00f;
                value = (value | value << 4) & 0x030c30c3;
                value = (value | value << 2) & 0x09249249;
                return value;
            }

            internal long UpdateWind()
            {
                if (_wind == null || Buffer == null || _windFrame == Time.frameCount) return 0;
                _wind.Update(_windData);
                Buffer.SetData(_windData, 0, _windOffset / 4, _windData.Length);
                _windFrame = Time.frameCount;
                return _windData.Length * 16L;
            }

            private long UploadRange(int start, int count)
            {
                UploadRangeCount++;
                long mapped = 0;
                long fallback = 0;
                if (InstanceUploadWriter.TryWrite(BoundsBuffer, _bounds, start, start, count)) mapped += count * 48L;
                else { BoundsBuffer.SetData(_bounds, start, start, count); fallback += count * 48L; }
                _uploadSegments.Clear();
                for (int array = 0; array < Prototype.PartCount * MatrixArrays; array++)
                {
                    int offset = 16 + (array * Capacity + start) * 12;
                    _uploadSegments.Add(new InstanceUploadSegment(offset, offset, count * 12));
                }
                if (_appearance != null)
                {
                    for (int channel = 0; channel < Prototype.PartCount * 3; channel++)
                    {
                        int offset = _appearanceOffset + (channel * Capacity + start) * 4;
                        _uploadSegments.Add(new InstanceUploadSegment(offset, offset, count * 4));
                    }
                }
                for (int part = 0; part < Prototype.PartCount; part++)
                {
                    int lighting = _lightingOffset + part * Capacity * LightingFloats;
                    if (Prototype.LegacyProbes)
                    {
                        int offset = lighting + start * 32;
                        _uploadSegments.Add(new InstanceUploadSegment(offset, offset, count * 32));
                    }
                    if (Prototype.HasLightmaps)
                    {
                        int offset = lighting + (Prototype.LegacyProbes ? Capacity * 32 : 0) + start * 4;
                        _uploadSegments.Add(new InstanceUploadSegment(offset, offset, count * 4));
                    }
                }
                if (_uploadRing.TryPatch(Buffer, _packed, _uploadSegments, out long patched)) mapped += patched;
                else
                {
                    foreach (InstanceUploadSegment segment in _uploadSegments)
                    {
                        if (InstanceUploadWriter.TryWrite(Buffer, _packed, segment.Source, segment.Destination, segment.Count))
                            mapped += segment.Count * 4L;
                        else
                        {
                            Buffer.SetData(_packed, segment.Source, segment.Destination, segment.Count);
                            fallback += segment.Count * 4L;
                        }
                    }
                }
                MappedUploadedBytes += mapped;
                FallbackUploadedBytes += fallback;
                return mapped + fallback;
            }

            private static float StableFraction(uint value)
            {
                unchecked
                {
                    value ^= value >> 16;
                    value *= 0x7feb352du;
                    value ^= value >> 15;
                    value *= 0x846ca68bu;
                    value ^= value >> 16;
                }
                return (value & 0x00ffffffu) / 16777216f;
            }

            private void PackVector(Vector4 value, int offset)
            {
                for (int i = 0; i < 4; i++)
                {
                    _packed[offset + i] = value[i];
                }
            }

            private void Pack(Matrix4x4 matrix, int offset)
            {
                for (int column = 0; column < 4; column++)
                {
                    for (int row = 0; row < 3; row++)
                    {
                        _packed[offset++] = matrix[row, column];
                    }
                }
            }

            private void ReleaseBuffers()
            {
                if (Buffer != null)
                {
                    foreach (BatchID batch in Batches)
                    {
                        _group.RemoveBatch(batch);
                    }
                }
                Buffer?.Dispose();
                BoundsBuffer?.Dispose();
                SpatialOrder?.Dispose();
                Clusters?.Dispose();
                Clusters = null;
                ClusterDirty = true;
                Buffer = null;
                BoundsBuffer = null;
                SpatialOrder = null;
                foreach (ViewBuffers view in Views)
                {
                    view.Dispose();
                }
                Views.Clear();
                foreach (var history in _histories.Values)
                {
                    history.Buffer.Dispose();
                }
                _histories.Clear();
            }

            public void Dispose()
            {
                ReleaseBuffers();
                _uploadRing.Dispose();
                _wind?.Dispose();
                _wind = null;
                if (_windData.IsCreated)
                {
                    _windData.Dispose();
                }
                for (int i = 0; i < _registeredMeshes; i++)
                {
                    _group.UnregisterMesh(Meshes[i]);
                }
                for (int i = 0; i < _registeredMaterials; i++)
                {
                    _group.UnregisterMaterial(Materials[i]);
                }
                _registeredMeshes = _registeredMaterials = 0;
                foreach (var material in _lightmapMaterials)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
                _lightmapMaterials.Clear();
                foreach (var binding in _materialBindings)
                {
                    binding.Dispose();
                }
                _materialBindings.Clear();
                PartParameters?.Dispose();
            }

            private struct SpatialEntry
            {
                internal uint Slot;
                internal uint Code;
            }

            private sealed class SpatialEntryComparer : IComparer<SpatialEntry>
            {
                internal static readonly SpatialEntryComparer Instance = new();

                public int Compare(SpatialEntry left, SpatialEntry right)
                {
                    int code = left.Code.CompareTo(right.Code);
                    return code != 0 ? code : left.Slot.CompareTo(right.Slot);
                }
            }

            private struct GpuBounds
            {
                internal Vector4 Sphere;
                internal Vector4 Lod;
                internal Vector4 State;
            }
        }
    }
}

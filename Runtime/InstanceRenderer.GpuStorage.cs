using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Contiguous slots of one prototype in a GPU-resident renderer. A compute pass writes their transforms.</summary>
    /// <remarks>The handle stays valid when the renderer moves the slots during compaction.</remarks>
    public readonly struct InstanceRange : IEquatable<InstanceRange>
    {
        internal readonly int Owner;
        internal readonly int Id;
        internal readonly uint Generation;

        /// <summary>Renderer-local prototype index.</summary>
        public int Prototype { get; }

        /// <summary>Number of slots in the range.</summary>
        public int Count { get; }

        internal InstanceRange(int owner, int prototype, int id, int count, uint generation)
        {
            Owner = owner;
            Prototype = prototype;
            Id = id;
            Count = count;
            Generation = generation;
        }

        public bool Equals(InstanceRange other) => Owner == other.Owner && Prototype == other.Prototype &&
            Id == other.Id && Generation == other.Generation;

        public override bool Equals(object obj) => obj is InstanceRange other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Owner, Prototype, Id, Generation);
    }

    /// <summary>One range write. The source holds Count transforms from SourceOffset.</summary>
    public readonly struct InstanceRangeWrite
    {
        /// <summary>Destination range.</summary>
        public InstanceRange Range { get; }

        /// <summary>Index of the first source transform.</summary>
        public int SourceOffset { get; }

        public InstanceRangeWrite(InstanceRange range, int sourceOffset)
        {
            Range = range;
            SourceOffset = sourceOffset;
        }
    }

    // GPU-resident storage. The CPU tracks slot ranges only. Compute passes write bounds and matrices in the
    // layout of the CPU upload path, so culling and draw commands are shared.
    public sealed partial class InstanceRenderer
    {
        /// <summary>Stride of one source transform: three float4 rows of an affine 3x4 matrix.</summary>
        public const int RangeTransformStride = 48;

        private readonly bool _gpuResident;
        private ComputeShader _writeShader;
        private int _writeRangesKernel;
        private int _clearRangesKernel;
        private int _initializeKernel;
        private int _moveRangesKernel;
        private GraphicsBuffer _rangeDescriptors;
        private uint[] _rangeDescriptorData = Array.Empty<uint>();

        /// <summary>True when compute passes write this renderer's instances. Per-instance APIs are not available.</summary>
        public bool GpuResident => _gpuResident;

        /// <summary>Reserve slots for one prototype. Write the slots with WriteRanges. Unwritten slots stay inactive.</summary>
        /// <param name="pivots">World bounds that contain every instance pivot in the range.</param>
        /// <param name="maximumScale">Largest axis scale of any instance transform in the range.</param>
        public InstanceRange AllocateRange(int prototype, int count, Bounds pivots, float maximumScale)
        {
            CheckDisposed();
            CheckGpuStorage();
            if ((uint)prototype >= (uint)_populations.Count) throw new ArgumentOutOfRangeException(nameof(prototype));
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (!float.IsFinite(maximumScale) || maximumScale <= 0) throw new ArgumentOutOfRangeException(nameof(maximumScale));
            if (!IsFinite(pivots.center) || !IsFinite(pivots.extents))
            {
                throw new ArgumentException("Range bounds must be finite.", nameof(pivots));
            }
            Population population = _populations[prototype];
            Population.RangeRecord record = population.AddRange(count, pivots);
            // Rotation can grow an axis-aligned extent by up to the square root of three.
            Bounds local = population.Prototype.Bounds;
            float reach = (local.center.magnitude + local.extents.magnitude * 1.7320508f) * maximumScale;
            population.HierarchyReach = Mathf.Max(population.HierarchyReach, reach);
            Bounds world = pivots;
            world.Expand(2 * reach);
            if (InstanceCount == 0)
            {
                _worldBounds = world;
            }
            else
            {
                _worldBounds.Encapsulate(world);
            }
            _boundsDirty = true;
            ChangeRangeCells(prototype, pivots, count, 1);
            _worldCellsDirty = true;
            population.ResidentCount += count;
            population.SourceRevision++;
            InstanceCount += count;
            return new InstanceRange(_owner, prototype, record.Id, count, record.Generation);
        }

        /// <summary>Release a range. Its slots become inactive before the next draw. Stale ranges return false.</summary>
        public bool ReleaseRange(InstanceRange range)
        {
            CheckDisposed();
            CheckGpuStorage();
            if (!TryResolve(range, out Population population, out Population.RangeRecord record)) return false;
            population.RemoveRange(record);
            ChangeRangeCells(range.Prototype, record.Pivots, range.Count, -1);
            population.ResidentCount -= range.Count;
            population.SourceRevision++;
            if (population.ResidentCount == 0)
            {
                population.HierarchyReach = 0;
            }
            InstanceCount -= range.Count;
            _boundsDirty = true;
            _worldCellsDirty = true;
            return true;
        }

        /// <summary>Expand source transforms into instance storage. The GPU runs this work before later submissions.</summary>
        /// <param name="transforms">Structured buffer with RangeTransformStride. A zero matrix makes its slot inactive.</param>
        /// <remarks>Execute the command buffer that fills the source before this call.</remarks>
        public void WriteRanges(GraphicsBuffer transforms, IReadOnlyList<InstanceRangeWrite> writes)
        {
            CheckDisposed();
            CheckGpuStorage();
            if (transforms == null) throw new ArgumentNullException(nameof(transforms));
            if (writes == null) throw new ArgumentNullException(nameof(writes));
            if (transforms.stride != RangeTransformStride || (transforms.target & GraphicsBuffer.Target.Structured) == 0)
            {
                throw new ArgumentException("Range transforms must be a structured buffer of 3x4 rows.", nameof(transforms));
            }
            foreach (InstanceRangeWrite write in writes)
            {
                if (!TryResolve(write.Range, out _, out _))
                {
                    throw new ArgumentException("A range write references a released or foreign range.", nameof(writes));
                }
                if (write.SourceOffset < 0 || (long)write.SourceOffset + write.Range.Count > transforms.count)
                {
                    throw new ArgumentException("A range write reads past the source transforms.", nameof(writes));
                }
            }
            for (int prototype = 0; prototype < _populations.Count; prototype++)
            {
                if (!HasWrite(writes, prototype)) continue;
                Population population = _populations[prototype];
                // Commit first. A rebuild can move slots, and the commit reuses the descriptor array.
                CommitGpu(population);
                int ranges = 0;
                int largest = 0;
                foreach (InstanceRangeWrite write in writes)
                {
                    if (write.Range.Prototype != prototype) continue;
                    TryResolve(write.Range, out _, out Population.RangeRecord record);
                    record.Written = true;
                    EnsureDescriptorCapacity((ranges + 1) * 4);
                    _rangeDescriptorData[ranges * 4] = (uint)write.SourceOffset;
                    _rangeDescriptorData[ranges * 4 + 1] = (uint)record.Start;
                    _rangeDescriptorData[ranges * 4 + 2] = (uint)record.Count;
                    _rangeDescriptorData[ranges * 4 + 3] = record.Generation;
                    largest = Mathf.Max(largest, record.Count);
                    ranges++;
                }
                CommandBuffer cmd = CommandBufferPool.Get("Looga Instances.Write Ranges");
                try
                {
                    UploadDescriptors(cmd, ranges * 4);
                    cmd.SetComputeIntParam(_writeShader, "_WriteCapacity", population.Capacity);
                    cmd.SetComputeIntParam(_writeShader, "_WriteRangeCount", ranges);
                    population.BindWriteParameters(cmd, _writeShader, _writeRangesKernel);
                    cmd.SetComputeBufferParam(_writeShader, _writeRangesKernel, "_WriteSource", transforms);
                    cmd.SetComputeBufferParam(_writeShader, _writeRangesKernel, "_WriteRanges", _rangeDescriptors);
                    cmd.SetComputeBufferParam(_writeShader, _writeRangesKernel, "_WriteData", population.Buffer);
                    cmd.SetComputeBufferParam(_writeShader, _writeRangesKernel, "_WriteBounds", population.BoundsBuffer);
                    cmd.DispatchCompute(_writeShader, _writeRangesKernel, (largest + 63) / 64, ranges, 1);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
                population.ClusterDirty = true;
                population.SourceRevision++;
            }
        }

        private static bool HasWrite(IReadOnlyList<InstanceRangeWrite> writes, int prototype)
        {
            foreach (InstanceRangeWrite write in writes)
            {
                if (write.Range.Prototype == prototype) return true;
            }
            return false;
        }

        private void InitializeGpuStorage()
        {
            _writeShader = Resources.Load<ComputeShader>("LoogaInstanceWrite");
            if (_writeShader == null)
            {
                throw new InvalidOperationException("The Looga instance write compute shader is missing.");
            }
            _writeRangesKernel = _writeShader.FindKernel("WriteRanges");
            _clearRangesKernel = _writeShader.FindKernel("ClearRanges");
            _initializeKernel = _writeShader.FindKernel("InitializeStorage");
            _moveRangesKernel = _writeShader.FindKernel("MoveRanges");
        }

        private void DisposeGpuStorage()
        {
            _rangeDescriptors?.Dispose();
            _rangeDescriptors = null;
        }

        private static void ValidateGpuPrototype(InstancePrototype prototype)
        {
            if (prototype.LegacyProbes || prototype.HasLightmaps)
            {
                throw new NotSupportedException("GPU-resident storage does not support per-instance probes or lightmaps.");
            }
        }

        private void CheckGpuStorage()
        {
            if (!_gpuResident) throw new InvalidOperationException("Range storage requires a GPU-resident renderer.");
        }

        private void CheckCpuStorage()
        {
            if (_gpuResident)
            {
                throw new InvalidOperationException("A GPU-resident renderer has no per-instance API. Use instance ranges.");
            }
        }

        private bool TryResolve(InstanceRange range, out Population population, out Population.RangeRecord record)
        {
            population = null;
            record = null;
            if (range.Owner != _owner || (uint)range.Prototype >= (uint)_populations.Count) return false;
            population = _populations[range.Prototype];
            return population.TryGetRange(range, out record);
        }

        // Apply queued clears, then capacity changes and compaction. Each step runs before any later GPU submission.
        private void CommitGpu(Population population)
        {
            if (!population.GpuCommitPending) return;
            // Clears use the current slot layout, so they run before a rebuild moves slots.
            int clears = population.CopyPendingClears(this);
            if (clears > 0 && population.Buffer != null)
            {
                CommandBuffer clearCommands = CommandBufferPool.Get("Looga Instances.Clear Ranges");
                try
                {
                    UploadDescriptors(clearCommands, clears * 4);
                    clearCommands.SetComputeIntParam(_writeShader, "_WriteCapacity", population.Capacity);
                    clearCommands.SetComputeIntParam(_writeShader, "_WriteRangeCount", clears);
                    clearCommands.SetComputeBufferParam(_writeShader, _clearRangesKernel, "_ClearRanges", _rangeDescriptors);
                    clearCommands.SetComputeBufferParam(_writeShader, _clearRangesKernel, "_WriteBounds", population.BoundsBuffer);
                    clearCommands.DispatchCompute(_writeShader, _clearRangesKernel,
                        (population.LargestPendingClear + 63) / 64, clears, 1);
                    Graphics.ExecuteCommandBuffer(clearCommands);
                }
                finally
                {
                    CommandBufferPool.Release(clearCommands);
                }
            }
            population.ClearPendingClears();
            population.ClusterDirty = true;
            if (!population.RebuildPending) return;
            int moves = population.PlanRebuild(this, out int largest);
            int oldCapacity = population.DetachStorage(out GraphicsBuffer oldData, out GraphicsBuffer oldBounds);
            CommandBuffer cmd = CommandBufferPool.Get("Looga Instances.Rebuild Ranges");
            try
            {
                if (population.Capacity == 0) return;
                EnsureGroupWhenNeeded();
                population.CreateStorage();
                cmd.SetComputeIntParam(_writeShader, "_WriteCapacity", population.Capacity);
                cmd.SetComputeBufferParam(_writeShader, _initializeKernel, "_WriteBounds", population.BoundsBuffer);
                cmd.SetComputeBufferParam(_writeShader, _initializeKernel, "_WriteSpatialOrder", population.SpatialOrder);
                cmd.DispatchCompute(_writeShader, _initializeKernel, (population.Capacity + 63) / 64, 1, 1);
                if (moves > 0 && oldData != null)
                {
                    UploadDescriptors(cmd, moves * 4);
                    cmd.SetComputeIntParam(_writeShader, "_WriteRangeCount", moves);
                    cmd.SetComputeIntParam(_writeShader, "_RelayoutCapacity", oldCapacity);
                    cmd.SetComputeIntParam(_writeShader, "_RelayoutArrays", population.MatrixArrayCount);
                    cmd.SetComputeBufferParam(_writeShader, _moveRangesKernel, "_MoveRanges", _rangeDescriptors);
                    cmd.SetComputeBufferParam(_writeShader, _moveRangesKernel, "_RelayoutData", oldData);
                    cmd.SetComputeBufferParam(_writeShader, _moveRangesKernel, "_RelayoutBounds", oldBounds);
                    cmd.SetComputeBufferParam(_writeShader, _moveRangesKernel, "_WriteData", population.Buffer);
                    cmd.SetComputeBufferParam(_writeShader, _moveRangesKernel, "_WriteBounds", population.BoundsBuffer);
                    cmd.DispatchCompute(_writeShader, _moveRangesKernel, (largest + 63) / 64, moves, 1);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
                // Unity defers the native release until submitted GPU work no longer uses the buffers.
                oldData?.Dispose();
                oldBounds?.Dispose();
            }
        }

        private void EnsureDescriptorCapacity(int words)
        {
            if (_rangeDescriptorData.Length >= words) return;
            Array.Resize(ref _rangeDescriptorData, Mathf.NextPowerOfTwo(Mathf.Max(256, words)));
        }

        private void UploadDescriptors(CommandBuffer cmd, int words)
        {
            // Every descriptor uses four words: write, clear and move ranges.
            int elements = (words + 3) / 4;
            if (_rangeDescriptors == null || _rangeDescriptors.count < elements)
            {
                // Earlier command buffers already ran, so the old buffer has no pending use on the CPU side.
                _rangeDescriptors?.Dispose();
                _rangeDescriptors = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(Mathf.Max(64, elements)), 16);
            }
            cmd.SetBufferData(_rangeDescriptors, _rangeDescriptorData, 0, 0, elements * 4);
        }

        private void ChangeRangeCells(int prototype, Bounds pivots, int count, int sign)
        {
            if (_worldCellSize != InstanceWorldCells.GetCellSize(_worldContentKind)) return;
            InstanceWorldCellId min = InstanceWorldCells.GetCell(pivots.min, _worldContentKind);
            InstanceWorldCellId max = InstanceWorldCells.GetCell(pivots.max, _worldContentKind);
            int cells = (max.X - min.X + 1) * (max.Z - min.Z + 1);
            int share = count / cells;
            int extra = count % cells;
            int index = 0;
            InstancePrototypeId id = _prototypeRegistrations[prototype].Id;
            for (int z = min.Z; z <= max.Z; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    int value = share + (index++ < extra ? 1 : 0);
                    if (value == 0) continue;
                    var key = new InstanceWorldCells.CellPrototypeKey(new InstanceWorldCellId(_worldContentKind, x, z), id);
                    _worldCellCounts.TryGetValue(key, out int current);
                    current = checked(current + sign * value);
                    if (current == 0)
                    {
                        _worldCellCounts.Remove(key);
                    }
                    else
                    {
                        _worldCellCounts[key] = current;
                    }
                }
            }
        }

        private void AddAllRangeCells(int prototype, Population population)
        {
            foreach (Population.RangeRecord record in population.Ranges.Values)
            {
                ChangeRangeCells(prototype, record.Pivots, record.Count, 1);
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        /// <summary>Test access to storage buffers. The buffers change when capacity changes.</summary>
        internal bool TryGetStorage(int prototype, out GraphicsBuffer data, out GraphicsBuffer bounds,
            out int capacity, out int highWater)
        {
            Population population = _populations[prototype];
            data = population.Buffer;
            bounds = population.BoundsBuffer;
            capacity = population.Capacity;
            highWater = population.HighWater;
            return data != null;
        }

        /// <summary>Test access to the storage rebuild counts of one prototype.</summary>
        internal void GetStorageRebuilds(int prototype, out int resizes, out int compactions)
        {
            Population population = _populations[prototype];
            resizes = population.Resizes;
            compactions = population.Compactions;
        }

        /// <summary>
        /// Storage summary for diagnostics: resident, high-water mark, capacity, views, bytes and rebuilds per prototype.
        /// </summary>
        internal string DescribeStorage()
        {
            var text = new System.Text.StringBuilder();
            foreach (Population population in _populations)
            {
                text.Append($"[{population.ResidentCount}/{population.HighWater}/{population.Capacity} " +
                    $"views={population.Views.Count} {population.Bytes / 1048576} MB " +
                    $"rebuilds={population.Resizes}/{population.Compactions}] ");
            }
            return text.ToString();
        }

        /// <summary>Test access to the current first slot of a live range. Compaction can change it.</summary>
        internal int GetRangeStart(InstanceRange range)
        {
            return TryResolve(range, out _, out Population.RangeRecord record) ? record.Start : -1;
        }

        private sealed partial class Population
        {
            internal sealed class RangeRecord
            {
                internal int Id;
                internal int Start;
                internal int Count;
                internal uint Generation;
                internal Bounds Pivots;
                // Only written ranges hold data that a rebuild must copy.
                internal bool Written;
            }

            // Compaction runs when free slots below the high-water mark exceed a quarter of the live slots.
            private const int CompactionMinimumSlots = 4096;
            private static readonly Comparison<RangeRecord> ByStart = (a, b) => a.Start.CompareTo(b.Start);

            private readonly bool _gpuResident;
            internal readonly Dictionary<int, RangeRecord> Ranges = new();
            private readonly List<RangeRecord> _orderedRanges = new();
            // Free slot ranges sorted by first slot. X is the first slot and Y is the count.
            private readonly List<Vector2Int> _freeRanges = new();
            private readonly List<Vector2Int> _pendingClears = new();
            private uint _rangeGeneration;
            private int _nextRangeId;
            private int _storageCapacity;
            private GraphicsBuffer _partTransforms;

            private bool NeedsCompaction =>
                HighWater - ResidentCount > Math.Max(CompactionMinimumSlots, ResidentCount / 4);
            internal bool RebuildPending => _resize || NeedsCompaction;
            internal bool GpuCommitPending => _gpuResident && (_resize || _pendingClears.Count > 0 || NeedsCompaction);
            internal int MatrixArrayCount => DataParts * MatrixArrays;
            internal int LargestPendingClear { get; private set; }
            // Storage rebuilds since creation. Each rebuild allocates new buffers and copies every written range.
            internal int Resizes { get; private set; }
            internal int Compactions { get; private set; }

            internal RangeRecord AddRange(int count, Bounds pivots)
            {
                int start = AllocateSlots(count);
                // Zero is reserved so that cleared LOD history never matches a live generation.
                if (++_rangeGeneration == 0)
                {
                    _rangeGeneration = 1;
                }
                var record = new RangeRecord
                {
                    Id = _nextRangeId++, Start = start, Count = count, Generation = _rangeGeneration, Pivots = pivots
                };
                Ranges.Add(record.Id, record);
                QueueClear(start, count);
                return record;
            }

            internal bool TryGetRange(InstanceRange range, out RangeRecord record)
            {
                return Ranges.TryGetValue(range.Id, out record) && record.Count == range.Count &&
                    record.Generation == range.Generation;
            }

            internal void RemoveRange(RangeRecord record)
            {
                Ranges.Remove(record.Id);
                QueueClear(record.Start, record.Count);
                FreeSlots(record.Start, record.Count);
                ClusterDirty = true;
            }

            private int AllocateSlots(int count)
            {
                for (int i = 0; i < _freeRanges.Count; i++)
                {
                    Vector2Int free = _freeRanges[i];
                    if (free.y < count) continue;
                    if (free.y == count)
                    {
                        _freeRanges.RemoveAt(i);
                    }
                    else
                    {
                        _freeRanges[i] = new Vector2Int(free.x + count, free.y - count);
                    }
                    return free.x;
                }
                EnsureCapacity(HighWater + count);
                int start = HighWater;
                HighWater += count;
                return start;
            }

            private void FreeSlots(int start, int count)
            {
                int index = 0;
                while (index < _freeRanges.Count && _freeRanges[index].x < start)
                {
                    index++;
                }
                _freeRanges.Insert(index, new Vector2Int(start, count));
                if (index + 1 < _freeRanges.Count && start + count == _freeRanges[index + 1].x)
                {
                    _freeRanges[index] = new Vector2Int(start, count + _freeRanges[index + 1].y);
                    _freeRanges.RemoveAt(index + 1);
                }
                if (index > 0 && _freeRanges[index - 1].x + _freeRanges[index - 1].y == start)
                {
                    _freeRanges[index - 1] = new Vector2Int(_freeRanges[index - 1].x,
                        _freeRanges[index - 1].y + _freeRanges[index].y);
                    _freeRanges.RemoveAt(index);
                }
                // A free range at the end lowers the high-water mark, so culling reads fewer slots.
                int last = _freeRanges.Count - 1;
                if (last >= 0 && _freeRanges[last].x + _freeRanges[last].y == HighWater)
                {
                    HighWater = _freeRanges[last].x;
                    _freeRanges.RemoveAt(last);
                }
            }

            private void QueueClear(int start, int count)
            {
                _pendingClears.Add(new Vector2Int(start, count));
                LargestPendingClear = Mathf.Max(LargestPendingClear, count);
            }

            internal int CopyPendingClears(InstanceRenderer owner)
            {
                owner.EnsureDescriptorCapacity(_pendingClears.Count * 4);
                for (int i = 0; i < _pendingClears.Count; i++)
                {
                    owner._rangeDescriptorData[i * 4] = (uint)_pendingClears[i].x;
                    owner._rangeDescriptorData[i * 4 + 1] = (uint)_pendingClears[i].y;
                    owner._rangeDescriptorData[i * 4 + 2] = 0;
                    owner._rangeDescriptorData[i * 4 + 3] = 0;
                }
                return _pendingClears.Count;
            }

            internal void ClearPendingClears()
            {
                _pendingClears.Clear();
                LargestPendingClear = 0;
            }

            // Assign slots in the rebuilt storage and write one move descriptor for each written range.
            // Compaction packs all ranges in slot order. A capacity change keeps each range at its slot.
            internal int PlanRebuild(InstanceRenderer owner, out int largest)
            {
                bool compact = NeedsCompaction;
                if (compact)
                {
                    Compactions++;
                }
                else
                {
                    Resizes++;
                }
                _orderedRanges.Clear();
                _orderedRanges.AddRange(Ranges.Values);
                _orderedRanges.Sort(ByStart);
                int moves = 0;
                int next = 0;
                largest = 0;
                foreach (RangeRecord record in _orderedRanges)
                {
                    int destination = compact ? next : record.Start;
                    if (record.Written && _storageCapacity > 0)
                    {
                        owner.EnsureDescriptorCapacity((moves + 1) * 4);
                        owner._rangeDescriptorData[moves * 4] = (uint)record.Start;
                        owner._rangeDescriptorData[moves * 4 + 1] = (uint)destination;
                        owner._rangeDescriptorData[moves * 4 + 2] = (uint)record.Count;
                        owner._rangeDescriptorData[moves * 4 + 3] = 0;
                        largest = Mathf.Max(largest, record.Count);
                        moves++;
                    }
                    record.Start = destination;
                    next = destination + record.Count;
                }
                if (compact)
                {
                    _freeRanges.Clear();
                    HighWater = next;
                    Capacity = Ranges.Count == 0 ? 0 : Mathf.NextPowerOfTwo(Mathf.Max(64, HighWater));
                }
                return moves;
            }

            // Shrink only after the high-water mark falls to a quarter. This avoids rebuilds at a size boundary.
            private void TrimGpuStorage()
            {
                if (HighWater == 0 && Ranges.Count == 0)
                {
                    _freeRanges.Clear();
                    if (Capacity == 0) return;
                    Capacity = 0;
                    _resize = true;
                    return;
                }
                int capacity = Mathf.NextPowerOfTwo(Mathf.Max(64, HighWater));
                if (capacity * 4 > Capacity) return;
                Capacity = capacity;
                _resize = true;
            }

            // Remove batches and views. The caller keeps the old source buffers for the move pass.
            internal int DetachStorage(out GraphicsBuffer data, out GraphicsBuffer bounds)
            {
                data = Buffer;
                bounds = BoundsBuffer;
                int capacity = _storageCapacity;
                Buffer = null;
                BoundsBuffer = null;
                ReleaseBuffers();
                if (data != null)
                {
                    foreach (BatchID batch in Batches)
                    {
                        _group.RemoveBatch(batch);
                    }
                }
                _storageCapacity = 0;
                _resize = false;
                return capacity;
            }

            internal void BindWriteParameters(CommandBuffer cmd, ComputeShader shader, int kernel)
            {
                cmd.SetComputeIntParam(shader, "_WriteDataParts", DataParts);
                cmd.SetComputeVectorParam(shader, "_WriteBoundsCenter", Prototype.Bounds.center);
                cmd.SetComputeVectorParam(shader, "_WriteBoundsExtents", Prototype.Bounds.extents);
                Vector3 lodCenter = Prototype.LodCenter;
                cmd.SetComputeVectorParam(shader, "_WriteLodCenter",
                    new Vector4(lodCenter.x, lodCenter.y, lodCenter.z, Prototype.LodSize));
                cmd.SetComputeBufferParam(shader, kernel, "_WriteParts", _partTransforms);
            }

            private void CreatePartTransforms()
            {
                var rows = new Vector4[DataParts * 6];
                for (int data = 0; data < DataParts; data++)
                {
                    Matrix4x4 local = Prototype.Parts[_dataSource[data]].Local;
                    Matrix4x4 inverse = local.inverse;
                    for (int row = 0; row < 3; row++)
                    {
                        rows[data * 6 + row] = local.GetRow(row);
                        rows[data * 6 + 3 + row] = inverse.GetRow(row);
                    }
                }
                _partTransforms = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rows.Length, 16);
                _partTransforms.SetData(rows);
            }
        }
    }
}

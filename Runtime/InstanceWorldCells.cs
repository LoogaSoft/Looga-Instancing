using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    public enum InstanceWorldContentKind
    {
        Generic,
        TerrainSurface,
        Tree,
        Detail,
        Grass,
        SceneObject,
        PaintedInstance,
        RuntimeObject,
        Impostor,
        Hlod
    }

    public enum InstanceCellResidencyState
    {
        Unloaded,
        Loading,
        Resident,
        Evicting,
        Failed
    }

    public readonly struct InstanceWorldCellId : IEquatable<InstanceWorldCellId>
    {
        public InstanceWorldContentKind Kind { get; }
        public int X { get; }
        public int Z { get; }
        public InstanceWorldCellId(InstanceWorldContentKind kind, int x, int z) { Kind = kind; X = x; Z = z; }
        public bool Equals(InstanceWorldCellId other) => Kind == other.Kind && X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is InstanceWorldCellId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)Kind, X, Z);
        public override string ToString() => Kind + ":" + X + ":" + Z;
    }

    public readonly struct InstanceWorldCellRecord
    {
        public InstanceWorldCellId Id { get; }
        public Bounds Bounds { get; }
        public InstanceCellResidencyState Residency { get; }
        public int SourceCount { get; }
        public int PrototypeCount { get; }
        public int InstanceCount { get; }
        public bool IsDirty { get; }
        public ulong Revision { get; }
        internal InstanceWorldCellRecord(InstanceWorldCellId id, Bounds bounds, InstanceCellResidencyState residency,
            int sources, int prototypes, int instances, bool dirty, ulong revision)
        {
            Id = id; Bounds = bounds; Residency = residency; SourceCount = sources; PrototypeCount = prototypes;
            InstanceCount = instances; IsDirty = dirty; Revision = revision;
        }
    }

    /// <summary>One source contribution used by the visibility hierarchy.</summary>
    public readonly struct InstanceWorldSourceCellRecord
    {
        public InstanceWorldCellId Cell { get; }
        public InstancePrototypeId Prototype { get; }
        public int InstanceCount { get; }
        public InstanceCellResidencyState Residency { get; }

        internal InstanceWorldSourceCellRecord(InstanceWorldCellId cell, InstancePrototypeId prototype,
            int count, InstanceCellResidencyState residency)
        {
            Cell = cell;
            Prototype = prototype;
            InstanceCount = count;
            Residency = residency;
        }
    }

    public readonly struct InstanceWorldCellDiagnostics
    {
        public int Sources { get; }
        public int Cells { get; }
        public int DirtyCells { get; }
        public int ResidentCells { get; }
        public int LoadingCells { get; }
        public int FailedCells { get; }
        public long Instances { get; }
        public ulong Revision { get; }
        internal InstanceWorldCellDiagnostics(int sources, int cells, int dirty, int resident, int loading,
            int failed, long instances, ulong revision)
        {
            Sources = sources; Cells = cells; DirtyCells = dirty; ResidentCells = resident;
            LoadingCells = loading; FailedCells = failed; Instances = instances; Revision = revision;
        }
    }

    /// <summary>Atomic, aggregated source update. Dispose without Commit to leave the world registry unchanged.</summary>
    public sealed class InstanceWorldCellUpdate : IDisposable
    {
        internal readonly string SourceId;
        internal readonly InstanceWorldContentKind Kind;
        internal readonly Dictionary<InstanceWorldCells.CellPrototypeKey, int> Counts = new();
        private bool _finished;
        internal InstanceWorldCellUpdate(string sourceId, InstanceWorldContentKind kind) { SourceId = sourceId; Kind = kind; }

        public void Add(Vector3 worldPosition, InstancePrototypeId prototype, int count = 1)
        {
            if (_finished) throw new ObjectDisposedException(nameof(InstanceWorldCellUpdate));
            if (!prototype.IsValid || count < 1 || !float.IsFinite(worldPosition.x) || !float.IsFinite(worldPosition.y) || !float.IsFinite(worldPosition.z))
                throw new ArgumentException("A finite position, valid prototype and positive count are required.");
            var key = new InstanceWorldCells.CellPrototypeKey(InstanceWorldCells.GetCell(worldPosition, Kind), prototype);
            Counts.TryGetValue(key, out int old);
            Counts[key] = checked(old + count);
        }

        public void Commit(InstanceCellResidencyState residency = InstanceCellResidencyState.Resident)
        {
            if (_finished) throw new ObjectDisposedException(nameof(InstanceWorldCellUpdate));
            _finished = true;
            InstanceWorldCells.Commit(this, residency);
        }

        public void Dispose() => _finished = true;
    }

    /// <summary>Shared horizontal spatial index for terrain, vegetation, ordinary objects and future HLOD data.</summary>
    public static class InstanceWorldCells
    {
        internal readonly struct CellPrototypeKey : IEquatable<CellPrototypeKey>
        {
            internal readonly InstanceWorldCellId Cell;
            internal readonly InstancePrototypeId Prototype;
            internal CellPrototypeKey(InstanceWorldCellId cell, InstancePrototypeId prototype) { Cell = cell; Prototype = prototype; }
            public bool Equals(CellPrototypeKey other) => Cell.Equals(other.Cell) && Prototype.Equals(other.Prototype);
            public override bool Equals(object obj) => obj is CellPrototypeKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Cell, Prototype);
        }

        private sealed class SourceEntry
        {
            internal InstanceWorldContentKind Kind;
            internal InstanceCellResidencyState Residency;
            internal Dictionary<CellPrototypeKey, int> Counts;
        }

        private sealed class CellEntry
        {
            internal readonly Dictionary<string, SourceCell> Sources = new(StringComparer.Ordinal);
            internal bool Dirty;
            internal ulong Revision;
        }

        private sealed class SourceCell
        {
            internal InstanceCellResidencyState Residency;
            internal readonly Dictionary<InstancePrototypeId, int> Counts = new();
        }

        private static readonly Dictionary<string, SourceEntry> Sources = new(StringComparer.Ordinal);
        private static readonly Dictionary<InstanceWorldCellId, CellEntry> Cells = new();
        private static readonly float[] Sizes = { 128, 256, 64, 32, 16, 128, 64, 128, 256, 512 };
        private static ulong _revision;

        public static float GetCellSize(InstanceWorldContentKind kind)
        {
            int index = (int)kind;
            if ((uint)index >= (uint)Sizes.Length) throw new ArgumentOutOfRangeException(nameof(kind));
            return Sizes[index];
        }

        /// <summary>Change a content layout before registering sources of that kind.</summary>
        public static void SetCellSize(InstanceWorldContentKind kind, float meters)
        {
            if (!float.IsFinite(meters) || meters < 1) throw new ArgumentOutOfRangeException(nameof(meters));
            foreach (SourceEntry source in Sources.Values)
                if (source.Kind == kind) throw new InvalidOperationException("Remove active sources before changing their cell size.");
            Sizes[(int)kind] = meters;
        }

        public static InstanceWorldCellId GetCell(Vector3 worldPosition, InstanceWorldContentKind kind)
        {
            float size = GetCellSize(kind);
            return new InstanceWorldCellId(kind, Mathf.FloorToInt(worldPosition.x / size), Mathf.FloorToInt(worldPosition.z / size));
        }

        public static Bounds GetBounds(InstanceWorldCellId id)
        {
            float size = GetCellSize(id.Kind);
            return new Bounds(new Vector3((id.X + 0.5f) * size, 0, (id.Z + 0.5f) * size), new Vector3(size, 1000000, size));
        }

        public static InstanceWorldCellUpdate BeginUpdate(string sourceId, InstanceWorldContentKind kind)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A stable source ID is required.", nameof(sourceId));
            GetCellSize(kind);
            return new InstanceWorldCellUpdate(sourceId, kind);
        }

        public static bool RemoveSource(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || !Sources.TryGetValue(sourceId, out SourceEntry old)) return false;
            Sources.Remove(sourceId);
            var touched = new HashSet<InstanceWorldCellId>();
            foreach (CellPrototypeKey key in old.Counts.Keys) touched.Add(key.Cell);
            foreach (InstanceWorldCellId id in touched)
            {
                if (Cells.TryGetValue(id, out CellEntry cell))
                {
                    cell.Sources.Remove(sourceId);
                    Mark(cell);
                }
            }
            return true;
        }

        /// <summary>Mark existing cells intersecting a world-space region. Broad requests remain bounded by resident metadata.</summary>
        public static int MarkDirty(Bounds region, InstanceWorldContentKind? kind = null)
        {
            if (!Finite(region.center) || !Finite(region.size) || region.size.x < 0 || region.size.y < 0 || region.size.z < 0)
                throw new ArgumentException("Dirty bounds must be finite and nonnegative.", nameof(region));
            int count = 0;
            foreach (var pair in Cells)
            {
                if (kind.HasValue && pair.Key.Kind != kind.Value || !GetBounds(pair.Key).Intersects(region)) continue;
                Mark(pair.Value);
                count++;
            }
            return count;
        }

        public static int CopyDirty(List<InstanceWorldCellRecord> results, int maximum = int.MaxValue)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
            int start = results.Count;
            foreach (var pair in Cells)
            {
                if (!pair.Value.Dirty) continue;
                results.Add(Record(pair.Key, pair.Value));
                if (results.Count - start == maximum) break;
            }
            return results.Count - start;
        }

        /// <summary>Copy one source layout without exposing mutable registry storage.</summary>
        public static int CopySource(string sourceId, List<InstanceWorldSourceCellRecord> results)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A stable source ID is required.", nameof(sourceId));
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (!Sources.TryGetValue(sourceId, out SourceEntry source)) return 0;
            int start = results.Count;
            foreach (var pair in source.Counts)
            {
                results.Add(new InstanceWorldSourceCellRecord(pair.Key.Cell, pair.Key.Prototype,
                    pair.Value, source.Residency));
            }
            return results.Count - start;
        }

        public static bool TryGetCell(InstanceWorldCellId id, out InstanceWorldCellRecord record)
        {
            if (Cells.TryGetValue(id, out CellEntry cell))
            {
                record = Record(id, cell);
                return true;
            }
            record = default;
            return false;
        }

        public static bool AcknowledgeDirty(InstanceWorldCellId id, ulong revision)
        {
            if (!Cells.TryGetValue(id, out CellEntry cell) || !cell.Dirty || cell.Revision != revision) return false;
            cell.Dirty = false;
            if (cell.Sources.Count == 0) Cells.Remove(id);
            return true;
        }

        public static InstanceWorldCellDiagnostics GetDiagnostics()
        {
            int dirty = 0, resident = 0, loading = 0, failed = 0;
            long instances = 0;
            foreach (var pair in Cells)
            {
                InstanceWorldCellRecord record = Record(pair.Key, pair.Value);
                if (record.IsDirty) dirty++;
                if (record.Residency == InstanceCellResidencyState.Resident) resident++;
                else if (record.Residency == InstanceCellResidencyState.Loading) loading++;
                else if (record.Residency == InstanceCellResidencyState.Failed) failed++;
                instances += record.InstanceCount;
            }
            return new InstanceWorldCellDiagnostics(Sources.Count, Cells.Count, dirty, resident, loading, failed, instances, _revision);
        }

        internal static void Commit(InstanceWorldCellUpdate update, InstanceCellResidencyState residency)
        {
            if (residency == InstanceCellResidencyState.Unloaded)
            {
                RemoveSource(update.SourceId);
                return;
            }
            Sources.TryGetValue(update.SourceId, out SourceEntry old);
            var touched = new HashSet<InstanceWorldCellId>();
            if (old != null)
            {
                foreach (CellPrototypeKey key in old.Counts.Keys) touched.Add(key.Cell);
                foreach (InstanceWorldCellId id in touched)
                    if (Cells.TryGetValue(id, out CellEntry cell)) cell.Sources.Remove(update.SourceId);
            }
            var next = new SourceEntry { Kind = update.Kind, Residency = residency,
                Counts = new Dictionary<CellPrototypeKey, int>(update.Counts) };
            Sources[update.SourceId] = next;
            foreach (var pair in update.Counts)
            {
                touched.Add(pair.Key.Cell);
                if (!Cells.TryGetValue(pair.Key.Cell, out CellEntry cell))
                {
                    cell = new CellEntry();
                    Cells.Add(pair.Key.Cell, cell);
                }
                if (!cell.Sources.TryGetValue(update.SourceId, out SourceCell source))
                {
                    source = new SourceCell { Residency = residency };
                    cell.Sources.Add(update.SourceId, source);
                }
                source.Counts[pair.Key.Prototype] = pair.Value;
            }
            foreach (InstanceWorldCellId id in touched)
            {
                if (!Cells.TryGetValue(id, out CellEntry cell))
                {
                    cell = new CellEntry();
                    Cells.Add(id, cell);
                }
                Mark(cell);
            }
        }

        private static void Mark(CellEntry cell)
        {
            cell.Dirty = true;
            cell.Revision = ++_revision;
        }

        private static InstanceWorldCellRecord Record(InstanceWorldCellId id, CellEntry cell)
        {
            int instances = 0;
            var prototypes = new HashSet<InstancePrototypeId>();
            InstanceCellResidencyState state = cell.Sources.Count == 0 ? InstanceCellResidencyState.Unloaded : InstanceCellResidencyState.Resident;
            foreach (SourceCell source in cell.Sources.Values)
            {
                if (Priority(source.Residency) > Priority(state)) state = source.Residency;
                foreach (var count in source.Counts)
                {
                    prototypes.Add(count.Key);
                    instances = checked(instances + count.Value);
                }
            }
            return new InstanceWorldCellRecord(id, GetBounds(id), state, cell.Sources.Count, prototypes.Count,
                instances, cell.Dirty, cell.Revision);
        }

        private static int Priority(InstanceCellResidencyState state) => state switch
        {
            InstanceCellResidencyState.Failed => 5,
            InstanceCellResidencyState.Evicting => 4,
            InstanceCellResidencyState.Loading => 3,
            InstanceCellResidencyState.Resident => 2,
            _ => 1
        };

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            Sources.Clear();
            Cells.Clear();
            _revision = 0;
            Sizes[0] = 128; Sizes[1] = 256; Sizes[2] = 64; Sizes[3] = 32; Sizes[4] = 16;
            Sizes[5] = 128; Sizes[6] = 64; Sizes[7] = 128; Sizes[8] = 256; Sizes[9] = 512;
        }
    }
}

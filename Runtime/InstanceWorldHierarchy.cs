using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Cost limits for region, cell, cluster, instance, and Hi-Z work.</summary>
    [Serializable]
    public struct InstanceHierarchySettings
    {
        [Range(2, 32)] public int RegionCells;
        [Min(1)] public int SharedSelectionInstances;
        [Min(1)] public int ClusterSelectionInstances;
        [Min(1)] public int OcclusionInstances;

        public static InstanceHierarchySettings Default => new InstanceHierarchySettings
        {
            RegionCells = 8,
            SharedSelectionInstances = 256,
            ClusterSelectionInstances = 2048,
            OcclusionInstances = 8192
        };

        internal void Validate()
        {
            if (RegionCells < 2 || RegionCells > 32 || SharedSelectionInstances < 1 ||
                ClusterSelectionInstances < SharedSelectionInstances ||
                OcclusionInstances < ClusterSelectionInstances)
            {
                throw new ArgumentOutOfRangeException(nameof(InstanceHierarchySettings));
            }
        }
    }

    /// <summary>One camera decision. It contains no per-frame managed storage.</summary>
    public readonly struct InstanceHierarchyDecision
    {
        public bool Visible { get; }
        public InstanceVisibilityMode VisibilityMode { get; }
        public bool UseOcclusion { get; }
        public int RegionsTested { get; }
        public int RegionsVisible { get; }
        public int CellsTested { get; }
        public int CellsVisible { get; }
        public int CandidateInstances { get; }

        internal InstanceHierarchyDecision(bool visible, InstanceVisibilityMode mode, bool occlusion,
            int regionsTested, int regionsVisible, int cellsTested, int cellsVisible, int instances)
        {
            Visible = visible;
            VisibilityMode = mode;
            UseOcclusion = occlusion;
            RegionsTested = regionsTested;
            RegionsVisible = regionsVisible;
            CellsTested = cellsTested;
            CellsVisible = cellsVisible;
            CandidateInstances = instances;
        }
    }

    /// <summary>Immutable region and cell hierarchy for one renderer source revision.</summary>
    public sealed class InstanceWorldHierarchy
    {
        private readonly Region[] _regions;
        private readonly Cell[] _cells;
        private readonly InstanceHierarchySettings _settings;

        private readonly struct Cell
        {
            internal readonly Bounds Bounds;
            internal readonly int Count;
            internal Cell(Bounds bounds, int count) { Bounds = bounds; Count = count; }
        }

        private readonly struct Region
        {
            internal readonly Bounds Bounds;
            internal readonly int Begin;
            internal readonly int Count;
            internal readonly int Instances;
            internal Region(Bounds bounds, int begin, int count, int instances)
            {
                Bounds = bounds; Begin = begin; Count = count; Instances = instances;
            }
        }

        private InstanceWorldHierarchy(Region[] regions, Cell[] cells, InstanceHierarchySettings settings)
        {
            _regions = regions;
            _cells = cells;
            _settings = settings;
        }

        public int RegionCount => _regions.Length;
        public int CellCount => _cells.Length;

        /// <summary>Build after a source upload completes. Compatible prototypes remain super-batched by the renderer.</summary>
        public static InstanceWorldHierarchy Build(string sourceId, InstanceHierarchySettings settings)
        {
            settings.Validate();
            var source = new List<InstanceWorldSourceCellRecord>();
            InstanceWorldCells.CopySource(sourceId, source);
            var cells = new Dictionary<InstanceWorldCellId, int>();
            foreach (var item in source)
            {
                if (item.Residency != InstanceCellResidencyState.Resident) continue;
                cells.TryGetValue(item.Cell, out int count);
                cells[item.Cell] = checked(count + item.InstanceCount);
            }
            var grouped = new Dictionary<(InstanceWorldContentKind kind, int x, int z), List<(InstanceWorldCellId id, int count)>>();
            foreach (var pair in cells)
            {
                var key = (pair.Key.Kind, FloorDiv(pair.Key.X, settings.RegionCells),
                    FloorDiv(pair.Key.Z, settings.RegionCells));
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = new List<(InstanceWorldCellId, int)>();
                    grouped.Add(key, list);
                }
                list.Add((pair.Key, pair.Value));
            }
            var outputCells = new List<Cell>(cells.Count);
            var outputRegions = new List<Region>(grouped.Count);
            foreach (var group in grouped)
            {
                int begin = outputCells.Count;
                int instances = 0;
                Bounds bounds = default;
                bool hasBounds = false;
                foreach (var item in group.Value)
                {
                    Bounds cellBounds = InstanceWorldCells.GetBounds(item.id);
                    outputCells.Add(new Cell(cellBounds, item.count));
                    instances = checked(instances + item.count);
                    if (!hasBounds) { bounds = cellBounds; hasBounds = true; }
                    else bounds.Encapsulate(cellBounds);
                }
                outputRegions.Add(new Region(bounds, begin, outputCells.Count - begin, instances));
            }
            return new InstanceWorldHierarchy(outputRegions.ToArray(), outputCells.ToArray(), settings);
        }

        /// <summary>Reject coarse bounds first and select the cheapest safe fine path.</summary>
        public InstanceHierarchyDecision Evaluate(Vector4[] planes, int planeCount, Vector3 camera,
            float maximumDistance, bool occlusionAvailable, InstanceVisibilityMode requested)
        {
            return Evaluate(planes, planeCount, camera, maximumDistance, occlusionAvailable, requested, 0);
        }

        /// <summary>Include transformed instance bounds beyond placement positions in coarse visibility.</summary>
        public InstanceHierarchyDecision Evaluate(Vector4[] planes, int planeCount, Vector3 camera,
            float maximumDistance, bool occlusionAvailable, InstanceVisibilityMode requested,
            float boundsPadding)
        {
            int visibleRegions = 0, testedCells = 0, visibleCells = 0, candidates = 0;
            float maximumDistanceSquared = float.IsPositiveInfinity(maximumDistance) ? float.PositiveInfinity : maximumDistance * maximumDistance;
            foreach (Region region in _regions)
            {
                if (!Visible(region.Bounds, planes, planeCount, camera, maximumDistanceSquared, boundsPadding)) continue;
                visibleRegions++;
                for (int index = region.Begin; index < region.Begin + region.Count; index++)
                {
                    Cell cell = _cells[index];
                    testedCells++;
                    if (!Visible(cell.Bounds, planes, planeCount, camera, maximumDistanceSquared, boundsPadding)) continue;
                    visibleCells++;
                    candidates = checked(candidates + cell.Count);
                }
            }
            InstanceVisibilityMode mode = ChooseVisibility(candidates, requested);
            bool occlusion = occlusionAvailable && ShouldUseOcclusion(candidates);
            return new InstanceHierarchyDecision(candidates > 0, mode, occlusion, _regions.Length,
                visibleRegions, testedCells, visibleCells, candidates);
        }

        /// <summary>Select fine visibility work for one prototype population.</summary>
        public InstanceVisibilityMode ChooseVisibility(int candidates, InstanceVisibilityMode requested)
        {
            if (candidates < _settings.SharedSelectionInstances) return InstanceVisibilityMode.Direct;
            if (candidates < _settings.ClusterSelectionInstances) return InstanceVisibilityMode.Shared;
            return requested < InstanceVisibilityMode.Hierarchical ?
                InstanceVisibilityMode.CachedHierarchy : requested;
        }

        /// <summary>True when a population is large enough to amortize temporal Hi-Z.</summary>
        public bool ShouldUseOcclusion(int candidates) => candidates >= _settings.OcclusionInstances;

        private static bool Visible(Bounds bounds, Vector4[] planes, int count, Vector3 camera,
            float maximumDistanceSquared, float boundsPadding)
        {
            bounds.Expand(2 * Mathf.Max(0, boundsPadding));
            Vector3 nearest = bounds.ClosestPoint(camera);
            if ((nearest - camera).sqrMagnitude > maximumDistanceSquared) return false;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            for (int i = 0; i < count; i++)
            {
                Vector4 plane = planes[i];
                float radius = Mathf.Abs(plane.x) * extents.x + Mathf.Abs(plane.y) * extents.y + Mathf.Abs(plane.z) * extents.z;
                if (plane.x * center.x + plane.y * center.y + plane.z * center.z + plane.w + radius < 0) return false;
            }
            return true;
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
        }
    }
}

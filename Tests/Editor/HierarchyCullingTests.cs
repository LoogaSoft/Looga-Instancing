using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class HierarchyCullingTests
    {
        [SetUp] public void Setup() => InstanceWorldCells.Reset();
        [TearDown] public void Cleanup() => InstanceWorldCells.Reset();

        [Test]
        public void HierarchyAggregatesNegativeCellsAndSelectsCostAwarePaths()
        {
            var prototype = new InstancePrototypeId(Hash128.Compute("grass"));
            using (var update = InstanceWorldCells.BeginUpdate("source", InstanceWorldContentKind.Grass))
            {
                for (int i = 0; i < 9000; i++) update.Add(new Vector3(-1 - i % 32, 0, i % 16), prototype);
                update.Commit();
            }
            var hierarchy = InstanceWorldHierarchy.Build("source", InstanceHierarchySettings.Default);
            Assert.Greater(hierarchy.CellCount, 0);
            Assert.Greater(hierarchy.RegionCount, 0);
            var decision = hierarchy.Evaluate(System.Array.Empty<Vector4>(), 0, Vector3.zero, 1000, true,
                InstanceVisibilityMode.Direct);
            Assert.IsTrue(decision.Visible);
            Assert.AreEqual(9000, decision.CandidateInstances);
            Assert.AreEqual(InstanceVisibilityMode.CachedHierarchy, decision.VisibilityMode);
            Assert.IsTrue(decision.UseOcclusion);
        }

        [Test]
        public void SmallWorkloadBypassesHierarchyAndDistantRegions()
        {
            var prototype = new InstancePrototypeId(Hash128.Compute("rock"));
            using (var update = InstanceWorldCells.BeginUpdate("source", InstanceWorldContentKind.SceneObject))
            {
                update.Add(new Vector3(10, 0, 10), prototype, 20);
                update.Commit();
            }
            var hierarchy = InstanceWorldHierarchy.Build("source", InstanceHierarchySettings.Default);
            var near = hierarchy.Evaluate(System.Array.Empty<Vector4>(), 0, Vector3.zero, 100, true,
                InstanceVisibilityMode.CachedHierarchy);
            Assert.AreEqual(InstanceVisibilityMode.Direct, near.VisibilityMode);
            Assert.IsFalse(near.UseOcclusion);
            var far = hierarchy.Evaluate(System.Array.Empty<Vector4>(), 0, new Vector3(1000, 0, 1000), 10,
                false, InstanceVisibilityMode.CachedHierarchy);
            Assert.IsFalse(far.Visible);
        }

        [Test]
        public void SourceCopyPreservesPrototypeContributions()
        {
            var first = new InstancePrototypeId(Hash128.Compute("a"));
            var second = new InstancePrototypeId(Hash128.Compute("b"));
            using (var update = InstanceWorldCells.BeginUpdate("source", InstanceWorldContentKind.Tree))
            {
                update.Add(Vector3.zero, first, 3);
                update.Add(Vector3.zero, second, 4);
                update.Commit();
            }
            var records = new List<InstanceWorldSourceCellRecord>();
            Assert.AreEqual(2, InstanceWorldCells.CopySource("source", records));
            Assert.AreEqual(7, records[0].InstanceCount + records[1].InstanceCount);
        }
    }
}

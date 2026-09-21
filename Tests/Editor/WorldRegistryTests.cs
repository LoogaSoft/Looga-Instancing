using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class WorldRegistryTests
    {
        [SetUp]
        public void Setup()
        {
            InstancePrototypeCompiler.ClearCache();
            InstancePrototypeRegistry.Reset();
            InstanceWorldCells.Reset();
        }

        [TearDown]
        public void Cleanup()
        {
            InstancePrototypeCompiler.ClearCache();
            InstancePrototypeRegistry.Reset();
            InstanceWorldCells.Reset();
        }

        [Test]
        public void CompatibleSourcesShareAStablePrototypeIdentityAndReference()
        {
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            first.GetComponent<Renderer>().sharedMaterial = material;
            second.GetComponent<Renderer>().sharedMaterial = material;
            try
            {
                InstancePrototype a = InstancePrototype.FromPrefab(first);
                InstancePrototype b = InstancePrototype.FromPrefab(second);
                Assert.AreEqual(a.StableId, b.StableId);

                using InstancePrototypeRegistration one = InstancePrototypeRegistry.Acquire(a);
                using InstancePrototypeRegistration two = InstancePrototypeRegistry.Acquire(b);
                Assert.AreEqual(one.Id, two.Id);
                Assert.AreSame(one.Prototype, two.Prototype);
                InstancePrototypeRegistryDiagnostics diagnostics = InstancePrototypeRegistry.GetDiagnostics();
                Assert.AreEqual(1, diagnostics.ActivePrototypes);
                Assert.AreEqual(2, diagnostics.References);
                Assert.AreEqual(1, diagnostics.Hits);
                Assert.AreEqual(1, diagnostics.Misses);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void MaterialStructureChangesPrototypeIdentity()
        {
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material a = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            Material b = new Material(a);
            b.SetColor("_BaseColor", Color.red);
            first.GetComponent<Renderer>().sharedMaterial = a;
            second.GetComponent<Renderer>().sharedMaterial = b;
            try
            {
                Assert.AreNotEqual(InstancePrototype.FromPrefab(first).StableId, InstancePrototype.FromPrefab(second).StableId);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void CellsUseContentSpecificSizesAndAggregateSourcesAndPrototypes()
        {
            var prototype = new InstancePrototypeId(Hash128.Compute("tree"));
            Assert.AreEqual(new InstanceWorldCellId(InstanceWorldContentKind.Tree, -1, 1),
                InstanceWorldCells.GetCell(new Vector3(-0.1f, 0, 64), InstanceWorldContentKind.Tree));
            Assert.AreEqual(64, InstanceWorldCells.GetCellSize(InstanceWorldContentKind.Tree));
            Assert.AreEqual(16, InstanceWorldCells.GetCellSize(InstanceWorldContentKind.Grass));

            using (InstanceWorldCellUpdate update = InstanceWorldCells.BeginUpdate("source-a", InstanceWorldContentKind.Tree))
            {
                update.Add(new Vector3(2, 0, 3), prototype, 4);
                update.Commit();
            }
            using (InstanceWorldCellUpdate update = InstanceWorldCells.BeginUpdate("source-b", InstanceWorldContentKind.Tree))
            {
                update.Add(new Vector3(4, 0, 5), prototype, 6);
                update.Commit(InstanceCellResidencyState.Loading);
            }

            var id = new InstanceWorldCellId(InstanceWorldContentKind.Tree, 0, 0);
            Assert.IsTrue(InstanceWorldCells.TryGetCell(id, out InstanceWorldCellRecord cell));
            Assert.AreEqual(2, cell.SourceCount);
            Assert.AreEqual(1, cell.PrototypeCount);
            Assert.AreEqual(10, cell.InstanceCount);
            Assert.AreEqual(InstanceCellResidencyState.Loading, cell.Residency);
            InstanceWorldCellDiagnostics diagnostics = InstanceWorldCells.GetDiagnostics();
            Assert.AreEqual(2, diagnostics.Sources);
            Assert.AreEqual(1, diagnostics.Cells);
            Assert.AreEqual(10, diagnostics.Instances);
        }

        [Test]
        public void DirtyRevisionsRejectStaleAcknowledgementAndRetainRemovalTombstone()
        {
            var prototype = new InstancePrototypeId(Hash128.Compute("rock"));
            using (InstanceWorldCellUpdate update = InstanceWorldCells.BeginUpdate("source", InstanceWorldContentKind.SceneObject))
            {
                update.Add(Vector3.zero, prototype);
                update.Commit();
            }
            var id = new InstanceWorldCellId(InstanceWorldContentKind.SceneObject, 0, 0);
            Assert.IsTrue(InstanceWorldCells.TryGetCell(id, out InstanceWorldCellRecord first));
            Assert.AreEqual(1, InstanceWorldCells.MarkDirty(new Bounds(Vector3.zero, Vector3.one * 10)));
            Assert.IsFalse(InstanceWorldCells.AcknowledgeDirty(id, first.Revision));
            Assert.IsTrue(InstanceWorldCells.TryGetCell(id, out InstanceWorldCellRecord changed));
            Assert.IsTrue(InstanceWorldCells.AcknowledgeDirty(id, changed.Revision));

            Assert.IsTrue(InstanceWorldCells.RemoveSource("source"));
            Assert.IsTrue(InstanceWorldCells.TryGetCell(id, out InstanceWorldCellRecord removed));
            Assert.AreEqual(InstanceCellResidencyState.Unloaded, removed.Residency);
            Assert.AreEqual(0, removed.InstanceCount);
            Assert.IsTrue(removed.IsDirty);
            Assert.IsTrue(InstanceWorldCells.AcknowledgeDirty(id, removed.Revision));
            Assert.IsFalse(InstanceWorldCells.TryGetCell(id, out _));
        }

        [Test]
        public void RendererPublishesCellsOnlyAfterUploadsCompleteAndCleansUp()
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            InstanceRenderer renderer = null;
            try
            {
                renderer = new InstanceRenderer(worldSourceId: "test", worldContentKind: InstanceWorldContentKind.SceneObject);
                int prototype = renderer.Register(InstancePrototype.FromPrefab(root));
                renderer.Add(prototype, Matrix4x4.Translate(new Vector3(130, 0, -1)));
                renderer.Flush();
                var id = new InstanceWorldCellId(InstanceWorldContentKind.SceneObject, 1, -1);
                Assert.IsTrue(InstanceWorldCells.TryGetCell(id, out InstanceWorldCellRecord cell));
                Assert.AreEqual(1, cell.InstanceCount);
                Assert.AreEqual(1, renderer.RegisteredPrototypeCount);
                Assert.AreEqual(1, InstancePrototypeRegistry.GetDiagnostics().References);
                renderer.Dispose();
                renderer = null;
                Assert.AreEqual(0, InstancePrototypeRegistry.GetDiagnostics().ActivePrototypes);
                Assert.IsTrue(InstanceWorldCells.TryGetCell(id, out InstanceWorldCellRecord removed));
                Assert.AreEqual(InstanceCellResidencyState.Unloaded, removed.Residency);
            }
            finally
            {
                renderer?.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DirtyCopyCanBeBounded()
        {
            var prototype = new InstancePrototypeId(Hash128.Compute("detail"));
            using (InstanceWorldCellUpdate update = InstanceWorldCells.BeginUpdate("source", InstanceWorldContentKind.Detail))
            {
                update.Add(Vector3.zero, prototype);
                update.Add(new Vector3(33, 0, 0), prototype);
                update.Commit();
            }
            var results = new List<InstanceWorldCellRecord>();
            Assert.AreEqual(1, InstanceWorldCells.CopyDirty(results, 1));
            Assert.AreEqual(1, results.Count);
        }
    }
}

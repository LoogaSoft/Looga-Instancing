using LoogaSoft.Instancing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class InstanceContainerTests
    {
        private Scene _scene;
        private GameObject _source;
        private GameObject _host;
        private Material _material;
        private InstanceContainer _container;

        [SetUp]
        public void Setup()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(_source, _scene);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _source.GetComponent<Renderer>().sharedMaterial = _material;
            _host = new GameObject("Container test");
            SceneManager.MoveGameObjectToScene(_host, _scene);
            _container = _host.AddComponent<InstanceContainer>();
            _container.Configure(_source, new[] { At("a", 0), At("b", 10) });
        }

        [TearDown]
        public void Cleanup()
        {
            Undo.ClearUndo(_container);
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_material);
        }

        private static InstanceContainer.Placement At(string id, float x)
        {
            return new InstanceContainer.Placement { Id = id, Position = Vector3.right * x, Scale = Vector3.one };
        }

        [Test]
        public void MissingIdsMigrateWithoutChangingCallerDataAndSurviveReenable()
        {
            var input = new[] { At(null, 0), At(null, 10) };
            _container.SetPlacements(input);
            var before = _container.CopyPlacements();
            Assert.IsNull(input[0].Id);
            Assert.IsNotEmpty(before[0].Id);
            Assert.AreNotEqual(before[0].Id, before[1].Id);
            _container.enabled = false;
            Assert.IsNull(_container.Renderer);
            _container.enabled = true;
            Assert.AreEqual(before[0].Id, _container.CopyPlacements()[0].Id);
            Assert.AreEqual(before[1].Id, _container.CopyPlacements()[1].Id);
        }

        [Test]
        public void BulkEditsKeepUnaffectedHandlesAndInvalidateRemovedSlots()
        {
            var renderer = _container.Renderer;
            Assert.IsTrue(_container.TryGetHandle("a", out var a));
            Assert.IsTrue(_container.TryGetHandle("b", out var b));
            _container.ApplyChanges(new[] { At("b", 20), At("c", 40) }, new[] { "a" });
            Assert.AreSame(renderer, _container.Renderer);
            Assert.IsTrue(_container.TryGetHandle("b", out var updated));
            Assert.AreEqual(b, updated);
            Assert.IsFalse(renderer.Update(a, Matrix4x4.identity));
            Assert.IsFalse(_container.TryGetPlacement("a", out _));
            Assert.AreEqual(2, renderer.InstanceCount);
            Assert.IsTrue(_container.TryGetPlacement("c", out var c));
            Assert.AreEqual(40, c.Position.x);
        }

        [Test]
        public void ReorderingPreservesIdentityAndUploadsNothing()
        {
            var before = _container.CopyPlacements();
            _container.TryGetHandle("a", out var handle);
            long bytes = _container.Renderer.UploadedBytes;
            _container.SetPlacements(new[] { before[1], before[0] });
            _container.TryGetHandle("a", out var after);
            Assert.AreEqual(handle, after);
            Assert.AreEqual(bytes, _container.Renderer.UploadedBytes);
        }

        [Test]
        public void MovingParentKeepsRendererAndUpdatesWorldQueries()
        {
            var parent = new GameObject("Moving parent");
            SceneManager.MoveGameObjectToScene(parent, _scene);
            _host.transform.SetParent(parent.transform, false);
            var renderer = _container.Renderer;
            _container.TryGetHandle("b", out var handle);
            long before = renderer.UploadedBytes;
            parent.transform.SetPositionAndRotation(new Vector3(100, 0, 0), Quaternion.Euler(0, 90, 0));
            parent.transform.localScale = new Vector3(2, 1, -3);
            _container.Synchronize();
            Assert.AreSame(renderer, _container.Renderer);
            Assert.Greater(renderer.UploadedBytes, before);
            _container.TryGetHandle("b", out var after);
            Assert.AreEqual(handle, after);
            var ids = new List<string>();
            Assert.AreEqual(1, _container.QueryOrigins(new Bounds(new Vector3(100, 0, -20), Vector3.one), ids));
            CollectionAssert.AreEqual(new[] { "b" }, ids);
        }

        [Test]
        public void StationarySynchronizationAllocatesAndUploadsNothing()
        {
            _container.Synchronize();
            long bytes = _container.Renderer.UploadedBytes;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++)
            {
                _container.Synchronize();
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.AreEqual(0, delta);
            Assert.AreEqual(bytes, _container.Renderer.UploadedBytes);
        }

        [Test]
        public void InvalidBatchDoesNotPartiallyRemoveOrUpdatePlacements()
        {
            var invalid = At("c", 20);
            invalid.Scale = Vector3.zero;
            var renderer = _container.Renderer;
            long bytes = renderer.UploadedBytes;
            Assert.Throws<ArgumentException>(() => _container.ApplyChanges(new[] { At("b", 80), invalid }, new[] { "a" }));
            Assert.IsTrue(_container.TryGetPlacement("a", out _));
            Assert.IsTrue(_container.TryGetPlacement("b", out var b));
            Assert.AreEqual(10, b.Position.x);
            Assert.AreEqual(bytes, renderer.UploadedBytes);
            Assert.AreEqual(2, renderer.InstanceCount);
        }

        [Test]
        public void DuplicateConflictingAndUnknownIdsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => _container.AddPlacements(new[] { At("a", 40) }));
            Assert.Throws<ArgumentException>(() => _container.ApplyChanges(new[] { At(null, 40) }));
            Assert.Throws<ArgumentException>(() => _container.ApplyChanges(new[] { At("a", 40) }, new[] { "a" }));
            Assert.Throws<ArgumentException>(() => _container.ApplyChanges(null, new[] { "missing" }));
            Assert.AreEqual(2, _container.PlacementCount);
        }

        [Test]
        public void NonfiniteAndSingularTransformsAreRejectedBeforeMutation()
        {
            var invalid = At("c", float.NaN);
            Assert.Throws<ArgumentException>(() => _container.AddPlacements(new[] { invalid }));
            _host.transform.localScale = Vector3.zero;
            Assert.Throws<ArgumentException>(() => _container.Synchronize());
            _host.transform.localScale = Vector3.one;
            _container.Synchronize();
            Assert.AreEqual(2, _container.Renderer.InstanceCount);
        }

        [Test]
        public void QueriesAndEditsRemainAvailableWithoutRenderResidency()
        {
            _container.enabled = false;
            _container.ApplyChanges(new[] { At("c", 40) }, new[] { "a" });
            var ids = new List<string>();
            _container.QueryOrigins(new Bounds(Vector3.right * 40, Vector3.one), ids);
            CollectionAssert.AreEqual(new[] { "c" }, ids);
            Assert.IsNull(_container.Renderer);
            _container.enabled = true;
            Assert.AreEqual(2, _container.Renderer.InstanceCount);
            Assert.IsTrue(_container.TryGetHandle("c", out _));
            Assert.IsFalse(_container.TryGetHandle("a", out _));
        }

        [Test]
        public void SnapshotRoundTripPreservesIdsAndHasIndependentOwnership()
        {
            var asset = ScriptableObject.CreateInstance<InstancePlacementAsset>();
            var restored = ScriptableObject.CreateInstance<InstancePlacementAsset>();
            try
            {
                asset.SetPlacements(_container.CopyPlacements());
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(asset), restored);
                _container.SetPlacements(restored.CopyPlacements());
                _container.ApplyChanges(new[] { At("b", 99) }, new[] { "a" });
                Assert.AreEqual(2, asset.Count);
                Assert.AreEqual("a", restored.CopyPlacements()[0].Id);
                Assert.AreEqual(10, restored.CopyPlacements()[1].Position.x);
                var copy = restored.CopyPlacements();
                copy[0].Id = "changed";
                Assert.AreEqual("a", restored.CopyPlacements()[0].Id);
            }
            finally
            {
                Object.DestroyImmediate(asset);
                Object.DestroyImmediate(restored);
            }
        }

        [Test]
        public void SavedAssetReloadKeepsIdsAndCanRestoreEditedContainer()
        {
            string path = "Assets/LoogaPlacementTest-" + Guid.NewGuid().ToString("N") + ".asset";
            var asset = ScriptableObject.CreateInstance<InstancePlacementAsset>();
            try
            {
                asset.SetPlacements(_container.CopyPlacements());
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssetIfDirty(asset);
                Resources.UnloadAsset(asset);
                asset = AssetDatabase.LoadAssetAtPath<InstancePlacementAsset>(path);
                Assert.AreEqual(2, asset.Count);
                _container.SetPlacements(Array.Empty<InstanceContainer.Placement>());
                _container.SetPlacements(asset.CopyPlacements());
                Assert.IsTrue(_container.TryGetHandle("a", out _));
                Assert.IsTrue(_container.TryGetHandle("b", out _));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                if (asset != null && !AssetDatabase.Contains(asset))
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void UndoRedoRestoresPlacementIdsAndRendering()
        {
            Undo.RecordObject(_container, "Edit placements");
            _container.ApplyChanges(new[] { At("c", 40) }, new[] { "a" });
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            _container.Synchronize();
            Assert.IsTrue(_container.TryGetPlacement("a", out _));
            Assert.IsFalse(_container.TryGetPlacement("c", out _));
            Assert.AreEqual(2, _container.Renderer.InstanceCount);
            Undo.PerformRedo();
            _container.Synchronize();
            Assert.IsFalse(_container.TryGetPlacement("a", out _));
            Assert.IsTrue(_container.TryGetHandle("c", out _));
        }

        [Test]
        public void InvalidPrototypeReplacementKeepsPreviousRenderer()
        {
            var unsupported = new GameObject("Unsupported source");
            SceneManager.MoveGameObjectToScene(unsupported, _scene);
            var renderer = _container.Renderer;
            Assert.Throws<ArgumentException>(() => _container.Configure(unsupported, new[] { At("new", 0) }));
            Assert.AreSame(renderer, _container.Renderer);
            Assert.IsTrue(_container.TryGetHandle("a", out _));
        }

        private sealed class ImmediateAssets : IVegetationAssetSource
        {
            internal GameObject Prefab;
            internal int Releases;
            public Task<VegetationAssetLease> AcquireAsync(string key, CancellationToken cancellation)
            {
                return Task.FromResult(new VegetationAssetLease(Prefab, () => Releases++));
            }
        }

        [UnityTest]
        public IEnumerator FailedStreamedReplacementRetainsPreviousLeaseUntilDisable()
        {
            var valid = new ImmediateAssets { Prefab = _source };
            var empty = new GameObject("Invalid streamed prototype");
            SceneManager.MoveGameObjectToScene(empty, _scene);
            var invalid = new ImmediateAssets { Prefab = empty };
            var first = _container.ConfigureAsync(valid, "valid", new[] { At("kept", 0) });
            while (!first.IsCompleted) yield return null;
            first.GetAwaiter().GetResult();
            var renderer = _container.Renderer;
            var failed = _container.ConfigureAsync(invalid, "invalid", new[] { At("rejected", 0) });
            while (!failed.IsCompleted) yield return null;
            Assert.IsTrue(failed.IsFaulted);
            Assert.AreSame(renderer, _container.Renderer);
            Assert.AreEqual(0, valid.Releases);
            Assert.AreEqual(1, invalid.Releases);
            Assert.IsTrue(_container.TryGetHandle("kept", out _));
            _container.enabled = false;
            Assert.AreEqual(1, valid.Releases);
        }
    }
}

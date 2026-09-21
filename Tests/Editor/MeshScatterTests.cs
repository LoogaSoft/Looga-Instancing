using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class MeshScatterTests
    {
        private Scene _scene;
        private Mesh _mesh;
        private MeshFilter _surface;
        private MeshScatterAuthoring _author;
        private InstanceContainer _target;
        private GameObject _prototype;
        private Material _material;
        private MeshScatterSettings _settings;

        [SetUp]
        public void Setup()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _mesh = new Mesh { name = "Two separate triangles" };
            _mesh.vertices = new[] { Vector3.zero, new Vector3(0, 0, 10), new Vector3(10, 0, 0),
                new Vector3(100, 0, 0), new Vector3(100, 0, 10), new Vector3(110, 0, 0) };
            _mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
            _surface = MakeObject("Scatter surface").AddComponent<MeshFilter>();
            _surface.sharedMesh = _mesh;
            _prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(_prototype, _scene);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _prototype.GetComponent<Renderer>().sharedMaterial = _material;
            _target = MakeObject("Target").AddComponent<InstanceContainer>();
            _target.enabled = false;
            _target.Configure(_prototype, new[] { new InstanceContainer.Placement { Id = "manual", Scale = Vector3.one } });
            _author = MakeObject("Scatter author").AddComponent<MeshScatterAuthoring>();
            _author.enabled = false;
            _settings = new MeshScatterSettings { Density = 1 };
            Configure();
        }

        [TearDown]
        public void Cleanup()
        {
            if (_author != null)
            {
                Undo.ClearUndo(_author);
            }
            Undo.ClearUndo(_target);
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_mesh);
            Object.DestroyImmediate(_material);
        }

        private GameObject MakeObject(string name)
        {
            var item = new GameObject(name);
            SceneManager.MoveGameObjectToScene(item, _scene);
            return item;
        }

        private void Configure(float mask = 1)
        {
            _author.Configure(_surface, new[] { new MeshScatterAuthoring.Target { Container = _target, Weight = 1 } }, _settings, mask);
        }

        private List<MeshScatterSurface.Sample> Samples(Matrix4x4 matrix, float[] weights = null)
        {
            var snapshot = new MeshScatterSurface(_mesh, matrix);
            snapshot.ValidateBudget(_settings);
            var output = new List<MeshScatterSurface.Sample>();
            for (int i = 0; i < snapshot.TriangleCount; i++)
            {
                snapshot.GenerateTriangle(i, "test:", _settings, weights ?? new[] { 1f }, null, output);
            }
            return output;
        }

        [Test]
        public void MaskEventsQueueLocalChangesAndPreserveUnrelatedPlacements()
        {
            _author.enabled = true;
            var mask = MakeObject("Road mask").AddComponent<PlacementExclusion>();
            mask.Configure(new[] { new Vector3(-20, 0, 0), new Vector3(20, 0, 0) }, 0, 0);
            _author.SetExclusions(new[] { mask });
            _author.Regenerate();
            var right = _target.CopyPlacements().Where(p => p.Position.x > 100).ToArray();
            mask.Configure(new[] { new Vector3(-20, 0, 0), new Vector3(20, 0, 0) }, 20, 0);
            Assert.AreEqual(100, _author.GeneratedCount, "Generation must not run inside a source callback.");
            _author.ProcessExclusionChanges();
            Assert.AreEqual(1, _author.LastRegeneratedTriangles);
            Assert.AreEqual(50, _author.GeneratedCount);
            CollectionAssert.AreEqual(right, _target.CopyPlacements().Where(p => p.Position.x > 100).ToArray());
            mask.Clear();
            _author.ProcessExclusionChanges();
            Assert.AreEqual(100, _author.GeneratedCount);
        }

        [Test]
        public void ReplacedMaskSubscriptionsDetachAfterInspectorValidation()
        {
            _author.enabled = true;
            var first = MakeObject("First road").AddComponent<PlacementExclusion>();
            var second = MakeObject("Second road").AddComponent<PlacementExclusion>();
            var line = new[] { new Vector3(-20, 0, 0), new Vector3(20, 0, 0) };
            first.Configure(line, 20, 0);
            second.Configure(line, 0, 0);
            _author.SetExclusions(new[] { first });
            _author.Regenerate();
            Assert.AreEqual(50, _author.GeneratedCount);
            var serialized = new SerializedObject(_author);
            serialized.FindProperty("_exclusions").GetArrayElementAtIndex(0).objectReferenceValue = second;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _author.ProcessExclusionChanges();
            _author.Regenerate();
            Assert.AreEqual(100, _author.GeneratedCount);
            second.Configure(line, 20, 0);
            _author.ProcessExclusionChanges();
            Assert.AreEqual(50, _author.GeneratedCount);
            _author.SetExclusions(null);
            _author.Regenerate();
            first.Clear();
            second.Clear();
            _author.ProcessExclusionChanges();
            Assert.AreEqual(100, _author.GeneratedCount);
        }

        [Test]
        public void RepeatedGenerationPreservesIdsTransformsMeshAndGlobalRandomState()
        {
            var vertices = _mesh.vertices;
            var triangles = _mesh.triangles;
            var before = _target.CopyPlacements();
            string random = JsonUtility.ToJson(UnityEngine.Random.state);
            _author.Regenerate();
            CollectionAssert.AreEqual(before, _target.CopyPlacements());
            CollectionAssert.AreEqual(vertices, _mesh.vertices);
            CollectionAssert.AreEqual(triangles, _mesh.triangles);
            Assert.AreEqual(random, JsonUtility.ToJson(UnityEngine.Random.state));
            Assert.AreEqual(100, _author.GeneratedCount);
        }

        [Test]
        public void DensityUsesWorldAreaAndKeepsEarlierCandidatePositions()
        {
            var before = Samples(Matrix4x4.identity);
            Assert.AreEqual(100, before.Count);
            Assert.AreEqual(200, Samples(Matrix4x4.Scale(new Vector3(2, 1, 1))).Count);
            _settings.Density = 2;
            var after = Samples(Matrix4x4.identity).ToDictionary(p => p.Id);
            foreach (var sample in before)
            {
                Assert.AreEqual(sample.Position, after[sample.Id].Position);
                Assert.AreEqual(sample.Rotation, after[sample.Id].Rotation);
            }
        }

        [Test]
        public void WeightedTargetsExcludeZeroWeightAndFollowExpectedRatio()
        {
            Assert.IsTrue(Samples(Matrix4x4.identity, new[] { 0f, 1f }).All(p => p.Target == 1));
            _settings.Density = 100;
            var samples = Samples(Matrix4x4.identity, new[] { 1f, 3f });
            float ratio = samples.Count(p => p.Target == 1) / (float)samples.Count;
            Assert.That(ratio, Is.InRange(0.72f, 0.78f));
        }

        [Test]
        public void HeightSlopeAndAspectFiltersUseWorldGeometry()
        {
            _settings.Height = new Vector2(1, 100);
            Assert.AreEqual(0, Samples(Matrix4x4.identity).Count);
            _settings.Height = new Vector2(-1000, 1000);
            _settings.Slope = new Vector2(50, 70);
            Assert.AreEqual(0, Samples(Matrix4x4.identity).Count);
            var tilted = Matrix4x4.Rotate(Quaternion.Euler(0, 0, 60));
            _settings.Aspect = 90;
            _settings.AspectRange = 10;
            Assert.AreEqual(0, Samples(tilted).Count);
            _settings.Aspect = 270;
            Assert.Greater(Samples(tilted).Count, 90);
        }

        [Test]
        public void HeightFalloffReducesDensityNearBoundary()
        {
            _settings.Height = new Vector2(-1, 100);
            _settings.HeightFalloff = 2;
            Assert.That(Samples(Matrix4x4.identity).Count, Is.InRange(25, 75));
        }

        [Test]
        public void OffsetsScaleAndAlignmentRemainWithinRequestedRanges()
        {
            _settings.Scale = new Vector2(0.5f, 2);
            _settings.NormalOffset = new Vector2(1, 1);
            foreach (var sample in Samples(Matrix4x4.identity))
            {
                Assert.AreEqual(1, sample.Position.y, 0.0001f);
                Assert.That(sample.Scale, Is.InRange(0.5f, 2));
                Assert.Less(Vector3.Angle(sample.Rotation * Vector3.up, Vector3.up), 0.001f);
            }
        }

        [Test]
        public void BrushRegeneratesOnlyIntersectingTriangleAndPreservesOtherHandles()
        {
            _target.enabled = true;
            var before = _target.CopyPlacements().First(p => p.Position.x > 100);
            _target.TryGetHandle(before.Id, out var handle);
            var renderer = _target.Renderer;
            _author.Paint(new Vector3(3, 0, 3), 20, -1, 0);
            Assert.AreEqual(1, _author.LastRegeneratedTriangles);
            Assert.AreEqual(50, _author.GeneratedCount);
            Assert.AreSame(renderer, _target.Renderer);
            _target.TryGetHandle(before.Id, out var after);
            Assert.AreEqual(handle, after);
            Assert.IsTrue(_target.TryGetPlacement("manual", out _));
        }

        [Test]
        public void PaintingWorksInsideLargeTrianglesWithoutVertexEdits()
        {
            Configure(0);
            Assert.AreEqual(0, _author.GeneratedCount);
            _author.Paint(new Vector3(3, 0, 3), 2, 1, 0);
            Assert.Greater(_author.GeneratedCount, 0);
            Assert.IsTrue(_target.CopyPlacements().Where(p => p.Id != "manual").All(p => Vector3.Distance(p.Position, new Vector3(3, 0, 3)) < 2));
        }

        [Test]
        public void MaskFollowsSourceTransformAndClearRestoresOriginalIds()
        {
            var ids = _target.CopyPlacements().Select(p => p.Id).ToArray();
            _author.Paint(new Vector3(3, 0, 3), 20, -1, 0);
            _surface.transform.position = Vector3.right * 50;
            _author.Regenerate();
            Assert.AreEqual(50, _author.GeneratedCount);
            Assert.IsTrue(_target.CopyPlacements().Where(p => p.Id != "manual").All(p => p.Position.x >= 150));
            _author.ClearMask();
            CollectionAssert.AreEqual(ids, _target.CopyPlacements().Select(p => p.Id).ToArray());
        }

        [Test]
        public void LocalGeometryEditPreservesUnrelatedTrianglePlacements()
        {
            var right = _target.CopyPlacements().Where(p => p.Position.x > 100).ToArray();
            var vertices = _mesh.vertices;
            for (int i = 0; i < 3; i++)
            {
                vertices[i].y = 2;
            }
            _mesh.vertices = vertices;
            _author.InvalidateSurface(new Bounds(new Vector3(5, 1, 5), new Vector3(12, 4, 12)));
            Assert.AreEqual(1, _author.LastRegeneratedTriangles);
            CollectionAssert.AreEqual(right, _target.CopyPlacements().Where(p => p.Position.x > 100).ToArray());
            Assert.IsTrue(_target.CopyPlacements().Where(p => p.Id != "manual" && p.Position.x < 50).All(p => Mathf.Abs(p.Position.y - 2) < 0.00001f));
        }

        [Test]
        public void TargetReplacementRemovesOnlyThisAuthorsPlacements()
        {
            var next = MakeObject("New target").AddComponent<InstanceContainer>();
            next.enabled = false;
            next.Configure(_prototype, null);
            _author.Configure(_surface, new[] { new MeshScatterAuthoring.Target { Container = next, Weight = 1 } }, _settings);
            Assert.AreEqual(1, _target.PlacementCount);
            Assert.IsTrue(_target.TryGetPlacement("manual", out _));
            Assert.AreEqual(100, next.PlacementCount);
        }

        [Test]
        public void CandidateLimitRejectsBeforeChangingTargets()
        {
            var before = _target.CopyPlacements();
            _settings.CandidateLimit = 1;
            Assert.Throws<InvalidOperationException>(() => Configure());
            CollectionAssert.AreEqual(before, _target.CopyPlacements());
        }

        [Test]
        public void NonuniformTargetsAndUnreadableMeshesReportUnsupportedInputs()
        {
            _target.transform.localScale = new Vector3(1, 2, 1);
            Assert.Throws<ArgumentException>(() => _author.Regenerate());
            _target.transform.localScale = Vector3.one;
            _mesh.UploadMeshData(true);
            Assert.Throws<ArgumentException>(() => _author.Regenerate());
        }

        [Test]
        public void MeshPickingUsesNearestTransformedTriangleWithoutColliders()
        {
            _surface.transform.position = Vector3.up * 3;
            Assert.IsTrue(_author.Raycast(new Ray(new Vector3(3, 10, 3), Vector3.down), out var point, out var normal));
            Assert.AreEqual(3, point.y, 0.0001f);
            Assert.Greater(Vector3.Dot(normal, Vector3.up), 0.999f);
            Assert.IsNull(_surface.GetComponent<Collider>());
            Assert.IsFalse(_author.Raycast(new Ray(new Vector3(50, 10, 50), Vector3.down), out _, out _));
        }

        [Test]
        public void UndoRedoRestoresMaskAndPlacements()
        {
            var before = _target.CopyPlacements();
            Undo.RegisterCompleteObjectUndo(new Object[] { _author, _target }, "Paint mesh mask");
            _author.Paint(new Vector3(3, 0, 3), 20, -1, 0);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            _author.Regenerate();
            Assert.AreEqual(0, _author.StrokeCount);
            CollectionAssert.AreEqual(before, _target.CopyPlacements());
            Undo.PerformRedo();
            _author.Regenerate();
            Assert.AreEqual(1, _author.StrokeCount);
            Assert.AreEqual(50, _author.GeneratedCount);
        }

        [Test]
        public void CancellingStrokeGroupRestoresMaskAndUnrelatedPlacements()
        {
            var before = _target.CopyPlacements();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RegisterCompleteObjectUndo(new Object[] { _author, _target }, "Mask stroke");
            _author.Paint(new Vector3(3, 0, 3), 20, -1, 0);
            _author.Paint(new Vector3(103, 0, 3), 20, -1, 0);
            Assert.AreEqual(0, _author.GeneratedCount);
            Undo.RevertAllDownToGroup(group);
            _author.Regenerate();
            Assert.AreEqual(0, _author.StrokeCount);
            CollectionAssert.AreEqual(before, _target.CopyPlacements());
        }

        [Test]
        public void SavedPrefabRoundTripKeepsMaskReferencesAndPlacementIdentity()
        {
            _author.Paint(new Vector3(3, 0, 3), 20, -1, 0);
            var before = _target.CopyPlacements();
            string prefix = "Assets/LoogaMeshPaintTest-" + Guid.NewGuid().ToString("N");
            GameObject loaded = null;
            try
            {
                AssetDatabase.CreateAsset(_mesh, prefix + ".asset");
                AssetDatabase.CreateAsset(_material, prefix + ".mat");
                var root = MakeObject("Saved scatter group");
                foreach (var child in new[] { _surface.gameObject, _target.gameObject, _prototype, _author.gameObject })
                {
                    child.transform.SetParent(root.transform, true);
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefix + ".prefab");
                loaded = PrefabUtility.LoadPrefabContents(prefix + ".prefab");
                var restored = loaded.GetComponentInChildren<MeshScatterAuthoring>(true);
                var target = loaded.GetComponentInChildren<InstanceContainer>(true);
                Assert.IsNotNull(restored.Surface);
                Assert.AreEqual(1, restored.StrokeCount);
                restored.Regenerate();
                CollectionAssert.AreEqual(before, target.CopyPlacements());
            }
            finally
            {
                if (loaded != null)
                {
                    PrefabUtility.UnloadPrefabContents(loaded);
                }
                AssetDatabase.DeleteAsset(prefix + ".prefab");
                AssetDatabase.DeleteAsset(prefix + ".mat");
                AssetDatabase.DeleteAsset(prefix + ".asset");
            }
        }
    }
}

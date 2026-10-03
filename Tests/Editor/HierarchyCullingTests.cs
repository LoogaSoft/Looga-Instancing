using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

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

        [Test]
        public void OutOfRangeRendererRejectsViewAndNearRendererDraws()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset))
            {
                Assert.Ignore("The camera request uses URP.");
            }
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(source.GetComponent<Collider>());
            source.GetComponent<Renderer>().sharedMaterial = material;
            SceneManager.MoveGameObjectToScene(source, scene);
            var target = new RenderTexture(128, 128, 24);
            var cameraObject = new GameObject("Out of range camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            // Both instances are inside the frustum. Only the near instance is inside the 100 m limit.
            var near = new InstanceRenderer(staticTransforms: true) { MaxDistance = 100 };
            var far = new InstanceRenderer(staticTransforms: true) { MaxDistance = 100 };
            try
            {
                near.Add(near.Register(InstancePrototype.FromPrefab(source)), Matrix4x4.Translate(new Vector3(0, 2, 10)));
                far.Add(far.Register(InstancePrototype.FromPrefab(source)), Matrix4x4.Translate(new Vector3(0, 2, 5000)));
                near.Flush();
                far.Flush();
                camera.enabled = false;
                camera.scene = scene;
                camera.cullingMask = 1;
                camera.farClipPlane = 10000;
                camera.targetTexture = target;
                camera.transform.SetPositionAndRotation(new Vector3(0, 2, 0), Quaternion.identity);
                RenderPipeline.SubmitRenderRequest(camera,
                    new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target });
                AsyncGPUReadback.WaitAllRequests();

                Assert.IsFalse(far.LastHierarchyDecision.Visible, "The far renderer must reject the view.");
                Assert.IsEmpty(far.ReadCameraDrawCounts(camera), "A rejected view must not record draws.");
                Assert.IsTrue(near.LastHierarchyDecision.Visible);
                uint drawn = 0;
                foreach (uint count in near.ReadCameraDrawCounts(camera))
                {
                    drawn += count;
                }
                Assert.Greater(drawn, 0u, "The near renderer must draw its instance.");
            }
            finally
            {
                near.Dispose();
                far.Dispose();
                camera.targetTexture = null;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }
    }
}

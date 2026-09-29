using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    // Unity calls every BatchRendererGroup for every culling view. Empty renderers must not own one.
    public sealed class InstanceRendererGroupTests
    {
        private Scene _scene;
        private GameObject _source;
        private Material _material;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer requires Direct3D 12 compute support.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(_source, _scene);
            Object.DestroyImmediate(_source.GetComponent<Collider>());
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _source.GetComponent<Renderer>().sharedMaterial = _material;
        }

        [TearDown]
        public void TearDown()
        {
            if (_scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_scene);
            }
            if (_material)
            {
                Object.DestroyImmediate(_material);
            }
        }

        [Test]
        public void CpuRendererOwnsGroupOnlyWhileItHasStorage()
        {
            using var renderer = new InstanceRenderer(staticTransforms: true);
            int prototype = renderer.Register(InstancePrototype.FromPrefab(_source));
            renderer.Flush();
            Assert.IsFalse(renderer.HasRendererGroup, "A renderer without instances must not own a group.");

            InstanceHandle handle = renderer.Add(prototype, Matrix4x4.identity);
            renderer.Flush();
            Assert.IsTrue(renderer.HasRendererGroup);

            Assert.IsTrue(renderer.Remove(handle));
            renderer.TrimExcess();
            renderer.Flush();
            Assert.IsFalse(renderer.HasRendererGroup, "Released storage must release the group.");

            // A second prototype and new instances create a new group with every registration attached.
            renderer.Register(InstancePrototype.FromPrefab(_source).WithLodBias(2));
            renderer.Add(prototype, Matrix4x4.Translate(Vector3.one));
            renderer.Flush();
            Assert.IsTrue(renderer.HasRendererGroup);
            Assert.AreEqual(1, renderer.InstanceCount);
        }

        [Test]
        public void GpuRendererOwnsGroupOnlyWhileItHasStorage()
        {
            using var renderer = new InstanceRenderer(gpuResident: true);
            int prototype = renderer.Register(InstancePrototype.FromPrefab(_source));
            renderer.FlushBudget(long.MaxValue);
            Assert.IsFalse(renderer.HasRendererGroup);

            using var source = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, InstanceRenderer.RangeTransformStride);
            source.SetData(new[] { new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 1, 0) });
            InstanceRange range = renderer.AllocateRange(prototype, 1, new Bounds(Vector3.zero, Vector3.one), 1);
            renderer.WriteRanges(source, new[] { new InstanceRangeWrite(range, 0) });
            Assert.IsTrue(renderer.HasRendererGroup);

            Assert.IsTrue(renderer.ReleaseRange(range));
            renderer.TrimExcess();
            renderer.FlushBudget(long.MaxValue);
            Assert.IsFalse(renderer.HasRendererGroup);
        }
    }
}

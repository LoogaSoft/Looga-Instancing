using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    // Batch removal, group disposal and group creation must not break the BatchRendererGroup state of a later render.
    // Unity reports a broken state as a console error during rendering. The test framework fails on that error.
    public sealed class InstanceBatchLifecycleTests
    {
        private Scene _scene;
        private GameObject _source;
        private Material _material;
        private Camera _camera;
        private RenderTexture _target;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer requires Direct3D 12 compute support.");
            }
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            {
                Assert.Ignore("The batch lifecycle tests render a URP camera request.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(_source, _scene);
            Object.DestroyImmediate(_source.GetComponent<Collider>());
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _source.GetComponent<Renderer>().sharedMaterial = _material;
            _target = new RenderTexture(64, 64, 24);
            var cameraObject = new GameObject("Batch lifecycle camera");
            SceneManager.MoveGameObjectToScene(cameraObject, _scene);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.scene = _scene;
            _camera.farClipPlane = 500;
            _camera.targetTexture = _target;
            _camera.transform.SetPositionAndRotation(new Vector3(0, 2, -10), Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            if (_camera)
            {
                _camera.targetTexture = null;
            }
            if (_scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_scene);
            }
            if (_target)
            {
                Object.DestroyImmediate(_target);
            }
            if (_material)
            {
                Object.DestroyImmediate(_material);
            }
        }

        [Test]
        public void RecreatedRendererRendersAfterDisposedRenderer()
        {
            InstanceRenderer first = CreateRenderer(8, out _);
            Render();
            first.Dispose();
            using InstanceRenderer second = CreateRenderer(8, out _);
            Render();
            Render();
        }

        [Test]
        public void RecreatedGroupRendersAfterReleasedGroup()
        {
            using InstanceRenderer renderer = CreateRenderer(8, out List<InstanceHandle> handles);
            Render();
            foreach (InstanceHandle handle in handles)
            {
                renderer.Remove(handle);
            }
            renderer.TrimExcess();
            renderer.Flush();
            Assert.IsFalse(renderer.HasRendererGroup);
            renderer.Add(0, Matrix4x4.identity);
            renderer.Flush();
            Assert.IsTrue(renderer.HasRendererGroup);
            Render();
            Render();
        }

        [Test]
        public void StorageGrowthInsideCameraCallbackRenders()
        {
            using InstanceRenderer renderer = CreateRenderer(8, out _);
            Render();
            // Unflushed growth makes the camera callback replace the batches.
            for (int i = 0; i < 200; i++)
            {
                renderer.Add(0, Matrix4x4.Translate(new Vector3(i % 20, 0, i / 20)));
            }
            Render();
            Render();
        }

        [Test]
        public void ManyRenderersRecreatedInOneFrameRender()
        {
            var renderers = new List<InstanceRenderer>();
            for (int i = 0; i < 16; i++)
            {
                renderers.Add(CreateRenderer(4 + i, out _));
            }
            Render();
            foreach (InstanceRenderer renderer in renderers)
            {
                renderer.Dispose();
            }
            renderers.Clear();
            for (int i = 0; i < 16; i++)
            {
                renderers.Add(CreateRenderer(4 + i, out _));
            }
            Render();
            Render();
            foreach (InstanceRenderer renderer in renderers)
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void DisposedRendererWithoutRenderRenders()
        {
            InstanceRenderer first = CreateRenderer(8, out _);
            first.Dispose();
            Render();
            InstanceRenderer second = CreateRenderer(8, out _);
            Render();
            second.Dispose();
            Render();
        }

        private InstanceRenderer CreateRenderer(int count, out List<InstanceHandle> handles)
        {
            var renderer = new InstanceRenderer();
            int prototype = renderer.Register(InstancePrototype.FromPrefab(_source));
            handles = new List<InstanceHandle>();
            for (int i = 0; i < count; i++)
            {
                handles.Add(renderer.Add(prototype, Matrix4x4.Translate(new Vector3(i * 1.5f - count * 0.75f, 0, 5))));
            }
            renderer.Flush();
            return renderer;
        }

        private void Render()
        {
            RenderPipeline.SubmitRenderRequest(_camera,
                new UniversalRenderPipeline.SingleCameraRequest { destination = _target });
        }
    }
}

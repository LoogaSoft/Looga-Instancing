using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstanceGpuStorageTests
    {
        private struct BoundsRecord
        {
            public Vector4 Sphere;
            public Vector4 Lod;
            public Vector4 State;
        }

        private Scene _scene;
        private GameObject _source;
        private Material _material;
        private readonly List<IDisposable> _disposables = new();

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("GPU-resident storage requires Direct3D 12 compute support.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            // Two child renderers with different meshes and local transforms produce two data blocks.
            _source = new GameObject("GPU storage source");
            SceneManager.MoveGameObjectToScene(_source, _scene);
            AddChild(PrimitiveType.Cube, new Vector3(0, 0.5f, 0), Quaternion.Euler(0, 30, 0), new Vector3(1, 2, 1));
            AddChild(PrimitiveType.Sphere, new Vector3(0.4f, 1.5f, -0.2f), Quaternion.Euler(10, 0, 20), Vector3.one * 0.7f);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }
            _disposables.Clear();
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
        public void WrittenRangeMatchesCpuUpload()
        {
            Matrix4x4[] transforms = CreateTransforms(37, 1);
            InstanceRenderer cpu = CreateCpuRenderer(out int cpuPrototype, transforms);
            InstanceRenderer gpu = CreateGpuRenderer(out int gpuPrototype);
            InstanceRange range = AllocateAndWrite(gpu, gpuPrototype, transforms);
            gpu.FlushBudget(long.MaxValue);

            Assert.AreEqual(0, gpu.GetRangeStart(range));
            Assert.AreEqual(transforms.Length, gpu.InstanceCount);
            Assert.AreEqual(transforms.Length, gpu.GetResidentCount(gpuPrototype));
            Assert.AreEqual(transforms.Length, WorldCellTotal(gpu), "World cells must count every range slot.");
            AssertStorageEqual(cpu, cpuPrototype, gpu, gpuPrototype, transforms.Length);
        }

        private static int WorldCellTotal(InstanceRenderer renderer)
        {
            var records = new List<InstanceWorldSourceCellRecord>();
            InstanceWorldCells.CopySource(renderer.WorldSourceId, records);
            int total = 0;
            foreach (InstanceWorldSourceCellRecord record in records)
            {
                total += record.InstanceCount;
            }
            return total;
        }

        [Test]
        public void ZeroTransformWritesAnInactiveSlot()
        {
            Matrix4x4[] transforms = CreateTransforms(4, 2);
            transforms[2] = Matrix4x4.zero;
            InstanceRenderer gpu = CreateGpuRenderer(out int prototype);
            AllocateAndWrite(gpu, prototype, transforms);
            BoundsRecord[] bounds = ReadBounds(gpu, prototype, out _, out _);
            Assert.AreEqual(Vector4.zero, bounds[2].State);
            Assert.AreEqual(Vector4.zero, bounds[2].Sphere);
            Assert.AreEqual(1f, bounds[1].State.x);
            Assert.AreEqual(1f, bounds[3].State.x);
        }

        [Test]
        public void CapacityGrowthPreservesWrittenRanges()
        {
            Matrix4x4[] first = CreateTransforms(40, 3);
            Matrix4x4[] second = CreateTransforms(100, 4);
            InstanceRenderer gpu = CreateGpuRenderer(out int prototype);
            AllocateAndWrite(gpu, prototype, first);
            gpu.TryGetStorage(prototype, out _, out _, out int before, out _);
            Assert.AreEqual(64, before);
            InstanceRange grown = AllocateAndWrite(gpu, prototype, second);
            Assert.AreEqual(40, gpu.GetRangeStart(grown));

            var all = new Matrix4x4[first.Length + second.Length];
            first.CopyTo(all, 0);
            second.CopyTo(all, first.Length);
            InstanceRenderer cpu = CreateCpuRenderer(out int cpuPrototype, all);
            AssertStorageEqual(cpu, cpuPrototype, gpu, prototype, all.Length);
        }

        [Test]
        public void ReleasedSlotsBecomeInactiveAndAreReused()
        {
            InstanceRenderer gpu = CreateGpuRenderer(out int prototype);
            InstanceRange first = AllocateAndWrite(gpu, prototype, CreateTransforms(40, 5));
            InstanceRange second = AllocateAndWrite(gpu, prototype, CreateTransforms(100, 6));

            Assert.IsTrue(gpu.ReleaseRange(first));
            Assert.IsFalse(gpu.ReleaseRange(first), "A released range must not release twice.");
            Assert.IsTrue(gpu.HasPendingUploads);
            gpu.FlushBudget(long.MaxValue);
            Assert.IsFalse(gpu.HasPendingUploads);
            BoundsRecord[] bounds = ReadBounds(gpu, prototype, out _, out int highWater);
            Assert.AreEqual(140, highWater);
            for (int slot = 0; slot < 40; slot++)
            {
                Assert.AreEqual(0f, bounds[slot].State.x, $"Slot {slot}");
            }
            Assert.AreEqual(1f, bounds[40].State.x);

            Matrix4x4[] reused = CreateTransforms(30, 7);
            InstanceRange third = AllocateAndWrite(gpu, prototype, reused);
            Assert.AreEqual(0, gpu.GetRangeStart(third), "First fit must reuse the released slots.");
            Assert.AreEqual(130, gpu.InstanceCount);

            // Releasing the last range merges the free slots at the end and lowers the high-water mark.
            Assert.IsTrue(gpu.ReleaseRange(second));
            gpu.TrimExcess();
            gpu.FlushBudget(long.MaxValue);
            gpu.TryGetStorage(prototype, out _, out _, out int capacity, out highWater);
            Assert.AreEqual(30, highWater);
            Assert.AreEqual(64, capacity, "Storage must shrink after the high-water mark falls to a quarter.");
            Assert.AreEqual(30, WorldCellTotal(gpu), "Released ranges must leave the world cells.");
            InstanceRenderer cpu = CreateCpuRenderer(out int cpuPrototype, reused);
            AssertStorageEqual(cpu, cpuPrototype, gpu, prototype, reused.Length);

            Assert.IsTrue(gpu.ReleaseRange(third));
            gpu.TrimExcess();
            gpu.FlushBudget(long.MaxValue);
            Assert.AreEqual(0, gpu.InstanceCount);
            Assert.IsFalse(gpu.TryGetStorage(prototype, out _, out _, out capacity, out _));
            Assert.AreEqual(0, capacity);
        }

        [Test]
        public void CompactionPacksRangesAndKeepsHandles()
        {
            Matrix4x4[] first = CreateTransforms(5000, 8);
            Matrix4x4[] second = CreateTransforms(10000, 9);
            Matrix4x4[] third = CreateTransforms(5000, 10);
            InstanceRenderer gpu = CreateGpuRenderer(out int prototype);
            AllocateAndWrite(gpu, prototype, first);
            InstanceRange middle = AllocateAndWrite(gpu, prototype, second);
            InstanceRange last = AllocateAndWrite(gpu, prototype, third);
            gpu.FlushBudget(long.MaxValue);
            gpu.TryGetStorage(prototype, out _, out _, out int capacity, out int highWater);
            Assert.AreEqual(32768, capacity);
            Assert.AreEqual(20000, highWater);

            // Free slots below the high-water mark now exceed a quarter of the live slots.
            Assert.IsTrue(gpu.ReleaseRange(middle));
            Assert.IsTrue(gpu.HasPendingUploads);
            gpu.FlushBudget(long.MaxValue);
            gpu.TryGetStorage(prototype, out _, out _, out capacity, out highWater);
            Assert.AreEqual(10000, highWater);
            Assert.AreEqual(16384, capacity);
            Assert.AreEqual(5000, gpu.GetRangeStart(last), "Compaction must move the last range down.");

            var remaining = new Matrix4x4[first.Length + third.Length];
            first.CopyTo(remaining, 0);
            third.CopyTo(remaining, first.Length);
            InstanceRenderer cpu = CreateCpuRenderer(out int cpuPrototype, remaining);
            AssertStorageEqual(cpu, cpuPrototype, gpu, prototype, remaining.Length);
            Assert.IsTrue(gpu.ReleaseRange(last), "A moved range must keep its handle.");
            Assert.AreEqual(5000, gpu.InstanceCount);
        }

        [Test]
        public void CameraDrawCountsMatchCpuStorage()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset))
            {
                Assert.Ignore("The draw-count comparison uses a URP camera request.");
            }
            // Instances in front of and behind the camera. Culling must reject the same instances in both paths.
            var transforms = new Matrix4x4[96];
            for (int i = 0; i < transforms.Length; i++)
            {
                float z = (i % 2 == 0 ? 1 : -1) * (8 + i * 1.5f);
                transforms[i] = Matrix4x4.TRS(new Vector3((i % 7 - 3) * 2.5f, 0, z), Quaternion.Euler(0, i * 37, 0),
                    Vector3.one * (0.6f + i % 5 * 0.2f));
            }
            InstanceRenderer cpu = CreateCpuRenderer(out int cpuPrototype, transforms);
            InstanceRenderer gpu = CreateGpuRenderer(out int gpuPrototype);
            AllocateAndWrite(gpu, gpuPrototype, transforms);
            gpu.FlushBudget(long.MaxValue);

            var target = new RenderTexture(256, 256, 24);
            var cameraObject = new GameObject("GPU storage camera");
            SceneManager.MoveGameObjectToScene(cameraObject, _scene);
            var camera = cameraObject.AddComponent<Camera>();
            try
            {
                camera.enabled = false;
                camera.scene = _scene;
                camera.cullingMask = 1;
                camera.farClipPlane = 500;
                camera.targetTexture = target;
                camera.transform.SetPositionAndRotation(new Vector3(0, 2, 0), Quaternion.identity);
                RenderPipeline.SubmitRenderRequest(camera,
                    new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target });
                AsyncGPUReadback.WaitAllRequests();
                uint[] expected = cpu.ReadCameraDrawCounts(camera);
                uint[] actual = gpu.ReadCameraDrawCounts(camera);
                Assert.Greater(expected.Length, 0, "The CPU renderer must record camera draw counts.");
                Assert.Greater(Sum(expected), 0u, "Some instances must be visible.");
                Assert.Less(Sum(expected), (uint)(transforms.Length * 2), "Instances behind the camera must be culled.");
                CollectionAssert.AreEqual(expected, actual);
                Assert.AreEqual(cpu.ReadCameraVisibleCount(camera, cpuPrototype),
                    gpu.ReadCameraVisibleCount(camera, gpuPrototype));
                Assert.AreEqual(cpu.GpuBytes, gpu.GpuBytes, "Equal instances must use equal GPU storage.");
            }
            finally
            {
                camera.targetTexture = null;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
            }
        }

        private static uint Sum(uint[] values)
        {
            uint total = 0;
            foreach (uint value in values)
            {
                total += value;
            }
            return total;
        }

        [Test]
        public void StorageModesRejectTheOtherApi()
        {
            InstanceRenderer gpu = CreateGpuRenderer(out int prototype);
            Assert.Throws<InvalidOperationException>(() => gpu.Add(prototype, Matrix4x4.identity));
            Assert.Throws<NotSupportedException>(() =>
                gpu.Register(InstancePrototype.FromPrefab(_source).WithLegacyLightProbes()));
            InstanceRenderer cpu = Track(new InstanceRenderer(staticTransforms: true));
            int cpuPrototype = cpu.Register(InstancePrototype.FromPrefab(_source));
            Assert.Throws<InvalidOperationException>(() =>
                cpu.AllocateRange(cpuPrototype, 1, new Bounds(Vector3.zero, Vector3.one), 1));
        }

        private void AddChild(PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject child = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(child.GetComponent<Collider>());
            child.GetComponent<Renderer>().sharedMaterial = _material;
            child.transform.SetParent(_source.transform, false);
            child.transform.SetLocalPositionAndRotation(position, rotation);
            child.transform.localScale = scale;
        }

        private T Track<T>(T value) where T : IDisposable
        {
            _disposables.Add(value);
            return value;
        }

        private InstanceRenderer CreateGpuRenderer(out int prototype)
        {
            InstanceRenderer renderer = Track(new InstanceRenderer(gpuResident: true));
            prototype = renderer.Register(InstancePrototype.FromPrefab(_source));
            return renderer;
        }

        private InstanceRenderer CreateCpuRenderer(out int prototype, Matrix4x4[] transforms)
        {
            InstanceRenderer renderer = Track(new InstanceRenderer(staticTransforms: true));
            prototype = renderer.Register(InstancePrototype.FromPrefab(_source));
            foreach (Matrix4x4 transform in transforms)
            {
                renderer.Add(prototype, transform);
            }
            renderer.Flush();
            return renderer;
        }

        private InstanceRange AllocateAndWrite(InstanceRenderer renderer, int prototype, Matrix4x4[] transforms)
        {
            var rows = new Vector4[transforms.Length * 3];
            var pivots = new Bounds(transforms[0].GetColumn(3), Vector3.zero);
            for (int i = 0; i < transforms.Length; i++)
            {
                for (int row = 0; row < 3; row++)
                {
                    rows[i * 3 + row] = transforms[i].GetRow(row);
                }
                pivots.Encapsulate(transforms[i].GetColumn(3));
            }
            GraphicsBuffer source = Track(new GraphicsBuffer(GraphicsBuffer.Target.Structured, transforms.Length,
                InstanceRenderer.RangeTransformStride));
            source.SetData(rows);
            InstanceRange range = renderer.AllocateRange(prototype, transforms.Length, pivots, 3);
            renderer.WriteRanges(source, new[] { new InstanceRangeWrite(range, 0) });
            return range;
        }

        // Random affine transforms with non-uniform scale. Every third transform mirrors one axis.
        private static Matrix4x4[] CreateTransforms(int count, int seed)
        {
            var random = new System.Random(seed);
            float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);
            var result = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                var scale = new Vector3(Next(0.5f, 2), Next(0.5f, 2), Next(0.5f, 2));
                if (i % 3 == 0)
                {
                    scale.x = -scale.x;
                }
                result[i] = Matrix4x4.TRS(new Vector3(Next(-500, 500), Next(-20, 80), Next(-500, 500)),
                    Quaternion.Euler(Next(-30, 30), Next(0, 360), Next(-30, 30)), scale);
            }
            return result;
        }

        private static BoundsRecord[] ReadBounds(InstanceRenderer renderer, int prototype, out int capacity,
            out int highWater)
        {
            Assert.IsTrue(renderer.TryGetStorage(prototype, out _, out GraphicsBuffer bounds, out capacity, out highWater));
            var result = new BoundsRecord[capacity];
            bounds.GetData(result);
            return result;
        }

        private static void AssertStorageEqual(InstanceRenderer cpu, int cpuPrototype, InstanceRenderer gpu,
            int gpuPrototype, int count)
        {
            Assert.IsTrue(cpu.TryGetStorage(cpuPrototype, out GraphicsBuffer cpuData, out _, out int cpuCapacity, out _));
            Assert.IsTrue(gpu.TryGetStorage(gpuPrototype, out GraphicsBuffer gpuData, out _, out int gpuCapacity, out _));
            Assert.AreEqual(cpuCapacity, gpuCapacity);
            Assert.AreEqual(cpuData.count, gpuData.count);
            var expected = new float[cpuData.count];
            var actual = new float[gpuData.count];
            cpuData.GetData(expected);
            gpuData.GetData(actual);
            BoundsRecord[] expectedBounds = ReadBounds(cpu, cpuPrototype, out _, out _);
            BoundsRecord[] actualBounds = ReadBounds(gpu, gpuPrototype, out _, out _);
            // World and inverse matrices for two data blocks.
            const int arrays = 4;
            for (int array = 0; array < arrays; array++)
            {
                for (int slot = 0; slot < count; slot++)
                {
                    for (int element = 0; element < 12; element++)
                    {
                        int index = 16 + (array * cpuCapacity + slot) * 12 + element;
                        Assert.That(actual[index], Is.EqualTo(expected[index]).Within(0.0005f + Math.Abs(expected[index]) * 0.0005f),
                            $"Array {array}, slot {slot}, element {element}");
                    }
                }
            }
            for (int slot = 0; slot < count; slot++)
            {
                AssertVector(expectedBounds[slot].Sphere, actualBounds[slot].Sphere, $"Sphere {slot}");
                AssertVector(expectedBounds[slot].Lod, actualBounds[slot].Lod, $"LOD {slot}");
                Assert.AreEqual(expectedBounds[slot].State.x, actualBounds[slot].State.x, $"Active {slot}");
                Assert.AreEqual(expectedBounds[slot].State.y, actualBounds[slot].State.y, $"Flip {slot}");
                Assert.AreEqual(expectedBounds[slot].State.w, actualBounds[slot].State.w, $"Visibility key {slot}");
            }
        }

        private static void AssertVector(Vector4 expected, Vector4 actual, string message)
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(0.0005f + Math.Abs(expected[i]) * 0.0005f), message);
            }
        }
    }
}

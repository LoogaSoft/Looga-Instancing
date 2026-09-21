using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class SuperBatchTests
    {
        [Test]
        public void CompatibleLogicalSourcesShareOnePopulation()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) Assert.Ignore("D3D12 is required.");
            GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            InstanceSuperBatch batch = null;
            try
            {
                batch = new InstanceSuperBatch(worldSourceId: "test-super-batch");
                using var first = batch.AddSource("first");
                using var second = batch.AddSource("second");
                int a = first.Register(InstancePrototype.FromPrefab(prefab));
                int b = second.Register(InstancePrototype.FromPrefab(prefab));
                Assert.AreEqual(a, b);
                first.Add(a, Matrix4x4.identity, InstanceAppearance.Default);
                second.Add(b, Matrix4x4.Translate(Vector3.right), InstanceAppearance.Default);
                batch.Flush();
                Assert.AreEqual(1, batch.Renderer.RegisteredPrototypeCount);
                Assert.AreEqual(2, batch.InstanceCount);
            }
            finally
            {
                batch?.Dispose();
                Object.DestroyImmediate(prefab);
            }
        }
    }
}

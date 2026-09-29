using System.Collections;
using System.Reflection;
using LoogaSoft.Instancing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstanceUploadRegressionTests
    {
        private struct BoundsRecord
        {
            public Vector4 Sphere;
            public Vector4 Lod;
            public Vector4 State;
        }

        [Test]
        public void FlushUploadsTransformsAndPreservesAdjacentSlotAfterUpdate()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 ||
                !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer uploads require Direct3D 12 compute support.");
            }

            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject source = null;
            Material material = null;
            InstanceRenderer renderer = null;
            try
            {
                source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(source, scene);
                Object.DestroyImmediate(source.GetComponent<Collider>());

                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                Assert.IsNotNull(shader);
                material = new Material(shader);
                source.GetComponent<Renderer>().sharedMaterial = material;

                InstancePrototype prototype = InstancePrototype.FromPrefab(source);
                Assert.AreEqual(1, prototype.PartCount);

                renderer = new InstanceRenderer();
                int prototypeIndex = renderer.Register(prototype);

                Matrix4x4 initial = Matrix4x4.TRS(
                    new Vector3(2.5f, -1f, 9f),
                    Quaternion.Euler(0f, 90f, 0f),
                    new Vector3(2f, 1f, 0.5f));
                Matrix4x4 adjacent = Matrix4x4.TRS(
                    new Vector3(-13f, 4f, 3f),
                    Quaternion.Euler(15f, 25f, -10f),
                    new Vector3(0.75f, 1.5f, 2.25f));
                InstanceHandle updatedHandle = renderer.Add(prototypeIndex, initial);
                renderer.Add(prototypeIndex, adjacent);

                renderer.Flush();

                GraphicsBuffer buffer = GetPopulationBuffer(renderer, prototypeIndex, out int capacity);
                float[] before = ReadBuffer(buffer);
                AssertPackedMatrix(before, capacity, 0, 0, initial);
                AssertPackedMatrix(before, capacity, 1, 0, adjacent);
                AssertPackedMatrix(before, capacity, 0, 1, initial.inverse);
                AssertPackedMatrix(before, capacity, 1, 1, adjacent.inverse);
                GraphicsBuffer boundsBuffer = GetPopulationBoundsBuffer(renderer, prototypeIndex);
                BoundsRecord[] boundsBefore = ReadBounds(boundsBuffer);
                Assert.That(boundsBefore[0].Sphere.x, Is.EqualTo(initial.m03).Within(0.0001f));
                Assert.That(boundsBefore[1].Sphere.x, Is.EqualTo(adjacent.m03).Within(0.0001f));
                long fallbackBefore = renderer.FallbackUploadedBytes;
                Assert.That(fallbackBefore, Is.GreaterThanOrEqualTo(2 * 48L));
                long mappedBefore = renderer.MappedUploadedBytes;
                Assert.Greater(mappedBefore, 2 * 48L,
                    "The initial upload must exercise the raw-buffer compute patch path.");

                Matrix4x4 updated = Matrix4x4.TRS(
                    new Vector3(10f, 5f, -6f),
                    Quaternion.Euler(15f, 35f, -20f),
                    new Vector3(0.8f, 1.4f, 2.2f));
                Assert.IsTrue(renderer.Update(updatedHandle, updated));

                renderer.Flush();

                float[] after = ReadBuffer(buffer);
                AssertPackedMatrix(after, capacity, 0, 0, updated);
                AssertPackedMatrix(after, capacity, 1, 0, adjacent);
                AssertPackedMatrix(after, capacity, 0, 1, updated.inverse);
                AssertPackedMatrix(after, capacity, 1, 1, adjacent.inverse);
                BoundsRecord[] boundsAfter = ReadBounds(boundsBuffer);
                Assert.That(boundsAfter[0].Sphere.x, Is.EqualTo(updated.m03).Within(0.0001f));
                Assert.That(boundsAfter[0].Sphere.z, Is.EqualTo(updated.m23).Within(0.0001f));
                Assert.That(boundsAfter[1].Sphere, Is.EqualTo(boundsBefore[1].Sphere));
                Assert.That(boundsAfter[1].Lod, Is.EqualTo(boundsBefore[1].Lod));
                Assert.That(boundsAfter[1].State, Is.EqualTo(boundsBefore[1].State));
                Assert.That(renderer.FallbackUploadedBytes - fallbackBefore, Is.GreaterThanOrEqualTo(48L));
                Assert.Greater(renderer.MappedUploadedBytes - mappedBefore, 48L,
                    "The update must exercise the raw-buffer compute patch path.");
            }
            finally
            {
                renderer?.Dispose();
                if (scene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                if (material)
                {
                    Object.DestroyImmediate(material);
                }
            }
        }

        private static GraphicsBuffer GetPopulationBuffer(
            InstanceRenderer renderer,
            int prototypeIndex,
            out int capacity)
        {
            FieldInfo populationsField = typeof(InstanceRenderer).GetField(
                "_populations",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(populationsField);

            IList populations = (IList)populationsField.GetValue(renderer);
            object population = populations[prototypeIndex];
            FieldInfo bufferField = population.GetType().GetField(
                "Buffer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo capacityField = population.GetType().GetField(
                "Capacity",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(bufferField);
            Assert.IsNotNull(capacityField);

            capacity = (int)capacityField.GetValue(population);
            GraphicsBuffer buffer = (GraphicsBuffer)bufferField.GetValue(population);
            Assert.IsNotNull(buffer);
            Assert.IsTrue(buffer.IsValid());
            return buffer;
        }

        private static GraphicsBuffer GetPopulationBoundsBuffer(InstanceRenderer renderer, int prototypeIndex)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo populationsField = typeof(InstanceRenderer).GetField("_populations", flags);
            Assert.IsNotNull(populationsField);
            object population = ((IList)populationsField.GetValue(renderer))[prototypeIndex];
            FieldInfo bufferField = population.GetType().GetField("BoundsBuffer", flags);
            Assert.IsNotNull(bufferField);
            GraphicsBuffer buffer = (GraphicsBuffer)bufferField.GetValue(population);
            Assert.IsNotNull(buffer);
            Assert.IsTrue(buffer.IsValid());
            return buffer;
        }

        private static BoundsRecord[] ReadBounds(GraphicsBuffer buffer)
        {
            var data = new BoundsRecord[buffer.count];
            buffer.GetData(data);
            return data;
        }

        private static float[] ReadBuffer(GraphicsBuffer buffer)
        {
            var data = new float[buffer.count];
            buffer.GetData(data);
            return data;
        }

        private static void AssertPackedMatrix(
            float[] data,
            int capacity,
            int slot,
            int matrixArray,
            Matrix4x4 expected)
        {
            int offset = 16 + (matrixArray * capacity + slot) * 12;
            for (int column = 0; column < 4; column++)
            {
                for (int row = 0; row < 3; row++)
                {
                    Assert.That(data[offset++], Is.EqualTo(expected[row, column]).Within(0.0001f),
                        $"Matrix array {matrixArray}, slot {slot}, column {column}, row {row}");
                }
            }
        }
    }
}

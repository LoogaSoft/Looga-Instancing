using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstanceSpatialMaintenanceTests
    {
        [Test]
        public void SourceCellsTrackMovesRemovalAndSlotReuse()
        {
            if (!Supported()) Assert.Ignore("InstanceRenderer requires Direct3D 12 compute support.");
            Scene scene = EditorSceneManager.NewPreviewScene();
            var sources = new List<GameObject>();
            Material material = null;
            Material secondMaterial = null;
            InstanceRenderer renderer = null;
            try
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                secondMaterial = new Material(material) { name = "Distinct second prototype material" };
                GameObject first = MakeSource(scene, material, sources);
                GameObject second = MakeSource(scene, secondMaterial, sources);
                renderer = new InstanceRenderer(worldContentKind: InstanceWorldContentKind.Detail);
                int firstType = renderer.Register(InstancePrototype.FromPrefab(first));
                int secondType = renderer.Register(InstancePrototype.FromPrefab(second));
                Assert.That(firstType, Is.Not.EqualTo(secondType));

                InstanceHandle moving = renderer.Add(firstType, Matrix4x4.Translate(new Vector3(5, 0, 5)));
                InstanceHandle removed = renderer.Add(firstType, Matrix4x4.Translate(new Vector3(15, 0, 5)));
                renderer.Add(secondType, Matrix4x4.Translate(new Vector3(80, 0, 5)));
                renderer.Flush();
                AssertSourceCells(renderer, 2, 3);

                Assert.IsTrue(renderer.Update(moving, Matrix4x4.Translate(new Vector3(80, 0, 5))));
                renderer.Flush();
                AssertSourceCells(renderer, 3, 3);

                Assert.IsTrue(renderer.Remove(removed));
                renderer.Flush();
                AssertSourceCells(renderer, 2, 2);

                renderer.Add(firstType, Matrix4x4.Translate(new Vector3(5, 0, 5)));
                renderer.Flush();
                AssertSourceCells(renderer, 3, 3);
            }
            finally
            {
                renderer?.Dispose();
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                if (secondMaterial) Object.DestroyImmediate(secondMaterial);
                if (material) Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SpatialOrderRemainsSortedAndCompleteAfterStreamingChanges()
        {
            if (!Supported()) Assert.Ignore("InstanceRenderer requires Direct3D 12 compute support.");
            Scene scene = EditorSceneManager.NewPreviewScene();
            var sources = new List<GameObject>();
            Material material = null;
            InstanceRenderer renderer = null;
            try
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                GameObject source = MakeSource(scene, material, sources);
                renderer = new InstanceRenderer(worldContentKind: InstanceWorldContentKind.Detail);
                int type = renderer.Register(InstancePrototype.FromPrefab(source));
                var handles = new List<InstanceHandle>();
                for (int i = 0; i < 257; i++)
                {
                    float x = i % 17 == 0 ? 0 : (i * 71 % 103) - 51;
                    float z = i % 17 == 0 ? 0 : (i * 19 % 67) - 33;
                    float y = i % 17 == 0 ? 0 : i % 7;
                    handles.Add(renderer.Add(type, Matrix4x4.Translate(new Vector3(x, y, z))));
                }
                renderer.Flush();
                AssertSpatialOrder(renderer, type);

                Assert.IsTrue(renderer.Remove(handles[256]));
                object population = GetPopulation(renderer, type);
                FieldInfo sortFrame = population.GetType().GetField("_lastSpatialSortFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(sortFrame);
                sortFrame.SetValue(population, Time.frameCount);
                renderer.TrimExcess();
                Assert.That((int)sortFrame.GetValue(population), Is.EqualTo(-1000));
                renderer.Flush();
                AssertSpatialOrder(renderer, type);

                for (int i = 0; i < handles.Count; i += 5)
                {
                    Assert.IsTrue(renderer.Remove(handles[i]));
                }
                for (int i = 0; i < 40; i++)
                {
                    renderer.Add(type, Matrix4x4.Translate(new Vector3(i - 20, 0, i % 9)));
                }
                renderer.Flush();
                AssertSpatialOrder(renderer, type);
            }
            finally
            {
                renderer?.Dispose();
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                if (material) Object.DestroyImmediate(material);
            }
        }

        private static bool Supported()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 && SystemInfo.supportsComputeShaders;
        }

        private static GameObject MakeSource(Scene scene, Material material, List<GameObject> sources)
        {
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(source, scene);
            Object.DestroyImmediate(source.GetComponent<Collider>());
            source.GetComponent<Renderer>().sharedMaterial = material;
            sources.Add(source);
            return source;
        }

        private static void AssertSourceCells(InstanceRenderer renderer, int expectedRecords, int expectedInstances)
        {
            var records = new List<InstanceWorldSourceCellRecord>();
            Assert.That(InstanceWorldCells.CopySource(renderer.WorldSourceId, records), Is.EqualTo(expectedRecords));
            int instances = 0;
            foreach (InstanceWorldSourceCellRecord record in records)
            {
                Assert.That(record.Residency, Is.EqualTo(InstanceCellResidencyState.Resident));
                Assert.That(record.InstanceCount, Is.GreaterThan(0));
                instances += record.InstanceCount;
            }
            Assert.That(instances, Is.EqualTo(expectedInstances));
        }

        private static object GetPopulation(InstanceRenderer renderer, int prototype)
        {
            FieldInfo populationsField = typeof(InstanceRenderer).GetField("_populations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(populationsField);
            return ((IList)populationsField.GetValue(renderer))[prototype];
        }

        private static void AssertSpatialOrder(InstanceRenderer renderer, int prototype)
        {
            object population = GetPopulation(renderer, prototype);
            Type type = population.GetType();
            int highWater = (int)type.GetField("HighWater", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(population);
            uint[] order = (uint[])type.GetField("_spatialOrder", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(population);
            Array entries = (Array)type.GetField("_spatialEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(population);
            Array bounds = (Array)type.GetField("_bounds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(population);
            bool[] active = (bool[])type.GetField("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(population);
            var centers = new Vector3[highWater];
            Vector3 minimum = Vector3.positiveInfinity;
            Vector3 maximum = Vector3.negativeInfinity;
            for (int slot = 0; slot < highWater; slot++)
            {
                if (!active[slot]) continue;
                object record = bounds.GetValue(slot);
                Vector4 sphere = (Vector4)record.GetType().GetField("Sphere", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(record);
                centers[slot] = new Vector3(sphere.x, sphere.y, sphere.z);
                minimum = Vector3.Min(minimum, centers[slot]);
                maximum = Vector3.Max(maximum, centers[slot]);
            }
            Vector3 extent = maximum - minimum;
            var expected = new List<KeyValuePair<uint, uint>>(highWater);
            for (int slot = 0; slot < highWater; slot++)
            {
                if (!active[slot]) continue;
                Vector3 center = centers[slot];
                var normalized = new Vector3(
                    extent.x > 0.0001f ? (center.x - minimum.x) / extent.x : 0.5f,
                    extent.y > 0.0001f ? (center.y - minimum.y) / extent.y : 0.5f,
                    extent.z > 0.0001f ? (center.z - minimum.z) / extent.z : 0.5f);
                expected.Add(new KeyValuePair<uint, uint>(MortonReference(normalized), (uint)slot));
            }
            for (int slot = 0; slot < highWater; slot++)
            {
                if (!active[slot]) expected.Add(new KeyValuePair<uint, uint>(uint.MaxValue, (uint)slot));
            }
            expected.Sort((left, right) =>
            {
                int byCode = left.Key.CompareTo(right.Key);
                return byCode != 0 ? byCode : left.Value.CompareTo(right.Value);
            });
            var seen = new HashSet<uint>();
            uint previousCode = 0;
            uint previousSlot = 0;
            for (int index = 0; index < highWater; index++)
            {
                object entry = entries.GetValue(index);
                uint code = (uint)entry.GetType().GetField("Code", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(entry);
                uint slot = (uint)entry.GetType().GetField("Slot", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(entry);
                Assert.That(order[index], Is.EqualTo(slot));
                Assert.That(code, Is.EqualTo(expected[index].Key), $"Morton code at index {index}");
                Assert.That(slot, Is.EqualTo(expected[index].Value), $"Spatial slot at index {index}");
                Assert.IsTrue(seen.Add(slot), "Spatial order contains a duplicate slot.");
                if (index > 0) Assert.IsTrue(code > previousCode || code == previousCode && slot > previousSlot);
                previousCode = code;
                previousSlot = slot;
            }
            Assert.That(seen.Count, Is.EqualTo(highWater));
        }

        private static uint MortonReference(Vector3 normalized)
        {
            uint x = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.x * 1023f), 0, 1023);
            uint y = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.y * 1023f), 0, 1023);
            uint z = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.z * 1023f), 0, 1023);
            return SpreadBitsReference(x) | SpreadBitsReference(y) << 1 | SpreadBitsReference(z) << 2;
        }

        private static uint SpreadBitsReference(uint value)
        {
            value &= 0x000003ff;
            value = (value | value << 16) & 0x030000ff;
            value = (value | value << 8) & 0x0300f00f;
            value = (value | value << 4) & 0x030c30c3;
            value = (value | value << 2) & 0x09249249;
            return value;
        }
    }
}

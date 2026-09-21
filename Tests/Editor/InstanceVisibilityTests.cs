using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstanceVisibilityTests
    {
        private struct BoundsRecord
        {
            internal Vector4 Sphere;
            internal Vector4 Lod;
            internal Vector4 State;
        }

        [TestCase(1f, 0f)]
        [TestCase(0.35f, 0f)]
        [TestCase(1f, 20f)]
        [TestCase(0f, 0f)]
        public void CachedAndUncachedSelectionMatchDirect(float density, float pixels)
        {
            const int count = 519;
            var records = new BoundsRecord[count];
            var random = new System.Random(8712);
            for (int i = 0; i < count; i++)
            {
                float x = (i / 64 - 4) * 18 + (float)random.NextDouble() * 8;
                float z = 2 + (float)random.NextDouble() * 30;
                records[i] = new BoundsRecord
                {
                    Sphere = new Vector4(x, 0, z, 1),
                    Lod = new Vector4(x, 0, z, 2),
                    State = new Vector4(i % 13 == 0 ? 0 : 1, 0, 1, (i * 157 % 521) / 521f)
                };
            }
            var shader = Object.Instantiate(Resources.Load<ComputeShader>("LoogaInstanceCulling"));
            using var bounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 48);
            using var selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
            using var clusters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (count + 63) / 64, 16);
            using var visible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count * 2, 4);
            using var arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 10, 4);
            using var history = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            using var instances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 16, 4);
            using var spatialOrder = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4);
            using var occlusionStatistics = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 2, 4);
            try
            {
                bounds.SetData(records);
                spatialOrder.SetData(Enumerable.Range(0, count).Select(value => (uint)value).ToArray());
                int reset = shader.FindKernel("Reset");
                int select = shader.FindKernel("SelectVisibility");
                int cull = shader.FindKernel("Cull");
                shader.SetInt("_Count", count);
                shader.SetInt("_Capacity", count);
                shader.SetInt("_Part", 0);
                shader.SetInt("_PartLod", 0);
                shader.SetInt("_PartFlip", 0);
                shader.SetInt("_PlaneCount", 4);
                shader.SetInt("_SplitCount", 0);
                shader.SetVectorArray("_Planes", new[]
                {
                    new Vector4(1, 0, 0, 15), new Vector4(-1, 0, 0, 15),
                    new Vector4(0, 0, 1, 0), new Vector4(0, 0, -1, 40)
                });
                shader.SetVector("_Camera", new Vector4(0, 0, -10, 0));
                shader.SetVector("_Lod", new Vector4(1, 0, 35, 0));
                shader.SetVector("_Quality", new Vector4(density, pixels, 0, 256));
                shader.SetInt("_LodCount", 1);
                shader.SetVectorArray("_Thresholds", new[] { Vector4.zero, Vector4.zero });
                shader.SetVectorArray("_FadeWidths", new[] { Vector4.zero, Vector4.zero });
                shader.SetInt("_CrossFade", 0);
                shader.SetInt("_AnimatedFade", 0);
                shader.SetInt("_PercentageLods", 0);
                shader.SetInt("_MeshLevels", 1);
                shader.SetInt("_MeshFade", 0);
                shader.SetInt("_UseSpatialOrder", 0);
                shader.SetInt("_MeasureOcclusion", 0);
                shader.SetBuffer(reset, "_Arguments", arguments);
                shader.SetBuffer(select, "_Bounds", bounds);
                shader.SetBuffer(select, "_Clusters", clusters);
                shader.SetBuffer(select, "_Selection", selection);
                shader.SetBuffer(select, "_SpatialOrder", spatialOrder);
                shader.SetBuffer(select, "_OcclusionStatistics", occlusionStatistics);
                shader.SetBuffer(cull, "_Bounds", bounds);
                shader.SetBuffer(cull, "_Selection", selection);
                shader.SetBuffer(cull, "_Visible", visible);
                shader.SetBuffer(cull, "_Arguments", arguments);
                shader.SetBuffer(cull, "_LodHistory", history);
                shader.SetBuffer(cull, "_MeshHistory", history);
                shader.SetBuffer(cull, "_InstanceData", instances);
                uint[] baseline = null;
                for (int mode = 0; mode < 4; mode++)
                {
                    shader.SetInt("_SelectionMode", mode);
                    shader.Dispatch(reset, 1, 1, 1);
                    if (mode > 0)
                    {
                        shader.SetBuffer(select, "_Bounds", bounds);
                        shader.SetBuffer(select, "_Selection", selection);
                        shader.SetBuffer(select, "_Clusters", clusters);
                        shader.Dispatch(select, (count + 63) / 64, 1, 1);
                    }
                    shader.Dispatch(cull, (count + 63) / 64, 1, 1);
                    var args = new uint[10];
                    arguments.GetData(args);
                    var slots = new uint[args[1]];
                    if (slots.Length > 0)
                    {
                        visible.GetData(slots, 0, 0, slots.Length);
                    }
                    Array.Sort(slots);
                    if (baseline == null)
                    {
                        baseline = slots;
                        if (density > 0 && pixels == 0)
                        {
                            Assert.Greater(slots.Length, 0);
                            Assert.Less(slots.Length, count);
                        }
                    }
                    else
                    {
                        CollectionAssert.AreEqual(baseline, slots, "Mode " + mode);
                    }
                    Assert.IsTrue(slots.All(i => records[i].State.x == 1 && records[i].State.w < density));
                }
            }
            finally
            {
                Object.DestroyImmediate(shader);
            }
        }

        [TestCase(0, 0)]
        [TestCase(0, 1)]
        [TestCase(1, 0)]
        [TestCase(1, 1)]
        public void FadeDrawsSeparateOpaqueEndpointsAndPreserveMirroredTransitions(int lod, int flipped)
        {
            const int count = 3;
            var shader = Object.Instantiate(Resources.Load<ComputeShader>("LoogaInstanceCulling"));
            using var bounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 48);
            using var selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
            using var visible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count * 4, 4);
            using var arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 20, 4);
            using var history = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
            using var instances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 16, 4);
            try
            {
                bounds.SetData(Enumerable.Range(0, count).Select(i => new BoundsRecord
                {
                    State = new Vector4(1, flipped, 1, 0)
                }).ToArray());
                // Near, transitioning, and far instances share one prototype and one draw range.
                selection.SetData(new[] { new Vector4(1, 2, 0.5f, 0), new Vector4(1, 6, 0.3f, 0), new Vector4(1, 20, 0.1f, 0) });
                int reset = shader.FindKernel("Reset");
                int cull = shader.FindKernel("Cull");
                shader.SetInt("_Count", count);
                shader.SetInt("_Capacity", count);
                shader.SetInt("_BucketStride", 4);
                shader.SetInt("_Part", 0);
                shader.SetInt("_PartLod", lod);
                shader.SetInt("_PartFlip", 0);
                shader.SetInt("_SelectionMode", 1);
                shader.SetInt("_LodCount", 2);
                shader.SetInt("_CrossFade", 1);
                shader.SetInt("_PercentageLods", 0);
                shader.SetInt("_AnimatedFade", 0);
                shader.SetInt("_MeshLevels", 1);
                shader.SetInt("_MeshFade", 0);
                shader.SetVector("_Lod", Vector4.zero);
                shader.SetVectorArray("_Thresholds", new[] { new Vector4(0.2f, 0, 0, 0), Vector4.zero });
                shader.SetVectorArray("_FadeWidths", new[] { new Vector4(0.2f, 0, 0, 0), Vector4.zero });
                shader.SetBuffer(reset, "_Arguments", arguments);
                shader.SetBuffer(cull, "_Arguments", arguments);
                shader.SetBuffer(cull, "_Bounds", bounds);
                shader.SetBuffer(cull, "_Selection", selection);
                shader.SetBuffer(cull, "_Visible", visible);
                shader.SetBuffer(cull, "_LodHistory", history);
                shader.SetBuffer(cull, "_MeshHistory", history);
                shader.SetBuffer(cull, "_InstanceData", instances);
                shader.Dispatch(reset, 1, 1, 1);
                shader.Dispatch(cull, 1, 1, 1);
                var counts = new uint[20];
                var slots = new uint[count * 4];
                arguments.GetData(counts);
                visible.GetData(slots);
                Assert.AreEqual(1, counts[flipped * 5 + 1]);
                Assert.AreEqual(1, counts[(flipped + 2) * 5 + 1]);
                Assert.AreEqual(0, counts[(1 - flipped) * 5 + 1]);
                Assert.AreEqual(0, counts[(3 - flipped) * 5 + 1]);
                Assert.AreEqual(lod == 0 ? 0u : 2u, slots[flipped * count]);
                uint transition = slots[(flipped + 2) * count];
                Assert.AreEqual(1u, transition & 0xFFFFFF);
                int fade = (int)transition >> 24;
                Assert.AreEqual(lod == 0 ? 85 : -85, fade);
                shader.Dispatch(reset, 1, 1, 1);
                arguments.GetData(counts);
                Assert.IsTrue(Enumerable.Range(0, 4).All(i => counts[i * 5 + 1] == 0));
            }
            finally
            {
                Object.DestroyImmediate(shader);
            }
        }

        [Test]
        public void FusedPartDispatchMatchesSeparatePartDispatches()
        {
            const int count = 8;
            const int partCount = 2;
            var shader = Object.Instantiate(Resources.Load<ComputeShader>("LoogaInstanceCulling"));
            using var bounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 48);
            using var selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
            using var separateVisible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count * partCount * 2, 4);
            using var fusedVisible = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count * partCount * 2, 4);
            using var separateArguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments,
                partCount * 10, 4);
            using var fusedArguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments,
                partCount * 10, 4);
            using var history = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
            using var instances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 16, 4);
            using var parts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, partCount, 64);
            try
            {
                separateArguments.SetData(new uint[partCount * 10]);
                fusedArguments.SetData(new uint[partCount * 10]);
                separateVisible.SetData(new uint[count * partCount * 2]);
                fusedVisible.SetData(new uint[count * partCount * 2]);
                bounds.SetData(Enumerable.Range(0, count).Select(index => new BoundsRecord
                {
                    State = new Vector4(1, index & 1, 1, 0)
                }).ToArray());
                selection.SetData(Enumerable.Range(0, count).Select(index =>
                    new Vector4(1, index, index < count / 2 ? 0.5f : 0.1f, 0)).ToArray());
                parts.SetData(new[]
                {
                    new PartRecord { Header = new Vector4(0, 0, 0, 1) },
                    new PartRecord { Header = new Vector4(1, 0, 0, 1) }
                });
                int reset = shader.FindKernel("Reset");
                int resetAll = shader.FindKernel("ResetAll");
                int cull = shader.FindKernel("Cull");
                int cullParts = shader.FindKernel("CullParts");
                shader.SetInt("_Count", count);
                shader.SetInt("_Capacity", count);
                shader.SetInt("_BucketStride", 2);
                shader.SetInt("_PartCount", partCount);
                shader.SetInt("_SelectionMode", 1);
                shader.SetInt("_LodCount", 2);
                shader.SetInt("_CrossFade", 0);
                shader.SetInt("_PercentageLods", 0);
                shader.SetInt("_AnimatedFade", 0);
                shader.SetInt("_MeshFade", 0);
                shader.SetVector("_Lod", Vector4.zero);
                shader.SetVectorArray("_Thresholds", new[] { new Vector4(0.2f, 0, 0, 0), Vector4.zero });
                shader.SetVectorArray("_FadeWidths", new[] { Vector4.zero, Vector4.zero });
                shader.SetBuffer(cull, "_Bounds", bounds);
                shader.SetBuffer(cull, "_Selection", selection);
                shader.SetBuffer(cull, "_Visible", separateVisible);
                shader.SetBuffer(cull, "_Arguments", separateArguments);
                shader.SetBuffer(cull, "_LodHistory", history);
                shader.SetBuffer(cull, "_MeshHistory", history);
                shader.SetBuffer(cull, "_InstanceData", instances);
                shader.SetBuffer(reset, "_Arguments", separateArguments);
                for (int part = 0; part < partCount; part++)
                {
                    shader.SetInt("_Part", part);
                    shader.SetInt("_PartLod", part);
                    shader.SetInt("_PartFlip", 0);
                    shader.SetInt("_MeshLevel", 0);
                    shader.SetInt("_MeshLevels", 1);
                    shader.Dispatch(reset, 1, 1, 1);
                    shader.Dispatch(cull, 1, 1, 1);
                }
                shader.SetBuffer(resetAll, "_Arguments", fusedArguments);
                shader.SetBuffer(cullParts, "_Bounds", bounds);
                shader.SetBuffer(cullParts, "_Selection", selection);
                shader.SetBuffer(cullParts, "_Visible", fusedVisible);
                shader.SetBuffer(cullParts, "_Arguments", fusedArguments);
                shader.SetBuffer(cullParts, "_LodHistory", history);
                shader.SetBuffer(cullParts, "_MeshHistory", history);
                shader.SetBuffer(cullParts, "_InstanceData", instances);
                shader.SetBuffer(cullParts, "_PartParameters", parts);
                shader.Dispatch(resetAll, 1, 1, 1);
                shader.Dispatch(cullParts, 1, partCount, 1);
                var expectedArguments = new uint[partCount * 10];
                var actualArguments = new uint[partCount * 10];
                var expectedVisible = new uint[count * partCount * 2];
                var actualVisible = new uint[count * partCount * 2];
                separateArguments.GetData(expectedArguments);
                fusedArguments.GetData(actualArguments);
                separateVisible.GetData(expectedVisible);
                fusedVisible.GetData(actualVisible);
                CollectionAssert.AreEqual(expectedArguments, actualArguments);
                CollectionAssert.AreEqual(expectedVisible, actualVisible);
            }
            finally
            {
                Object.DestroyImmediate(shader);
            }
        }

        [TestCase(true, 0.8f, 0.3f, 0f, false)]
        [TestCase(true, 0f, 0.3f, 0f, true)]
        [TestCase(true, 0.8f, 0.9f, 0f, true)]
        [TestCase(false, 0.2f, 0.7f, 0f, false)]
        [TestCase(false, 1f, 0.7f, 0f, true)]
        [TestCase(true, 0.8f, 0.3f, 1f, true)]
        [TestCase(true, 0.8f, 0.01f, 0f, true)]
        public void DepthRefinementRejectsOnlyFullyHiddenBounds(bool reversed, float depth, float z, float x, bool visible)
        {
            var shader = Object.Instantiate(Resources.Load<ComputeShader>("LoogaInstanceCulling"));
            var texture = new Texture2D(4, 4, TextureFormat.RFloat, false, true);
            using var bounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 48);
            using var selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            try
            {
                texture.SetPixelData(Enumerable.Repeat(depth, 16).ToArray(), 0);
                texture.Apply();
                bounds.SetData(new[] { new BoundsRecord { Sphere = new Vector4(x, 0, z, 0.05f) } });
                selection.SetData(new[] { Vector4.one });
                int kernel = shader.FindKernel("ApplyOcclusion");
                shader.SetInt("_Count", 1);
                shader.SetBuffer(kernel, "_Bounds", bounds);
                shader.SetBuffer(kernel, "_Selection", selection);
                shader.SetTexture(kernel, "_OcclusionDepth", texture);
                shader.SetMatrix("_OcclusionViewProjection", Matrix4x4.identity);
                shader.SetVector("_OcclusionSize", new Vector4(4, 4, 1, reversed ? 1 : 0));
                shader.SetFloat("_OcclusionBias", 0.0001f);
                shader.Dispatch(kernel, 1, 1, 1);
                var result = new Vector4[1];
                selection.GetData(result);
                Assert.AreEqual(visible, result[0].x > 0);
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(shader);
            }
        }

        private struct PartRecord
        {
            internal Vector4 Header;
            internal Vector4 Selection;
            internal Vector4 Center;
            internal Vector4 Extents;
        }

        [Test]
        public void DecorativeReductionsRejectGameplayAndColliderSources()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(source, scene);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            source.GetComponent<Renderer>().sharedMaterial = material;
            try
            {
                using var renderer = new InstanceRenderer();
                int withCollider = renderer.Register(InstancePrototype.FromPrefab(source));
                var policy = InstanceQualitySettings.Default;
                policy.Density = 0.5f;
                Assert.Throws<InvalidOperationException>(() => renderer.SetQuality(withCollider, policy));
                policy.Decorative = true;
                Assert.Throws<InvalidOperationException>(() => renderer.SetQuality(withCollider, policy));
                Object.DestroyImmediate(source.GetComponent<Collider>());
                int decorative = renderer.Register(InstancePrototype.FromPrefab(source));
                Assert.DoesNotThrow(() => renderer.SetQuality(decorative, policy));
                var handle = renderer.Add(decorative, Matrix4x4.identity);
                Assert.IsTrue(renderer.SetVisibilityKey(handle, 834u));
                renderer.Remove(handle);
                Assert.IsFalse(renderer.SetVisibilityKey(handle, 123u));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }
    }
}

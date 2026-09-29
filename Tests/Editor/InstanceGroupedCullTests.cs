using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstanceGroupedCullTests
    {
        private const int Count = 4103;
        private const int Capacity = 8192;
        private const int Buckets = 4;

        private struct BoundsData
        {
            public Vector4 Sphere;
            public Vector4 Lod;
            public Vector4 State;
        }

        private struct PartData
        {
            public Vector4 Header;
            public Vector4 Selection;
            public Vector4 Center;
            public Vector4 Extents;
        }

        [TestCase("Cull", "CullGrouped", false)]
        [TestCase("CullParts", "CullPartsGrouped", true)]
        public void GroupedAppendMatchesVisibleLodMirrorAndFadeBuckets(
            string directKernelName, string groupedKernelName, bool fusedParts)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 ||
                !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Grouped instance culling requires Direct3D 12 compute support.");
            }

            ComputeShader shader = Resources.Load<ComputeShader>("LoogaInstanceCulling");
            Assert.IsNotNull(shader);
            int directKernel = shader.FindKernel(directKernelName);
            int groupedKernel = shader.FindKernel(groupedKernelName);
            var boundsData = new BoundsData[Capacity];
            var selectionData = new Vector4[Capacity];
            var expected = new HashSet<uint>[Buckets];
            for (int bucket = 0; bucket < Buckets; bucket++) expected[bucket] = new HashSet<uint>();
            for (int index = 0; index < Count; index++)
            {
                int kind = index % 3;
                bool visible = index % 11 != 0 && kind != 2;
                float ratio = kind == 0 ? 0.3f : kind == 1 ? 0.6f : 0.1f;
                boundsData[index].Sphere = new Vector4(0, 0, 0, 1);
                boundsData[index].State = new Vector4(1, index & 1, 0, 0);
                selectionData[index] = new Vector4(index % 11 == 0 ? 0 : 1, 10, ratio, 0);
                if (visible)
                {
                    int bucket = ((index & 1) ^ 1) + (kind == 0 ? 2 : 0);
                    expected[bucket].Add((uint)index);
                }
            }

            using var bounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, 48);
            using var selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, 16);
            using var arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments,
                Buckets * 5, 4);
            using var visibleIndices = new GraphicsBuffer(GraphicsBuffer.Target.Raw, Buckets * Capacity, 4);
            using var history = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, 16);
            using var instanceData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 16, 4);
            using var partData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 64);
            bounds.SetData(boundsData);
            selection.SetData(selectionData);
            partData.SetData(new[] { new PartData { Header = new Vector4(0, 1, 0, 1) } });

            shader.SetInt("_Count", Count);
            shader.SetInt("_Capacity", Capacity);
            shader.SetInt("_BucketStride", Buckets);
            shader.SetInt("_Part", 0);
            shader.SetInt("_PartCount", 1);
            shader.SetInt("_PartLod", 0);
            shader.SetInt("_PartFlip", 1);
            shader.SetInt("_MeshLevel", 0);
            shader.SetInt("_MeshLevels", 1);
            shader.SetInt("_MeshFade", 0);
            shader.SetInt("_PartMaterialFade", 0);
            shader.SetInt("_SelectionMode", 1);
            shader.SetInt("_CrossFade", 1);
            shader.SetInt("_PercentageLods", 0);
            shader.SetInt("_AnimatedFade", 0);
            shader.SetInt("_LodCount", 2);
            shader.SetVector("_Lod", new Vector4(1, 0, 1000, 0));
            shader.SetVector("_Quality", new Vector4(1, 0, 0, 1080));
            shader.SetVector("_ViewFade", Vector4.zero);
            shader.SetVectorArray("_Thresholds", new[] { new Vector4(0.2f, 0.01f, 0, 0), Vector4.zero });
            shader.SetVectorArray("_FadeWidths", new[] { new Vector4(0.2f, 0, 0, 0), Vector4.zero });

            DispatchAndCheck(shader, directKernel, fusedParts, bounds, selection, arguments,
                visibleIndices, history, instanceData, partData, expected);
            DispatchAndCheck(shader, groupedKernel, fusedParts, bounds, selection, arguments,
                visibleIndices, history, instanceData, partData, expected);
        }

        private static void DispatchAndCheck(ComputeShader shader, int kernel, bool fusedParts,
            GraphicsBuffer bounds, GraphicsBuffer selection, GraphicsBuffer arguments,
            GraphicsBuffer visibleIndices, GraphicsBuffer history, GraphicsBuffer instanceData,
            GraphicsBuffer partData, HashSet<uint>[] expected)
        {
            arguments.SetData(new uint[Buckets * 5]);
            shader.SetBuffer(kernel, "_Bounds", bounds);
            shader.SetBuffer(kernel, "_Selection", selection);
            shader.SetBuffer(kernel, "_Arguments", arguments);
            shader.SetBuffer(kernel, "_Visible", visibleIndices);
            shader.SetBuffer(kernel, "_LodHistory", history);
            shader.SetBuffer(kernel, "_MeshHistory", history);
            shader.SetBuffer(kernel, "_InstanceData", instanceData);
            if (fusedParts) shader.SetBuffer(kernel, "_PartParameters", partData);
            shader.Dispatch(kernel, (Count + 63) / 64, 1, 1);

            var counts = new uint[Buckets * 5];
            var output = new uint[Buckets * Capacity];
            arguments.GetData(counts);
            visibleIndices.GetData(output);
            for (int bucket = 0; bucket < Buckets; bucket++)
            {
                int count = (int)counts[bucket * 5 + 1];
                Assert.That(count, Is.EqualTo(expected[bucket].Count), $"Bucket {bucket}");
                var actual = new HashSet<uint>();
                for (int index = 0; index < count; index++)
                {
                    uint packed = output[bucket * Capacity + index];
                    uint sourceIndex = packed & 0x00ffffffu;
                    Assert.IsTrue(actual.Add(sourceIndex), $"Duplicate slot {sourceIndex} in bucket {bucket}");
                    if (bucket >= 2) Assert.That(packed >> 24, Is.GreaterThan(0u));
                    else Assert.That(packed >> 24, Is.Zero);
                }
                CollectionAssert.AreEquivalent(expected[bucket], actual, $"Bucket {bucket}");
            }
        }
    }
}

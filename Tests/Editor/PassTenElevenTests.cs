using System;
using System.IO;
using System.Threading;
using LoogaSoft.Instancing.Editor;
using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class PassTenElevenTests
    {
        [Test]
        public void ForestReducerIsDeterministicAndRemovesOverlap()
        {
            var placements = new[]
            {
                Placement("c", 0), Placement("a", 0.5f), Placement("b", 4), Placement("d", 8)
            };
            InstanceContainer.Placement[] first = ForestHlodBaker.Reduce(placements, null, 2, 2);
            InstanceContainer.Placement[] second = ForestHlodBaker.Reduce(placements, null, 2, 2);
            Assert.AreEqual(2, first.Length);
            Assert.AreEqual(first[0].Id, second[0].Id);
            Assert.AreEqual(first[1].Id, second[1].Id);
            Assert.GreaterOrEqual(Vector3.Distance(first[0].Position, first[1].Position), 2);
        }

        [Test]
        public void ForestShadowPolicyScalesDistanceLodAndFrequency()
        {
            ForestShadowPolicy policy = ForestShadowPolicy.Default;
            policy.Validate();
            Assert.AreEqual(0, policy.MinimumLod(10));
            Assert.AreEqual(policy.MiddleMinimumLod, policy.MinimumLod(150));
            Assert.AreEqual(policy.FarMinimumLod, policy.MinimumLod(500));
            Assert.AreEqual(1, policy.UpdateInterval(10));
            Assert.Greater(policy.UpdateInterval(500), policy.UpdateInterval(150));
        }

        [Test]
        public void CompressedPageRoundTripReportsBudgetsAndRejectsSmallLimits()
        {
            string directory = Path.Combine(Path.GetTempPath(), "LoogaCompressedPages-" + Guid.NewGuid().ToString("N"));
            try
            {
                var records = new VegetationPageRecord[64];
                for (int i = 0; i < records.Length; i++)
                    records[i] = new VegetationPageRecord(0, Matrix4x4.Translate(Vector3.right * i), InstanceAppearance.Default, "tree-" + i);
                FileVegetationPageSource.Write(directory, Vector2Int.zero, true, 7, records, true);
                var source = new FileVegetationPageSource(directory, 7, 128);
                var result = source.LoadAsync(new VegetationPageRequest(Vector2Int.zero, true, 1 << 20, 1 << 20),
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(records.Length, result.Records.Length);
                Assert.Greater(result.DiskBytes, 0);
                Assert.Greater(result.DecompressedBytes, result.DiskBytes);
                Assert.Throws<InvalidDataException>(() => source.LoadAsync(new VegetationPageRequest(Vector2Int.zero, true, 16, 1 << 20),
                    CancellationToken.None).GetAwaiter().GetResult());
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void AddedStreamingBudgetFieldsNormalizeLegacyValues()
        {
            var legacy = new VegetationStreamingBudget
            {
                PlacementRecords = 8, UploadBytes = 4096, CpuMilliseconds = 1, ResidentGpuBytes = 4096
            };
            VegetationStreamingBudget current = legacy.Normalized();
            Assert.Greater(current.DiskReadBytes, 0);
            Assert.Greater(current.DecompressionBytes, 0);
            Assert.Greater(current.ConcurrentReads, 0);
            Assert.Greater(current.ResidencyChanges, 0);
        }

        [Test]
        public void ExplicitInterestVelocityPredictsWithoutChangingTransform()
        {
            var host = new GameObject("Interest");
            try
            {
                var interest = host.AddComponent<VegetationInterest>();
                interest.SetVelocity(new Vector3(10, 0, 2));
                Assert.AreEqual(new Vector3(15, 0, 3), interest.PredictPosition(1.5f));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        private static InstanceContainer.Placement Placement(string id, float x) => new InstanceContainer.Placement
        { Id = id, Position = Vector3.right * x, Scale = Vector3.one };
    }
}

using LoogaSoft.Instancing;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class VegetationPageTests
    {
        [Test]
        public void DiskPagesRoundTripRejectStaleRevisionsAndHonorCancellation()
        {
            string directory = Path.Combine(Path.GetTempPath(), "LoogaPages-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "trees-2-3.lvpage");
            try
            {
                var record = new VegetationPageRecord(3, Matrix4x4.TRS(new Vector3(2, 5, 7), Quaternion.Euler(0, 35, 0), Vector3.one),
                    new InstanceAppearance(Color.green, Color.white, new Vector4(0.4f, 2, 3, 4)));
                FileVegetationPageSource.Write(directory, new Vector2Int(2, 3), true, 19, new[] { record });
                var source = new FileVegetationPageSource(directory, 19, 4);
                var result = source.LoadAsync(new Vector2Int(2, 3), true, CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(1, result.Length);
                Assert.AreEqual(record.Prototype, result[0].Prototype);
                Assert.AreEqual(record.LocalTransform, result[0].LocalTransform);
                Assert.AreEqual(record.Appearance, result[0].Appearance);
                var stale = new FileVegetationPageSource(directory, 20, 4);
                Assert.Throws<InvalidDataException>(() => stale.LoadAsync(new Vector2Int(2, 3), true, CancellationToken.None).GetAwaiter().GetResult());
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Assert.Throws<TaskCanceledException>(() => source.LoadAsync(new Vector2Int(2, 3), true, cancellation.Token).GetAwaiter().GetResult());
                File.WriteAllBytes(path, new byte[21]);
                Assert.Throws<InvalidDataException>(() => source.LoadAsync(new Vector2Int(2, 3), true, CancellationToken.None).GetAwaiter().GetResult());
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory);
                }
            }
        }

        [Test]
        public void BudgetedUploadsSurviveGrowthAndTrimWithoutRevivingOldHandles()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            root.GetComponent<Renderer>().sharedMaterial = material;
            try
            {
                using var renderer = new InstanceRenderer { RenderingEnabled = false };
                int prototype = renderer.Register(InstancePrototype.FromPrefab(root));
                var handles = new InstanceHandle[257];
                for (int i = 0; i < handles.Length; i++)
                {
                    handles[i] = renderer.Add(prototype, Matrix4x4.Translate(new Vector3(i, 0, 0)));
                }
                int updates = 0;
                while (renderer.HasPendingUploads && updates++ < 100)
                {
                    long before = renderer.UploadedBytes;
                    renderer.FlushBudget(4096);
                    Assert.LessOrEqual(renderer.UploadedBytes - before, 4096);
                }
                Assert.IsFalse(renderer.HasPendingUploads);
                Assert.Greater(updates, 1);
                for (int i = 1; i < handles.Length; i++)
                {
                    Assert.IsTrue(renderer.Remove(handles[i]));
                }
                renderer.TrimExcess();
                renderer.Flush();
                Assert.IsTrue(renderer.Update(handles[0], Matrix4x4.Translate(Vector3.up)));
                var replacement = renderer.Add(prototype, Matrix4x4.identity);
                Assert.IsFalse(renderer.Remove(handles[1]));
                Assert.IsTrue(renderer.Remove(replacement));
                Assert.IsTrue(renderer.Remove(handles[0]));
                renderer.TrimExcess();
                Assert.AreEqual(0, renderer.GpuBytes);
                Assert.IsFalse(renderer.HasPendingUploads);
                replacement = renderer.Add(prototype, Matrix4x4.identity);
                Assert.IsFalse(renderer.Remove(handles[0]));
                Assert.IsTrue(renderer.Remove(replacement));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}

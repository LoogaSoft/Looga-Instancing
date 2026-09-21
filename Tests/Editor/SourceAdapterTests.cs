using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class SourceAdapterTests
    {
        private sealed class RuntimeSource : IRuntimeInstanceSource
        {
            public event Action Changed;
            internal RuntimeInstanceSnapshot Snapshot;
            internal string Failure;

            public bool TryCapture(out RuntimeInstanceSnapshot snapshot, out string diagnostic)
            {
                snapshot = Snapshot;
                diagnostic = Failure;
                return Failure == null;
            }

            internal void Notify() => Changed?.Invoke();
        }

        [Test]
        public void StatusCombinesMixedNativeAndLoogaCoverage()
        {
            var native = new InstanceSourceAdapterStatus(2, 1, 1, false, "one native fallback");
            var waiting = new InstanceSourceAdapterStatus(1, 0, 1, true, null);
            InstanceSourceAdapterStatus combined = InstanceSourceAdapterStatus.Combine(native, waiting);
            Assert.AreEqual(3, combined.RequestedSources);
            Assert.AreEqual(1, combined.LoogaOwnedSources);
            Assert.AreEqual(2, combined.NativeFallbackSources);
            Assert.IsTrue(combined.HasCompleteCoverage);
            Assert.IsTrue(combined.IsWaiting);
            StringAssert.Contains("native fallback", combined.Diagnostic);
        }

        [Test]
        public void RuntimeAdapterCommitsCompleteRevisionsAndKeepsLastGoodRevisionOnFailure()
        {
            GameObject prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var targetObject = new GameObject("Runtime target");
            var adapterObject = new GameObject("Runtime adapter");
            try
            {
                var target = targetObject.AddComponent<InstanceContainer>();
                var adapter = adapterObject.AddComponent<RuntimeInstanceSourceAdapter>();
                var source = new RuntimeSource
                {
                    Snapshot = new RuntimeInstanceSnapshot(prototype, new[]
                    {
                        new InstanceContainer.Placement { Id = "first", Scale = Vector3.one }
                    })
                };
                adapter.Configure(source, target);
                Assert.IsTrue(adapter.SourceStatus.HasCompleteCoverage, adapter.Diagnostic);
                Assert.AreEqual(1, target.PlacementCount);
                Assert.AreEqual("first", target.CopyPlacements()[0].Id);

                source.Failure = "source revision incomplete";
                source.Notify();
                Assert.IsFalse(adapter.ProcessChanges());
                Assert.AreEqual("first", target.CopyPlacements()[0].Id);
                StringAssert.Contains("incomplete", adapter.Diagnostic);

                source.Failure = null;
                source.Snapshot = new RuntimeInstanceSnapshot(prototype, new[]
                {
                    new InstanceContainer.Placement { Id = "second", Position = Vector3.right, Scale = Vector3.one }
                });
                source.Notify();
                Assert.IsTrue(adapter.ProcessChanges(), adapter.Diagnostic);
                Assert.AreEqual("second", target.CopyPlacements()[0].Id);

                adapter.enabled = false;
                Assert.AreEqual(0, target.PlacementCount);
                Assert.IsNull(target.Prototype);
            }
            finally
            {
                Object.DestroyImmediate(adapterObject);
                Object.DestroyImmediate(targetObject);
                Object.DestroyImmediate(prototype);
            }
        }

        [Test]
        public void SceneAdapterReportsNativeFallbackAndRestoresItWhenDisabled()
        {
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var host = new GameObject("Scene adapter");
            try
            {
                var provider = host.AddComponent<SceneInstanceProvider>();
                provider.Configure(new[] { source });
                Assert.AreEqual(1, provider.SourceStatus.LoogaOwnedSources, provider.Diagnostic);
                Assert.AreEqual(0, provider.SourceStatus.NativeFallbackSources);
                provider.enabled = false;
                Assert.AreEqual(0, provider.SourceStatus.LoogaOwnedSources);
                Assert.AreEqual(1, provider.SourceStatus.NativeFallbackSources);
                Assert.IsFalse(source.GetComponent<Renderer>().forceRenderingOff);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(source);
            }
        }
    }
}

using System.Linq;
using LoogaSoft.Instancing.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class SharedArchitectureTests
    {
        [Test]
        public void DeferredCullCommandsKeepCameraOrderAndOwnership()
        {
            var first = new GameObject("First culling camera").AddComponent<Camera>();
            var second = new GameObject("Second culling camera").AddComponent<Camera>();
            var owner = new InstanceRenderer();
            var feature = new object();
            var seen = new System.Collections.Generic.List<string>();
            try
            {
                var unsupported = CommandBufferPool.Get("unsupported");
                try
                {
                    Assert.IsFalse(InstanceCullScheduler.TryQueue(owner, first, default, unsupported));
                }
                finally
                {
                    CommandBufferPool.Release(unsupported);
                }

                InstanceCullScheduler.Begin(first, feature);
                InstanceCullScheduler.Begin(second, feature);
                Assert.IsTrue(InstanceCullScheduler.TryQueue(owner, first, default, CommandBufferPool.Get("first 1")));
                Assert.IsTrue(InstanceCullScheduler.TryQueue(owner, second, default, CommandBufferPool.Get("second")));
                Assert.IsTrue(InstanceCullScheduler.TryQueue(owner, first, default, CommandBufferPool.Get("first 2")));

                Assert.AreEqual(1, InstanceCullScheduler.Drain(second, feature, (_, commands) => seen.Add(commands.name)));
                CollectionAssert.AreEqual(new[] { "second" }, seen);
                Assert.AreEqual(2, InstanceCullScheduler.Drain(first, feature, (_, commands) => seen.Add(commands.name)));
                CollectionAssert.AreEqual(new[] { "second", "first 1", "first 2" }, seen);
                Assert.AreEqual(0, InstanceCullScheduler.Drain(first, feature, (_, commands) => seen.Add(commands.name)));
            }
            finally
            {
                InstanceCullScheduler.Cancel(first);
                InstanceCullScheduler.Cancel(second);
                owner.Dispose();
                Object.DestroyImmediate(first.gameObject);
                Object.DestroyImmediate(second.gameObject);
            }
        }

        [Test]
        public void DisposingRendererKeepsOtherOwnerWork()
        {
            var camera = new GameObject("Shared culling camera").AddComponent<Camera>();
            var first = new InstanceRenderer();
            var second = new InstanceRenderer();
            var feature = new object();
            var seen = new System.Collections.Generic.List<string>();
            try
            {
                InstanceCullScheduler.Begin(camera, feature);
                Assert.IsTrue(InstanceCullScheduler.TryQueue(first, camera, default, CommandBufferPool.Get("first owner")));
                Assert.IsTrue(InstanceCullScheduler.TryQueue(second, camera, default, CommandBufferPool.Get("second owner")));
                first.Dispose();
                Assert.AreEqual(1, InstanceCullScheduler.Drain(camera, feature, (_, commands) => seen.Add(commands.name)));
                CollectionAssert.AreEqual(new[] { "second owner" }, seen);
            }
            finally
            {
                InstanceCullScheduler.Cancel(camera);
                first.Dispose();
                second.Dispose();
                Object.DestroyImmediate(camera.gameObject);
            }
        }

        [Test]
        public void FeatureCancellationKeepsOtherFeatureScopes()
        {
            var firstCamera = new GameObject("First feature camera").AddComponent<Camera>();
            var secondCamera = new GameObject("Second feature camera").AddComponent<Camera>();
            var owner = new InstanceRenderer();
            var firstFeature = new object();
            var secondFeature = new object();
            var seen = new System.Collections.Generic.List<string>();
            try
            {
                InstanceCullScheduler.Begin(firstCamera, firstFeature);
                InstanceCullScheduler.Begin(secondCamera, secondFeature);
                Assert.IsTrue(InstanceCullScheduler.TryQueue(owner, firstCamera, default, CommandBufferPool.Get("first feature")));
                Assert.IsTrue(InstanceCullScheduler.TryQueue(owner, secondCamera, default, CommandBufferPool.Get("second feature")));
                Assert.AreEqual(1, InstanceCullScheduler.CancelFeature(firstFeature));
                Assert.AreEqual(0, InstanceCullScheduler.Drain(firstCamera, firstFeature, (_, commands) => seen.Add(commands.name)));
                Assert.AreEqual(1, InstanceCullScheduler.Drain(secondCamera, secondFeature, (_, commands) => seen.Add(commands.name)));
                CollectionAssert.AreEqual(new[] { "second feature" }, seen);
            }
            finally
            {
                InstanceCullScheduler.Cancel(firstCamera);
                InstanceCullScheduler.Cancel(secondCamera);
                owner.Dispose();
                Object.DestroyImmediate(firstCamera.gameObject);
                Object.DestroyImmediate(secondCamera.gameObject);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void MeshLodDrawRangeIncludesSubmeshOffset(int level)
        {
            var mesh = new Mesh();
            var root = new GameObject("Submesh range test");
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
                mesh.subMeshCount = 2;
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                mesh.SetTriangles(new[] { 1, 3, 2, 0, 1, 2 }, 1);
                mesh.lodCount = 2;
                mesh.SetLod(0, 0, new MeshLodRange { indexStart = 0, indexCount = 3 });
                mesh.SetLod(0, 1, new MeshLodRange { indexStart = 0, indexCount = 3 });
                mesh.SetLod(1, 0, new MeshLodRange { indexStart = 0, indexCount = 6 });
                mesh.SetLod(1, 1, new MeshLodRange { indexStart = 3, indexCount = 3 });
                var renderer = root.AddComponent<MeshRenderer>();
                var part = new InstancePrototype.Part(mesh, material, Matrix4x4.identity, 1, 0, renderer, level);
                Assert.AreEqual(level == 0 ? 3u : 6u, part.IndexStart);
                Assert.AreEqual(level == 0 ? 6u : 3u, part.IndexCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SharedAssembliesDoNotDependOnEitherProductOrReplacedRenderers()
        {
            foreach (var assembly in new[] { typeof(InstanceRenderer).Assembly, typeof(InstanceConversionTools).Assembly })
            {
                var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
                Assert.IsFalse(references.Any(name => name.StartsWith("LoogaSoft.Terrain") || name.StartsWith("LoogaSoft.Lighting") ||
                    name.Contains("Flora") || name.Contains("FoliageRenderer") || name.Contains("BRGInstancedRenderer")), string.Join(", ", references));
            }
            Assert.AreEqual("LoogaSoft.Instancing", typeof(InstanceContainer).Assembly.GetName().Name);
        }

        [Test]
        public void InstanceMaterialBindingDoesNotMutateTheSourceAndReleasesAfterDrawOwnership()
        {
            var source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.SetActive(false);
            root.GetComponent<Renderer>().sharedMaterial = source;
            var profile = ScriptableObject.CreateInstance<LeaseProfile>();
            var renderer = new InstanceRenderer();
            try
            {
                renderer.Register(InstancePrototype.FromPrefab(root, profile));
                Assert.AreEqual(1, profile.Created);
                Assert.AreEqual(0, profile.Released);
                Assert.AreNotSame(source, profile.LastMaterial);
                Assert.AreEqual(Color.white, source.GetColor("_BaseColor"));
                renderer.Dispose();
                Assert.AreEqual(1, profile.Released);
                Assert.IsFalse(profile.LastMaterial);
                renderer.Dispose();
                Assert.AreEqual(1, profile.Released);
            }
            finally
            {
                renderer.Dispose();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(profile);
            }
        }

        private sealed class LeaseProfile : InstanceMaterialProfile
        {
            public int Created;
            public int Released;
            public Material LastMaterial;
            public override bool Supports(Material material) => true;
            public override float BoundsPadding => 0;
            public override InstanceMaterialBinding CreateMaterial(Material source)
            {
                Created++;
                LastMaterial = new Material(source);
                LastMaterial.SetColor("_BaseColor", Color.red);
                return new Lease(this);
            }
            private sealed class Lease : InstanceMaterialBinding
            {
                private readonly LeaseProfile _owner;
                public override Material Material => _owner.LastMaterial;
                internal Lease(LeaseProfile owner) => _owner = owner;
                public override void Dispose()
                {
                    _owner.Released++;
                    Object.DestroyImmediate(_owner.LastMaterial);
                }
            }
        }
    }
}

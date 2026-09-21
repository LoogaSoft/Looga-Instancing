using System.Linq;
using LoogaSoft.Instancing.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class SharedArchitectureTests
    {
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

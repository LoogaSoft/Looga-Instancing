using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstancePrototypeCompilerTests
    {
        [SetUp]
        public void Setup()
        {
            InstancePrototypeCompiler.ClearCache();
        }

        [TearDown]
        public void Cleanup()
        {
            InstancePrototypeCompiler.ClearCache();
        }

        [Test]
        public void OrdinaryMultiRendererLodCompilesWithoutChangingSourceObjects()
        {
            GameObject root = new GameObject("Ordinary prefab");
            GameObject near = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject far = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            near.transform.SetParent(root.transform, false);
            far.transform.SetParent(root.transform, false);
            near.GetComponent<Renderer>().sharedMaterial = material;
            far.GetComponent<Renderer>().sharedMaterial = material;
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[]
            {
                new LOD(0.4f, new[] { near.GetComponent<Renderer>() }),
                new LOD(0.1f, new[] { far.GetComponent<Renderer>() })
            });
            try
            {
                InstancePrototypeCompilation result = InstancePrototypeCompiler.Compile(root);
                Assert.IsTrue(result.Succeeded, result.Summary);
                Assert.AreEqual(2, result.Descriptor.RendererCount);
                Assert.AreEqual(2, result.Descriptor.MeshCount);
                Assert.AreEqual(1, result.Descriptor.MaterialCount);
                Assert.AreEqual(2, result.Descriptor.PartCount);
                Assert.AreEqual(2, result.Descriptor.LodCount);
                Assert.Greater(result.Descriptor.LocalBounds.size.sqrMagnitude, 0);
                Assert.IsTrue(result.Descriptor.HasColliders);
                Assert.IsFalse(result.ReusedDerivedData);
                Assert.IsFalse(near.GetComponent<Renderer>().forceRenderingOff);
                Assert.IsFalse(far.GetComponent<Renderer>().forceRenderingOff);
                Assert.AreSame(material, near.GetComponent<Renderer>().sharedMaterial);

                InstancePrototypeCompilation repeated = InstancePrototypeCompiler.Compile(root);
                Assert.IsTrue(repeated.ReusedDerivedData);
                Assert.AreEqual(result.SourceRevision, repeated.SourceRevision);

                near.SetActive(false);
                InstancePrototypeCompilation hierarchyChanged = InstancePrototypeCompiler.Compile(root);
                Assert.IsTrue(hierarchyChanged.Succeeded, hierarchyChanged.Summary);
                Assert.IsFalse(hierarchyChanged.ReusedDerivedData);
                Assert.AreNotEqual(result.SourceRevision, hierarchyChanged.SourceRevision);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void MaterialChangeRebuildsOnlyTheAffectedDerivedPrototype()
        {
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Material firstMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            Material secondMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            first.GetComponent<Renderer>().sharedMaterial = firstMaterial;
            second.GetComponent<Renderer>().sharedMaterial = secondMaterial;
            try
            {
                Assert.IsFalse(InstancePrototypeCompiler.Compile(first).ReusedDerivedData);
                Assert.IsFalse(InstancePrototypeCompiler.Compile(second).ReusedDerivedData);
                Assert.AreEqual(2, InstancePrototypeCompiler.CachedPrototypeCount);
                Assert.AreEqual(1, InstancePrototypeCompiler.InvalidateDependency(firstMaterial));
                Assert.AreEqual(1, InstancePrototypeCompiler.CachedPrototypeCount);
                Assert.IsTrue(InstancePrototypeCompiler.Compile(second).ReusedDerivedData);
                Assert.IsFalse(InstancePrototypeCompiler.Compile(first).ReusedDerivedData);

                firstMaterial.SetColor("_BaseColor", Color.red);
                InstancePrototypeCompilation changed = InstancePrototypeCompiler.Compile(first);
                Assert.IsFalse(changed.ReusedDerivedData);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(firstMaterial);
                Object.DestroyImmediate(secondMaterial);
            }
        }

        [Test]
        public void UnsupportedRendererProducesStructuredNativeFallbackReport()
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var child = new GameObject("Animated child");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<SkinnedMeshRenderer>();
            try
            {
                InstancePrototypeCompilation result = InstancePrototypeCompiler.Compile(root);
                Assert.IsFalse(result.Succeeded);
                Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "SkinnedMeshRenderer" &&
                    item.Severity == InstancePrototypeDiagnosticSeverity.Error));
                StringAssert.Contains("Animated child", result.Summary);
                Assert.Throws<System.NotSupportedException>(() => InstancePrototype.FromPrefab(root));
                Assert.IsFalse(root.GetComponent<Renderer>().forceRenderingOff);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AdditionalNativeRendererIsReportedWithoutBlockingMeshCompilation()
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var lineObject = new GameObject("Native line");
            lineObject.transform.SetParent(root.transform, false);
            lineObject.AddComponent<LineRenderer>();
            try
            {
                InstancePrototypeCompilation result = InstancePrototypeCompiler.Compile(root);
                Assert.IsTrue(result.Succeeded, result.Summary);
                Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "NativeRenderer" &&
                    item.Severity == InstancePrototypeDiagnosticSeverity.Warning));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ExplicitSpeedTreeProfileCapturesLodAndWindMetadata()
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(0.1f, new[] { root.GetComponent<Renderer>() }) });
            group.fadeMode = LODFadeMode.SpeedTree;
            var profile = ScriptableObject.CreateInstance<TestSpeedTreeProfile>();
            try
            {
                InstancePrototypeCompilation result = InstancePrototypeCompiler.Compile(root, profile);
                Assert.IsTrue(result.Succeeded, result.Summary);
                Assert.IsTrue(result.Descriptor.UsesSpeedTreeLod);
                Assert.IsTrue(result.Descriptor.HasWind);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(profile);
            }
        }

        private sealed class TestSpeedTreeProfile : InstanceMaterialProfile
        {
            public override bool Supports(Material material) => true;
            public override float BoundsPadding => 1;
            public override bool SpeedTreeLod => true;
        }
    }
}

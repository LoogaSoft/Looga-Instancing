using LoogaSoft.Instancing;
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class SceneInstanceProviderTests
    {
        private GameObject _source;
        private GameObject _host;
        private Material _material;
        private SceneInstanceProvider _provider;

        [SetUp]
        public void Setup()
        {
            _source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _source.GetComponent<Renderer>().sharedMaterial = _material;
            _host = new GameObject("Scene provider test");
            _provider = _host.AddComponent<SceneInstanceProvider>();
        }

        [TearDown]
        public void Cleanup()
        {
            Object.DestroyImmediate(_host);
            Object.DestroyImmediate(_source);
            Object.DestroyImmediate(_material);
        }

        [Test]
        public void OwnershipPreservesObjectsMaterialsCollidersAndRestoresOnDisable()
        {
            var native = _source.GetComponent<Renderer>();
            var collider = _source.GetComponent<Collider>();
            _provider.Configure(new[] { _source });
            Assert.AreEqual(1, _provider.ActiveSourceCount, _provider.Diagnostic);
            Assert.IsTrue(native.enabled);
            Assert.IsTrue(native.forceRenderingOff);
            Assert.IsTrue(collider.enabled);
            Assert.AreSame(_material, native.sharedMaterial);
            _source.transform.position = new Vector3(20, 1, 0);
            _provider.Synchronize();
            Assert.AreEqual(1, _provider.ActiveSourceCount);
            _provider.enabled = false;
            Assert.IsFalse(native.forceRenderingOff);
            Assert.IsTrue(native.enabled);
            Assert.IsTrue(collider.enabled);
            _provider.enabled = true;
            Assert.AreEqual(1, _provider.ActiveSourceCount);
        }

        [Test]
        public void IdenticalSceneRootsShareOneDrawPrototype()
        {
            var second = Object.Instantiate(_source);
            try
            {
                second.transform.position = Vector3.right * 10;
                _provider.Configure(new[] { _source, second });
                Assert.AreEqual(2, _provider.ActiveSourceCount, _provider.Diagnostic);
                Assert.AreEqual(1, _provider.PrototypeCount);
            }
            finally
            {
                _provider.enabled = false;
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void RotatedAndScaledCopiesWithChildMeshesShareOneDrawPrototype()
        {
            // Scattered copies of one prefab: a root with an offset child mesh, each with its own rotation and scale.
            var roots = new GameObject[4];
            try
            {
                for (int index = 0; index < roots.Length; index++)
                {
                    roots[index] = new GameObject("Scattered root " + index);
                    GameObject child = Object.Instantiate(_source, roots[index].transform);
                    child.transform.localPosition = new Vector3(0.3f, 1.1f, -0.2f);
                    child.transform.localRotation = Quaternion.Euler(10f, 20f, 0f);
                    roots[index].transform.SetPositionAndRotation(new Vector3(index * 17.3f, 3.7f, index * -9.1f),
                        Quaternion.Euler(index * 11.7f, index * 73.9f, index * 5.3f));
                    roots[index].transform.localScale = Vector3.one * (0.83f + index * 0.61f);
                }
                _provider.Configure(Array.ConvertAll(roots, root => root.transform.GetChild(0).gameObject));
                Assert.AreEqual(roots.Length, _provider.ActiveSourceCount, _provider.Diagnostic);
                Assert.AreEqual(1, _provider.PrototypeCount);

                _provider.Configure(roots);
                Assert.AreEqual(roots.Length, _provider.ActiveSourceCount, _provider.Diagnostic);
                Assert.AreEqual(1, _provider.PrototypeCount);
            }
            finally
            {
                _provider.enabled = false;
                foreach (GameObject root in roots)
                {
                    if (root) Object.DestroyImmediate(root);
                }
            }
        }

        [Test]
        public void NestedRootsStayNativeAndOtherRootsDraw()
        {
            GameObject parent = Object.Instantiate(_source);
            GameObject child = Object.Instantiate(_source, parent.transform);
            try
            {
                _provider.Configure(new[] { parent, child, _source });
                Assert.AreEqual(1, _provider.ActiveSourceCount);
                StringAssert.Contains("Nested source roots overlap.", _provider.Diagnostic);
                Assert.IsTrue(_source.GetComponent<Renderer>().forceRenderingOff);
                Assert.IsFalse(parent.GetComponent<Renderer>().forceRenderingOff);
                Assert.IsFalse(child.GetComponent<Renderer>().forceRenderingOff);
            }
            finally
            {
                _provider.enabled = false;
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void StaticSourcesSkipChangeChecksUntilRebuild()
        {
            _provider.TrackSourceChanges = false;
            _provider.Configure(new[] { _source });
            Assert.AreEqual(1, _provider.ActiveSourceCount, _provider.Diagnostic);
            _material.renderQueue = 3000;
            _provider.Synchronize();
            Assert.AreEqual(1, _provider.ActiveSourceCount);
            _provider.Rebuild();
            Assert.AreEqual(0, _provider.ActiveSourceCount);
            Assert.IsFalse(_source.GetComponent<Renderer>().forceRenderingOff);
        }

        [Test]
        public void UnsupportedMaterialChangeRestoresNativeRendering()
        {
            _provider.Configure(new[] { _source });
            _material.renderQueue = 3000;
            _provider.Synchronize();
            Assert.AreEqual(0, _provider.ActiveSourceCount);
            Assert.IsFalse(_source.GetComponent<Renderer>().forceRenderingOff);
            StringAssert.Contains("Transparent", _provider.Diagnostic);
            _material.renderQueue = 2000;
            _provider.Rebuild();
            Assert.AreEqual(1, _provider.ActiveSourceCount);
        }

        [Test]
        public void PropertyBlocksStayNativeAndDoNotLoseOverrides()
        {
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", Color.red);
            var native = _source.GetComponent<Renderer>();
            native.SetPropertyBlock(block);
            _provider.Configure(new[] { _source });
            Assert.AreEqual(0, _provider.ActiveSourceCount);
            Assert.IsFalse(native.forceRenderingOff);
            Assert.IsTrue(native.HasPropertyBlock());
        }

        [Test]
        public void DuplicateProviderDoesNotStealOrReleaseOwnership()
        {
            _provider.Configure(new[] { _source });
            var otherObject = new GameObject("Second provider");
            try
            {
                var other = otherObject.AddComponent<SceneInstanceProvider>();
                other.Configure(new[] { _source });
                Assert.AreEqual(0, other.ActiveSourceCount);
                other.enabled = false;
                Assert.IsTrue(_source.GetComponent<Renderer>().forceRenderingOff);
            }
            finally
            {
                Object.DestroyImmediate(otherObject);
            }
        }

        [Test]
        public void DisabledAndDeletedSourcesReleaseTheirDraws()
        {
            _provider.Configure(new[] { _source });
            _source.SetActive(false);
            _provider.Synchronize();
            Assert.AreEqual(0, _provider.ActiveSourceCount);
            Assert.IsFalse(_source.GetComponent<Renderer>().forceRenderingOff);
            _source.SetActive(true);
            _provider.Synchronize();
            Assert.AreEqual(1, _provider.ActiveSourceCount);
            Object.DestroyImmediate(_source);
            _provider.Synchronize();
            Assert.AreEqual(0, _provider.ActiveSourceCount);
        }

        [Test]
        public void ShaderWideDotsKeywordDoesNotHideAnUnsupportedSurfacePass()
        {
            var material = new Material(Shader.Find("Hidden/Looga/Tests/Incomplete BRG"));
            try
            {
                Assert.IsTrue(material.shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON").isValid);
                Assert.IsFalse(InstanceShaderInspection.TryValidate(material, InstanceShaderCapabilities.Surface, out var reason));
                StringAssert.Contains("UniversalForwardOnly", reason);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void DisabledObjectMotionKeepsNativeMaterialPolicy()
        {
            _material.SetShaderPassEnabled("MotionVectors", false);
            _provider.Configure(new[] { _source });
            Assert.AreEqual(1, _provider.ActiveSourceCount, _provider.Diagnostic);
            Assert.IsFalse(_material.GetShaderPassEnabled("MotionVectors"));
            Assert.IsFalse(InstanceShaderInspection.TryValidate(_material, InstanceShaderCapabilities.Motion, out _));
        }

        [Test]
        public void RequiredPassChecksRespectMaterialDisabledPasses()
        {
            _material.SetShaderPassEnabled("DepthOnly", false);
            Assert.IsFalse(InstanceShaderInspection.TryValidate(_material, InstanceShaderCapabilities.Depth, out var reason));
            StringAssert.Contains("Depth", reason);
        }
    }
}

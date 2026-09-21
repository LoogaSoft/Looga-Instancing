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

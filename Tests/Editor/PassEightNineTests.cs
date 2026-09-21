using System;
using LoogaSoft.Instancing.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class PassEightNineTests
    {
        private Scene _scene;
        private GameObject _source;
        private GameObject _host;
        private GameObject _proxy;
        private GameObject _interestObject;
        private Material _material;
        private InstanceContainer _container;
        private InteractiveInstanceResidency _residency;

        [SetUp]
        public void Setup()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _source.name = "Pass 8 source";
            SceneManager.MoveGameObjectToScene(_source, _scene);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _source.GetComponent<Renderer>().sharedMaterial = _material;
            _host = new GameObject("Pass 8 host");
            SceneManager.MoveGameObjectToScene(_host, _scene);
            _container = _host.AddComponent<InstanceContainer>();
            _container.Configure(_source, new[]
            {
                new InstanceContainer.Placement { Id = "tree-a", Position = Vector3.zero, Scale = Vector3.one }
            });
            _proxy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _proxy.name = "Interactive tree";
            SceneManager.MoveGameObjectToScene(_proxy, _scene);
            _proxy.AddComponent<ProxyProbe>();
            _interestObject = new GameObject("Interest");
            SceneManager.MoveGameObjectToScene(_interestObject, _scene);
            var interest = _interestObject.AddComponent<VegetationInterest>();
            interest.Radius = 4;
            _residency = _host.AddComponent<InteractiveInstanceResidency>();
            _residency.Configure(_container, _proxy, new[] { interest }, 8, 8, 2);
        }

        [TearDown]
        public void Cleanup()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_material);
        }

        [Test]
        public void PromotionPreservesIdentityStateAndRendererHandle()
        {
            _container.TryGetHandle("tree-a", out var handle);
            _residency.Synchronize();
            Assert.AreEqual(1, _residency.ActiveCount);
            Assert.IsTrue(_container.Renderer.TryGetVisible(handle, out bool visible));
            Assert.IsFalse(visible);
            var identity = Object.FindAnyObjectByType<InstanceProxyIdentity>(FindObjectsInactive.Include);
            Assert.AreEqual(_container.SourceId, identity.Key.SourceId);
            Assert.AreEqual("tree-a", identity.Key.PlacementId);
            var probe = identity.GetComponent<ProxyProbe>();
            Assert.AreEqual(1, probe.Promotions);
            probe.Value = 42;

            _interestObject.transform.position = Vector3.right * 20;
            _residency.Synchronize();
            Assert.AreEqual(0, _residency.ActiveCount);
            Assert.AreEqual(1, _residency.PooledCount);
            Assert.IsTrue(_container.Renderer.TryGetVisible(handle, out visible));
            Assert.IsTrue(visible);

            _interestObject.transform.position = Vector3.zero;
            _residency.Synchronize();
            identity = Object.FindAnyObjectByType<InstanceProxyIdentity>(FindObjectsInactive.Exclude);
            probe = identity.GetComponent<ProxyProbe>();
            Assert.AreEqual(42, probe.Value);
            Assert.AreEqual(2, probe.Promotions);
        }

        [Test]
        public void DestructionRemovesPersistentPlacement()
        {
            _residency.Synchronize();
            var identity = Object.FindAnyObjectByType<InstanceProxyIdentity>(FindObjectsInactive.Exclude);
            identity.DestroyInstance();
            Assert.IsFalse(_container.TryGetResolvedPlacement("tree-a", out _));
            Assert.IsFalse(_container.TryGetHandle("tree-a", out _));
            Assert.AreEqual(0, _residency.ActiveCount);
        }

        [Test]
        public void StateStoreRejectsDuplicateImportsAndIncrementsRevisions()
        {
            var store = new InstanceProxyStateStore();
            var record = new InstanceProxyStateRecord { SourceId = "cell", PlacementId = "tree", Payload = "1" };
            store.Set(record);
            store.Set(record);
            Assert.IsTrue(store.TryGet(new InstanceProxyKey("cell", "tree"), out var current));
            Assert.AreEqual(2, current.Revision);
            Assert.Throws<ArgumentException>(() => store.Import(new[] { record, record }));
        }

        [Test]
        public void ImpostorMeshesAndOctahedralDirectionsAreValid()
        {
            var bounds = new Bounds(new Vector3(0, 2, 0), new Vector3(4, 8, 4));
            Mesh cross = InstanceImpostorBaker.CreateCrossCardMesh(bounds, 3, 4);
            Mesh octahedral = InstanceImpostorBaker.CreateBillboardMesh(bounds);
            try
            {
                Assert.AreEqual(12, cross.vertexCount);
                Assert.AreEqual(18, cross.triangles.Length);
                Assert.AreEqual(4, octahedral.vertexCount);
                for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    Vector3 direction = InstanceImpostorBaker.DecodeOctahedralView(x, y, 8);
                    Assert.That(direction.magnitude, Is.EqualTo(1).Within(0.0001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(cross);
                Object.DestroyImmediate(octahedral);
            }
        }

        [Test]
        public void SmallBakeProducesRelightableDataAndCompiles()
        {
            const string folder = "Assets/LoogaPassNineTest";
            AssetDatabase.CreateFolder("Assets", "LoogaPassNineTest");
            try
            {
                var sourceMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(sourceMaterial, folder + "/Source.mat");
                _source.GetComponent<Renderer>().sharedMaterial = sourceMaterial;
                GameObject sourcePrefab = PrefabUtility.SaveAsPrefabAsset(_source, folder + "/Source.prefab");
                var settings = InstanceImpostorBakeSettings.Default;
                settings.Mode = InstanceImpostorMode.CrossCard;
                settings.TileResolution = 32;
                var asset = InstanceImpostorBaker.Bake(sourcePrefab, settings, folder + "/Cube.asset");
                Assert.IsTrue(asset.TryValidate(out string reason), reason);
                Assert.AreEqual(64, asset.AlbedoOpacity.width);
                Assert.AreEqual(64, asset.ObjectNormals.width);
                Assert.AreEqual(64, asset.MaterialMask.width);
                Assert.NotNull(asset.CreatePrototype());
                var capabilities = InstanceShaderInspection.Inspect(asset.Material);
                Assert.AreEqual(InstanceShaderCapabilities.Surface | InstanceShaderCapabilities.Depth |
                    InstanceShaderCapabilities.DepthNormals | InstanceShaderCapabilities.Shadow | InstanceShaderCapabilities.Motion,
                    capabilities & (InstanceShaderCapabilities.Surface | InstanceShaderCapabilities.Depth |
                    InstanceShaderCapabilities.DepthNormals | InstanceShaderCapabilities.Shadow | InstanceShaderCapabilities.Motion));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        public sealed class ProxyProbe : MonoBehaviour, IInstanceProxyLifecycle, IInstanceProxyStateAdapter,
            IInstanceProxyNetworkBridge
        {
            public int Value;
            public int Promotions;
            public int NetworkPromotions;

            public void OnInstancePromoted(InstanceProxyContext context) => Promotions++;
            public void OnInstanceReleased(InstanceProxyContext context, InstanceProxyReleaseReason reason) { }
            public void OnInstanceProxyPromoted(InstanceProxyContext context) => NetworkPromotions++;
            public void OnInstanceProxyReleased(InstanceProxyContext context, InstanceProxyReleaseReason reason) { }
            public string CaptureInstanceState() => Value.ToString();
            public void RestoreInstanceState(string payload)
            {
                if (!string.IsNullOrEmpty(payload)) Value = int.Parse(payload);
            }
        }
    }
}

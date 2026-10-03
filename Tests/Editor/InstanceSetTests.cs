using System;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class InstanceSetTests
    {
        private Scene _scene;
        private GameObject _cube;
        private GameObject _sphere;
        private Material _material;
        private GameObject _ownerA;
        private GameObject _ownerB;
        private InstanceSet _set;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer requires Direct3D 12 compute support.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _cube = MakePrototype(PrimitiveType.Cube);
            _sphere = MakePrototype(PrimitiveType.Sphere);
            _ownerA = Make("Owner A");
            _ownerB = Make("Owner B");
            _set = Make("Set").AddComponent<InstanceSet>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_scene);
            }
            Object.DestroyImmediate(_material);
        }

        [Test]
        public void EntriesShareOnePrototypeRegistrationAndOneRenderer()
        {
            _set.SetEntry(_ownerA, _cube, Row(3, 0));
            _set.SetEntry(_ownerB, _cube, Row(2, 20));
            _set.SetEntry(_ownerA, _sphere, Row(1, 40));

            Assert.AreEqual(3, _set.EntryCount);
            Assert.AreEqual(6, _set.PlacementCount);
            Assert.IsNotNull(_set.Renderer, _set.Diagnostic);
            Assert.AreEqual(6, _set.Renderer.InstanceCount);
            Assert.AreEqual(2, _set.Renderer.RegisteredPrototypeCount);
            Assert.AreEqual(3, _set.GetPlacementCount(_ownerA, _cube));
            Assert.AreEqual(2, _set.GetPlacementCount(_ownerB, _cube));
        }

        [Test]
        public void ReplacingOneEntryKeepsTheOtherEntries()
        {
            _set.SetEntry(_ownerA, _cube, Row(3, 0));
            _set.SetEntry(_ownerB, _cube, Row(2, 20));
            InstanceRenderer renderer = _set.Renderer;

            _set.SetEntry(_ownerA, _cube, Row(5, 0));
            Assert.AreSame(renderer, _set.Renderer, "An entry update must not rebuild the renderer.");
            Assert.AreEqual(7, _set.Renderer.InstanceCount);
            Assert.AreEqual(2, _set.GetPlacementCount(_ownerB, _cube));

            _set.SetEntry(_ownerB, _cube, Array.Empty<InstanceSet.Placement>());
            Assert.AreEqual(1, _set.EntryCount);
            Assert.AreEqual(5, _set.Renderer.InstanceCount);
            CollectionAssert.AreEqual(Row(5, 0), _set.CopyPlacements(_ownerA, _cube));
        }

        [Test]
        public void InvalidInputLeavesTheEntryUnchanged()
        {
            _set.SetEntry(_ownerA, _cube, Row(2, 0));
            var invalid = new[] { new InstanceSet.Placement { Position = Vector3.zero, EulerAngles = Vector3.zero, Scale = Vector3.zero } };
            Assert.Throws<ArgumentException>(() => _set.SetEntry(_ownerA, _cube, invalid));
            Assert.Throws<ArgumentNullException>(() => _set.SetEntry(_ownerA, null, Row(1, 0)));
            Assert.AreEqual(2, _set.GetPlacementCount(_ownerA, _cube));
            Assert.AreEqual(2, _set.Renderer.InstanceCount);
        }

        [Test]
        public void DisableAndEnableRebuildFromSavedEntries()
        {
            _set.SetEntry(_ownerA, _cube, Row(3, 0));
            _set.SetEntry(_ownerA, _sphere, Row(2, 20));
            _set.enabled = false;
            Assert.IsNull(_set.Renderer);
            Assert.AreEqual(5, _set.PlacementCount);

            _set.enabled = true;
            Assert.IsNotNull(_set.Renderer, _set.Diagnostic);
            Assert.AreEqual(5, _set.Renderer.InstanceCount);
        }

        [Test]
        public void MovingTheSetRebuildsWithTheNewTransform()
        {
            _set.SetEntry(_ownerA, _cube, Row(2, 0));
            InstanceRenderer renderer = _set.Renderer;
            _set.transform.position = new Vector3(0, 0, 100);
            _set.Synchronize();
            Assert.AreNotSame(renderer, _set.Renderer);
            Assert.AreEqual(2, _set.Renderer.InstanceCount);
        }

        [Test]
        public void RemoveOtherPrototypesKeepsListedEntriesAndOtherOwners()
        {
            _set.SetEntry(_ownerA, _cube, Row(3, 0));
            _set.SetEntry(_ownerA, _sphere, Row(2, 20));
            _set.SetEntry(_ownerB, _sphere, Row(4, 40));

            Assert.AreEqual(1, _set.RemoveOtherPrototypes(_ownerA, new[] { _cube }));
            Assert.AreEqual(2, _set.EntryCount);
            Assert.AreEqual(3, _set.GetPlacementCount(_ownerA, _cube));
            Assert.AreEqual(0, _set.GetPlacementCount(_ownerA, _sphere));
            Assert.AreEqual(4, _set.GetPlacementCount(_ownerB, _sphere), "Other owners must keep their entries.");
            Assert.AreEqual(7, _set.Renderer.InstanceCount);
            Assert.AreEqual(0, _set.RemoveOtherPrototypes(_ownerA, new[] { _cube }));
        }

        [Test]
        public void RemoveOtherOwnersRemovesUnlistedAndDestroyedOwners()
        {
            GameObject destroyed = Make("Destroyed owner");
            _set.SetEntry(_ownerA, _cube, Row(3, 0));
            _set.SetEntry(_ownerB, _sphere, Row(4, 40));
            _set.SetEntry(destroyed, _cube, Row(2, 80));
            Object.DestroyImmediate(destroyed);

            Assert.AreEqual(2, _set.RemoveOtherOwners(new Object[] { _ownerA }));
            Assert.AreEqual(1, _set.EntryCount);
            Assert.AreEqual(3, _set.GetPlacementCount(_ownerA, _cube));
            Assert.AreEqual(0, _set.GetPlacementCount(_ownerB, _sphere));
            Assert.AreEqual(3, _set.Renderer.InstanceCount);
            Assert.AreEqual(0, _set.RemoveOtherOwners(new Object[] { _ownerA }));
        }

        [Test]
        public void ConfigureRenderingAppliesSettingsAndKeepsEntries()
        {
            _set.SetEntry(_ownerA, _cube, Row(3, 0));
            InstanceRenderer renderer = _set.Renderer;
            InstanceQualitySettings quality = InstanceQualitySettings.Default;
            quality.ViewDistance = 245;
            quality.ViewFadeDistance = 55;
            _set.ConfigureRendering(InstanceWorldContentKind.PaintedInstance, 220, 800, 2, quality,
                InstanceVisibilityMode.Shared);

            Assert.AreNotSame(renderer, _set.Renderer, "New settings must rebuild the renderer.");
            Assert.AreEqual(220, _set.Renderer.MaxDistance);
            Assert.AreEqual(800, _set.Renderer.ShadowDistance);
            Assert.AreEqual(2, _set.Renderer.MinimumShadowLod);
            Assert.AreEqual(InstanceVisibilityMode.Shared, _set.Renderer.VisibilityMode);
            Assert.AreEqual(3, _set.Renderer.InstanceCount);
        }

        [Test]
        public void InvalidRenderingSettingsLeaveTheRendererUnchanged()
        {
            GameObject solid = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(solid, _scene);
            solid.GetComponent<Renderer>().sharedMaterial = _material;
            solid.SetActive(false);
            _set.SetEntry(_ownerA, solid, Row(2, 0));
            InstanceRenderer renderer = _set.Renderer;
            InstanceQualitySettings thinned = InstanceQualitySettings.Default;
            thinned.Decorative = true;
            thinned.Density = 0.5f;

            Assert.Throws<ArgumentOutOfRangeException>(() => _set.ConfigureRendering(
                InstanceWorldContentKind.SceneObject, 0, 1000, 0, InstanceQualitySettings.Default, default));
            Assert.Throws<InvalidOperationException>(() => _set.ConfigureRendering(
                InstanceWorldContentKind.SceneObject, 100, 1000, 0, thinned, default),
                "Density reductions must reject prototypes with colliders.");
            Assert.AreSame(renderer, _set.Renderer);
            Assert.AreEqual(1000, _set.Renderer.MaxDistance);
        }

        [Test]
        public void DisabledContainerStillFeedsColliderResidency()
        {
            // A set can draw the placements while the disabled container remains the collider source.
            GameObject solid = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(solid, _scene);
            solid.SetActive(false);
            var container = Make("Rocks").AddComponent<InstanceContainer>();
            container.transform.position = new Vector3(1000, 0, 0);
            container.Configure(solid, new[]
            {
                new InstanceContainer.Placement { Id = "near", Position = new Vector3(0, 0, 5), EulerAngles = Vector3.zero, Scale = Vector3.one },
                new InstanceContainer.Placement { Id = "far", Position = new Vector3(0, 0, 500), EulerAngles = Vector3.zero, Scale = Vector3.one }
            }, 100);
            container.enabled = false;
            var interest = Make("Player").AddComponent<VegetationInterest>();
            interest.transform.position = new Vector3(1000, 0, 0);
            interest.Radius = 20;
            var residency = container.gameObject.AddComponent<InstanceColliderResidency>();
            residency.Configure(container, solid, new[] { interest });

            residency.Synchronize();
            Assert.IsNull(container.Renderer, "A disabled container must not own a renderer.");
            Assert.AreEqual(1, residency.ResidentCount, residency.Diagnostic);
            residency.enabled = false;
            Assert.AreEqual(0, residency.ResidentCount);
        }

        private GameObject Make(string name)
        {
            var result = new GameObject(name);
            SceneManager.MoveGameObjectToScene(result, _scene);
            return result;
        }

        private GameObject MakePrototype(PrimitiveType type)
        {
            GameObject prototype = GameObject.CreatePrimitive(type);
            SceneManager.MoveGameObjectToScene(prototype, _scene);
            Object.DestroyImmediate(prototype.GetComponent<Collider>());
            prototype.GetComponent<Renderer>().sharedMaterial = _material;
            prototype.SetActive(false);
            return prototype;
        }

        private static InstanceSet.Placement[] Row(int count, float z)
        {
            var result = new InstanceSet.Placement[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new InstanceSet.Placement
                {
                    Position = new Vector3(i * 4, 0, z),
                    EulerAngles = new Vector3(0, i * 30, 0),
                    Scale = Vector3.one
                };
            }
            return result;
        }
    }
}

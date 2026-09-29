using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.EditorTests
{
    public sealed class InstanceQualityPolicyTests
    {
        [Test]
        public void LegacyDefaultsKeepInheritedPolicies()
        {
            InstanceQualitySettings quality = InstanceQualitySettings.Default;
            Assert.That(quality.Density, Is.EqualTo(1));
            Assert.That(quality.ViewDistance, Is.Zero);
            Assert.That(quality.ViewFadeDistance, Is.Zero);
            Assert.That(quality.LodBias, Is.Zero);
            Assert.That(quality.ShadowMode, Is.EqualTo(InstanceShadowMode.Inherit));
            Assert.DoesNotThrow(() => quality.Validate(false));
        }

        [Test]
        public void InvalidViewAndShadowPoliciesAreRejected()
        {
            InstanceQualitySettings quality = InstanceQualitySettings.Default;
            quality.ViewDistance = -1;
            Assert.Throws<ArgumentOutOfRangeException>(() => quality.Validate(false));
            quality.ViewDistance = 100;
            quality.ViewFadeDistance = float.NaN;
            Assert.Throws<ArgumentOutOfRangeException>(() => quality.Validate(false));
            quality.ViewFadeDistance = 20;
            quality.LodBias = float.PositiveInfinity;
            Assert.Throws<ArgumentOutOfRangeException>(() => quality.Validate(false));
            quality.LodBias = 1;
            quality.ShadowMode = (InstanceShadowMode)4;
            Assert.Throws<ArgumentOutOfRangeException>(() => quality.Validate(false));
        }

        [Test]
        public void HierarchyPaddingKeepsOffsetBoundsEligible()
        {
            InstanceWorldCells.Reset();
            try
            {
                var prototype = new InstancePrototypeId(Hash128.Compute("offset-bounds"));
                using (var update = InstanceWorldCells.BeginUpdate("offset-source", InstanceWorldContentKind.Grass))
                {
                    update.Add(new Vector3(100, 0, 0), prototype);
                    update.Commit();
                }
                InstanceWorldHierarchy hierarchy = InstanceWorldHierarchy.Build("offset-source",
                    InstanceHierarchySettings.Default);
                InstanceHierarchyDecision clipped = hierarchy.Evaluate(Array.Empty<Vector4>(), 0,
                    Vector3.zero, 10, false, InstanceVisibilityMode.Direct);
                InstanceHierarchyDecision padded = hierarchy.Evaluate(Array.Empty<Vector4>(), 0,
                    Vector3.zero, 10, false, InstanceVisibilityMode.Direct, 100);
                Assert.That(clipped.Visible, Is.False);
                Assert.That(padded.Visible, Is.True);
            }
            finally
            {
                InstanceWorldCells.Reset();
            }
        }

        [Test]
        public void RuntimePoliciesRemainIndependentAndDoNotUploadSources()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer requires Direct3D 12 compute shaders.");
            }
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material firstMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            Material secondMaterial = new Material(firstMaterial);
            first.GetComponent<Renderer>().sharedMaterial = firstMaterial;
            second.GetComponent<Renderer>().sharedMaterial = secondMaterial;
            SceneManager.MoveGameObjectToScene(first, scene);
            SceneManager.MoveGameObjectToScene(second, scene);
            Object.DestroyImmediate(first.GetComponent<Collider>());
            Object.DestroyImmediate(second.GetComponent<Collider>());
            try
            {
                using var renderer = new InstanceRenderer();
                int firstId = renderer.Register(InstancePrototype.FromPrefab(first));
                int secondId = renderer.Register(InstancePrototype.FromPrefab(second));
                Assert.That(firstId, Is.Not.EqualTo(secondId));
                InstanceHandle firstHandle = renderer.Add(firstId, Matrix4x4.identity);
                renderer.Add(secondId, Matrix4x4.identity);
                renderer.Flush();
                long uploaded = renderer.UploadedBytes;
                var quality = InstanceQualitySettings.Default;
                quality.ViewDistance = 245;
                quality.ViewFadeDistance = 55;
                quality.LodBias = 1.2f;
                quality.ShadowMode = InstanceShadowMode.TwoSided;
                renderer.SetQuality(firstId, quality);
                Assert.That(renderer.GetQuality(firstId).ViewDistance, Is.EqualTo(245));
                Assert.That(renderer.GetQuality(secondId).ViewDistance, Is.Zero);
                var invalid = quality;
                invalid.ViewDistance = -1;
                Assert.Throws<ArgumentOutOfRangeException>(() => renderer.SetQuality(firstId, invalid));
                Assert.That(renderer.GetQuality(firstId).ViewDistance, Is.EqualTo(245));
                Assert.That(renderer.GetResidentCount(firstId), Is.EqualTo(1));
                Assert.That(renderer.GetResidentCount(secondId), Is.EqualTo(1));
                Assert.That(renderer.UploadedBytes, Is.EqualTo(uploaded));
                Assert.That(renderer.HasPendingUploads, Is.False);
                renderer.Remove(firstHandle);
                Assert.That(renderer.GetResidentCount(firstId), Is.Zero);
                Assert.That(renderer.GetResidentCount(secondId), Is.EqualTo(1));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(firstMaterial);
                Object.DestroyImmediate(secondMaterial);
            }
        }

        [Test]
        public void MaterialFadeOwnsCloneAndPreservesSource()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer requires Direct3D 12 compute shaders.");
            }
            Shader shader = Shader.Find("Hidden/Looga/Tests/Distance Fade");
            Assert.That(shader, Is.Not.Null);
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject source = new GameObject("Distance fade source");
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            first.transform.SetParent(source.transform, false);
            second.transform.SetParent(source.transform, false);
            second.transform.localPosition = Vector3.right * 2;
            Material material = new Material(shader);
            material.SetFloat("_FadeStart", 190);
            material.SetFloat("_FadeEnd", 245);
            first.GetComponent<Renderer>().sharedMaterial = material;
            second.GetComponent<Renderer>().sharedMaterial = material;
            SceneManager.MoveGameObjectToScene(source, scene);
            Object.DestroyImmediate(first.GetComponent<Collider>());
            Object.DestroyImmediate(second.GetComponent<Collider>());
            Material[] clones = null;
            try
            {
                using (var renderer = new InstanceRenderer())
                {
                    int prototype = renderer.Register(InstancePrototype.FromPrefab(source, null,
                        InstanceShaderCapabilities.Surface));
                    renderer.Add(prototype, Matrix4x4.identity);
                    renderer.Flush();
                    long uploaded = renderer.UploadedBytes;
                    FieldInfo populationsField = typeof(InstanceRenderer).GetField("_populations",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    object population = ((IList)populationsField.GetValue(renderer))[prototype];
                    FieldInfo fadeMaterialsField = population.GetType().GetField("_fadeMaterials",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    clones = (Material[])fadeMaterialsField.GetValue(population);
                    Assert.That(clones.Length, Is.EqualTo(2));
                    Assert.That(clones, Is.All.Null);

                    var quality = InstanceQualitySettings.Default;
                    quality.ViewDistance = 500;
                    renderer.SetQuality(prototype, quality);
                    Assert.That(clones, Is.All.Not.Null);
                    foreach (Material clone in clones)
                    {
                        Assert.That(clone, Is.Not.SameAs(material));
                        Assert.That(clone.GetFloat("_FadeStart"), Is.EqualTo(500));
                        Assert.That(clone.GetFloat("_FadeEnd"), Is.EqualTo(500));
                    }
                    Material firstClone = clones[0];
                    quality.ViewDistance = 300;
                    quality.ViewFadeDistance = 55;
                    renderer.SetQuality(prototype, quality);
                    Assert.That(clones[0], Is.SameAs(firstClone));
                    Assert.That(clones[0].GetFloat("_FadeStart"), Is.EqualTo(245));
                    Assert.That(clones[1].GetFloat("_FadeEnd"), Is.EqualTo(300));
                    Assert.That(material.GetFloat("_FadeStart"), Is.EqualTo(190));
                    Assert.That(material.GetFloat("_FadeEnd"), Is.EqualTo(245));
                    Assert.That(renderer.UploadedBytes, Is.EqualTo(uploaded));
                    Assert.That(renderer.HasPendingUploads, Is.False);
                    renderer.SetQuality(prototype, InstanceQualitySettings.Default);
                    Assert.That(firstClone == null, Is.True);
                    Assert.That(clones, Is.All.Null);
                    renderer.SetQuality(prototype, quality);
                    Assert.That(clones, Is.All.Not.Null);
                }
                Assert.That(clones, Is.All.Null);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void CameraVisibleCountClearsAfterEmptyEvaluation()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("InstanceRenderer requires Direct3D 12 compute shaders.");
            }
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject cameraObject = new GameObject("Diagnostic camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            source.GetComponent<Renderer>().sharedMaterial = material;
            SceneManager.MoveGameObjectToScene(source, scene);
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            try
            {
                using var renderer = new InstanceRenderer();
                int prototype = renderer.Register(InstancePrototype.FromPrefab(source));
                renderer.Add(prototype, Matrix4x4.identity);
                renderer.Flush();

                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                object population = ((IList)typeof(InstanceRenderer).GetField("_populations", flags)
                    .GetValue(renderer))[prototype];
                object view = population.GetType().GetMethod("GetView", flags)
                    .Invoke(population, new object[] { 0 });
                Type viewType = view.GetType();
                long evaluation = (long)typeof(InstanceRenderer).GetMethod("RecordCameraEvaluation", flags)
                    .Invoke(renderer, new object[] { camera });
                viewType.GetField("Camera", flags).SetValue(view, camera);
                viewType.GetField("EvaluationSequence", flags).SetValue(view, evaluation);
                viewType.GetField("SourceRevision", flags).SetValue(view,
                    population.GetType().GetField("SourceRevision", flags).GetValue(population));
                GraphicsBuffer arguments = (GraphicsBuffer)viewType.GetField("Arguments", flags).GetValue(view);
                GraphicsBuffer visible = (GraphicsBuffer)viewType.GetField("Visible", flags).GetValue(view);
                var counts = new uint[arguments.count];
                arguments.GetData(counts);
                counts[1] = 1;
                arguments.SetData(counts);
                visible.SetData(new uint[] { 0 }, 0, 0, 1);
                Assert.That(renderer.ReadCameraVisibleCount(camera, prototype), Is.EqualTo(1));

                typeof(InstanceRenderer).GetMethod("RecordCameraEvaluation", flags)
                    .Invoke(renderer, new object[] { camera });
                Assert.That(renderer.ReadCameraVisibleCount(camera, prototype), Is.Zero);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }
    }
}

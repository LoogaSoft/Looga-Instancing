using LoogaSoft.Instancing;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class VegetationInteractionHistoryTests
    {
        [UnityTest]
        public IEnumerator InteractionHistoryPreservesAllCamerasAndRetiresDisabledSources()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Interaction history fixture");
                SceneManager.MoveGameObjectToScene(root, scene);
                var interactor = root.AddComponent<VegetationInteractor>();
                interactor.Configure(2, 0.25f);
                VegetationInteractor.Publish();
                yield return NextEditorUpdate();
                VegetationInteractor.Publish();
                root.transform.position = Vector3.right * 3;
                interactor.Configure(2, 0.75f);
                VegetationInteractor.Publish();
                Assert.AreEqual(3, Shader.GetGlobalVectorArray("_LoogaInteractionSpheres")[0].x);
                Assert.AreEqual(0, Shader.GetGlobalVectorArray("_LoogaInteractionPreviousSpheres")[0].x);
                Assert.AreEqual(0.25f, Shader.GetGlobalVectorArray("_LoogaInteractionPreviousStrengths")[0].x);
                VegetationInteractor.Publish();
                Assert.AreEqual(0, Shader.GetGlobalVectorArray("_LoogaInteractionPreviousSpheres")[0].x);
                yield return NextEditorUpdate();
                VegetationInteractor.Publish();
                Assert.AreEqual(3, Shader.GetGlobalVectorArray("_LoogaInteractionPreviousSpheres")[0].x);
                Assert.AreEqual(0.75f, Shader.GetGlobalVectorArray("_LoogaInteractionPreviousStrengths")[0].x);
                interactor.enabled = false;
                VegetationInteractor.Publish();
                Assert.Zero(Shader.GetGlobalInt("_LoogaInteractionCount"));
                yield return NextEditorUpdate();
                VegetationInteractor.Publish();
                Assert.Zero(Shader.GetGlobalInt("_LoogaInteractionPreviousCount"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                VegetationInteractor.Publish();
            }
        }
        [Test]
        public void WindFieldPublishesGustWavesAndReleasesThemOnDisable()
        {
            var root = new GameObject("Wind field fixture");
            try
            {
                var field = root.AddComponent<VegetationWindField>();
                field.ConfigureWaves(30, 0.4f);
                Assert.AreEqual(new Vector4(30, 0.4f, 0, 0), Shader.GetGlobalVector("_LoogaFieldWave"));
                field.ConfigureWaves(0, 2);
                Assert.AreEqual(new Vector4(1, 1, 0, 0), Shader.GetGlobalVector("_LoogaFieldWave"));
                Assert.Throws<System.ArgumentException>(() => field.ConfigureWaves(float.NaN, 0.5f));
                field.enabled = false;
                Assert.AreEqual(Vector4.zero, Shader.GetGlobalVector("_LoogaFieldWave"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static IEnumerator NextEditorUpdate()
        {
            bool advanced = false;
            void Updated() => advanced = true;
            EditorApplication.update += Updated;
            try
            {
                while (!advanced)
                {
                    yield return null;
                }
            }
            finally
            {
                EditorApplication.update -= Updated;
            }
        }
    }
}

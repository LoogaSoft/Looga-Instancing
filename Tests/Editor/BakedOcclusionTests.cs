using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class BakedOcclusionTests
    {
        [Test]
        public void ChangedSourcesFailVisibleAndRejectedBakeIsAtomic()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(root, scene);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var source = root.GetComponent<MeshRenderer>();
            source.sharedMaterial = material;
            var bake = root.AddComponent<BakedInstanceOcclusion>();
            var matrices = new Matrix4x4[16];
            try
            {
                bake.Bake(new[] { source });
                Assert.AreEqual(1, BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 0));
                Assert.AreEqual(1, bake.Revision);
                root.transform.position = Vector3.right;
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                root.transform.position = Vector3.zero;
                source.enabled = false;
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                source.enabled = true;
                material.SetFloat("_AlphaClip", 1);
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                Assert.Throws<ArgumentException>(() => bake.Bake(new[] { source }));
                Assert.AreEqual(1, bake.Revision);
                Assert.AreEqual(1, bake.BoxCount);
                material.SetFloat("_AlphaClip", 0);
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", Color.white);
                source.SetPropertyBlock(block);
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                source.SetPropertyBlock(null);
                Assert.AreEqual(1, BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1), "Restored source before serialization.");
                string serialized = EditorJsonUtility.ToJson(bake);
                bake.Invalidate();
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                EditorJsonUtility.FromJsonOverwrite(serialized, bake);
                Assert.AreEqual(1, bake.BoxCount, serialized);
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1), "Unresolved preview-scene JSON references must fail visible.");
                bake.Bake(new[] { source });
                Assert.AreEqual(1, BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
                bake.enabled = false;
                Assert.Zero(BakedInstanceOcclusion.CopyActiveBoxes(matrices, 1));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SavedPrefabRestoresBakedSourceReferences()
        {
            string folder = "Assets/LoogaBakedTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, folder + "/Box.mat");
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(root, scene);
                var source = root.GetComponent<MeshRenderer>();
                source.sharedMaterial = material;
                var bake = root.AddComponent<BakedInstanceOcclusion>();
                bake.Bake(new[] { source });
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Occluder.prefab");
                Object.DestroyImmediate(root);
                var restored = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                Assert.AreEqual(1, restored.GetComponent<BakedInstanceOcclusion>().Revision);
                Assert.AreEqual(1, BakedInstanceOcclusion.CopyActiveBoxes(new Matrix4x4[16], 1));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [TestCase(0f, 10f, 0.25f, 0f, false)]
        [TestCase(5f, 10f, 1f, 0f, true)]
        [TestCase(0f, 2f, 0.1f, 0f, true)]
        [TestCase(0f, 5f, 1f, 0f, true)]
        [TestCase(0f, 10f, 5f, 0f, true)]
        [TestCase(0f, 10f, 0.25f, 5f, true)]
        public void GpuBoxesOnlyRejectFullyOccludedBounds(float x, float z, float radius, float cameraZ, bool visible)
        {
            var shader = Object.Instantiate(Resources.Load<ComputeShader>("LoogaInstanceCulling"));
            using var bounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 48);
            using var selection = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            using var clusters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            using var spatialOrder = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            try
            {
                bounds.SetData(new[] { new Vector4(x, 0, z, radius), new Vector4(x, 0, z, radius), new Vector4(1, 0, 1, 0) });
                spatialOrder.SetData(new uint[] { 0 });
                int kernel = shader.FindKernel("SelectVisibility");
                shader.SetInt("_Count", 1);
                shader.SetInt("_SelectionMode", 1);
                shader.SetInt("_UseSpatialOrder", 0);
                shader.SetInt("_PlaneCount", 0);
                shader.SetInt("_SplitCount", 0);
                shader.SetInt("_BakedOccluderCount", 1);
                shader.SetMatrixArray("_BakedOccluders", new[] { Matrix4x4.TRS(new Vector3(0, 0, 5), Quaternion.identity, new Vector3(4, 4, 1)).inverse });
                shader.SetVector("_Camera", new Vector4(0, 0, cameraZ, 0));
                shader.SetVector("_Quality", new Vector4(1, 0, 0, 256));
                shader.SetVector("_Lod", new Vector4(1, 0, 100, 0));
                shader.SetBuffer(kernel, "_Bounds", bounds);
                shader.SetBuffer(kernel, "_Selection", selection);
                shader.SetBuffer(kernel, "_Clusters", clusters);
                shader.SetBuffer(kernel, "_SpatialOrder", spatialOrder);
                shader.Dispatch(kernel, 1, 1, 1);
                var result = new Vector4[1];
                selection.GetData(result);
                Assert.AreEqual(visible, result[0].x > 0);
                shader.SetInt("_BakedOccluderCount", 0);
                shader.Dispatch(kernel, 1, 1, 1);
                selection.GetData(result);
                Assert.Greater(result[0].x, 0);
            }
            finally
            {
                Object.DestroyImmediate(shader);
            }
        }
    }
}

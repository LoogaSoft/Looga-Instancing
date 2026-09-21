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
    public sealed class HierarchyMigrationTests
    {
        [Test]
        public void NestedLodPartsCollidersAndProbeReferencesSurviveExportAndReload()
        {
            string folder = "Assets/LoogaHierarchyMigration_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Composite LOD prototype");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.SetActive(false);
                var anchor = new GameObject("Probe anchor");
                anchor.transform.SetParent(root.transform, false);
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                var lods = new LOD[2];
                for (int lod = 0; lod < 2; lod++)
                {
                    var branch = new GameObject("LOD" + lod);
                    branch.transform.SetParent(root.transform, false);
                    branch.transform.localPosition = new Vector3(2, 1, -3);
                    branch.transform.localRotation = Quaternion.Euler(0, 27, 0);
                    var parts = new Renderer[2];
                    for (int part = 0; part < 2; part++)
                    {
                        var child = GameObject.CreatePrimitive(part == 0 ? PrimitiveType.Cube : PrimitiveType.Capsule);
                        child.name = "Part" + part;
                        child.transform.SetParent(branch.transform, false);
                        child.transform.localPosition = Vector3.right * part * 3;
                        var renderer = child.GetComponent<MeshRenderer>();
                        renderer.sharedMaterial = material;
                        renderer.probeAnchor = anchor.transform;
                        renderer.forceRenderingOff = true;
                        parts[part] = renderer;
                    }
                    lods[lod] = new LOD(lod == 0 ? 0.6f : 0.02f, parts) { fadeTransitionWidth = 0.1f };
                }
                var group = root.AddComponent<LODGroup>();
                group.SetLODs(lods);
                group.fadeMode = LODFadeMode.CrossFade;
                group.animateCrossFading = true;
                group.RecalculateBounds();
                root.AddComponent<VegetationInteractor>();
                var exported = InstanceMigrationTools.ExportPrototype(root, folder + "/Composite.prefab");
                AssetDatabase.ImportAsset(folder + "/Composite.prefab", ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                exported = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Composite.prefab");
                Assert.IsEmpty(exported.GetComponentsInChildren<MonoBehaviour>(true));
                Assert.AreEqual(4, exported.GetComponentsInChildren<Collider>(true).Length);
                Assert.AreEqual(4, InstancePrototype.FromPrefab(exported).PartCount);
                var copy = exported.GetComponent<LODGroup>();
                Assert.AreEqual(group.size, copy.size);
                Assert.AreEqual(LODFadeMode.CrossFade, copy.fadeMode);
                Assert.IsTrue(copy.animateCrossFading);
                for (int lod = 0; lod < 2; lod++)
                {
                    Assert.AreEqual(2, copy.GetLODs()[lod].renderers.Length);
                    foreach (var renderer in copy.GetLODs()[lod].renderers)
                    {
                        Assert.IsTrue(renderer.transform.IsChildOf(exported.transform));
                        Assert.AreSame(exported.transform.Find("Probe anchor"), renderer.probeAnchor);
                        Assert.IsFalse(renderer.forceRenderingOff);
                    }
                    Assert.AreEqual(root.transform.Find("LOD" + lod).localRotation, exported.transform.Find("LOD" + lod).localRotation);
                }
                Assert.IsTrue(lods[0].renderers[0].forceRenderingOff);
                Assert.IsFalse(root.activeSelf);
                Assert.IsEmpty(InstanceMigrationTools.FindReplacementDependencies(exported));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}

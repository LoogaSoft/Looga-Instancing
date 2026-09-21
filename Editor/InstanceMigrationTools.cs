using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Editor
{
    /// <summary>Exports native rendering prototypes without source renderer controllers. Original objects and assets remain unchanged.</summary>
    public static class InstanceMigrationTools
    {
        private static readonly string[] _excludedRoots =
        {
            "Assets/FoliageRenderer/", "Assets/BRGInstancedRenderer/", "Packages/com.ma.flora/",
            "Packages/com.ma.flora.", "Assets/Flora/"
        };

        [MenuItem("GameObject/Looga/Export Standalone Instance Prototype", false, 21)]
        private static void ExportSelected()
        {
            if (!Selection.activeGameObject) return;
            string path = EditorUtility.SaveFilePanelInProject("Export standalone prototype", Selection.activeGameObject.name, "prefab", "Choose a new asset.");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Selection.activeObject = ExportPrototype(Selection.activeGameObject, path);
            }
            catch (Exception exception)
            {
                Debug.LogError(exception.Message);
            }
        }

        /// <summary>Reject dependencies on the replaced renderers. Resolve reported materials, shaders or meshes before export.</summary>
        public static string[] FindReplacementDependencies(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            var failures = new SortedSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
            foreach (string dependency in AssetDatabase.GetDependencies(path, true))
            {
                foreach (string root in _excludedRoots)
                {
                    if (dependency.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(dependency);
                    }
                }
            }
            var result = new string[failures.Count];
            failures.CopyTo(result);
            return result;
        }

        /// <summary>Copy native mesh, LOD and collider components into a new prefab. Custom scripts and source controllers are not copied.</summary>
        public static GameObject ExportPrototype(GameObject source, string path)
        {
            if (!source || string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("..") || AssetDatabase.LoadMainAssetAtPath(path))
            {
                throw new ArgumentException("Assign a source and a new prefab path under Assets.");
            }
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not MeshRenderer)
                {
                    throw new ArgumentException("Standalone export supports MeshRenderer prototypes. Convert other renderer types explicitly first.");
                }
            }
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                ValidateAsset(filter.sharedMesh);
            }
            foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                ValidateAsset(renderer.additionalVertexStreams);
                if (renderer.HasPropertyBlock()) throw new ArgumentException("Bake or remove renderer property blocks before prototype export.");
                foreach (var material in renderer.sharedMaterials)
                {
                    ValidateAsset(material);
                }
            }
            foreach (var collider in source.GetComponentsInChildren<MeshCollider>(true))
            {
                ValidateAsset(collider.sharedMesh);
            }
            var copies = new Dictionary<Transform, Transform>();
            GameObject output = null;
            Scene preview = EditorSceneManager.NewPreviewScene();
            var references = new Dictionary<Object, Object>();
            try
            {
                output = CopyHierarchy(source.transform, null, copies, preview).gameObject;
                output.SetActive(false);
                foreach (var pair in copies)
                {
                    references.Add(pair.Key, pair.Value);
                    references.Add(pair.Key.gameObject, pair.Value.gameObject);
                }
                foreach (var pair in copies)
                {
                    foreach (var component in pair.Key.GetComponents<Component>())
                    {
                        if (!component || component is Transform || component is LODGroup) continue;
                        Type type = component.GetType();
                        if (type != typeof(MeshFilter) && type != typeof(MeshRenderer) && type != typeof(BoxCollider) &&
                            type != typeof(SphereCollider) && type != typeof(CapsuleCollider) && type != typeof(MeshCollider)) continue;
                        Component copy = pair.Value.gameObject.AddComponent(type);
                        EditorUtility.CopySerialized(component, copy);
                        references.Add(component, copy);
                        if (copy is MeshRenderer renderer)
                        {
                            renderer.forceRenderingOff = false;
                        }
                    }
                }
                foreach (var pair in copies)
                {
                    var group = pair.Key.GetComponent<LODGroup>();
                    if (!group) continue;
                    var lods = group.GetLODs();
                    for (int i = 0; i < lods.Length; i++)
                    {
                        var renderers = new Renderer[lods[i].renderers.Length];
                        for (int j = 0; j < renderers.Length; j++)
                        {
                            var original = lods[i].renderers[j];
                            if (original && copies.TryGetValue(original.transform, out var target))
                            {
                                renderers[j] = target.GetComponent<MeshRenderer>();
                            }
                        }
                        lods[i].renderers = renderers;
                    }
                    var copy = pair.Value.gameObject.AddComponent<LODGroup>();
                    copy.SetLODs(lods);
                    copy.fadeMode = group.fadeMode;
                    copy.animateCrossFading = group.animateCrossFading;
                    copy.localReferencePoint = group.localReferencePoint;
                    copy.size = group.size;
                    copy.enabled = group.enabled;
                }
                // Remap native component references after every copy exists.
                foreach (var pair in references)
                {
                    if (pair.Value is not Component component || component is Transform) continue;
                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        Object value = property.objectReferenceValue;
                        if (!value) continue;
                        if (references.TryGetValue(value, out Object replacement))
                        {
                            property.objectReferenceValue = replacement;
                        }
                        else if (!EditorUtility.IsPersistent(value) &&
                            ((value is Component external && external.gameObject.scene != preview) ||
                            (value is GameObject externalObject && externalObject.scene != preview)))
                        {
                            throw new ArgumentException("Remove external scene references before export: " + property.propertyPath);
                        }
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                output.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                output.transform.localScale = Vector3.one;
                InstancePrototype.FromPrefab(output);
                output.SetActive(true);
                return PrefabUtility.SaveAsPrefabAsset(output, path);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static Transform CopyHierarchy(Transform source, Transform parent, Dictionary<Transform, Transform> copies, Scene preview)
        {
            var target = new GameObject(source.name);
            SceneManager.MoveGameObjectToScene(target, preview);
            target.SetActive(false);
            target.layer = source.gameObject.layer;
            target.transform.SetParent(parent, false);
            target.transform.localPosition = source.localPosition;
            target.transform.localRotation = source.localRotation;
            target.transform.localScale = source.localScale;
            copies.Add(source, target.transform);
            foreach (Transform child in source)
            {
                CopyHierarchy(child, target.transform, copies, preview);
            }
            target.SetActive(source.gameObject.activeSelf && parent != null);
            return target.transform;
        }

        private static void ValidateAsset(Object asset)
        {
            if (!asset) return;
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset)))
            {
                throw new ArgumentException("Save generated meshes and materials as independent assets before export.");
            }
            string[] failures = FindReplacementDependencies(asset);
            if (failures.Length > 0)
            {
                throw new ArgumentException("Replace dependencies before export: " + string.Join(", ", failures));
            }
        }
    }
}

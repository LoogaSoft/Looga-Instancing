using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    /// <summary>Reversible scene submission and explicit placement snapshot tools.</summary>
    public static class InstanceConversionTools
    {
        [MenuItem("GameObject/Looga/Render Selected Roots With BRG", false, 20)]
        private static void ConvertSelected()
        {
            try
            {
                Selection.activeGameObject = Convert(Selection.gameObjects).gameObject;
            }
            catch (Exception exception)
            {
                Debug.LogError(exception.Message);
            }
        }

        /// <summary>Create one provider for explicit roots. Undo or disabling the provider restores native submission.</summary>
        public static SceneInstanceProvider Convert(GameObject[] roots)
        {
            if (roots == null || roots.Length == 0 || !roots[0])
            {
                throw new ArgumentException("Select scene roots that contain mesh renderers.");
            }
            var scene = roots[0].scene;
            var seen = new HashSet<GameObject>();
            foreach (var root in roots)
            {
                if (!root || EditorUtility.IsPersistent(root) || root.scene != scene || !seen.Add(root))
                {
                    throw new ArgumentException("Select distinct objects in one loaded scene.");
                }
                foreach (var other in roots)
                {
                    if (other && other != root && root.transform.IsChildOf(other.transform))
                    {
                        throw new ArgumentException("Select outer roots without nested selections.");
                    }
                }
                InstancePrototype.FromPrefab(root);
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Convert scene rendering to Looga");
            var owner = new GameObject("Looga Scene Instances");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            Undo.RegisterCreatedObjectUndo(owner, "Create Looga provider");
            try
            {
                var provider = Undo.AddComponent<SceneInstanceProvider>(owner);
                provider.Configure(roots);
                if (provider.ActiveSourceCount != roots.Length)
                {
                    throw new InvalidOperationException(provider.Diagnostic ?? "Some roots could not acquire rendering ownership.");
                }
                EditorUtility.SetDirty(provider);
                Undo.CollapseUndoOperations(group);
                return provider;
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        /// <summary>Copy matching prefab placements into a disabled container. Original objects remain unchanged for review.</summary>
        public static InstanceContainer BakeSnapshot(GameObject prefab, GameObject[] roots)
        {
            if (!prefab || !EditorUtility.IsPersistent(prefab) || roots == null || roots.Length == 0 || !roots[0])
            {
                throw new ArgumentException("Assign a prefab asset and at least one matching scene root.");
            }
            var scene = roots[0].scene;
            var placements = new InstanceContainer.Placement[roots.Length];
            string[] dependencies = InstanceMigrationTools.FindReplacementDependencies(prefab);
            if (dependencies.Length > 0)
            {
                throw new ArgumentException("The snapshot prototype depends on a replaced renderer: " + string.Join(", ", dependencies));
            }
            var prototype = InstancePrototype.FromPrefab(prefab);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (!root || root.scene != scene ||
                    !prototype.CanShareWith(InstancePrototype.FromPrefab(root)))
                {
                    throw new ArgumentException("Every root must match the prefab's mesh, material and renderer state.");
                }
                var matrix = root.transform.localToWorldMatrix;
                var reconstructed = Matrix4x4.TRS(root.transform.position, root.transform.rotation, root.transform.lossyScale);
                for (int j = 0; j < 16; j++)
                {
                    if (Mathf.Abs(matrix[j] - reconstructed[j]) > 0.0001f)
                    {
                        throw new ArgumentException("A sheared hierarchy cannot be stored as a placement transform.");
                    }
                }
                string id = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
                if (!ids.Add(id))
                {
                    throw new ArgumentException("Select distinct objects in a saved scene before creating a persistent snapshot.");
                }
                placements[i] = new InstanceContainer.Placement
                {
                    Id = id,
                    Position = root.transform.position,
                    EulerAngles = root.transform.rotation.eulerAngles,
                    Scale = root.transform.lossyScale
                };
            }
            if (string.IsNullOrEmpty(scene.path))
            {
                throw new ArgumentException("Save the source scene before creating persistent placement IDs.");
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var owner = new GameObject("Looga Baked Placements (Preview)");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            Undo.RegisterCreatedObjectUndo(owner, "Bake instance snapshot");
            try
            {
                var container = Undo.AddComponent<InstanceContainer>(owner);
                container.enabled = false;
                container.Configure(prefab, placements);
                var preview = Undo.AddComponent<InstanceSnapshotPreview>(owner);
                preview.Configure(roots);
                EditorUtility.SetDirty(preview);
                EditorUtility.SetDirty(container);
                Undo.CollapseUndoOperations(group);
                return container;
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }
    }
}

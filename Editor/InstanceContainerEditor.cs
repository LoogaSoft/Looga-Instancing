using System;
using LoogaSoft.Instancing;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    [CustomEditor(typeof(InstanceContainer))]
    internal sealed class InstanceContainerEditor : UnityEditor.Editor
    {
        private InstancePlacementAsset _snapshot;
        private string _error;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var container = (InstanceContainer)target;
            EditorGUILayout.HelpBox("Placements use container-local space. Moving the container updates its existing instances. " +
                "Snapshots copy placement data and preserve IDs. Shared snapshot assets remain unchanged. " +
                "Set BatchRendererGroup Variants to Keep All before a player build.", MessageType.Info);
            EditorGUILayout.LabelField("Source placements", container.PlacementCount.ToString());
            if (!string.IsNullOrEmpty(container.Diagnostic))
            {
                EditorGUILayout.HelpBox(container.Diagnostic, MessageType.Warning);
            }
            _snapshot = (InstancePlacementAsset)EditorGUILayout.ObjectField("Placement snapshot", _snapshot, typeof(InstancePlacementAsset), false);
            using (new EditorGUI.DisabledScope(_snapshot == null))
            {
                if (GUILayout.Button("Load Snapshot Copy"))
                {
                    try
                    {
                        Undo.RecordObject(container, "Load instance placements");
                        container.SetPlacements(_snapshot.CopyPlacements());
                        EditorUtility.SetDirty(container);
                        _error = null;
                    }
                    catch (Exception exception)
                    {
                        _error = exception.Message;
                    }
                }
            }
            if (GUILayout.Button("Save New Placement Snapshot"))
            {
                string path = EditorUtility.SaveFilePanelInProject("Save placements", "InstancePlacements", "asset", "Choose a new placement asset.");
                if (!string.IsNullOrEmpty(path))
                {
                    SaveSnapshot(container, path);
                }
            }
            if (GUILayout.Button("Rebuild Prototype"))
            {
                try
                {
                    container.Rebuild();
                    _error = null;
                }
                catch (Exception exception)
                {
                    _error = exception.Message;
                }
            }
            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }
        }

        private void SaveSnapshot(InstanceContainer container, string path)
        {
            InstancePlacementAsset asset = null;
            try
            {
                // Create a new asset instead of replacing a shared snapshot.
                path = AssetDatabase.GenerateUniqueAssetPath(path);
                asset = CreateInstance<InstancePlacementAsset>();
                asset.SetPlacements(container.CopyPlacements());
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssetIfDirty(asset);
                _snapshot = asset;
                _error = null;
            }
            catch (Exception exception)
            {
                if (asset != null && !AssetDatabase.Contains(asset))
                {
                    DestroyImmediate(asset);
                }
                _error = exception.Message;
            }
        }
    }
}

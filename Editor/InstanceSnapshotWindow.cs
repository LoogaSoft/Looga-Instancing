using System;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    internal sealed class InstanceSnapshotWindow : EditorWindow
    {
        [SerializeField] private GameObject _prototype;
        private string _diagnostic;

        [MenuItem("Tools/Looga/Instancing/Bake Selected Instance Snapshot")]
        private static void Open() => GetWindow<InstanceSnapshotWindow>("Instance Snapshot");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Select static source roots in one saved scene. Assign an independent matching prefab. " +
                "The new snapshot starts disabled. Its preview component switches rendering and keeps original objects for rollback.", MessageType.Info);
            _prototype = (GameObject)EditorGUILayout.ObjectField("Standalone prototype", _prototype, typeof(GameObject), false);
            EditorGUILayout.LabelField("Selected roots", Selection.gameObjects.Length.ToString());
            using (new EditorGUI.DisabledScope(!_prototype || Selection.gameObjects.Length == 0))
            {
                if (GUILayout.Button("Create Review Snapshot"))
                {
                    try
                    {
                        var container = InstanceConversionTools.BakeSnapshot(_prototype, Selection.gameObjects);
                        Selection.activeGameObject = container.gameObject;
                        _diagnostic = null;
                    }
                    catch (Exception exception)
                    {
                        _diagnostic = exception.Message;
                    }
                }
            }
            if (!string.IsNullOrEmpty(_diagnostic))
            {
                EditorGUILayout.HelpBox(_diagnostic, MessageType.Error);
            }
        }
    }
}

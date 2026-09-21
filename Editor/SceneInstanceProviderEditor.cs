using LoogaSoft.Instancing;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    [CustomEditor(typeof(SceneInstanceProvider))]
    internal sealed class SceneInstanceProviderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var provider = (SceneInstanceProvider)target;
            EditorGUILayout.HelpBox("Configured scene roots use BRG in edit and play mode. Disable this component to restore native rendering. " +
                "Use opaque or cutout DOTS shaders. Property blocks need a data adapter. Set Graphics > Shader Stripping > " +
                "BatchRendererGroup Variants to Keep All before a player build.", MessageType.Info);
            EditorGUILayout.LabelField("Active sources", provider.ActiveSourceCount.ToString());
            if (!string.IsNullOrEmpty(provider.Diagnostic))
            {
                EditorGUILayout.HelpBox(provider.Diagnostic, MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(provider.CompatibilityReport))
            {
                EditorGUILayout.HelpBox(provider.CompatibilityReport, MessageType.Info);
            }
            using (new EditorGUI.DisabledScope(!provider.isActiveAndEnabled))
            {
                if (GUILayout.Button("Rebuild Scene Instances"))
                {
                    provider.Rebuild();
                }
            }
        }
    }
}

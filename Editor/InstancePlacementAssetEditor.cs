using LoogaSoft.Instancing;
using UnityEditor;

namespace LoogaSoft.Instancing.Editor
{
    [CustomEditor(typeof(InstancePlacementAsset))]
    internal sealed class InstancePlacementAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var asset = (InstancePlacementAsset)target;
            EditorGUILayout.LabelField("Placements", asset.Count.ToString());
            EditorGUILayout.HelpBox("Load this snapshot into an InstanceContainer to edit its copy. " +
                "Use Save New Placement Snapshot to keep the original data.", MessageType.Info);
        }
    }
}

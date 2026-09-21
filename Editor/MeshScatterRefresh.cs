using LoogaSoft.Instancing;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    [InitializeOnLoad]
    internal static class MeshScatterRefresh
    {
        static MeshScatterRefresh()
        {
            EditorApplication.projectChanged += RefreshSources;
            Undo.undoRedoPerformed += RefreshSources;
        }

        private static void RefreshSources()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var author in Object.FindObjectsByType<MeshScatterAuthoring>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                author.RequestRebuild();
            }
        }
    }
}

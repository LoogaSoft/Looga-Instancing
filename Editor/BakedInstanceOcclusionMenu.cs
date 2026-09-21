using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    internal static class BakedInstanceOcclusionMenu
    {
        [MenuItem("Tools/Looga/Instancing/Bake Selected Box Occluders")]
        private static void Bake()
        {
            var sources = Selection.gameObjects.SelectMany(item => item.GetComponentsInChildren<MeshRenderer>()).Distinct().ToArray();
            if (sources.Length == 0) return;
            var root = new GameObject("Looga Baked Occluders");
            try
            {
                root.AddComponent<BakedInstanceOcclusion>().Bake(sources);
                Undo.RegisterCreatedObjectUndo(root, "Bake box occluders");
                Selection.activeGameObject = root;
            }
            catch
            {
                Object.DestroyImmediate(root);
                throw;
            }
        }
    }
}

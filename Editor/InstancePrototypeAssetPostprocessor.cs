using System;
using UnityEditor;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Editor
{
    /// <summary>Invalidates compiler entries that reference changed source assets.</summary>
    internal sealed class InstancePrototypeAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            Invalidate(importedAssets);
            Invalidate(movedAssets);
            if (deletedAssets.Length > 0)
            {
                // Deleted objects no longer have instance IDs that can identify individual entries.
                InstancePrototypeCompiler.ClearCache();
            }
        }

        private static void Invalidate(string[] paths)
        {
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                Object[] assets;
                try
                {
                    assets = AssetDatabase.LoadAllAssetsAtPath(path);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (Object asset in assets)
                {
                    InstancePrototypeCompiler.InvalidateDependency(asset);
                }
            }
        }
    }
}

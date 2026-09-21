using LoogaSoft.Instancing;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Instancing.Editor
{
    internal sealed class SceneInstanceBuildValidation : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return;
            bool hasSources = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                hasSources |= root.GetComponentsInChildren<SceneInstanceProvider>(true).Length != 0 ||
                    root.GetComponentsInChildren<InstanceContainer>(true).Length != 0;
            }
            if (!hasSources) return;
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets.Length == 0)
            {
                throw new BuildFailedException("Cannot verify BatchRendererGroup shader variant preservation.");
            }
            var settings = new SerializedObject(assets[0]);
            var stripping = settings.FindProperty("m_BrgStripping");
            // Unity 6 uses 2 for KeepAll. KeepIfEntitiesGraphics does not detect custom BRG renderers.
            if (stripping == null || stripping.intValue != 2)
            {
                throw new BuildFailedException("Looga instances require Graphics > Shader Stripping > " +
                    "BatchRendererGroup Variants = Keep All. Custom BRG rendering does not install Entities Graphics.");
            }
        }
    }
}

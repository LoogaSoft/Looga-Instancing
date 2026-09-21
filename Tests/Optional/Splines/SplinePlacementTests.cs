using System;
using LoogaSoft.Instancing;
using LoogaSoft.Instancing.Splines;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class SplinePlacementTests
    {
        [Test]
        public void CurveEditsUndoReplacementAndUnloadRefreshTheCorridor()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Spline edit fixture");
                SceneManager.MoveGameObjectToScene(root, scene);
                var spline = root.AddComponent<SplineContainer>();
                spline.Spline = new Spline(new[] {new BezierKnot(new Vector3(0, 0, 0)), new BezierKnot(new Vector3(0, 0, 20))});
                var receiver = new GameObject("Spline placement receiver");
                SceneManager.MoveGameObjectToScene(receiver, scene);
                var mask = receiver.AddComponent<PlacementExclusion>();
                var bridge = receiver.AddComponent<SplinePlacementBridge>();
                bridge.Configure(spline, mask, 2, 0);
                Assert.IsNull(bridge.Diagnostic);
                Assert.Zero(mask.Sample(new Vector3(0, 0, 10)));
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(spline, "Move spline knots");
                for (int i = 0; i < spline.Spline.Count; i++)
                {
                    var knot = spline.Spline[i];
                    knot.Position.x += 10;
                    spline.Spline[i] = knot;
                }
                Undo.FlushUndoRecordObjects();
                bridge.ProcessChanges();
                Assert.AreEqual(1, mask.Sample(new Vector3(0, 0, 10)));
                Assert.Zero(mask.Sample(new Vector3(10, 0, 10)));
                Undo.PerformUndo();
                bridge.ProcessChanges();
                Assert.Zero(mask.Sample(new Vector3(0, 0, 10)));
                var replacement = new GameObject("Replacement spline").AddComponent<SplineContainer>();
                SceneManager.MoveGameObjectToScene(replacement.gameObject, scene);
                replacement.Spline = new Spline(new[] {new BezierKnot(new Vector3(20, 0, 0)), new BezierKnot(new Vector3(20, 0, 20))});
                bridge.Configure(replacement, mask, 2, 0);
                Assert.Zero(mask.Sample(new Vector3(20, 0, 10)));
                bridge.enabled = false;
                Assert.AreEqual(1, mask.Sample(new Vector3(20, 0, 10)));
                bridge.enabled = true;
                bridge.ProcessChanges();
                Assert.Zero(mask.Sample(new Vector3(20, 0, 10)));
                Object.DestroyImmediate(replacement.gameObject);
                bridge.ProcessChanges();
                Assert.IsNotEmpty(bridge.Diagnostic);
                Assert.AreEqual(1, mask.Sample(new Vector3(20, 0, 10)));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}

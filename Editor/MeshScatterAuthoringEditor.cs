using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    [CustomEditor(typeof(MeshScatterAuthoring))]
    internal sealed class MeshScatterAuthoringEditor : UnityEditor.Editor
    {
        private bool _painting;
        private bool _erase;
        private float _radius = 5;
        private float _flow = 0.5f;
        private float _falloff = 1;
        private int _undoGroup = -1;
        private int _control;
        private Vector3 _lastDab;
        private string _error;

        public override void OnInspectorGUI()
        {
            var author = (MeshScatterAuthoring)target;
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
            {
                RunEdit(author, "Change mesh scatter", author.Regenerate);
            }
            EditorGUILayout.HelpBox("Assign a readable mesh and one container per prototype. Targets require positive uniform scale. " +
                "Paint changes density without editing the source mesh. Shift reverses paint/erase; Escape cancels the current stroke. " +
                "The brush affects nearby faces within its spherical radius. Source changes can regenerate placements. Remove this component to keep a fixed authored result.", MessageType.Info);
            EditorGUILayout.LabelField("Last generated placements", author.GeneratedCount.ToString());
            EditorGUILayout.LabelField("Last regenerated triangles", author.LastRegeneratedTriangles.ToString());
            EditorGUILayout.LabelField("Saved dabs", author.StrokeCount.ToString());
            if (GUILayout.Button("Regenerate Surface"))
            {
                RunEdit(author, "Regenerate mesh scatter", author.Regenerate);
            }
            if (GUILayout.Button("Clear Painted Mask"))
            {
                RunEdit(author, "Clear scatter mask", author.ClearMask);
            }
            bool painting = GUILayout.Toggle(_painting, "Paint Mesh Mask", "Button");
            if (_painting && !painting)
            {
                EndStroke(false);
            }
            _painting = painting;
            _erase = EditorGUILayout.Toggle("Erase", _erase);
            _radius = EditorGUILayout.Slider("Radius (meters)", _radius, 0.1f, 100);
            _flow = EditorGUILayout.Slider("Flow per dab", _flow, 0.01f, 1);
            _falloff = EditorGUILayout.Slider("Falloff", _falloff, 0, 4);
            string diagnostic = _error ?? author.Diagnostic;
            if (!string.IsNullOrEmpty(diagnostic))
            {
                EditorGUILayout.HelpBox(diagnostic, MessageType.Warning);
            }
        }

        private void OnDisable() => EndStroke(false);

        private void OnSceneGUI()
        {
            var author = (MeshScatterAuthoring)target;
            var current = Event.current;
            if (!_painting || author.Surface == null || Application.isPlaying) return;
            int control = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                EndStroke(true);
                current.Use();
                return;
            }
            if ((current.rawType == EventType.MouseUp || current.type == EventType.Ignore) && _undoGroup >= 0)
            {
                EndStroke(false);
                current.Use();
                return;
            }
            if (current.alt) return;
            if (current.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(control);
            }
            try
            {
                if (!author.Raycast(HandleUtility.GUIPointToWorldRay(current.mousePosition), out var point, out var normal)) return;
                bool erase = _erase ^ current.shift;
                Handles.color = erase ? new Color(1, 0.25f, 0.15f, 0.8f) : new Color(0.2f, 1, 0.4f, 0.8f);
                Handles.DrawWireDisc(point, normal, _radius);
                Handles.DrawWireDisc(point, normal, _radius * 0.25f);
                if (current.type == EventType.MouseMove)
                {
                    SceneView.RepaintAll();
                }
                if (current.button != 0 || (current.type != EventType.MouseDown && current.type != EventType.MouseDrag)) return;
                bool first = _undoGroup < 0;
                if (first)
                {
                    if (current.type != EventType.MouseDown) return;
                    Undo.IncrementCurrentGroup();
                    _undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Paint mesh scatter mask");
                    Undo.RegisterCompleteObjectUndo(UndoTargets(author), "Paint mesh scatter mask");
                    _control = control;
                    GUIUtility.hotControl = control;
                    _lastDab = point;
                }
                float distance = Vector3.Distance(_lastDab, point);
                int steps = first ? 1 : Mathf.Min(64, Mathf.FloorToInt(distance / (_radius * 0.25f)));
                Vector3 start = _lastDab;
                for (int i = 1; i <= steps; i++)
                {
                    var center = first ? point : Vector3.Lerp(start, point, (float)i / steps);
                    author.Paint(center, _radius, erase ? -_flow : _flow, _falloff);
                    _lastDab = center;
                }
                current.Use();
                _error = null;
                Repaint();
                SceneView.RepaintAll();
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                EndStroke(false);
                Repaint();
            }
        }

        private void EndStroke(bool cancel)
        {
            if (_undoGroup < 0) return;
            if (GUIUtility.hotControl == _control)
            {
                GUIUtility.hotControl = 0;
            }
            if (cancel)
            {
                Undo.RevertAllDownToGroup(_undoGroup);
            }
            else
            {
                Undo.CollapseUndoOperations(_undoGroup);
            }
            _undoGroup = -1;
        }

        private void RunEdit(MeshScatterAuthoring author, string label, Action action)
        {
            try
            {
                Undo.RegisterCompleteObjectUndo(UndoTargets(author), label);
                action();
                _error = null;
            }
            catch (Exception exception)
            {
                _error = exception.Message;
            }
        }

        private static UnityEngine.Object[] UndoTargets(MeshScatterAuthoring author)
        {
            var objects = new List<UnityEngine.Object> { author };
            objects.AddRange(author.GetAffectedContainers());
            return objects.ToArray();
        }
    }
}

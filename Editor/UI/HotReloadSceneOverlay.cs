// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Nyks.HotReload.Editor.Core;
using Nyks.HotReload.Editor.State;
using UnityEditor;
using UnityEngine;

namespace Nyks.HotReload.Editor.UI
{
    /// <summary>
    /// Renders floating SceneView toast notifications upon script hot-reload and injects
    /// live editable controls into the Unity Inspector for fields dynamically added during Play Mode.
    /// </summary>
    [InitializeOnLoad]
    public static class HotReloadSceneOverlay
    {
        private const double TOAST_DURATION_SECONDS = 3.2;

        private static HotReloadPatchEvent _activeToastEvent;
        private static double _toastStartTime;

        static HotReloadSceneOverlay()
        {
            HotReloadSessionManager.OnPatchApplied += OnPatchApplied;
            SceneView.duringSceneGui += OnDuringSceneGui;
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnInspectorHeaderGui;
        }

        private static void OnPatchApplied(HotReloadPatchEvent evt)
        {
            if (!HotReloadSessionManager.ShowToastNotifications || evt == null)
            {
                return;
            }

            _activeToastEvent = evt;
            _toastStartTime = EditorApplication.timeSinceStartup;
            SceneView.RepaintAll();

            // Also show brief notification in focused GameView if playing
            try
            {
                if (EditorWindow.focusedWindow != null &&
                    EditorWindow.focusedWindow.GetType().Name == "GameView")
                {
                    string msg = evt.Success
                        ? $"⚡ Hot Reloaded: {evt.ScriptName} ({evt.TotalTimeMs:F0}ms)"
                        : $"❌ Hot Reload Compile Error: {evt.ScriptName}";
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(msg), 1.8);
                }
            }
            catch
            {
                // Ignore if GameView notification is unavailable
            }
        }

        private static void OnDuringSceneGui(SceneView sceneView)
        {
            if (_activeToastEvent == null)
            {
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - _toastStartTime;
            if (elapsed > TOAST_DURATION_SECONDS)
            {
                _activeToastEvent = null;
                return;
            }

            float alpha = 1f;
            if (elapsed > TOAST_DURATION_SECONDS - 0.6)
            {
                alpha = Mathf.Clamp01((float)((TOAST_DURATION_SECONDS - elapsed) / 0.6));
            }

            Handles.BeginGUI();

            Color prevColor = GUI.color;
            Color bgTint = _activeToastEvent.Success
                ? new Color(0.08f, 0.22f, 0.14f, 0.92f * alpha)
                : new Color(0.28f, 0.10f, 0.10f, 0.92f * alpha);

            const float width = 340f;
            const float height = 54f;
            Rect viewRect = sceneView.position;
            Rect boxRect = new Rect(
                viewRect.width - width - 16f,
                viewRect.height - height - 38f,
                width,
                height);

            EditorGUI.DrawRect(boxRect, bgTint);

            Color borderTint = _activeToastEvent.Success
                ? new Color(0.29f, 0.87f, 0.50f, alpha)
                : new Color(0.97f, 0.44f, 0.44f, alpha);

            EditorGUI.DrawRect(new Rect(boxRect.x, boxRect.y, 4f, boxRect.height), borderTint);

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal = { textColor = new Color(1f, 1f, 1f, alpha) }
            };

            GUIStyle subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.82f, 0.88f, 0.85f, alpha) }
            };

            string title = _activeToastEvent.Success
                ? $"⚡ Hot Reloaded: {_activeToastEvent.ScriptName} ({_activeToastEvent.TotalTimeMs:F0}ms)"
                : $"❌ Compile Error: {_activeToastEvent.ScriptName}";

            string subtitle = _activeToastEvent.Success
                ? $"{_activeToastEvent.PatchedMethods.Count} methods patched • {_activeToastEvent.AddedFields.Count} dynamic fields"
                : (_activeToastEvent.Diagnostics.Count > 0 ? _activeToastEvent.Diagnostics[0].Message : "Check Console / Hot Reload Pro window");

            GUI.Label(new Rect(boxRect.x + 12f, boxRect.y + 8f, boxRect.width - 20f, 20f), title, titleStyle);
            GUI.Label(new Rect(boxRect.x + 12f, boxRect.y + 28f, boxRect.width - 20f, 18f), subtitle, subStyle);

            GUI.color = prevColor;
            Handles.EndGUI();

            sceneView.Repaint();
        }

        private static void OnInspectorHeaderGui(UnityEditor.Editor editor)
        {
            if (editor == null || !(editor.target is GameObject go))
            {
                return;
            }

            MonoBehaviour[] components = go.GetComponents<MonoBehaviour>();
            if (components == null || components.Length == 0)
            {
                return;
            }

            bool headerDrawn = false;

            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour comp = components[i];
                if (comp == null)
                {
                    continue;
                }

                string typeFullName = comp.GetType().FullName;
                IReadOnlyList<DynamicFieldDescriptor> dynFields = HotReloadStateStore.GetDynamicFieldsForType(typeFullName);
                if (dynFields == null || dynFields.Count == 0)
                {
                    continue;
                }

                if (!headerDrawn)
                {
                    headerDrawn = true;
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField("⚡ Hot Reload — Live Dynamic Fields", EditorStyles.boldLabel);
                }

                EditorGUILayout.LabelField($"Component: {comp.GetType().Name}", EditorStyles.miniBoldLabel);

                for (int f = 0; f < dynFields.Count; f++)
                {
                    DrawDynamicFieldControl(comp, dynFields[f]);
                }
            }

            if (headerDrawn)
            {
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4f);
            }
        }

        internal static void DrawDynamicFieldControl(object instance, DynamicFieldDescriptor desc)
        {
            if (desc == null)
            {
                return;
            }

            object currentVal = HotReloadStateStore.GetBoxed(instance, desc);
            string label = ObjectNames.NicifyVariableName(desc.FieldName);

            EditorGUI.BeginChangeCheck();
            object newVal = currentVal;

            if (desc.FieldType == typeof(float))
            {
                float v = currentVal is float fv ? fv : 0f;
                newVal = EditorGUILayout.FloatField(label, v);
            }
            else if (desc.FieldType == typeof(int))
            {
                int v = currentVal is int iv ? iv : 0;
                newVal = EditorGUILayout.IntField(label, v);
            }
            else if (desc.FieldType == typeof(bool))
            {
                bool v = currentVal is bool bv && bv;
                newVal = EditorGUILayout.Toggle(label, v);
            }
            else if (desc.FieldType == typeof(string))
            {
                string v = currentVal as string ?? string.Empty;
                newVal = EditorGUILayout.TextField(label, v);
            }
            else if (desc.FieldType == typeof(Vector2))
            {
                Vector2 v = currentVal is Vector2 v2 ? v2 : Vector2.zero;
                newVal = EditorGUILayout.Vector2Field(label, v);
            }
            else if (desc.FieldType == typeof(Vector3))
            {
                Vector3 v = currentVal is Vector3 v3 ? v3 : Vector3.zero;
                newVal = EditorGUILayout.Vector3Field(label, v);
            }
            else if (desc.FieldType == typeof(Color))
            {
                Color v = currentVal is Color c ? c : Color.white;
                newVal = EditorGUILayout.ColorField(label, v);
            }
            else if (desc.FieldType != null && desc.FieldType.IsEnum)
            {
                Enum v = currentVal is Enum ev ? ev : (Enum)Activator.CreateInstance(desc.FieldType);
                newVal = EditorGUILayout.EnumPopup(label, v);
            }
            else
            {
                EditorGUILayout.LabelField(label, currentVal != null ? currentVal.ToString() : "null");
            }

            if (EditorGUI.EndChangeCheck())
            {
                HotReloadStateStore.SetBoxed(instance, desc, newVal);
            }
        }
    }
}

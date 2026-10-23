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
            if (HotReloadSessionManager.IsBackgroundWorkerOrBatchProcess())
            {
                return;
            }

            HotReloadSessionManager.OnPatchApplied += OnPatchApplied;
            SceneView.duringSceneGui += OnDuringSceneGui;
        }

        private static void OnPatchApplied(HotReloadPatchEvent evt)
        {
            if (!HotReloadSessionManager.ShowToastNotifications || evt == null)
            {
                return;
            }

            _activeToastEvent = evt;
            _toastStartTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= OnOverlayUpdate;
            EditorApplication.update += OnOverlayUpdate;
            SceneView.RepaintAll();

            // Also show brief notification in focused GameView if playing
            try
            {
                if (EditorWindow.focusedWindow != null &&
                    EditorWindow.focusedWindow.GetType().Name == "GameView")
                {
                    bool tr = HotReloadSessionManager.IsTurkish;
                    string msg = !evt.Success
                        ? (tr ? $"📝 Kod Hatası ({evt.ScriptName})" : $"📝 C# Script Error: {evt.ScriptName}")
                        : (evt.RequiresDomainReload
                            ? (tr ? $"⚠️ Tam Yenileme Gerekli ({evt.ScriptName}): {evt.ReloadReason}" : $"⚠️ Full Reload Needed ({evt.ScriptName}): {evt.ReloadReason}")
                            : (tr ? $"⚡ Güncellendi: {evt.ScriptName} ({evt.TotalTimeMs:F0}ms)" : $"⚡ Hot Reloaded: {evt.ScriptName} ({evt.TotalTimeMs:F0}ms)"));
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(msg), 2.2);
                }
            }
            catch
            {
                // Ignore if GameView notification is unavailable
            }
        }

        private static void OnOverlayUpdate()
        {
            if (_activeToastEvent == null)
            {
                EditorApplication.update -= OnOverlayUpdate;
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - _toastStartTime;
            if (elapsed > TOAST_DURATION_SECONDS)
            {
                _activeToastEvent = null;
                EditorApplication.update -= OnOverlayUpdate;
                SceneView.RepaintAll();
            }
        }

        private static void OnDuringSceneGui(SceneView sceneView)
        {
            if (_activeToastEvent == null)
            {
                return;
            }

            if (Event.current != null && Event.current.type != EventType.Repaint)
            {
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - _toastStartTime;
            if (elapsed > TOAST_DURATION_SECONDS)
            {
                _activeToastEvent = null;
                return;
            }

            const float alpha = 1f;

            Handles.BeginGUI();

            Color prevColor = GUI.color;
            Color bgTint = !_activeToastEvent.Success
                ? new Color(0.28f, 0.10f, 0.10f, 0.92f * alpha)
                : (_activeToastEvent.RequiresDomainReload
                    ? new Color(0.26f, 0.20f, 0.06f, 0.92f * alpha)
                    : new Color(0.08f, 0.22f, 0.14f, 0.92f * alpha));

            const float width = 340f;
            const float height = 54f;
            Rect viewRect = sceneView.position;
            Rect boxRect = new Rect(
                viewRect.width - width - 16f,
                viewRect.height - height - 38f,
                width,
                height);

            EditorGUI.DrawRect(boxRect, bgTint);

            Color borderTint = !_activeToastEvent.Success
                ? new Color(0.97f, 0.44f, 0.44f, alpha)
                : (_activeToastEvent.RequiresDomainReload
                    ? new Color(0.98f, 0.75f, 0.14f, alpha)
                    : new Color(0.29f, 0.87f, 0.50f, alpha));

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

            bool tr = HotReloadSessionManager.IsTurkish;

            string title = !_activeToastEvent.Success
                ? (tr ? $"📝 Kod Hatası: {_activeToastEvent.ScriptName}" : $"📝 Script Error: {_activeToastEvent.ScriptName}")
                : (_activeToastEvent.RequiresDomainReload
                    ? (tr ? $"⚠️ Tam Yenileme Gerekli: {_activeToastEvent.ScriptName}" : $"⚠️ Full Reload Needed: {_activeToastEvent.ScriptName}")
                    : (tr ? $"⚡ Güncellendi: {_activeToastEvent.ScriptName} ({_activeToastEvent.TotalTimeMs:F0}ms)" : $"⚡ Hot Reloaded: {_activeToastEvent.ScriptName} ({_activeToastEvent.TotalTimeMs:F0}ms)"));

            string subtitle = !_activeToastEvent.Success
                ? (_activeToastEvent.Diagnostics.Count > 0
                    ? HotReloadSessionManager.FormatDiagnosticForUser(_activeToastEvent.Diagnostics[0], tr)
                    : (tr ? "Yazdığın kodda hata var (Console'a bak)" : "Check Console / Hot Reload Pro window"))
                : (_activeToastEvent.RequiresDomainReload
                    ? (tr ? $"{_activeToastEvent.ReloadReason} (Ctrl+Alt+R)" : $"{_activeToastEvent.ReloadReason} (Press Ctrl+Alt+R)")
                    : (tr ? $"{_activeToastEvent.PatchedMethods.Count} metot • {_activeToastEvent.AddedFields.Count} yeni değişken" : $"{_activeToastEvent.PatchedMethods.Count} methods patched • {_activeToastEvent.AddedFields.Count} dynamic fields"));

            GUI.Label(new Rect(boxRect.x + 12f, boxRect.y + 8f, boxRect.width - 20f, 20f), title, titleStyle);
            GUI.Label(new Rect(boxRect.x + 12f, boxRect.y + 28f, boxRect.width - 20f, 18f), subtitle, subStyle);

            GUI.color = prevColor;
            Handles.EndGUI();
        }
    }
}

// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Nyks.HotReload.Editor.Core;
using Nyks.HotReload.Editor.Detour;
using Nyks.HotReload.Editor.State;
using UnityEditor;
using UnityEngine;

namespace Nyks.HotReload.Editor.UI
{
    /// <summary>
    /// Studio dashboard window for Nyks Hot Reload Pro (Tools -> ⚡ Hot Reload Pro).
    /// </summary>
    public sealed class HotReloadWindow : EditorWindow
    {
        private int _selectedTab;
        private Vector2 _scrollPosition;
        private readonly HashSet<int> _expandedEvents = new HashSet<int>();

        private static readonly string[] _tabLabelsEn =
        {
            "📜 Live Patch Feed",
            "🔧 Active Native Detours",
            "🧬 Dynamic Fields (Selected)"
        };

        private static readonly string[] _tabLabelsTr =
        {
            "📜 Canlı Yama Geçmişi",
            "🔧 Aktif Metot Yamaları",
            "🧬 Dinamik Değişkenler (Seçili)"
        };

        [MenuItem("Tools/⚡ Hot Reload Pro %#h", false, 100)]
        public static void ShowWindow()
        {
            var window = GetWindow<HotReloadWindow>("⚡ Hot Reload Pro");
            window.minSize = new Vector2(460f, 420f);
            window.Show();
        }

        [MenuItem("Tools/🔄 Hot Reload Pro — Full Domain Reload %&r", false, 101)]
        public static void ShortcutFullDomainReload()
        {
            bool tr = HotReloadSessionManager.IsTurkish;
            Debug.Log(tr
                ? "<color=#38BDF8><b>[⚡ Hot Reload Pro]</b></color> Tam Derleme / Domain Reload başlatılıyor (<b>Ctrl+Alt+R</b>)..."
                : "<color=#38BDF8><b>[⚡ Hot Reload Pro]</b></color> Triggering Full Domain Reload (<b>Ctrl+Alt+R</b>)...");
            HotReloadSessionManager.ForceFullUnityReload();
        }

        [MenuItem("Tools/🧩 Hot Reload Pro — Regenerate IDE .csproj Files", false, 102)]
        public static void ShortcutRegenerateCsprojFiles()
        {
            bool tr = HotReloadSessionManager.IsTurkish;
            if (HotReloadSessionManager.EnsureIdeProjectFilesSynced(force: true))
            {
                Debug.Log(tr
                    ? "<color=#4ADE80><b>[⚡ Hot Reload Pro]</b></color> IDE için <b>.sln</b> ve <b>.csproj</b> proje dosyaları başarıyla oluşturuldu!"
                    : "<color=#4ADE80><b>[⚡ Hot Reload Pro]</b></color> Successfully generated <b>.sln</b> and <b>.csproj</b> files for your IDE!");
            }
            else
            {
                EditorUtility.RequestScriptReload();
            }
        }

        private void OnEnable()
        {
            HotReloadSessionManager.OnPatchApplied += OnPatchEvent;
            HotReloadSessionManager.OnStatusChanged += Repaint;
            Selection.selectionChanged += Repaint;
        }

        private void OnDisable()
        {
            HotReloadSessionManager.OnPatchApplied -= OnPatchEvent;
            HotReloadSessionManager.OnStatusChanged -= Repaint;
            Selection.selectionChanged -= Repaint;
        }

        private void OnPatchEvent(HotReloadPatchEvent _)
        {
            Repaint();
        }

        private void OnGUI()
        {
            bool tr = HotReloadSessionManager.IsTurkish;

            DrawHeaderBanner(tr);
            EditorGUILayout.Space(4f);
            DrawConfigurationPanel(tr);
            EditorGUILayout.Space(4f);
            DrawActionToolbar(tr);
            EditorGUILayout.Space(6f);

            string[] tabs = tr ? _tabLabelsTr : _tabLabelsEn;
            _selectedTab = GUILayout.Toolbar(_selectedTab, tabs, GUILayout.Height(24f));
            EditorGUILayout.Space(4f);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            switch (_selectedTab)
            {
                case 0:
                    DrawLivePatchFeedTab(tr);
                    break;
                case 1:
                    DrawActiveDetoursTab(tr);
                    break;
                case 2:
                    DrawDynamicFieldsTab(tr);
                    break;
            }
            EditorGUILayout.EndScrollView();

            DrawFooter(tr);
        }

        private void DrawHeaderBanner(bool tr)
        {
            Rect bannerRect = EditorGUILayout.GetControlRect(false, 48f);
            bool active = HotReloadSessionManager.IsEnabled;

            Color bg = active
                ? new Color(0.11f, 0.20f, 0.15f, 1f)
                : new Color(0.22f, 0.14f, 0.14f, 1f);

            EditorGUI.DrawRect(bannerRect, bg);

            Color accent = active
                ? new Color(0.29f, 0.87f, 0.50f, 1f)
                : new Color(0.95f, 0.45f, 0.45f, 1f);

            EditorGUI.DrawRect(new Rect(bannerRect.x, bannerRect.y, 5f, bannerRect.height), accent);

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                normal = { textColor = Color.white }
            };

            GUIStyle subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.82f, 0.88f, 0.85f, 1f) }
            };

            string statusText = active
                ? (tr ? "⚡ NYKS HOT RELOAD PRO — AKTİF" : "⚡ NYKS HOT RELOAD PRO — ACTIVE")
                : (tr ? "⏸ NYKS HOT RELOAD PRO — DURAKLATILDI" : "⏸ NYKS HOT RELOAD PRO — PAUSED");

            string modeInfo = EditorApplication.isPlaying
                ? (tr ? "Play Mode Aktif ⚡ (Domain Reload Kilitli 🔒)" : "Play Mode Active ⚡ (Domain Reload Locked 🔒)")
                : (HotReloadSessionManager.AllowEditModePatching
                    ? (tr ? "Edit Mode + Play Mode Aktif ⚡ (Donma Kilitli 🔒)" : "Edit Mode + Play Mode Active ⚡ (Domain Reload Locked 🔒)")
                    : (tr ? "Sadece Play Mode (Play Mode Bekleniyor)" : "Play Mode Only (Waiting for Play Mode)"));

            string subText = tr
                ? $"{modeInfo}  •  Aktif Yama: {NativeMethodDetour.ActiveDetourCount}  •  Unity {Application.unityVersion}"
                : $"{modeInfo}  •  Active Detours: {NativeMethodDetour.ActiveDetourCount}  •  Unity {Application.unityVersion}";

            GUI.Label(new Rect(bannerRect.x + 14f, bannerRect.y + 6f, bannerRect.width - 20f, 20f), statusText, titleStyle);
            GUI.Label(new Rect(bannerRect.x + 14f, bannerRect.y + 26f, bannerRect.width - 20f, 18f), subText, subStyle);
        }

        private void DrawConfigurationPanel(bool tr)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            HotReloadSessionManager.IsEnabled = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    tr ? "Hot Reload İzleyiciyi Etkinleştir" : "Enable Hot Reload Watcher",
                    tr ? "Değişen .cs dosyalarını otomatik derler ve anında yamalar." : "Automatically compiles and patches modified .cs files."),
                HotReloadSessionManager.IsEnabled);

            HotReloadSessionManager.LockPlayModeAutoRefresh = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    tr ? "Unity Donmasını / Domain Reload'u Kilitle" : "Lock Unity Domain Reload",
                    tr ? "Unity'nin her kayıtta donmasını ve baştan derleme yapmasını engeller." : "Prevents Unity from freezing and performing a full Domain Reload."),
                HotReloadSessionManager.LockPlayModeAutoRefresh);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            HotReloadSessionManager.AllowEditModePatching = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    tr ? "Edit Mode (Oyun Dışı) Anında Yama" : "Allow Edit-Mode Hot Patching",
                    tr ? "Oyun çalışmıyorken de (Edit Mode) kodları donmadan anında yamalar." : "Also hot-patches methods when saving scripts outside Play Mode."),
                HotReloadSessionManager.AllowEditModePatching);

            HotReloadSessionManager.ShowToastNotifications = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    tr ? "Sahne / Oyun Ekranı Bildirimi Göster" : "Show SceneView / GameView Toast",
                    tr ? "Kod güncellendiğinde sağ altta bildirim kutusu gösterir." : "Displays a floating notification badge whenever a script is hot-reloaded."),
                HotReloadSessionManager.ShowToastNotifications);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent(
                    tr ? "🎯 Tetikleme Modu:" : "🎯 Patch Trigger Mode:",
                    tr ? "Ctrl+S yapıldığı anda mı yoksa Unity ekranına tıkladığında mı yama yapılacağını seçin." : "Choose whether to compile & patch immediately on Ctrl+S or wait until you click back into the Unity Editor window."),
                EditorStyles.boldLabel,
                GUILayout.Width(145f));

            string[] triggerOptions = tr
                ? new[] { "⚡ Kaydedince Anında (Ctrl+S)", "🖱️ Unity'ye Tıklayınca (Odaklanınca)" }
                : new[] { "⚡ Instant on Save (Ctrl+S)", "🖱️ On Unity Focus (When Clicked)" };

            int currentTrigger = (int)HotReloadSessionManager.TriggerMode;
            int newTrigger = GUILayout.Toolbar(currentTrigger, triggerOptions, GUILayout.Height(21f));
            if (newTrigger != currentTrigger)
            {
                HotReloadSessionManager.TriggerMode = (HotReloadTriggerMode)newTrigger;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent(
                    tr ? "🌐 Dil / Language:" : "🌐 Language / Dil:",
                    tr ? "Arayüz ve Console hata açıklamalarının dilini seçin (Otomatik hatırlanır)." : "Select UI and Console error explanation language (Saved automatically)."),
                EditorStyles.boldLabel,
                GUILayout.Width(145f));

            string[] langOptions = { "🇹🇷 Türkçe", "🇬🇧 English" };
            int currentLang = (int)HotReloadSessionManager.Language;
            int newLang = GUILayout.Toolbar(currentLang, langOptions, GUILayout.Height(21f));
            if (newLang != currentLang)
            {
                HotReloadSessionManager.Language = (HotReloadLanguage)newLang;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawActionToolbar(bool tr)
        {
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !string.IsNullOrEmpty(HotReloadSessionManager.LastModifiedScriptPath);
            if (GUILayout.Button(tr ? "⚡ Son Scripti Yamala" : "⚡ Re-Patch Last Script", GUILayout.Height(24f)))
            {
                HotReloadSessionManager.PatchScriptNow(HotReloadSessionManager.LastModifiedScriptPath);
            }
            GUI.enabled = true;

            if (GUILayout.Button(tr ? "📂 .cs Seç ve Yamala..." : "📂 Patch .cs File...", GUILayout.Height(24f)))
            {
                string chosen = EditorUtility.OpenFilePanel(
                    tr ? "Yamalanacak C# Scriptini Seçin" : "Select C# Script to Hot-Patch",
                    Application.dataPath,
                    "cs");
                if (!string.IsNullOrEmpty(chosen))
                {
                    HotReloadSessionManager.PatchScriptNow(chosen);
                }
            }

            GUI.enabled = NativeMethodDetour.ActiveDetourCount > 0;
            if (GUILayout.Button(tr ? "↩ Yamaları Geri Al" : "↩ Revert Patches", GUILayout.Height(24f)))
            {
                int count = HotReloadSessionManager.RevertAllPatches();
                ShowNotification(new GUIContent(tr ? $"{count} metot yaması geri alındı." : $"Reverted {count} method detours."));
            }
            GUI.enabled = true;

            if (GUILayout.Button(
                new GUIContent(
                    tr ? "🔄 Tam Derleme (Ctrl+Alt+R)" : "🔄 Full Domain Reload (Ctrl+Alt+R)",
                    tr ? "Unity'nin tam derlemesini ve Domain Reload işlemini başlatır (Kısayol: Ctrl+Alt+R)." : "Triggers a full Unity script compilation and Domain Reload (Shortcut: Ctrl+Alt+R)."),
                GUILayout.Height(24f)))
            {
                HotReloadSessionManager.ForceFullUnityReload();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawLivePatchFeedTab(bool tr)
        {
            IReadOnlyList<HotReloadPatchEvent> history = HotReloadSessionManager.History;
            if (history.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    tr
                        ? "Hot Reload Pro hem Edit Mode hem Play Mode'da AKTİF!\n\n" +
                          "1. IDE'nde herhangi bir C# scriptini aç (ör. PlayerController.cs).\n" +
                          "2. Bir metodu değiştir veya yeni değişken ekle ve Ctrl+S yap.\n" +
                          "3. Unity donmadan milisaniyeler içinde yamalandığını burada gör!"
                        : "Hot Reload Pro is ACTIVE in both Edit Mode and Play Mode!\n\n" +
                          "1. Open any C# script in your IDE (e.g. PlayerController.cs) right now (in Edit Mode OR Play Mode).\n" +
                          "2. Modify any method or add a field and press Ctrl+S.\n" +
                          "3. Watch it compile and patch here in milliseconds without freezing Unity!",
                    MessageType.Info);
                return;
            }

            for (int i = 0; i < history.Count; i++)
            {
                HotReloadPatchEvent evt = history[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                string icon = !evt.Success ? "📝" : (evt.RequiresDomainReload ? "⚠️" : "✅");
                string statusPrefix = !evt.Success
                    ? (tr ? "[Kod Hatası] " : "[Script Error] ")
                    : string.Empty;
                string header = $"{icon} [{evt.TimestampLocal:HH:mm:ss}]  {statusPrefix}{evt.ScriptName}  —  {evt.TotalTimeMs:F0} ms ({evt.CompilerEngine})";

                bool isExpanded = _expandedEvents.Contains(i);
                bool nextExpanded = EditorGUILayout.Foldout(isExpanded, header, true, EditorStyles.boldLabel);
                if (nextExpanded != isExpanded)
                {
                    if (nextExpanded) _expandedEvents.Add(i);
                    else _expandedEvents.Remove(i);
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(
                    tr ? $"{evt.PatchedMethods.Count} metot" : $"{evt.PatchedMethods.Count} methods",
                    EditorStyles.miniLabel,
                    GUILayout.Width(72f));
                EditorGUILayout.EndHorizontal();

                if (nextExpanded)
                {
                    EditorGUI.indentLevel++;
                    if (evt.PatchedMethods.Count > 0)
                    {
                        EditorGUILayout.LabelField(tr ? "Yamalanan Metotlar:" : "Patched Methods:", EditorStyles.miniBoldLabel);
                        foreach (string m in evt.PatchedMethods)
                        {
                            EditorGUILayout.LabelField($"• {m}", EditorStyles.miniLabel);
                        }
                    }

                    if (evt.AddedFields.Count > 0)
                    {
                        EditorGUILayout.LabelField(tr ? "Eklenen Dinamik Değişkenler:" : "Dynamic Virtual Fields Added:", EditorStyles.miniBoldLabel);
                        foreach (string f in evt.AddedFields)
                        {
                            EditorGUILayout.LabelField($"• {f}", EditorStyles.miniLabel);
                        }
                    }

                    if (evt.Diagnostics.Count > 0)
                    {
                        EditorGUILayout.LabelField(
                            tr ? "Kod Hataları (Eklenti hatası değildir, yazdığın C# kodundaki hatalar):" : "Script Compiler Diagnostics:",
                            EditorStyles.miniBoldLabel);
                        foreach (CompilerDiagnosticEntry d in evt.Diagnostics)
                        {
                            MessageType msgType = d.IsError ? MessageType.Error : MessageType.Warning;
                            string text = HotReloadSessionManager.FormatDiagnosticForUser(d, tr).Replace("<b>", "").Replace("</b>", "");
                            EditorGUILayout.HelpBox(text, msgType);
                        }
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawActiveDetoursTab(bool tr)
        {
            IReadOnlyCollection<DetourRecord> detours = NativeMethodDetour.ActiveDetours;
            if (detours.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    tr ? "Şu anda bellekte aktif bir metot yaması yok." : "No native method detours are currently active in memory.",
                    MessageType.Info);
                return;
            }

            foreach (DetourRecord record in detours)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                string ownerName = record.OriginalMethod.DeclaringType != null ? record.OriginalMethod.DeclaringType.FullName : "<Global>";
                EditorGUILayout.LabelField($"🔧 {ownerName}.{record.OriginalMethod.Name}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    tr
                        ? $"Orijinal Adres: 0x{record.OriginalSite.ToInt64():X}  →  Yama Adresi: 0x{record.TargetSite.ToInt64():X}   |   Revizyon: {record.PatchCount}"
                        : $"Original Ptr: 0x{record.OriginalSite.ToInt64():X}  →  Patched Ptr: 0x{record.TargetSite.ToInt64():X}   |   Revisions: {record.PatchCount}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawDynamicFieldsTab(bool tr)
        {
            GameObject selectedGo = Selection.activeGameObject;
            if (selectedGo == null)
            {
                EditorGUILayout.HelpBox(
                    tr
                        ? "Play Mode sırasında yeni eklediğin değişkenleri görmek ve düzenlemek için Hierarchy'den bir GameObject seç."
                        : "Select a GameObject in the Hierarchy to inspect and edit any fields dynamically added during Play Mode.",
                    MessageType.Info);
                return;
            }

            MonoBehaviour[] comps = selectedGo.GetComponents<MonoBehaviour>();
            bool anyFound = false;

            foreach (MonoBehaviour comp in comps)
            {
                if (comp == null) continue;
                IReadOnlyList<DynamicFieldDescriptor> fields = HotReloadStateStore.GetDynamicFieldsForType(comp.GetType().FullName);
                if (fields == null || fields.Count == 0) continue;

                anyFound = true;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"🧬 {comp.GetType().Name} ({selectedGo.name})", EditorStyles.boldLabel);
                foreach (DynamicFieldDescriptor desc in fields)
                {
                    DrawDynamicFieldControl(comp, desc);
                }
                EditorGUILayout.EndVertical();
            }

            if (!anyFound)
            {
                EditorGUILayout.HelpBox(
                    tr
                        ? $"Seçili '{selectedGo.name}' objesinde henüz dinamik eklenmiş bir değişken yok.\n" +
                          "Play Mode sırasında bu objenin scriptine yeni bir değişken ekleyip Ctrl+S yapabilirsin!"
                        : $"Selected GameObject '{selectedGo.name}' has no dynamically added fields yet.\n" +
                          "Try adding a new field (e.g., '[SerializeField] private float _boostBonus = 25f;') to one of its scripts during Play Mode and press Ctrl+S!",
                    MessageType.Info);
            }
        }

        private static void DrawDynamicFieldControl(object instance, DynamicFieldDescriptor desc)
        {
            if (desc == null) return;
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

        private void DrawFooter(bool tr)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(
                tr
                    ? "Nyks Hot Reload Pro v1.0.0  •  Apache-2.0 Lisansı  •  Sıfır Build İzi"
                    : "Nyks Hot Reload Pro v1.0.0  •  Apache-2.0 License  •  Zero Build Footprint",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
    }
}

// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#pragma warning disable UAC0005
#pragma warning disable UAC0007
#pragma warning disable UAC0020

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Nyks.HotReload.Editor.Detour;
using Nyks.HotReload.Editor.State;
using UnityEditor;
using UnityEngine;

namespace Nyks.HotReload.Editor.Core
{
    /// <summary>
    /// Defines when modified C# scripts should be compiled and hot-patched.
    /// </summary>
    public enum HotReloadTriggerMode
    {
        InstantOnSave = 0,
        OnUnityFocus = 1
    }

    /// <summary>
    /// Represents a single Hot Reload compilation & patch event recorded in the session history.
    /// </summary>
    public sealed class HotReloadPatchEvent
    {
        public DateTime TimestampLocal { get; set; }
        public string ScriptName { get; set; }
        public string ScriptPath { get; set; }
        public bool Success { get; set; }
        public double TotalTimeMs { get; set; }
        public string CompilerEngine { get; set; }
        public List<string> PatchedMethods { get; } = new List<string>();
        public List<string> AddedFields { get; } = new List<string>();
        public List<CompilerDiagnosticEntry> Diagnostics { get; } = new List<CompilerDiagnosticEntry>();
    }

    /// <summary>
    /// Central orchestrator for watching C# script changes in BOTH Edit Mode and Play Mode,
    /// intercepting Unity's slow Domain Reloads, compiling modified scripts, and applying native method detours.
    /// </summary>
    [InitializeOnLoad]
    public static class HotReloadSessionManager
    {
        private const string PREF_ENABLED = "Nyks.HotReload.IsEnabled";
        private const string PREF_LOCK_DOMAIN_RELOAD = "Nyks.HotReload.LockDomainReload";
        private const string PREF_ALLOW_EDITMODE = "Nyks.HotReload.AllowEditModePatch_v2";
        private const string PREF_SHOW_TOAST = "Nyks.HotReload.ShowToast";
        private const string PREF_TRIGGER_MODE = "Nyks.HotReload.TriggerMode";
        private const string PREF_SAVED_UNITY_AUTOREFRESH = "Nyks.HotReload.SavedUnityAutoRefresh";

        private const string UNITY_PREF_AUTOREFRESH = "kAutoRefresh";
        private const string UNITY_PREF_AUTOREFRESH_MODE = "kAutoRefreshMode";

        private const double DEBOUNCE_SECONDS = 0.10;

        private static FileSystemWatcher _watcher;
        private static readonly ConcurrentDictionary<string, double> _pendingFiles =
            new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<HotReloadPatchEvent> _history = new List<HotReloadPatchEvent>(64);
        private static readonly HashSet<string> _dirtyScriptsSinceDomainReload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _loggedReloadAdvisories = new HashSet<string>(StringComparer.Ordinal);

        private static bool _autoRefreshLockedByUs;
        private static string _lastModifiedScriptPath;
        private static bool _warmedUp;
        private static double _startupTime;

        public static event Action<HotReloadPatchEvent> OnPatchApplied;
        public static event Action OnStatusChanged;

        /// <summary>
        /// Master switch for Hot Reload Pro (Active in both Edit Mode and Play Mode by default).
        /// </summary>
        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(PREF_ENABLED, true);
            set
            {
                if (EditorPrefs.GetBool(PREF_ENABLED, true) != value)
                {
                    EditorPrefs.SetBool(PREF_ENABLED, value);
                    UpdateAutoRefreshLockState();
                    OnStatusChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Prevents Unity from freezing and performing a full Domain Reload when saving scripts in Edit Mode or Play Mode.
        /// </summary>
        public static bool LockPlayModeAutoRefresh
        {
            get => EditorPrefs.GetBool(PREF_LOCK_DOMAIN_RELOAD, true);
            set
            {
                if (EditorPrefs.GetBool(PREF_LOCK_DOMAIN_RELOAD, true) != value)
                {
                    EditorPrefs.SetBool(PREF_LOCK_DOMAIN_RELOAD, value);
                    UpdateAutoRefreshLockState();
                    OnStatusChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Enabled by default so saving scripts outside Play Mode (in Edit Mode) also compiles & hot-patches immediately without Domain Reload.
        /// </summary>
        public static bool AllowEditModePatching
        {
            get => EditorPrefs.GetBool(PREF_ALLOW_EDITMODE, true);
            set
            {
                if (EditorPrefs.GetBool(PREF_ALLOW_EDITMODE, true) != value)
                {
                    EditorPrefs.SetBool(PREF_ALLOW_EDITMODE, value);
                    UpdateAutoRefreshLockState();
                    OnStatusChanged?.Invoke();
                }
            }
        }

        public static bool ShowToastNotifications
        {
            get => EditorPrefs.GetBool(PREF_SHOW_TOAST, true);
            set
            {
                if (EditorPrefs.GetBool(PREF_SHOW_TOAST, true) != value)
                {
                    EditorPrefs.SetBool(PREF_SHOW_TOAST, value);
                    OnStatusChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Controls whether hot-patching triggers immediately on Ctrl+S or waits until the Unity Editor window is focused/clicked.
        /// </summary>
        public static HotReloadTriggerMode TriggerMode
        {
            get => (HotReloadTriggerMode)EditorPrefs.GetInt(PREF_TRIGGER_MODE, (int)HotReloadTriggerMode.InstantOnSave);
            set
            {
                int intVal = (int)value;
                if (EditorPrefs.GetInt(PREF_TRIGGER_MODE, 0) != intVal)
                {
                    EditorPrefs.SetInt(PREF_TRIGGER_MODE, intVal);
                    OnStatusChanged?.Invoke();
                }
            }
        }

        public static IReadOnlyList<HotReloadPatchEvent> History => _history;
        public static string LastModifiedScriptPath => _lastModifiedScriptPath;
        public static bool IsAutoRefreshLocked => _autoRefreshLockedByUs;
        public static int PendingUncompiledScriptCount => _dirtyScriptsSinceDomainReload.Count;
        public static int QueuedFocusFileCount => _pendingFiles.Count;

        static HotReloadSessionManager()
        {
            _startupTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            EditorApplication.delayCall += () =>
            {
                StartFileWatcher();
                UpdateAutoRefreshLockState();
            };
        }

        /// <summary>
        /// Manually triggers compilation and hot-patching for the specified C# script path (works in both Edit Mode and Play Mode).
        /// </summary>
        public static HotReloadPatchEvent PatchScriptNow(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                return null;
            }

            _lastModifiedScriptPath = scriptPath;
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            HotReloadCompileResult compileResult = HotReloadCompilerService.CompileScriptFile(scriptPath);

            var evt = new HotReloadPatchEvent
            {
                TimestampLocal = DateTime.Now,
                ScriptName = Path.GetFileName(scriptPath),
                ScriptPath = scriptPath,
                CompilerEngine = compileResult.EngineUsed ?? "Roslyn"
            };

            evt.Diagnostics.AddRange(compileResult.Diagnostics);

            if (!compileResult.Success || compileResult.CompiledAssembly == null)
            {
                totalSw.Stop();
                evt.Success = false;
                evt.TotalTimeMs = totalSw.Elapsed.TotalMilliseconds;
                RecordEvent(evt);
                return evt;
            }

            if (compileResult.RewriteInfo != null)
            {
                foreach (DynamicFieldDescriptor addedField in compileResult.RewriteInfo.AddedFields)
                {
                    evt.AddedFields.Add($"{addedField.OwnerTypeFullName}.{addedField.FieldName}");

                    if (addedField.RequiresUnityObjectSerialization)
                    {
                        LogDomainReloadAdvisoryOnce(
                            $"SerializedRef:{addedField.FieldKey}",
                            $"New serialized reference field (<b>{addedField.DeclaredTypeName} {addedField.FieldName}</b>) detected in <b>{evt.ScriptName}</b>. " +
                            "To assign object references via Drag & Drop in the Unity Inspector, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    }
                }
            }

            int patchedCount = ApplyAssemblyDetours(compileResult.CompiledAssembly, evt.ScriptName, evt.PatchedMethods, evt.Diagnostics);

            totalSw.Stop();
            evt.Success = patchedCount > 0 || evt.AddedFields.Count > 0;
            evt.TotalTimeMs = totalSw.Elapsed.TotalMilliseconds;

            _dirtyScriptsSinceDomainReload.Add(scriptPath);

            RecordEvent(evt);

            // Repaint all open Inspectors & SceneViews so changes appear immediately in Edit Mode and Play Mode
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            SceneView.RepaintAll();

            return evt;
        }

        /// <summary>
        /// Reverts all active native method detours and clears virtual field state.
        /// </summary>
        public static int RevertAllPatches()
        {
            int count = NativeMethodDetour.RevertAll();
            HotReloadStateStore.ClearAll();
            OnStatusChanged?.Invoke();
            return count;
        }

        /// <summary>
        /// Unlocks Auto-Refresh and triggers a clean Unity AssetDatabase Refresh outside the IMGUI loop.
        /// </summary>
        public static void ForceFullUnityReload()
        {
            _dirtyScriptsSinceDomainReload.Clear();
            _loggedReloadAdvisories.Clear();
            ReleaseAutoRefreshLock();
            RevertAllPatches();

            EditorApplication.delayCall += () =>
            {
                AssetDatabase.Refresh();
                UpdateAutoRefreshLockState();
            };
        }

        private static void StartFileWatcher()
        {
            try
            {
                DisposeFileWatcher();

                string assetsPath = Application.dataPath;
                if (!Directory.Exists(assetsPath))
                {
                    return;
                }

                _watcher = new FileSystemWatcher(assetsPath, "*.cs")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };

                _watcher.Created += OnFileChanged;
                _watcher.Changed += OnFileChanged;
                _watcher.Renamed += OnFileRenamed;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HotReloadPro] Failed to initialize FileSystemWatcher: {ex.Message}");
            }
        }

        private static void DisposeFileWatcher()
        {
            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnFileChanged;
                    _watcher.Changed -= OnFileChanged;
                    _watcher.Renamed -= OnFileRenamed;
                    _watcher.Dispose();
                }
                catch
                {
                    // Ignore dispose exceptions
                }
                _watcher = null;
            }
        }

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            QueueScriptChange(e.FullPath);
        }

        private static void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            QueueScriptChange(e.FullPath);
        }

        private static void QueueScriptChange(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string fileName = Path.GetFileName(fullPath);
            if (fileName.StartsWith(".", StringComparison.Ordinal) || fileName.EndsWith("~", StringComparison.Ordinal))
            {
                return;
            }

            _pendingFiles[fullPath] = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }

        private static void OnEditorUpdate()
        {
            // Warm up Roslyn in background 1.0s after Editor domain reload
            if (!_warmedUp && EditorApplication.timeSinceStartup - _startupTime > 1.0)
            {
                _warmedUp = true;
                HotReloadCompilerService.WarmUp();
            }

            if (_pendingFiles.IsEmpty)
            {
                return;
            }

            if (!IsEnabled)
            {
                _pendingFiles.Clear();
                return;
            }

            bool canPatchNow = EditorApplication.isPlaying || AllowEditModePatching;
            if (!canPatchNow)
            {
                _pendingFiles.Clear();
                return;
            }

            // If TriggerMode is OnUnityFocus, wait until the user clicks/focuses the Unity Editor window!
            if (TriggerMode == HotReloadTriggerMode.OnUnityFocus &&
                !UnityEditorInternal.InternalEditorUtility.isApplicationActive)
            {
                return;
            }

            double nowSec = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
            var readyPaths = new List<string>();

            foreach (var kvp in _pendingFiles)
            {
                if (nowSec - kvp.Value >= DEBOUNCE_SECONDS)
                {
                    readyPaths.Add(kvp.Key);
                }
            }

            foreach (string path in readyPaths)
            {
                _pendingFiles.TryRemove(path, out _);
                if (File.Exists(path))
                {
                    PatchScriptNow(path);
                }
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    UpdateAutoRefreshLockState();
                    // If scripts were hot-patched in Edit Mode and a Domain Reload happened on Play Mode entry,
                    // or if we want to ensure all Edit-Mode hot patches are active in Play Mode:
                    if (NativeMethodDetour.ActiveDetourCount == 0 && _dirtyScriptsSinceDomainReload.Count > 0)
                    {
                        foreach (string scriptPath in new List<string>(_dirtyScriptsSinceDomainReload))
                        {
                            if (File.Exists(scriptPath))
                            {
                                PatchScriptNow(scriptPath);
                            }
                        }
                    }
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    UpdateAutoRefreshLockState();
                    // If Edit-Mode patching is disabled, sync with Unity when exiting Play Mode.
                    // If Edit-Mode patching is enabled, keep patches active and Editor unfrozen!
                    if (!AllowEditModePatching && _dirtyScriptsSinceDomainReload.Count > 0)
                    {
                        _dirtyScriptsSinceDomainReload.Clear();
                        RevertAllPatches();
                        ReleaseAutoRefreshLock();
                        AssetDatabase.Refresh();
                    }
                    break;
            }
            OnStatusChanged?.Invoke();
        }

        private static void OnBeforeAssemblyReload()
        {
            DisposeFileWatcher();
            RevertAllPatches();
            ReleaseAutoRefreshLock();
        }

        private static void UpdateAutoRefreshLockState()
        {
            bool shouldLock = IsEnabled && LockPlayModeAutoRefresh &&
                              (EditorApplication.isPlayingOrWillChangePlaymode || AllowEditModePatching);

            if (shouldLock && !_autoRefreshLockedByUs)
            {
                // Save original Unity AutoRefresh preference before disabling it so Unity doesn't trigger Domain Reload on focus
                if (!EditorPrefs.HasKey(PREF_SAVED_UNITY_AUTOREFRESH))
                {
                    EditorPrefs.SetInt(PREF_SAVED_UNITY_AUTOREFRESH, EditorPrefs.GetInt(UNITY_PREF_AUTOREFRESH, 1));
                }
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH, 0);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH_MODE, 0);
                AssetDatabase.DisallowAutoRefresh();
                _autoRefreshLockedByUs = true;
            }
            else if (!shouldLock && _autoRefreshLockedByUs)
            {
                ReleaseAutoRefreshLock();
            }
        }

        private static void ReleaseAutoRefreshLock()
        {
            if (_autoRefreshLockedByUs)
            {
                AssetDatabase.AllowAutoRefresh();
                _autoRefreshLockedByUs = false;
            }

            if (EditorPrefs.HasKey(PREF_SAVED_UNITY_AUTOREFRESH))
            {
                int saved = EditorPrefs.GetInt(PREF_SAVED_UNITY_AUTOREFRESH, 1);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH, saved);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH_MODE, saved);
                EditorPrefs.DeleteKey(PREF_SAVED_UNITY_AUTOREFRESH);
            }
        }

        private static int ApplyAssemblyDetours(
            Assembly patchAssembly,
            string scriptName,
            List<string> patchedMethodNamesOut,
            List<CompilerDiagnosticEntry> diagnosticsOut)
        {
            int patchedCount = 0;
            Type[] patchTypes;
            try
            {
                patchTypes = patchAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                patchTypes = rtle.Types;
            }

            if (patchTypes == null)
            {
                return 0;
            }

            foreach (Type patchType in patchTypes)
            {
                if (patchType == null ||
                    patchType.Name == "IgnoresAccessChecksToAttribute" ||
                    patchType.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal))
                {
                    continue;
                }

                if (patchType.IsNested && patchType.Name.StartsWith("<", StringComparison.Ordinal))
                {
                    continue;
                }

                Type originalType = FindOriginalRuntimeType(patchType);
                if (originalType == null)
                {
                    LogDomainReloadAdvisoryOnce(
                        $"NewType:{patchType.FullName}",
                        $"New class/type (<b>{patchType.Name}</b>) detected in <b>{scriptName}</b>. " +
                        "For Unity's native engine and 'Add Component' menu to register this new type, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    continue;
                }

                if (patchType.IsEnum)
                {
                    if (HasEnumChanges(originalType, patchType))
                    {
                        LogDomainReloadAdvisoryOnce(
                            $"EnumChange:{patchType.FullName}",
                            $"Enum definition (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                            "To update enum values across the Inspector and project, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    }
                    continue;
                }

                if (patchType.IsInterface)
                {
                    LogDomainReloadAdvisoryOnce(
                        $"InterfaceChange:{patchType.FullName}",
                        $"Interface (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                        "To apply structural interface changes across the project, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    continue;
                }

                if (patchType.IsGenericTypeDefinition)
                {
                    LogDomainReloadAdvisoryOnce(
                        $"GenericType:{patchType.FullName}",
                        $"Generic type (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                        "To apply generic type signature changes, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                }
                else if (patchType.IsValueType && !patchType.IsEnum)
                {
                    LogDomainReloadAdvisoryOnce(
                        $"StructType:{patchType.FullName}",
                        $"Struct (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                        "To realign value-type memory layouts, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                }
                else if (HasInheritanceOrInterfaceChanges(originalType, patchType))
                {
                    LogDomainReloadAdvisoryOnce(
                        $"InheritanceChange:{patchType.FullName}",
                        $"Inheritance hierarchy or implemented interfaces of <b>{patchType.Name}</b> changed in <b>{scriptName}</b>. " +
                        "To apply structural inheritance changes, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                }

                SynchronizeStaticFields(originalType, patchType);

                const BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly;

                MethodInfo[] patchMethods = patchType.GetMethods(flags);
                foreach (MethodInfo patchMethod in patchMethods)
                {
                    if (patchMethod.IsAbstract || patchMethod.ContainsGenericParameters)
                    {
                        continue;
                    }

                    MethodInfo originalMethod = FindMatchingOriginalMethod(originalType, patchMethod, flags);
                    if (originalMethod == null)
                    {
                        continue;
                    }

                    if (NativeMethodDetour.TryDetour(originalMethod, patchMethod, out string err))
                    {
                        patchedCount++;
                        patchedMethodNamesOut.Add($"{originalType.Name}.{originalMethod.Name}");
                    }
                    else if (!string.IsNullOrEmpty(err))
                    {
                        diagnosticsOut.Add(new CompilerDiagnosticEntry
                        {
                            Id = "HR_DETOUR",
                            Message = err,
                            IsError = false
                        });
                    }
                }
            }

            return patchedCount;
        }

        private static Type FindOriginalRuntimeType(Type patchType)
        {
            string fullName = patchType.FullName;
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                Assembly asm = loaded[i];
                if (asm == null || asm.IsDynamic || asm == patchType.Assembly)
                {
                    continue;
                }

                string asmName = asm.GetName().Name;
                if (asmName.StartsWith("NyksHotReload_Patch_", StringComparison.Ordinal))
                {
                    continue;
                }

                Type match = asm.GetType(fullName, false);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static MethodInfo FindMatchingOriginalMethod(Type originalType, MethodInfo patchMethod, BindingFlags flags)
        {
            ParameterInfo[] patchParams = patchMethod.GetParameters();
            MethodInfo[] candidates = originalType.GetMethods(flags);

            foreach (MethodInfo candidate in candidates)
            {
                if (!string.Equals(candidate.Name, patchMethod.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (candidate.IsStatic != patchMethod.IsStatic)
                {
                    continue;
                }

                ParameterInfo[] candParams = candidate.GetParameters();
                if (candParams.Length != patchParams.Length)
                {
                    continue;
                }

                bool paramsMatch = true;
                for (int i = 0; i < candParams.Length; i++)
                {
                    Type tA = candParams[i].ParameterType;
                    Type tB = patchParams[i].ParameterType;
                    if (tA != tB && !string.Equals(tA.FullName, tB.FullName, StringComparison.Ordinal))
                    {
                        paramsMatch = false;
                        break;
                    }
                }

                if (paramsMatch)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void SynchronizeStaticFields(Type originalType, Type patchType)
        {
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            FieldInfo[] origFields = originalType.GetFields(staticFlags);

            foreach (FieldInfo origField in origFields)
            {
                if (origField.IsLiteral || origField.IsInitOnly)
                {
                    continue;
                }

                FieldInfo patchField = patchType.GetField(origField.Name, staticFlags);
                if (patchField != null && patchField.FieldType == origField.FieldType)
                {
                    try
                    {
                        object val = origField.GetValue(null);
                        patchField.SetValue(null, val);
                    }
                    catch
                    {
                        // Ignore non-copyable static fields
                    }
                }
            }
        }

        private static bool HasEnumChanges(Type originalEnum, Type patchEnum)
        {
            if (!originalEnum.IsEnum || !patchEnum.IsEnum)
            {
                return false;
            }

            string[] origNames = Enum.GetNames(originalEnum);
            string[] patchNames = Enum.GetNames(patchEnum);
            if (origNames.Length != patchNames.Length)
            {
                return true;
            }

            for (int i = 0; i < origNames.Length; i++)
            {
                if (!string.Equals(origNames[i], patchNames[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasInheritanceOrInterfaceChanges(Type originalType, Type patchType)
        {
            string origBase = originalType.BaseType != null ? originalType.BaseType.FullName : string.Empty;
            string patchBase = patchType.BaseType != null ? patchType.BaseType.FullName : string.Empty;

            if (!string.Equals(origBase, patchBase, StringComparison.Ordinal))
            {
                return true;
            }

            Type[] origIfaces = originalType.GetInterfaces();
            Type[] patchIfaces = patchType.GetInterfaces();
            if (origIfaces.Length != patchIfaces.Length)
            {
                return true;
            }

            var origSet = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < origIfaces.Length; i++)
            {
                if (origIfaces[i].FullName != null)
                {
                    origSet.Add(origIfaces[i].FullName);
                }
            }

            for (int i = 0; i < patchIfaces.Length; i++)
            {
                if (patchIfaces[i].FullName != null && !origSet.Contains(patchIfaces[i].FullName))
                {
                    return true;
                }
            }

            return false;
        }

        private static void LogDomainReloadAdvisoryOnce(string advisoryKey, string message)
        {
            if (string.IsNullOrEmpty(advisoryKey))
            {
                return;
            }

            if (_loggedReloadAdvisories.Add(advisoryKey))
            {
                Debug.LogWarning($"<color=#FBBF24><b>[⚠️ Hot Reload Pro — Domain Reload Required]</b></color> {message}");
            }
        }

        private static void RecordEvent(HotReloadPatchEvent evt)
        {
            _history.Insert(0, evt);
            if (_history.Count > 50)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            string modeTag = EditorApplication.isPlaying ? "Play Mode" : "Edit Mode";

            if (evt.Success)
            {
                Debug.Log(
                    $"<color=#4ADE80><b>[⚡ Hot Reload Pro • {modeTag}]</b></color> Patched <b>{evt.ScriptName}</b> in <b>{evt.TotalTimeMs:F0}ms</b> " +
                    $"({evt.PatchedMethods.Count} methods, {evt.AddedFields.Count} new fields) via <i>{evt.CompilerEngine}</i>.");
            }
            else
            {
                var errSb = new System.Text.StringBuilder();
                errSb.AppendLine($"<color=#F87171><b>[⚡ Hot Reload Pro • {modeTag}]</b></color> Failed to compile <b>{evt.ScriptName}</b>:");
                foreach (CompilerDiagnosticEntry d in evt.Diagnostics)
                {
                    if (d.IsError)
                    {
                        errSb.AppendLine($"  • {d}");
                    }
                }
                Debug.LogWarning(errSb.ToString());
            }

            OnPatchApplied?.Invoke(evt);
            OnStatusChanged?.Invoke();
        }
    }
}

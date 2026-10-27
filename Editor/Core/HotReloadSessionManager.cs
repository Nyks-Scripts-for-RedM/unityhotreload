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
    /// Display language for Hot Reload Pro UI, toast notifications, and console messages.
    /// </summary>
    public enum HotReloadLanguage
    {
        Turkish = 0,
        English = 1
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
        public bool RequiresDomainReload { get; set; }
        public string ReloadReason { get; set; }
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
        private const string PREF_ALLOW_EDITMODE = "Nyks.HotReload.AllowEditModePatch_v3";
        private const string PREF_SKIP_PLAYMODE_DOMAIN_RELOAD = "Nyks.HotReload.SkipPlayModeDomainReload_v1";
        private const string PREF_SHOW_TOAST = "Nyks.HotReload.ShowToast";
        private const string PREF_TRIGGER_MODE = "Nyks.HotReload.TriggerMode";
        private const string PREF_LANGUAGE = "Nyks.HotReload.Language";
        private const string PREF_SAVED_UNITY_AUTOREFRESH = "Nyks.HotReload.SavedUnityAutoRefresh";

        private const string UNITY_PREF_AUTOREFRESH = "kAutoRefresh";
        private const string UNITY_PREF_AUTOREFRESH_MODE = "kAutoRefreshMode";

        private const double DEBOUNCE_SECONDS = 0.08;
        private const double POLL_INTERVAL_SECONDS = 0.25;

        private static readonly Dictionary<string, long> _sourceAssetDbTicks =
            new Dictionary<string, long>(256, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ulong> _scriptContentHashes =
            new Dictionary<string, ulong>(256, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _dirtyScriptRealTicks =
            new Dictionary<string, long>(64, StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, double> _pendingFiles =
            new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<HotReloadPatchEvent> _history = new List<HotReloadPatchEvent>(64);
        private static readonly HashSet<string> _dirtyScriptsSinceDomainReload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _uncompiledNewScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _loggedReloadAdvisories = new HashSet<string>(StringComparer.Ordinal);

        private sealed class DynamicLifecycleHook
        {
            public Type OriginalComponentType;
            public string MethodName;
            public IntPtr FunctionPointer;
            public bool IsOneShot;
            public HashSet<int> ExecutedInstanceIds;
        }

        private sealed class DynamicScheduledInvoke
        {
            public MonoBehaviour Instance;
            public string Key;
            public string MethodName;
            public double NextExecutionTime;
            public float RepeatRate;
            public bool IsRepeating;
        }

        private sealed class DynamicAddedCoroutine
        {
            public Type OriginalComponentType;
            public string MethodName;
            public MethodInfo PatchMethod;
        }

        private sealed class DynamicRunningCoroutine
        {
            public MonoBehaviour Instance;
            public string MethodName;
            public Coroutine Handle;
        }

        private static readonly Dictionary<string, DynamicLifecycleHook> _dynamicLifecycleHooks =
            new Dictionary<string, DynamicLifecycleHook>(StringComparer.Ordinal);

        private static readonly Dictionary<string, DynamicLifecycleHook> _dynamicAddedMethods =
            new Dictionary<string, DynamicLifecycleHook>(StringComparer.Ordinal);

        private static readonly List<DynamicScheduledInvoke> _dynamicScheduledInvokes =
            new List<DynamicScheduledInvoke>(16);

        private static readonly Dictionary<string, DynamicAddedCoroutine> _dynamicAddedCoroutines =
            new Dictionary<string, DynamicAddedCoroutine>(StringComparer.Ordinal);

        private static readonly List<DynamicRunningCoroutine> _dynamicRunningCoroutines =
            new List<DynamicRunningCoroutine>(16);

        private static readonly HashSet<string> _unityNativeMessagesRequiringReload = new HashSet<string>(StringComparer.Ordinal)
        {
            "OnDisable", "OnDestroy",
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit",
            "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
            "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
            "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseDrag",
            "OnBecameVisible", "OnBecameInvisible", "OnControllerColliderHit", "OnAnimatorMove", "OnAnimatorIK"
        };

        private static bool _autoRefreshLockedByUs;
        private static bool _isPerformingFullReload;
        private static bool _initialBaselineComplete;
        private static bool _newScriptFileCreated;
        private static bool _requiresReloadOnExitPlayMode;
        private static double _lastPollTime;
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
        /// Enables instant ~25ms Hot Patching in Edit Mode as well as Play Mode by pinning SourceAssetDB
        /// timestamps on disk (preventing Unity 6 QuickSearch ASIAI0B double reloads) and caching patches
        /// across Play Mode transitions.
        /// </summary>
        public static bool AllowEditModePatching
        {
            get => EditorPrefs.GetBool(PREF_ALLOW_EDITMODE, true);
            set
            {
                if (EditorPrefs.GetBool(PREF_ALLOW_EDITMODE, true) != value)
                {
                    EditorPrefs.SetBool(PREF_ALLOW_EDITMODE, value);
                    if (!value && !EditorApplication.isPlaying && _dirtyScriptsSinceDomainReload.Count > 0)
                    {
                        ForceFullUnityReload();
                        return;
                    }
                    if (value && !_initialBaselineComplete && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
                    {
                        StartFileWatcher();
                    }
                    UpdateAutoRefreshLockState();
                    OnStatusChanged?.Invoke();
                }
            }
        }

        private static bool CanHotPatchInCurrentMode()
        {
            return IsEnabled && (EditorApplication.isPlaying || AllowEditModePatching);
        }

        /// <summary>
        /// When true (default), entering Play Mode skips Unity's native "Reloading assemblies for play mode" Domain Reload
        /// (Project Settings > Editor > Enter Play Mode Settings > "Reload Scene only"). Scenes are still reloaded,
        /// so scene objects run Awake/OnEnable/Start normally without a 4-5s Domain Reload wait on Play Mode entry.
        /// </summary>
        public static bool SkipDomainReloadOnEnterPlayMode
        {
            get => EditorPrefs.GetBool(PREF_SKIP_PLAYMODE_DOMAIN_RELOAD, true);
            set
            {
                if (EditorPrefs.GetBool(PREF_SKIP_PLAYMODE_DOMAIN_RELOAD, true) != value)
                {
                    EditorPrefs.SetBool(PREF_SKIP_PLAYMODE_DOMAIN_RELOAD, value);
                }
                SyncEnterPlayModeSettings();
                OnStatusChanged?.Invoke();
            }
        }

        private static void SyncEnterPlayModeSettings()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            bool skipReload = IsEnabled && EditorPrefs.GetBool(PREF_SKIP_PLAYMODE_DOMAIN_RELOAD, true);
            if (skipReload)
            {
                if (!EditorSettings.enterPlayModeOptionsEnabled ||
                    EditorSettings.enterPlayModeOptions != EnterPlayModeOptions.DisableDomainReload)
                {
                    EditorSettings.enterPlayModeOptionsEnabled = true;
                    EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                }
            }
            else
            {
                if ((EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0)
                {
                    EditorSettings.enterPlayModeOptions &= ~EnterPlayModeOptions.DisableDomainReload;
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

        /// <summary>
        /// Persistent UI and Console diagnostic language (remembers selection across Unity restarts).
        /// </summary>
        public static HotReloadLanguage Language
        {
            get => (HotReloadLanguage)EditorPrefs.GetInt(PREF_LANGUAGE, (int)HotReloadLanguage.Turkish);
            set
            {
                int intVal = (int)value;
                if (EditorPrefs.GetInt(PREF_LANGUAGE, (int)HotReloadLanguage.Turkish) != intVal)
                {
                    EditorPrefs.SetInt(PREF_LANGUAGE, intVal);
                    OnStatusChanged?.Invoke();
                }
            }
        }

        public static bool IsTurkish => Language == HotReloadLanguage.Turkish;

        public static IReadOnlyList<HotReloadPatchEvent> History => _history;
        public static string LastModifiedScriptPath => _lastModifiedScriptPath;
        public static bool IsAutoRefreshLocked => _autoRefreshLockedByUs;
        public static int PendingUncompiledScriptCount => _dirtyScriptsSinceDomainReload.Count;
        public static int QueuedFocusFileCount => _pendingFiles.Count;

        /// <summary>
        /// Returns true when running inside Unity's background AssetImportWorker process or headless batchmode.
        /// Hot Reload Pro must never start FileSystemWatchers or compilers inside background worker processes.
        /// </summary>
        public static bool IsBackgroundWorkerOrBatchProcess()
        {
            if (Application.isBatchMode)
            {
                return true;
            }

            try
            {
                if (AssetDatabase.IsAssetImportWorkerProcess())
                {
                    return true;
                }
            }
            catch
            {
                // Ignore if unavailable on older Unity versions
            }

            string cmd = Environment.CommandLine ?? string.Empty;
            return cmd.IndexOf("AssetImportWorker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cmd.IndexOf("-batchmode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cmd.IndexOf("-noUpm", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static HotReloadSessionManager()
        {
            if (IsBackgroundWorkerOrBatchProcess())
            {
                return;
            }

            _startupTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            Application.logMessageReceived += OnUnityLogMessageReceived;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnUnityCompilationStarted;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnUnityCompilationFinished;

            HotReloadStateStore.OnScheduleDynamicInvoke = ScheduleDynamicInvoke;
            HotReloadStateStore.OnCancelDynamicInvoke = CancelDynamicInvoke;
            HotReloadStateStore.OnCheckDynamicIsInvoking = CheckDynamicIsInvoking;
            HotReloadStateStore.OnStartDynamicCoroutine = StartDynamicCoroutine;
            HotReloadStateStore.OnStopDynamicCoroutine = StopDynamicCoroutine;

            // If we are entering Play Mode (or reloading in Edit Mode with AllowEditModePatching enabled),
            // immediately restore and detour any cached Edit-Mode/Play-Mode patches BEFORE Unity invokes
            // AwakeInstancesAfterBackupRestoration (Awake / OnEnable) on scene objects!
            if (IsEnabled && (EditorApplication.isPlayingOrWillChangePlaymode || AllowEditModePatching))
            {
                TryRestoreAndApplyCachedPatchOnDomainReload();
            }

            // Defer disk enumeration (StartFileWatcher) and EditorPrefs (kAutoRefresh / kAutoRefreshMode) mutation
            // until the first EditorApplication.update tick after StopAssetImportingV2 finishes.
            EditorApplication.CallbackFunction initOnFirstUpdate = null;
            initOnFirstUpdate = () =>
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    return;
                }
                EditorApplication.update -= initOnFirstUpdate;

                if (IsBackgroundWorkerOrBatchProcess())
                {
                    return;
                }

                if (SessionState.GetBool("Nyks.HotReload.ManualReloadRequested", false))
                {
                    SessionState.SetBool("Nyks.HotReload.ManualReloadRequested", false);
                    bool tr = IsTurkish;
                    Debug.Log(tr
                        ? "<color=#4ADE80><b>[⚡ Hot Reload Pro]</b></color> Tam Derleme tamamlandı — Hot Reload hazır!"
                        : "<color=#4ADE80><b>[⚡ Hot Reload Pro]</b></color> Full Domain Reload complete — Hot Reload is ready!");
                }

                if (CanHotPatchInCurrentMode())
                {
                    if (!_initialBaselineComplete)
                    {
                        StartFileWatcher();
                    }
                    UpdateAutoRefreshLockState();
                }
                else if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    RestoreAutoRefreshPref();
                }

                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    SyncEnterPlayModeSettings();
                    EnsureIdeProjectFilesSynced(force: false);
                }
            };
            EditorApplication.update += initOnFirstUpdate;
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

            return PatchScriptsBatchNow(new[] { scriptPath });
        }

        /// <summary>
        /// Compiles and hot-patches one or more modified C# scripts together in a single compilation pass.
        /// Eliminates sequential multi-script compilation delays and resolves cross-file dependencies cleanly.
        /// </summary>
        public static HotReloadPatchEvent PatchScriptsBatchNow(IReadOnlyList<string> scriptPaths)
        {
            if (scriptPaths == null || scriptPaths.Count == 0)
            {
                return null;
            }

            var validPaths = new List<string>(scriptPaths.Count);
            var scriptNames = new List<string>(scriptPaths.Count);
            for (int i = 0; i < scriptPaths.Count; i++)
            {
                string p = scriptPaths[i];
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    validPaths.Add(p);
                    scriptNames.Add(Path.GetFileName(p));
                }
            }

            if (validPaths.Count == 0)
            {
                return null;
            }

            string primaryPath = validPaths[validPaths.Count - 1];
            _lastModifiedScriptPath = primaryPath;
            string combinedDisplayName = scriptNames.Count == 1
                ? scriptNames[0]
                : string.Join(", ", scriptNames);

            // Include any other dirty scripts modified since the last Domain Reload as companion syntax trees
            // so cross-script references (e.g. new constants in Consts.cs or new public methods in another modified script)
            // resolve cleanly in In-Memory Roslyn without re-triggering field/detour checks on those companion scripts.
            var companionPaths = new List<string>();
            foreach (string dirtyPath in _dirtyScriptsSinceDomainReload)
            {
                if (!string.IsNullOrEmpty(dirtyPath) && File.Exists(dirtyPath) &&
                    !validPaths.Exists(p => string.Equals(p, dirtyPath, StringComparison.OrdinalIgnoreCase)))
                {
                    companionPaths.Add(dirtyPath);
                }
            }

            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            HotReloadCompileResult compileResult = HotReloadCompilerService.CompileScriptBatch(validPaths, companionPaths);

            var evt = new HotReloadPatchEvent
            {
                TimestampLocal = DateTime.Now,
                ScriptName = combinedDisplayName,
                ScriptPath = primaryPath,
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
                        evt.RequiresDomainReload = true;
                        evt.ReloadReason = $"New serialized reference field '{addedField.DeclaredTypeName} {addedField.FieldName}'";
                        LogDomainReloadAdvisoryOnce(
                            $"SerializedRef:{addedField.FieldKey}",
                            $"New serialized reference field (<b>{addedField.DeclaredTypeName} {addedField.FieldName}</b>) detected in <b>{evt.ScriptName}</b>. " +
                            "To assign object references via Drag & Drop in the Unity Inspector, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    }
                }
            }

            int patchedCount = ApplyAssemblyDetours(
                compileResult.CompiledAssembly,
                evt.ScriptName,
                compileResult.RewriteInfo?.DeclaredTypeNames,
                compileResult.RewriteInfo?.CompanionTypeNames,
                evt.PatchedMethods,
                evt.Diagnostics,
                out bool detourRequiresReload,
                out string detourReloadReason);

            if (detourRequiresReload)
            {
                evt.RequiresDomainReload = true;
                if (string.IsNullOrEmpty(evt.ReloadReason))
                {
                    evt.ReloadReason = detourReloadReason;
                }
            }

            if (evt.RequiresDomainReload && EditorApplication.isPlaying)
            {
                _requiresReloadOnExitPlayMode = true;
            }

            totalSw.Stop();
            evt.Success = true;
            evt.TotalTimeMs = totalSw.Elapsed.TotalMilliseconds;

            if (patchedCount > 0 || evt.AddedFields.Count > 0 || evt.RequiresDomainReload)
            {
                for (int i = 0; i < validPaths.Count; i++)
                {
                    _dirtyScriptsSinceDomainReload.Add(validPaths[i]);
                }

                if (compileResult.RawAssemblyBytes != null && compileResult.RawAssemblyBytes.Length > 0)
                {
                    SavePatchCache(compileResult.RawAssemblyBytes, _requiresReloadOnExitPlayMode || evt.RequiresDomainReload);
                }

                RecordEvent(evt);
            }

            // Structural changes made in Edit Mode (new class/enum value/interface/serialized reference field) cannot be
            // detoured safely, so trigger ONE clean Unity reload right away instead of leaving a half-applied patch that
            // would later force an unexpected reload when entering Play Mode.
            // (Normal method-body edits in Edit Mode or Play Mode NEVER trigger a Domain Reload!)
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode && evt.RequiresDomainReload)
            {
                ForceFullUnityReload();
                return evt;
            }

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
            ClearAllDynamicRuntimeState();
            int count = NativeMethodDetour.RevertAll();
            HotReloadStateStore.ClearAll();
            OnStatusChanged?.Invoke();
            return count;
        }

        private static void ClearAllDynamicRuntimeState()
        {
            Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
            _dynamicLifecycleHooks.Clear();
            _dynamicAddedMethods.Clear();
            _dynamicScheduledInvokes.Clear();
            _dynamicAddedCoroutines.Clear();
            _dynamicRunningCoroutines.Clear();
        }

        private static void ClearPlayModeOnlyDynamicState()
        {
            _dynamicScheduledInvokes.Clear();
            _dynamicRunningCoroutines.Clear();
            foreach (var kvp in _dynamicLifecycleHooks)
            {
                if (kvp.Value != null && kvp.Value.ExecutedInstanceIds != null)
                {
                    kvp.Value.ExecutedInstanceIds.Clear();
                }
            }
        }

        /// <summary>
        /// Unpins all modified .cs file timestamps on disk, unlocks Auto-Refresh, and triggers a clean Unity AssetDatabase Refresh.
        /// </summary>
        public static void ForceFullUnityReload()
        {
            UnpinDirtyScriptTimestamps();
            ClearPatchCache();
            _dirtyScriptsSinceDomainReload.Clear();
            _dirtyScriptRealTicks.Clear();
            _uncompiledNewScripts.Clear();
            _loggedReloadAdvisories.Clear();
            _pendingFiles.Clear();
            _requiresReloadOnExitPlayMode = false;
            _isPerformingFullReload = true;
            SessionState.SetBool("Nyks.HotReload.ManualReloadRequested", true);
            ReleaseAutoRefreshLock();

            ClearAllDynamicRuntimeState();
            NativeMethodDetour.RevertAll();
            HotReloadStateStore.ClearAll();

            EditorApplication.CallbackFunction runReload = null;
            runReload = () =>
            {
                EditorApplication.update -= runReload;
                if (_newScriptFileCreated)
                {
                    _newScriptFileCreated = false;
                    EnsureIdeProjectFilesSynced(force: true);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                // AssetDatabase.Refresh synchronously sets EditorApplication.isCompiling = true if any .cs file changed on disk.
                // Only call RequestScriptReload when no script compilation was queued; otherwise RequestScriptReload
                // forces a redundant second StopAssetImportingV2(ForceSynchronousImport | ForceDomainReload) pass!
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
                {
                    if (EditorUtility.scriptCompilationFailed)
                    {
                        _isPerformingFullReload = false;
                        SessionState.SetBool("Nyks.HotReload.ManualReloadRequested", false);
                        bool tr = IsTurkish;
                        Debug.LogWarning(tr
                            ? "<color=#FBBF24><b>[⚠️ Hot Reload Pro]</b></color> Console'daki kırmızı C# kod hataları nedeniyle Unity Domain Reload yapamıyor. Önce kod hatalarını düzeltip kaydedin."
                            : "<color=#FBBF24><b>[⚠️ Hot Reload Pro]</b></color> Unity cannot perform a Domain Reload while C# compiler errors exist in the Console.");
                        return;
                    }
                    EditorUtility.RequestScriptReload();
                }
            };
            EditorApplication.update += runReload;
        }

        /// <summary>
        /// Generates .sln and .csproj files (via com.unity.ide.visualstudio's SdkStyleProjectGeneration)
        /// even when Unity classifies custom IDEs (such as Antigravity IDE, Cursor, or Windsurf) as "(internal)"
        /// and hides the "Regenerate project files" button in Preferences -> External Tools.
        /// </summary>
        public static bool EnsureIdeProjectFilesSynced(bool force = false)
        {
            try
            {
                string assetsPath = Application.dataPath;
                if (string.IsNullOrEmpty(assetsPath))
                {
                    return false;
                }

                DirectoryInfo parentDir = Directory.GetParent(assetsPath);
                if (parentDir == null)
                {
                    return false;
                }

                string projectRoot = parentDir.FullName;
                string csprojPath = Path.Combine(projectRoot, "Assembly-CSharp.csproj");
                if (!force && File.Exists(csprojPath))
                {
                    return false;
                }

                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type sdkGenType = asm.GetType("Microsoft.Unity.VisualStudio.Editor.SdkStyleProjectGeneration", false);
                    if (sdkGenType == null)
                    {
                        continue;
                    }

                    object generator = Activator.CreateInstance(sdkGenType);
                    MethodInfo syncMethod = sdkGenType.GetMethod("Sync", BindingFlags.Public | BindingFlags.Instance);
                    if (syncMethod != null)
                    {
                        syncMethod.Invoke(generator, null);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HotReloadPro] Failed to sync IDE project files: {ex.Message}");
            }
            return false;
        }

        private static void StartFileWatcher()
        {
            try
            {
                _uncompiledNewScripts.Clear();
                string assetsPath = Application.dataPath;
                if (!Directory.Exists(assetsPath))
                {
                    return;
                }

                foreach (string filePath in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!_sourceAssetDbTicks.ContainsKey(filePath))
                        {
                            _sourceAssetDbTicks[filePath] = File.GetLastWriteTimeUtc(filePath).Ticks;
                        }
                        if (!_scriptContentHashes.ContainsKey(filePath))
                        {
                            _scriptContentHashes[filePath] = ComputeFileHash64(filePath);
                        }
                    }
                    catch
                    {
                        // Ignore inaccessible files
                    }
                }
                _initialBaselineComplete = true;
                _lastPollTime = EditorApplication.timeSinceStartup;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HotReloadPro] Failed to initialize script baseline: {ex.Message}");
            }
        }

        private static void DisposeFileWatcher()
        {
            _uncompiledNewScripts.Clear();
            _pendingFiles.Clear();
        }

        private static void PollModifiedScriptsMainThread()
        {
            if (!CanHotPatchInCurrentMode() || !_initialBaselineComplete || _isPerformingFullReload ||
                EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastPollTime < POLL_INTERVAL_SECONDS)
            {
                return;
            }
            _lastPollTime = EditorApplication.timeSinceStartup;

            ScanModifiedScriptsCore(queueForLivePatch: true);
        }

        private static void ScanModifiedScriptsCore(bool queueForLivePatch)
        {
            string assetsPath = Application.dataPath;
            if (string.IsNullOrEmpty(assetsPath) || !Directory.Exists(assetsPath))
            {
                return;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (fileName.StartsWith(".", StringComparison.Ordinal) || fileName.EndsWith("~", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    long currentTicks;
                    try
                    {
                        currentTicks = File.GetLastWriteTimeUtc(filePath).Ticks;
                    }
                    catch
                    {
                        continue;
                    }

                    if (_sourceAssetDbTicks.TryGetValue(filePath, out long pinnedTicks))
                    {
                        if (currentTicks != pinnedTicks)
                        {
                            ulong currentHash = ComputeFileHash64(filePath);

                            // Immediately pin the file's LastWriteTimeUtc on disk back to Unity's SourceAssetDB timestamp!
                            // This prevents Unity 6's background AssetImportWorker (QuickSearch ASIAI0B / PreviewImporter)
                            // from throwing Import Error Code:(4) (SourceDataChangedDuringImport) and forcing a Domain Reload.
                            TryPinFileTimestampToSourceAssetDb(filePath, pinnedTicks);

                            bool contentChanged = !_scriptContentHashes.TryGetValue(filePath, out ulong prevHash) ||
                                                  (currentHash != 0UL && currentHash != prevHash);
                            if (contentChanged)
                            {
                                _scriptContentHashes[filePath] = currentHash;
                                _dirtyScriptRealTicks[filePath] = currentTicks > pinnedTicks ? currentTicks : DateTime.UtcNow.Ticks;
                                _dirtyScriptsSinceDomainReload.Add(filePath);
                                if (queueForLivePatch && CanHotPatchInCurrentMode())
                                {
                                    QueueScriptChange(filePath);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Newly created script file: record baseline and mark for IDE .csproj sync
                        ulong currentHash = ComputeFileHash64(filePath);
                        _sourceAssetDbTicks[filePath] = currentTicks;
                        _scriptContentHashes[filePath] = currentHash;
                        _dirtyScriptRealTicks[filePath] = currentTicks;
                        _newScriptFileCreated = true;
                        _uncompiledNewScripts.Add(filePath);
                        _dirtyScriptsSinceDomainReload.Add(filePath);
                        if (queueForLivePatch && CanHotPatchInCurrentMode())
                        {
                            QueueScriptChange(filePath);
                        }
                    }
                }
            }
            catch
            {
                // Ignore transient file system enumeration errors
            }
        }

        private static void QueueScriptChange(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            if (!CanHotPatchInCurrentMode())
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
            if (_isPerformingFullReload)
            {
                return;
            }

            // Pre-resolve compiler paths, warm up MetadataReferences, and JIT-warm Roslyn on a background thread
            // 1.0s after Editor domain reload so Play Mode has 0ms main-thread hitch and ~20ms first compile.
            if (!_warmedUp && !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                EditorApplication.timeSinceStartup - _startupTime > 1.0)
            {
                _warmedUp = true;
                HotReloadCompilerService.WarmUp();
                if (CanHotPatchInCurrentMode())
                {
                    if (!_initialBaselineComplete)
                    {
                        StartFileWatcher();
                    }
                    UpdateAutoRefreshLockState();
                }
            }

            PollModifiedScriptsMainThread();

            if (_pendingFiles.IsEmpty)
            {
                return;
            }

            if (!CanHotPatchInCurrentMode() || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _pendingFiles.Clear();
                _uncompiledNewScripts.Clear();
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

            if (readyPaths.Count == 0)
            {
                return;
            }

            var validBatchPaths = new List<string>(readyPaths.Count);

            foreach (string path in readyPaths)
            {
                _pendingFiles.TryRemove(path, out _);
                if (!File.Exists(path))
                {
                    continue;
                }

                if (_uncompiledNewScripts.Contains(path))
                {
                    if (!HasNonWhitespaceContent(path))
                    {
                        // Newly created empty file (e.g., 'New File' in IDE); wait until user writes code and saves.
                        continue;
                    }

                    _uncompiledNewScripts.Remove(path);
                }

                validBatchPaths.Add(path);
            }

            if (validBatchPaths.Count > 0)
            {
                PatchScriptsBatchNow(validBatchPaths);
            }
        }

        private static void FlushPendingPatchesImmediately()
        {
            if (_pendingFiles.IsEmpty)
            {
                return;
            }

            var batch = new List<string>();
            foreach (var kvp in _pendingFiles)
            {
                if (File.Exists(kvp.Key) && HasNonWhitespaceContent(kvp.Key))
                {
                    batch.Add(kvp.Key);
                }
            }
            _pendingFiles.Clear();

            if (batch.Count > 0)
            {
                PatchScriptsBatchNow(batch);
            }
        }

        private static bool HasNonWhitespaceContent(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length == 0)
                {
                    return false;
                }

                string text = File.ReadAllText(filePath);
                return !string.IsNullOrWhiteSpace(text);
            }
            catch
            {
                return false;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // If the user saved a file in their IDE right before clicking Play (or with TriggerMode == OnUnityFocus),
                    // scan, pin its timestamp to SourceAssetDB, and compile it into the Cross-Domain Patch Cache now
                    // so Play Mode starts with the latest code and zero extra Domain Reload!
                    if (IsEnabled && AllowEditModePatching && _initialBaselineComplete)
                    {
                        ScanModifiedScriptsCore(queueForLivePatch: true);
                        FlushPendingPatchesImmediately();
                    }
                    else
                    {
                        _pendingFiles.Clear();
                        _uncompiledNewScripts.Clear();
                        ReleaseAutoRefreshLock();
                    }
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    _pendingFiles.Clear();
                    if (!_initialBaselineComplete)
                    {
                        StartFileWatcher();
                    }
                    UpdateAutoRefreshLockState();
                    ExecutePendingOneShotLifecycleHooks();
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    if (IsEnabled && _initialBaselineComplete)
                    {
                        ScanModifiedScriptsCore(queueForLivePatch: true);
                        FlushPendingPatchesImmediately();
                    }
                    _pendingFiles.Clear();
                    ClearPlayModeOnlyDynamicState();
                    if (!AllowEditModePatching || !IsEnabled)
                    {
                        RevertAllPatches();
                    }
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    _pendingFiles.Clear();
                    if (_initialBaselineComplete)
                    {
                        ScanModifiedScriptsCore(queueForLivePatch: false);
                    }

                    // Only trigger a full Unity recompile on exit Play Mode if:
                    // 1. Edit Mode Hot Patching is disabled, OR
                    // 2. A structural change (new MonoBehaviour class, new [SerializeField] ref, or enum change) occurred during Play Mode.
                    if (!AllowEditModePatching || !IsEnabled || _requiresReloadOnExitPlayMode || _newScriptFileCreated)
                    {
                        _initialBaselineComplete = false;
                        RevertAllPatches();
                        ReleaseAutoRefreshLock();

                        if (_dirtyScriptsSinceDomainReload.Count > 0 || _newScriptFileCreated)
                        {
                            UnpinDirtyScriptTimestamps();
                            ClearPatchCache();
                            _dirtyScriptsSinceDomainReload.Clear();
                            _dirtyScriptRealTicks.Clear();
                            _requiresReloadOnExitPlayMode = false;
                            if (_newScriptFileCreated)
                            {
                                _newScriptFileCreated = false;
                                EnsureIdeProjectFilesSynced(force: true);
                            }
                            AssetDatabase.Refresh(ImportAssetOptions.Default);
                        }
                    }
                    else
                    {
                        if (!_initialBaselineComplete)
                        {
                            StartFileWatcher();
                        }
                        UpdateAutoRefreshLockState();
                    }
                    break;
            }
            OnStatusChanged?.Invoke();
        }

        private static void OnUnityCompilationStarted(object _)
        {
            // Whenever Unity's own script compiler starts compiling, unpin all dirty script timestamps
            // and clear the patch cache so the newly compiled Assembly-CSharp.dll becomes the clean baseline.
            UnpinDirtyScriptTimestamps();
            ClearPatchCache();
            _dirtyScriptsSinceDomainReload.Clear();
            _dirtyScriptRealTicks.Clear();
            _requiresReloadOnExitPlayMode = false;
        }

        private static void OnUnityCompilationFinished(object _)
        {
            // If compilation finished with errors, Unity aborts the Domain Reload ("Editor compiler errors found. Will not reload assemblies.").
            // Reset _isPerformingFullReload so Hot Reload Pro never stays stuck waiting for a reload that Unity aborted!
            if (EditorUtility.scriptCompilationFailed)
            {
                _isPerformingFullReload = false;
                if (SessionState.GetBool("Nyks.HotReload.ManualReloadRequested", false))
                {
                    SessionState.SetBool("Nyks.HotReload.ManualReloadRequested", false);
                    bool tr = IsTurkish;
                    Debug.LogWarning(tr
                        ? "<color=#FBBF24><b>[⚠️ Hot Reload Pro]</b></color> Console'daki kırmızı C# kod hataları nedeniyle Unity Domain Reload işlemini durdurdu. Hataları düzeltip kaydettiğinizde derleme tamamlanacaktır."
                        : "<color=#FBBF24><b>[⚠️ Hot Reload Pro]</b></color> Unity aborted the Domain Reload due to C# compiler errors in the Console.");
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (EditorApplication.isCompiling)
            {
                UnpinDirtyScriptTimestamps();
                ClearPatchCache();
            }

            DisposeFileWatcher();
            ClearAllDynamicRuntimeState();
            NativeMethodDetour.ClearOnDomainUnload();
            HotReloadStateStore.ClearAll();

            if (_autoRefreshLockedByUs)
            {
                AssetDatabase.AllowAutoRefresh();
                _autoRefreshLockedByUs = false;
            }
        }

        private static void OnEditorQuitting()
        {
            UnpinDirtyScriptTimestamps();
            ClearPatchCache();
            OnBeforeAssemblyReload();
            ReleaseAutoRefreshLock();
        }

        private static void UpdateAutoRefreshLockState()
        {
            if (_isPerformingFullReload)
            {
                return;
            }

            bool shouldLock = IsEnabled && LockPlayModeAutoRefresh && (EditorApplication.isPlaying || AllowEditModePatching);

            if (shouldLock)
            {
                if (!EditorPrefs.HasKey(PREF_SAVED_UNITY_AUTOREFRESH))
                {
                    int currentMode = EditorPrefs.GetInt(UNITY_PREF_AUTOREFRESH_MODE, 1);
                    EditorPrefs.SetInt(PREF_SAVED_UNITY_AUTOREFRESH, currentMode > 0 ? currentMode : 1);
                }
                // Set both kAutoRefresh (bool/int) and kAutoRefreshMode (0 = Disabled) so neither Unity nor IDE packages
                // (e.g. com.unity.ide.visualstudio / com.unity.ide.rider) trigger an unwanted AssetDatabase.Refresh() on Ctrl+S
                EditorPrefs.SetBool(UNITY_PREF_AUTOREFRESH, false);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH, 0);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH_MODE, 0);
            }
            else
            {
                RestoreAutoRefreshPref();
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (shouldLock && !_autoRefreshLockedByUs)
            {
                AssetDatabase.DisallowAutoRefresh();
                _autoRefreshLockedByUs = true;
            }
            else if (!shouldLock && _autoRefreshLockedByUs)
            {
                AssetDatabase.AllowAutoRefresh();
                _autoRefreshLockedByUs = false;
            }
        }

        private static void ReleaseAutoRefreshLock()
        {
            if (_autoRefreshLockedByUs)
            {
                AssetDatabase.AllowAutoRefresh();
                _autoRefreshLockedByUs = false;
            }
            RestoreAutoRefreshPref();
        }

        private static void RestoreAutoRefreshPref()
        {
            if (EditorPrefs.HasKey(PREF_SAVED_UNITY_AUTOREFRESH))
            {
                int saved = EditorPrefs.GetInt(PREF_SAVED_UNITY_AUTOREFRESH, 1);
                if (saved <= 0)
                {
                    saved = 1;
                }
                EditorPrefs.SetBool(UNITY_PREF_AUTOREFRESH, true);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH, saved);
                EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH_MODE, saved);
                EditorPrefs.DeleteKey(PREF_SAVED_UNITY_AUTOREFRESH);
            }
            else if (!EditorApplication.isPlaying && !AllowEditModePatching)
            {
                // Ensure Unity Auto-Refresh is never left disabled when Edit Mode Hot Patching is turned off
                if (EditorPrefs.GetInt(UNITY_PREF_AUTOREFRESH_MODE, 1) == 0)
                {
                    EditorPrefs.SetBool(UNITY_PREF_AUTOREFRESH, true);
                    EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH, 1);
                    EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH_MODE, 1);
                }
            }
        }

        private static ulong ComputeFileHash64(string filePath)
        {
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            ulong hash = 14695981039346656037UL;
                            byte[] buf = new byte[4096];
                            int read;
                            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                for (int i = 0; i < read; i++)
                                {
                                    hash ^= buf[i];
                                    hash *= 1099511628211UL;
                                }
                            }
                            return hash == 0UL ? 1UL : hash;
                        }
                    }
                    catch (IOException)
                    {
                        if (attempt == 2) throw;
                        System.Threading.Thread.Sleep(5);
                    }
                }
            }
            catch
            {
                // Fallback if file is temporarily locked
            }
            return 0UL;
        }

        private static void TryPinFileTimestampToSourceAssetDb(string filePath, long pinnedTicks)
        {
            if (pinnedTicks <= 0 || string.IsNullOrEmpty(filePath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.SetLastWriteTimeUtc(filePath, new DateTime(pinnedTicks, DateTimeKind.Utc));
                    return;
                }
                catch (IOException)
                {
                    if (attempt < 2)
                    {
                        System.Threading.Thread.Sleep(5);
                    }
                }
                catch
                {
                    return;
                }
            }
        }

        private static void UnpinDirtyScriptTimestamps()
        {
            try
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                foreach (string dirtyPath in _dirtyScriptsSinceDomainReload)
                {
                    if (string.IsNullOrEmpty(dirtyPath) || !File.Exists(dirtyPath))
                    {
                        continue;
                    }

                    long targetTicks = nowTicks;
                    if (_dirtyScriptRealTicks.TryGetValue(dirtyPath, out long realTicks) && realTicks > targetTicks)
                    {
                        targetTicks = realTicks;
                    }

                    try
                    {
                        File.SetLastWriteTimeUtc(dirtyPath, new DateTime(targetTicks, DateTimeKind.Utc));
                        _sourceAssetDbTicks[dirtyPath] = targetTicks;
                    }
                    catch
                    {
                        // Ignore inaccessible files
                    }
                }
            }
            catch
            {
                // Non-fatal
            }
        }

        private static string GetPatchCacheDir()
        {
            try
            {
                string assetsPath = Application.dataPath;
                if (string.IsNullOrEmpty(assetsPath))
                {
                    return null;
                }

                DirectoryInfo parent = Directory.GetParent(assetsPath);
                return parent != null ? Path.Combine(parent.FullName, "Library", "HotReloadPatchCache") : null;
            }
            catch
            {
                return null;
            }
        }

        private static long GetPrimaryScriptAssemblyTicks()
        {
            try
            {
                string assetsPath = Application.dataPath;
                if (string.IsNullOrEmpty(assetsPath))
                {
                    return 0L;
                }

                DirectoryInfo parent = Directory.GetParent(assetsPath);
                if (parent == null)
                {
                    return 0L;
                }

                string asmPath = Path.Combine(parent.FullName, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
                if (File.Exists(asmPath))
                {
                    return File.GetLastWriteTimeUtc(asmPath).Ticks;
                }
            }
            catch
            {
                // Ignore
            }
            return 0L;
        }

        private static void SavePatchCache(byte[] peBytes, bool requiresReloadOnExit)
        {
            try
            {
                string cacheDir = GetPatchCacheDir();
                if (string.IsNullOrEmpty(cacheDir) || peBytes == null || peBytes.Length == 0)
                {
                    return;
                }

                Directory.CreateDirectory(cacheDir);
                string binPath = Path.Combine(cacheDir, "latest_patch.bin");
                string statePath = Path.Combine(cacheDir, "dirty_state.txt");

                File.WriteAllBytes(binPath, peBytes);

                var sb = new System.Text.StringBuilder(2048);
                sb.AppendLine($"ASM_TICKS|{GetPrimaryScriptAssemblyTicks()}");
                sb.AppendLine($"REQ_RELOAD|{(requiresReloadOnExit ? 1 : 0)}");

                foreach (string dirtyPath in _dirtyScriptsSinceDomainReload)
                {
                    if (string.IsNullOrEmpty(dirtyPath))
                    {
                        continue;
                    }
                    long pinned = _sourceAssetDbTicks.TryGetValue(dirtyPath, out long pt) ? pt : 0L;
                    long real = _dirtyScriptRealTicks.TryGetValue(dirtyPath, out long rt) ? rt : DateTime.UtcNow.Ticks;
                    ulong hash = _scriptContentHashes.TryGetValue(dirtyPath, out ulong h) ? h : 0UL;
                    sb.AppendLine($"DIRTY|{dirtyPath}|{pinned}|{real}|{hash}");
                }

                List<DynamicFieldDescriptor> allFields = HotReloadStateStore.GetAllDynamicFields();
                for (int i = 0; i < allFields.Count; i++)
                {
                    DynamicFieldDescriptor f = allFields[i];
                    if (f == null || string.IsNullOrEmpty(f.OwnerTypeFullName) || string.IsNullOrEmpty(f.FieldName))
                    {
                        continue;
                    }
                    sb.AppendLine($"FIELD|{f.OwnerTypeFullName}|{f.FieldName}|{f.DeclaredTypeName}|{f.FieldKey}|{(f.IsStatic ? 1 : 0)}|{(f.IsPublicOrSerialized ? 1 : 0)}|{(f.RequiresUnityObjectSerialization ? 1 : 0)}");
                }

                File.WriteAllText(statePath, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch
            {
                // Non-fatal if cache write fails
            }
        }

        private static bool TryRestoreAndApplyCachedPatchOnDomainReload()
        {
            try
            {
                string cacheDir = GetPatchCacheDir();
                if (string.IsNullOrEmpty(cacheDir))
                {
                    return false;
                }

                string binPath = Path.Combine(cacheDir, "latest_patch.bin");
                string statePath = Path.Combine(cacheDir, "dirty_state.txt");
                if (!File.Exists(binPath) || !File.Exists(statePath))
                {
                    return false;
                }

                string[] lines = File.ReadAllLines(statePath, System.Text.Encoding.UTF8);
                long savedAsmTicks = -1L;
                long currentAsmTicks = GetPrimaryScriptAssemblyTicks();

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] parts = line.Split('|');
                    if (parts.Length >= 2 && parts[0] == "ASM_TICKS")
                    {
                        long.TryParse(parts[1], out savedAsmTicks);
                        break;
                    }
                }

                // If Unity recompiled Assembly-CSharp.dll since the cache was saved, discard the cache.
                if (savedAsmTicks <= 0L || savedAsmTicks != currentAsmTicks)
                {
                    ClearPatchCache();
                    return false;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] parts = line.Split('|');
                    if (parts[0] == "REQ_RELOAD" && parts.Length >= 2)
                    {
                        _requiresReloadOnExitPlayMode = parts[1] == "1";
                    }
                    else if (parts[0] == "DIRTY" && parts.Length >= 5)
                    {
                        string path = parts[1];
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            _dirtyScriptsSinceDomainReload.Add(path);
                            if (long.TryParse(parts[2], out long pinned) && pinned > 0L)
                            {
                                _sourceAssetDbTicks[path] = pinned;
                            }
                            if (long.TryParse(parts[3], out long real) && real > 0L)
                            {
                                _dirtyScriptRealTicks[path] = real;
                            }
                            if (ulong.TryParse(parts[4], out ulong hash) && hash > 0UL)
                            {
                                _scriptContentHashes[path] = hash;
                            }
                        }
                    }
                    else if (parts[0] == "FIELD" && parts.Length >= 8)
                    {
                        HotReloadStateStore.RegisterDynamicField(new DynamicFieldDescriptor
                        {
                            OwnerTypeFullName = parts[1],
                            FieldName = parts[2],
                            DeclaredTypeName = parts[3],
                            FieldKey = parts[4],
                            IsStatic = parts[5] == "1",
                            IsPublicOrSerialized = parts[6] == "1",
                            RequiresUnityObjectSerialization = parts[7] == "1"
                        });
                    }
                }

                byte[] peBytes = File.ReadAllBytes(binPath);
                if (peBytes == null || peBytes.Length == 0)
                {
                    return false;
                }

                Assembly patchAsm = Assembly.Load(peBytes);
                if (patchAsm == null)
                {
                    return false;
                }

                ApplyAssemblyDetours(
                    patchAsm,
                    "CachedPatch",
                    primaryDeclaredTypeNames: null,
                    companionTypeNames: null,
                    new List<string>(),
                    new List<CompilerDiagnosticEntry>(),
                    out _,
                    out _);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ClearPatchCache()
        {
            try
            {
                string cacheDir = GetPatchCacheDir();
                if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
                {
                    return;
                }

                string binPath = Path.Combine(cacheDir, "latest_patch.bin");
                string statePath = Path.Combine(cacheDir, "dirty_state.txt");
                if (File.Exists(binPath)) File.Delete(binPath);
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            catch
            {
                // Ignore
            }
        }

        private static bool IsCompanionOnlyType(
            Type patchType,
            List<string> primaryDeclaredTypeNames,
            HashSet<string> companionTypeNames)
        {
            if (patchType == null || companionTypeNames == null || companionTypeNames.Count == 0)
            {
                return false;
            }

            Type rootType = patchType;
            while (rootType.DeclaringType != null)
            {
                rootType = rootType.DeclaringType;
            }

            string fullName = rootType.FullName ?? rootType.Name;
            string shortName = rootType.Name;

            if (primaryDeclaredTypeNames != null)
            {
                for (int i = 0; i < primaryDeclaredTypeNames.Count; i++)
                {
                    string primary = primaryDeclaredTypeNames[i];
                    if (string.Equals(primary, fullName, StringComparison.Ordinal) ||
                        string.Equals(primary, shortName, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return companionTypeNames.Contains(fullName) || companionTypeNames.Contains(shortName);
        }

        private static int ApplyAssemblyDetours(
            Assembly patchAssembly,
            string scriptName,
            List<string> primaryDeclaredTypeNames,
            HashSet<string> companionTypeNames,
            List<string> patchedMethodNamesOut,
            List<CompilerDiagnosticEntry> diagnosticsOut,
            out bool requiresDomainReload,
            out string reloadReason)
        {
            requiresDomainReload = false;
            reloadReason = null;
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

                // Compiler-generated nested types (<Coroutine>d__N, <>c, <>c__DisplayClass) are matched and detoured
                // via their declaring parent type below so ordinal shifts (d__3 -> d__4) never cause false NewType reloads.
                if (patchType.IsNested && patchType.Name.StartsWith("<", StringComparison.Ordinal))
                {
                    continue;
                }

                if (companionTypeNames != null && companionTypeNames.Count > 0 &&
                    IsCompanionOnlyType(patchType, primaryDeclaredTypeNames, companionTypeNames))
                {
                    continue;
                }

                Type originalType = FindOriginalRuntimeType(patchType);
                if (originalType == null)
                {
                    // Pure constant/static containers (e.g. `public struct CatAnimations { public const string ... }` in Consts.cs)
                    // have no instance layout or Unity engine registration requirements and never need a Domain Reload.
                    if (IsPureConstOrStaticContainer(patchType))
                    {
                        continue;
                    }

                    requiresDomainReload = true;
                    reloadReason = $"New type '{patchType.Name}' added";
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
                        requiresDomainReload = true;
                        reloadReason = $"Enum '{patchType.Name}' modified";
                        LogDomainReloadAdvisoryOnce(
                            $"EnumChange:{patchType.FullName}",
                            $"Enum definition (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                            "To update enum values across the Inspector and project, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    }
                    continue;
                }

                if (patchType.IsInterface)
                {
                    requiresDomainReload = true;
                    reloadReason = $"Interface '{patchType.Name}' modified";
                    LogDomainReloadAdvisoryOnce(
                        $"InterfaceChange:{patchType.FullName}",
                        $"Interface (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                        "To apply structural interface changes across the project, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    continue;
                }

                if (patchType.IsGenericTypeDefinition)
                {
                    requiresDomainReload = true;
                    reloadReason = $"Generic type '{patchType.Name}' modified";
                    LogDomainReloadAdvisoryOnce(
                        $"GenericType:{patchType.FullName}",
                        $"Generic type (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                        "To apply generic type signature changes, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                }
                else if (patchType.IsValueType && !patchType.IsEnum)
                {
                    if (!AreInstanceFieldLayoutsCompatible(originalType, patchType))
                    {
                        requiresDomainReload = true;
                        reloadReason = $"Struct '{patchType.Name}' layout modified";
                        LogDomainReloadAdvisoryOnce(
                            $"StructType:{patchType.FullName}",
                            $"Struct (<b>{patchType.Name}</b>) instance layout was modified in <b>{scriptName}</b>. " +
                            "To realign value-type memory layouts, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
                    }
                }
                else if (HasInheritanceOrInterfaceChanges(originalType, patchType))
                {
                    requiresDomainReload = true;
                    reloadReason = $"Base class or interface list of '{patchType.Name}' changed";
                    LogDomainReloadAdvisoryOnce(
                        $"InheritanceChange:{patchType.FullName}",
                        $"Inheritance hierarchy or implemented interfaces of <b>{patchType.Name}</b> changed in <b>{scriptName}</b>. " +
                        "To apply structural inheritance/interface changes (e.g. TryGetComponent<Interface>), click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
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

                    if (patchMethod.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                    {
                        if (IsRewriterInjectedHelper(patchMethod))
                        {
                            continue;
                        }

                        // If this is a compiler-generated lambda method on the class (e.g. <Awake>b__5_0 or <OnResumeButtonClicked>b__11_0)
                        // or an existing auto-property accessor, detour it if a matching method exists on originalType!
                        MethodInfo matchingCgMethod = FindMatchingCompilerGeneratedMethod(originalType, patchMethod, flags);
                        if (matchingCgMethod != null && NativeMethodDetour.TryDetour(matchingCgMethod, patchMethod, out _))
                        {
                            patchedCount++;
                            patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name}");
                        }
                        continue;
                    }

                    MethodInfo originalMethod = FindMatchingOriginalMethod(originalType, patchMethod, flags);
                    if (originalMethod == null)
                    {
                        bool isMonoBehaviour = typeof(MonoBehaviour).IsAssignableFrom(originalType);

                        if (isMonoBehaviour && !patchMethod.IsStatic)
                        {
                            if (patchMethod.GetParameters().Length == 0)
                            {
                                if (patchMethod.Name == "Update" || patchMethod.Name == "LateUpdate" || patchMethod.Name == "FixedUpdate")
                                {
                                    if (TryRegisterDynamicLifecycleHook(originalType, patchMethod, isOneShot: false))
                                    {
                                        patchedCount++;
                                        patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name} (Dynamic Bridge)");
                                    }
                                    continue;
                                }

                                if (patchMethod.Name == "Awake" || patchMethod.Name == "OnEnable" || patchMethod.Name == "Start")
                                {
                                    if (TryRegisterDynamicLifecycleHook(originalType, patchMethod, isOneShot: true))
                                    {
                                        patchedCount++;
                                        patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name} (Dynamic Lifecycle)");
                                    }
                                    continue;
                                }
                            }

                            if (_unityNativeMessagesRequiringReload.Contains(patchMethod.Name))
                            {
                                requiresDomainReload = true;
                                reloadReason = $"New Unity callback '{originalType.Name}.{patchMethod.Name}()' added";
                                LogDomainReloadAdvisoryOnce(
                                    $"NewUnityMsg:{originalType.FullName}.{patchMethod.Name}",
                                    $"New Unity lifecycle/physics callback '<b>{originalType.Name}.{patchMethod.Name}()</b>' was added to <b>{scriptName}</b>. " +
                                    "Because this method did not exist when Unity last built its native C++ callback table, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> (or press <b>Ctrl+Alt+R</b>) once.");
                                continue;
                            }
                        }

                        // Support newly added IEnumerator Coroutine methods (both StartCoroutine(MyRoutine()) and StartCoroutine("MyRoutine"))
                        if (typeof(System.Collections.IEnumerator).IsAssignableFrom(patchMethod.ReturnType))
                        {
                            if (isMonoBehaviour && !patchMethod.IsStatic)
                            {
                                TryRegisterDynamicAddedCoroutine(originalType, patchMethod);
                            }

                            if (patchMethod.IsPrivate || EditorApplication.isPlaying)
                            {
                                patchedCount++;
                                patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name} (New Coroutine)");
                                continue;
                            }
                        }

                        // Any newly added non-virtual method (private, internal, protected, or public helper method)
                        // that does not require a new vtable/interface slot is called directly by patched methods in the batch
                        // and NEVER requires a Domain Reload!
                        if (!patchMethod.IsVirtual && !patchMethod.IsAbstract)
                        {
                            if (isMonoBehaviour && !patchMethod.IsStatic &&
                                patchMethod.GetParameters().Length == 0 && patchMethod.ReturnType == typeof(void))
                            {
                                TryRegisterDynamicAddedMethod(originalType, patchMethod);
                            }

                            patchedCount++;
                            patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name} (New Method)");
                            continue;
                        }

                        // Any new virtual/interface method requires a Domain Reload in Edit Mode (or advisory in Play Mode)
                        // so the runtime vtable/interface dispatch map is updated.
                        requiresDomainReload = true;
                        if (string.IsNullOrEmpty(reloadReason))
                        {
                            reloadReason = $"New method '{originalType.Name}.{patchMethod.Name}()' added";
                        }
                        LogDomainReloadAdvisoryOnce(
                            $"NewMethod:{originalType.FullName}.{patchMethod.Name}",
                            $"New method '<b>{originalType.Name}.{patchMethod.Name}()</b>' was added to <b>{scriptName}</b>. " +
                            "To register new public/interface methods in the runtime type table, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> (or press <b>Ctrl+Alt+R</b>) once.");
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

                    // If this method is an IEnumerator Coroutine or Async state-machine method, also detour its MoveNext()
                    // on the compiler-generated state machine class (<MethodName>d__N) when field layouts are compatible,
                    // so even already-running Coroutines (e.g. while(true) loops) update live on their next yield!
                    if (TryDetourStateMachineMoveNext(originalType, patchType, originalMethod, patchMethod, flags))
                    {
                        patchedCount++;
                        patchedMethodNamesOut.Add($"{originalType.Name}.{originalMethod.Name}.MoveNext (Coroutine)");
                    }
                }

                // Also detour nested closure/lambda classes (<>c and <>c__DisplayClass) for this type
                patchedCount += ApplyNestedClosureDetours(originalType, patchType, flags, patchedMethodNamesOut);
            }

            return patchedCount;
        }

        private static bool IsRewriterInjectedHelper(MethodInfo method)
        {
            string name = method.Name;
            if (name == "Invoke" || name == "InvokeRepeating" || name == "CancelInvoke" || name == "IsInvoking" ||
                name == "StartCoroutine" || name == "StopCoroutine")
            {
                return true;
            }

            if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal))
            {
                string propName = name.Substring(4);
                string declaringFullName = method.DeclaringType != null ? method.DeclaringType.FullName : null;
                if (!string.IsNullOrEmpty(declaringFullName))
                {
                    IReadOnlyList<DynamicFieldDescriptor> dynFields = HotReloadStateStore.GetDynamicFieldsForType(declaringFullName);
                    for (int i = 0; i < dynFields.Count; i++)
                    {
                        if (string.Equals(dynFields[i].FieldName, propName, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static MethodInfo FindMatchingCompilerGeneratedMethod(Type originalType, MethodInfo patchMethod, BindingFlags flags)
        {
            // Direct exact name + signature match first
            MethodInfo exact = FindMatchingOriginalMethod(originalType, patchMethod, flags);
            if (exact != null)
            {
                return exact;
            }

            // If it's a lambda method <ParentMethod>b__X_Y and ordinal X shifted, match by <ParentMethod>b__ prefix if unambiguous
            string name = patchMethod.Name;
            int closeBracket = name.IndexOf(">b__", StringComparison.Ordinal);
            if (name.StartsWith("<", StringComparison.Ordinal) && closeBracket > 1)
            {
                string prefix = name.Substring(0, closeBracket + 4); // "<ParentMethod>b__"
                MethodInfo[] candidates = originalType.GetMethods(flags);
                MethodInfo singleMatch = null;
                int matchCount = 0;

                foreach (MethodInfo cand in candidates)
                {
                    if (cand.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                        cand.IsStatic == patchMethod.IsStatic &&
                        AreMethodSignaturesEqual(cand, patchMethod))
                    {
                        singleMatch = cand;
                        matchCount++;
                    }
                }

                if (matchCount == 1)
                {
                    return singleMatch;
                }
            }

            return null;
        }

        private static bool TryDetourStateMachineMoveNext(
            Type originalType,
            Type patchType,
            MethodInfo originalMethod,
            MethodInfo patchMethod,
            BindingFlags flags)
        {
            try
            {
                Type origSmType = FindStateMachineNestedType(originalType, originalMethod);
                Type patchSmType = FindStateMachineNestedType(patchType, patchMethod);

                if (origSmType == null || patchSmType == null)
                {
                    return false;
                }

                if (origSmType.IsGenericTypeDefinition || patchSmType.IsGenericTypeDefinition)
                {
                    return false;
                }

                // Verify that the state machine's instance fields (e.g. <>1__state, <>2__current, <>4__this, and hoisted locals)
                // have a compatible memory layout before detouring MoveNext() for already-running instances.
                if (!AreInstanceFieldLayoutsCompatible(origSmType, patchSmType))
                {
                    return false;
                }

                MethodInfo origMoveNext = origSmType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                MethodInfo patchMoveNext = patchSmType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (origMoveNext == null || patchMoveNext == null)
                {
                    return false;
                }

                return NativeMethodDetour.TryDetour(origMoveNext, patchMoveNext, out _);
            }
            catch
            {
                return false;
            }
        }

        private static Type FindStateMachineNestedType(Type ownerType, MethodInfo method)
        {
            if (ownerType == null || method == null)
            {
                return null;
            }

            // 1. Check [IteratorStateMachine] and [AsyncStateMachine] attributes first
            var iterAttr = method.GetCustomAttribute<System.Runtime.CompilerServices.IteratorStateMachineAttribute>();
            if (iterAttr != null && iterAttr.StateMachineType != null)
            {
                return iterAttr.StateMachineType;
            }

            var asyncAttr = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
            if (asyncAttr != null && asyncAttr.StateMachineType != null)
            {
                return asyncAttr.StateMachineType;
            }

            // 2. Fallback: look for nested type named "<MethodName>d__*"
            string prefix = $"<{method.Name}>d__";
            Type[] nestedTypes = ownerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            Type match = null;
            int count = 0;
            for (int i = 0; i < nestedTypes.Length; i++)
            {
                if (nestedTypes[i].Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    match = nestedTypes[i];
                    count++;
                }
            }

            return count == 1 ? match : null;
        }

        private static int ApplyNestedClosureDetours(
            Type originalType,
            Type patchType,
            BindingFlags flags,
            List<string> patchedMethodNamesOut)
        {
            int count = 0;
            try
            {
                Type[] origNested = originalType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                Type[] patchNested = patchType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);

                if (origNested == null || patchNested == null || origNested.Length == 0 || patchNested.Length == 0)
                {
                    return 0;
                }

                foreach (Type pNested in patchNested)
                {
                    if (pNested == null || pNested.IsGenericTypeDefinition || !pNested.Name.StartsWith("<>c", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Type oNested = null;
                    if (pNested.Name == "<>c")
                    {
                        for (int i = 0; i < origNested.Length; i++)
                        {
                            if (origNested[i] != null && origNested[i].Name == "<>c")
                            {
                                oNested = origNested[i];
                                break;
                            }
                        }
                    }
                    else if (pNested.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
                    {
                        for (int i = 0; i < origNested.Length; i++)
                        {
                            if (origNested[i] != null &&
                                origNested[i].Name == pNested.Name &&
                                AreInstanceFieldLayoutsCompatible(origNested[i], pNested))
                            {
                                oNested = origNested[i];
                                break;
                            }
                        }
                    }

                    if (oNested == null)
                    {
                        continue;
                    }

                    MethodInfo[] pMethods = pNested.GetMethods(flags);
                    foreach (MethodInfo pm in pMethods)
                    {
                        if (pm.IsAbstract || pm.ContainsGenericParameters || pm.IsSpecialName)
                        {
                            continue;
                        }

                        MethodInfo om = FindMatchingCompilerGeneratedMethod(oNested, pm, flags);
                        if (om != null && NativeMethodDetour.TryDetour(om, pm, out _))
                        {
                            count++;
                            patchedMethodNamesOut.Add($"{originalType.Name}.{pm.Name}");
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal if closure matching fails
            }
            return count;
        }

        private static bool IsPureConstOrStaticContainer(Type patchType)
        {
            if (patchType == null || patchType.IsEnum || patchType.IsInterface || typeof(UnityEngine.Object).IsAssignableFrom(patchType))
            {
                return false;
            }

            const BindingFlags instFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (patchType.GetFields(instFlags).Length > 0)
            {
                return false;
            }

            MethodInfo[] instMethods = patchType.GetMethods(instFlags);
            for (int i = 0; i < instMethods.Length; i++)
            {
                if (!instMethods[i].IsSpecialName)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreInstanceFieldLayoutsCompatible(Type typeA, Type typeB)
        {
            const BindingFlags instFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            FieldInfo[] fieldsA = typeA.GetFields(instFlags);
            FieldInfo[] fieldsB = typeB.GetFields(instFlags);

            if (fieldsA.Length != fieldsB.Length)
            {
                return false;
            }

            for (int i = 0; i < fieldsA.Length; i++)
            {
                Type ftA = fieldsA[i].FieldType;
                Type ftB = fieldsB[i].FieldType;
                if (ftA != ftB && !string.Equals(ftA.FullName, ftB.FullName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreMethodSignaturesEqual(MethodInfo a, MethodInfo b)
        {
            if (a.ReturnType != b.ReturnType &&
                !string.Equals(a.ReturnType.FullName, b.ReturnType.FullName, StringComparison.Ordinal))
            {
                return false;
            }

            ParameterInfo[] pa = a.GetParameters();
            ParameterInfo[] pb = b.GetParameters();
            if (pa.Length != pb.Length)
            {
                return false;
            }

            for (int i = 0; i < pa.Length; i++)
            {
                Type tA = pa[i].ParameterType;
                Type tB = pb[i].ParameterType;
                if (tA != tB && !string.Equals(tA.FullName, tB.FullName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
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

        private static unsafe bool TryRegisterDynamicLifecycleHook(Type originalType, MethodInfo patchMethod, bool isOneShot = false)
        {
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(patchMethod.MethodHandle);
                IntPtr fnPtr = patchMethod.MethodHandle.GetFunctionPointer();
                if (fnPtr == IntPtr.Zero)
                {
                    return false;
                }

                string key = $"{originalType.FullName}.{patchMethod.Name}";
                var hook = new DynamicLifecycleHook
                {
                    OriginalComponentType = originalType,
                    MethodName = patchMethod.Name,
                    FunctionPointer = fnPtr,
                    IsOneShot = isOneShot,
                    ExecutedInstanceIds = isOneShot ? new HashSet<int>() : null
                };
                _dynamicLifecycleHooks[key] = hook;

                Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
                Application.onBeforeRender += OnBeforeRenderDynamicUpdates;

                // If we are already actively in Play Mode when this one-shot lifecycle method (Awake / OnEnable / Start)
                // is added or edited, execute it immediately on all active scene instances!
                if (isOneShot && EditorApplication.isPlaying)
                {
                    ExecuteOneShotHookOnActiveInstances(hook);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static unsafe void ExecuteOneShotHookOnActiveInstances(DynamicLifecycleHook hook)
        {
            if (hook == null || !hook.IsOneShot || hook.OriginalComponentType == null || hook.FunctionPointer == IntPtr.Zero)
            {
                return;
            }

            if (hook.ExecutedInstanceIds == null)
            {
                hook.ExecutedInstanceIds = new HashSet<int>();
            }

#pragma warning disable CS0618
            UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsOfType(hook.OriginalComponentType);
#pragma warning restore CS0618
            if (instances == null || instances.Length == 0)
            {
                return;
            }

            var fn = (delegate*<object, void>)hook.FunctionPointer;
            for (int i = 0; i < instances.Length; i++)
            {
                if (instances[i] is MonoBehaviour mb && mb != null && mb.isActiveAndEnabled)
                {
                    int instKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mb);
                    if (hook.ExecutedInstanceIds.Add(instKey))
                    {
                        try
                        {
                            fn(mb);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, mb);
                        }
                    }
                }
            }
        }

        private static void ExecutePendingOneShotLifecycleHooks()
        {
            if (!EditorApplication.isPlaying || _dynamicLifecycleHooks.Count == 0)
            {
                return;
            }

            // Execute in Unity's deterministic initialization order: Awake -> OnEnable -> Start
            string[] order = { "Awake", "OnEnable", "Start" };
            for (int o = 0; o < order.Length; o++)
            {
                string targetName = order[o];
                foreach (var kvp in _dynamicLifecycleHooks)
                {
                    DynamicLifecycleHook hook = kvp.Value;
                    if (hook != null && hook.IsOneShot && string.Equals(hook.MethodName, targetName, StringComparison.Ordinal))
                    {
                        ExecuteOneShotHookOnActiveInstances(hook);
                    }
                }
            }
        }

        private static unsafe bool TryRegisterDynamicAddedMethod(Type originalType, MethodInfo patchMethod)
        {
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(patchMethod.MethodHandle);
                IntPtr fnPtr = patchMethod.MethodHandle.GetFunctionPointer();
                if (fnPtr == IntPtr.Zero)
                {
                    return false;
                }

                string key = $"{originalType.FullName}.{patchMethod.Name}";
                _dynamicAddedMethods[key] = new DynamicLifecycleHook
                {
                    OriginalComponentType = originalType,
                    MethodName = patchMethod.Name,
                    FunctionPointer = fnPtr
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRegisterDynamicAddedCoroutine(Type originalType, MethodInfo patchMethod)
        {
            if (originalType == null || patchMethod == null)
            {
                return false;
            }

            string key = $"{originalType.FullName}.{patchMethod.Name}";
            _dynamicAddedCoroutines[key] = new DynamicAddedCoroutine
            {
                OriginalComponentType = originalType,
                MethodName = patchMethod.Name,
                PatchMethod = patchMethod
            };
            return true;
        }

        private static Coroutine StartDynamicCoroutine(MonoBehaviour instance, string methodName, object value)
        {
            if (instance == null || string.IsNullOrEmpty(methodName))
            {
                return null;
            }

            string key = $"{instance.GetType().FullName}.{methodName}";
            if (!_dynamicAddedCoroutines.TryGetValue(key, out DynamicAddedCoroutine item) || item == null || item.PatchMethod == null)
            {
                return null;
            }

            try
            {
                ParameterInfo[] ps = item.PatchMethod.GetParameters();
                object[] args = ps.Length == 0 ? null : new object[] { value };
                if (item.PatchMethod.Invoke(instance, args) is System.Collections.IEnumerator routine)
                {
                    Coroutine handle = instance.StartCoroutine(routine);
                    if (handle != null)
                    {
                        _dynamicRunningCoroutines.Add(new DynamicRunningCoroutine
                        {
                            Instance = instance,
                            MethodName = methodName,
                            Handle = handle
                        });
                    }
                    return handle;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, instance);
            }

            return null;
        }

        private static void StopDynamicCoroutine(MonoBehaviour instance, string methodName)
        {
            if (instance == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            for (int i = _dynamicRunningCoroutines.Count - 1; i >= 0; i--)
            {
                DynamicRunningCoroutine running = _dynamicRunningCoroutines[i];
                if (running == null || running.Instance == null)
                {
                    _dynamicRunningCoroutines.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(running.Instance, instance) &&
                    string.Equals(running.MethodName, methodName, StringComparison.Ordinal))
                {
                    if (running.Handle != null)
                    {
                        instance.StopCoroutine(running.Handle);
                    }
                    _dynamicRunningCoroutines.RemoveAt(i);
                }
            }
        }

        private static void ScheduleDynamicInvoke(MonoBehaviour instance, string methodName, float time, float repeatRate, bool isRepeating)
        {
            if (instance == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            string key = $"{instance.GetType().FullName}.{methodName}";
            double now = EditorApplication.isPlaying ? Time.timeAsDouble : EditorApplication.timeSinceStartup;

            for (int i = 0; i < _dynamicScheduledInvokes.Count; i++)
            {
                DynamicScheduledInvoke existing = _dynamicScheduledInvokes[i];
                if (ReferenceEquals(existing.Instance, instance) &&
                    string.Equals(existing.MethodName, methodName, StringComparison.Ordinal))
                {
                    existing.NextExecutionTime = now + Math.Max(0f, time);
                    existing.RepeatRate = repeatRate;
                    existing.IsRepeating = isRepeating;
                    return;
                }
            }

            _dynamicScheduledInvokes.Add(new DynamicScheduledInvoke
            {
                Instance = instance,
                Key = key,
                MethodName = methodName,
                NextExecutionTime = now + Math.Max(0f, time),
                RepeatRate = repeatRate,
                IsRepeating = isRepeating
            });

            Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
            Application.onBeforeRender += OnBeforeRenderDynamicUpdates;
        }

        private static void CancelDynamicInvoke(MonoBehaviour instance, string methodName)
        {
            if (instance == null)
            {
                return;
            }

            for (int i = _dynamicScheduledInvokes.Count - 1; i >= 0; i--)
            {
                DynamicScheduledInvoke item = _dynamicScheduledInvokes[i];
                if (item.Instance == null ||
                    (ReferenceEquals(item.Instance, instance) &&
                     (string.IsNullOrEmpty(methodName) || string.Equals(item.MethodName, methodName, StringComparison.Ordinal))))
                {
                    _dynamicScheduledInvokes.RemoveAt(i);
                }
            }
        }

        private static bool CheckDynamicIsInvoking(MonoBehaviour instance, string methodName)
        {
            if (instance == null)
            {
                return false;
            }

            for (int i = 0; i < _dynamicScheduledInvokes.Count; i++)
            {
                DynamicScheduledInvoke item = _dynamicScheduledInvokes[i];
                if (ReferenceEquals(item.Instance, instance) &&
                    (string.IsNullOrEmpty(methodName) || string.Equals(item.MethodName, methodName, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            return false;
        }

        private static unsafe void OnUnityLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            // Intercept Unity's native C++ Invoke/InvokeRepeating failure message when a newly added method
            // was invoked before a Domain Reload: "Trying to Invoke method: TypeName.MethodName couldn't be called."
            const string prefix = "Trying to Invoke method: ";
            const string suffix = " couldn't be called.";

            if (string.IsNullOrEmpty(condition) ||
                !condition.StartsWith(prefix, StringComparison.Ordinal) ||
                !condition.EndsWith(suffix, StringComparison.Ordinal))
            {
                return;
            }

            string targetKey = condition.Substring(prefix.Length, condition.Length - prefix.Length - suffix.Length).Trim();
            if (!_dynamicAddedMethods.TryGetValue(targetKey, out DynamicLifecycleHook hook) ||
                hook == null || hook.OriginalComponentType == null || hook.FunctionPointer == IntPtr.Zero)
            {
                return;
            }

#pragma warning disable CS0618
            UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsOfType(hook.OriginalComponentType);
#pragma warning restore CS0618
            if (instances == null || instances.Length == 0)
            {
                return;
            }

            var fn = (delegate*<object, void>)hook.FunctionPointer;
            for (int i = 0; i < instances.Length; i++)
            {
                if (instances[i] is MonoBehaviour mb && mb != null && mb.isActiveAndEnabled)
                {
                    try
                    {
                        fn(mb);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, mb);
                    }
                }
            }
        }

        private static unsafe void OnBeforeRenderDynamicUpdates()
        {
            if (!EditorApplication.isPlaying ||
                (_dynamicLifecycleHooks.Count == 0 && _dynamicScheduledInvokes.Count == 0))
            {
                return;
            }

            // Ensure any newly instantiated objects run dynamic Awake / OnEnable / Start once before Update!
            ExecutePendingOneShotLifecycleHooks();

            foreach (var kvp in _dynamicLifecycleHooks)
            {
                DynamicLifecycleHook hook = kvp.Value;
                if (hook == null || hook.IsOneShot || hook.OriginalComponentType == null || hook.FunctionPointer == IntPtr.Zero)
                {
                    continue;
                }

#pragma warning disable CS0618
                UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsOfType(hook.OriginalComponentType);
#pragma warning restore CS0618
                if (instances == null || instances.Length == 0)
                {
                    continue;
                }

                var fn = (delegate*<object, void>)hook.FunctionPointer;
                for (int i = 0; i < instances.Length; i++)
                {
                    if (instances[i] is MonoBehaviour mb && mb != null && mb.isActiveAndEnabled)
                    {
                        try
                        {
                            fn(mb);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, mb);
                        }
                    }
                }
            }

            if (_dynamicScheduledInvokes.Count > 0)
            {
                double now = Time.timeAsDouble;
                for (int i = _dynamicScheduledInvokes.Count - 1; i >= 0; i--)
                {
                    DynamicScheduledInvoke item = _dynamicScheduledInvokes[i];
                    if (item == null || item.Instance == null)
                    {
                        _dynamicScheduledInvokes.RemoveAt(i);
                        continue;
                    }

                    if (now < item.NextExecutionTime)
                    {
                        continue;
                    }

                    if (_dynamicAddedMethods.TryGetValue(item.Key, out DynamicLifecycleHook hook) &&
                        hook != null && hook.FunctionPointer != IntPtr.Zero &&
                        item.Instance.isActiveAndEnabled)
                    {
                        var fn = (delegate*<object, void>)hook.FunctionPointer;
                        try
                        {
                            fn(item.Instance);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, item.Instance);
                        }
                    }

                    if (item.IsRepeating && item.RepeatRate > 0f)
                    {
                        item.NextExecutionTime = now + item.RepeatRate;
                    }
                    else
                    {
                        _dynamicScheduledInvokes.RemoveAt(i);
                    }
                }
            }
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
            // In Edit Mode, structural changes automatically trigger ForceFullUnityReload(),
            // so manual reload advisories are only needed during Play Mode.
            if (!EditorApplication.isPlaying || string.IsNullOrEmpty(advisoryKey))
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
            bool tr = IsTurkish;

            if (evt.Success)
            {
                if (evt.RequiresDomainReload)
                {
                    Debug.LogWarning(tr
                        ? $"<color=#FBBF24><b>[⚠️ Hot Reload Pro • {modeTag}]</b></color> <b>{evt.ScriptName}</b> kısmen güncellendi ({evt.TotalTimeMs:F0}ms): " +
                          $"<b>{evt.ReloadReason}</b> için Play Mode'dan çıkmanız veya Tam Yenileme (<b>Ctrl+Alt+R</b>) yapmanız gerekir."
                        : $"<color=#FBBF24><b>[⚠️ Hot Reload Pro • {modeTag}]</b></color> Partial patch on <b>{evt.ScriptName}</b> ({evt.TotalTimeMs:F0}ms): " +
                          $"<b>{evt.ReloadReason}</b> requires a Full Domain Reload (<b>Ctrl+Alt+R</b>) or exiting Play Mode to take effect.");
                }
                else
                {
                    Debug.Log(tr
                        ? $"<color=#4ADE80><b>[⚡ Hot Reload Pro • {modeTag}]</b></color> <b>{evt.ScriptName}</b> <b>{evt.TotalTimeMs:F0}ms</b> içinde güncellendi " +
                          $"({evt.PatchedMethods.Count} metot, {evt.AddedFields.Count} yeni değişken)."
                        : $"<color=#4ADE80><b>[⚡ Hot Reload Pro • {modeTag}]</b></color> Patched <b>{evt.ScriptName}</b> in <b>{evt.TotalTimeMs:F0}ms</b> " +
                          $"({evt.PatchedMethods.Count} methods, {evt.AddedFields.Count} new fields) via <i>{evt.CompilerEngine}</i>.");
                }
            }
            else
            {
                var errSb = new System.Text.StringBuilder();
                bool allInfra = evt.Diagnostics.Count > 0 &&
                                evt.Diagnostics.TrueForAll(d => !d.IsError || HotReloadCompilerService.IsInfrastructureDiagnostic(d.Id));
                if (tr)
                {
                    errSb.AppendLine(allInfra
                        ? $"<color=#F87171><b>[⚙️ Hot Reload Derleyici Hatası • {evt.ScriptName}]</b></color> <i>(Senin kodunla ilgili değil — eklenti altyapı hatası. Unity'nin normal derlemesi etkilenmez.)</i>:"
                        : $"<color=#F87171><b>[📝 Kod Hatası • {evt.ScriptName}]</b></color> <i>(Eklenti hatası değildir — yazdığın C# kodunda hata var)</i>:");
                }
                else
                {
                    errSb.AppendLine(allInfra
                        ? $"<color=#F87171><b>[⚙️ Hot Reload Compiler Error • {evt.ScriptName}]</b></color> <i>(Plugin infrastructure error, not caused by your C# script. Normal Unity compilation is unaffected.)</i>:"
                        : $"<color=#F87171><b>[📝 C# Script Error • {evt.ScriptName}]</b></color> <i>(Code error in your C# script, not a plugin error)</i>:");
                }

                foreach (CompilerDiagnosticEntry d in evt.Diagnostics)
                {
                    if (d.IsError)
                    {
                        errSb.AppendLine($"  • {FormatDiagnosticForUser(d, tr)}");
                    }
                }
                if (EditorApplication.isPlaying)
                {
                    Debug.LogWarning(errSb.ToString());
                }
                else
                {
                    Debug.LogError(errSb.ToString());
                }
            }

            OnPatchApplied?.Invoke(evt);
            OnStatusChanged?.Invoke();
        }

        public static string FormatDiagnosticForUser(CompilerDiagnosticEntry d, bool turkish)
        {
            if (d == null)
            {
                return string.Empty;
            }

            string filePrefix = !string.IsNullOrEmpty(d.FilePath)
                ? $"<b>[{Path.GetFileName(d.FilePath)}]</b> "
                : string.Empty;

            if (!turkish)
            {
                return filePrefix + d.ToString();
            }

            string loc = d.Line > 0 ? $"(Satır {d.Line}, Sütun {d.Column}) " : string.Empty;
            string translated = TranslateCompilerErrorToTurkish(d.Id, d.Message);
            return $"{filePrefix}{loc}<b>{d.Id}</b>: {translated}";
        }

        private static string TranslateCompilerErrorToTurkish(string id, string msg)
        {
            if (string.IsNullOrEmpty(msg))
            {
                return string.Empty;
            }

            switch (id)
            {
                case "CS0103":
                {
                    string sym = ExtractFirstSingleQuoted(msg);
                    return !string.IsNullOrEmpty(sym)
                        ? $"'{sym}' adında bir değişken veya metot tanımlı değil (Yukarıda tanımlamayı unutmuş olabilirsin veya yazım hatası var)."
                        : $"Tanımlanmamış değişken veya metot kullanımı ({msg})";
                }
                case "CS0246":
                {
                    string sym = ExtractFirstSingleQuoted(msg);
                    return !string.IsNullOrEmpty(sym)
                        ? $"'{sym}' tipi/sınıfı bulunamadı (Dosyanın en üstüne ilgili 'using ...;' kütüphanesini eklemeyi unutmuş olabilirsin veya sınıf adı yanlış)."
                        : $"Tip veya namespace bulunamadı ({msg})";
                }
                case "CS1002":
                    return "Satır sonunda noktalı virgül (;) eksik.";
                case "CS1003":
                {
                    string sym = ExtractFirstSingleQuoted(msg);
                    return sym == ":"
                        ? "Sözdizimi hatası: ':' (iki nokta üst üste) bekleniyor (örneğin 'switch/case' satırında ';' yerine ':' olmalı)."
                        : $"Sözdizimi hatası: '{sym ?? msg}' karakteri bekleniyor ({msg})";
                }
                case "CS1513":
                    return "Süslü parantez kapatma (}) eksik.";
                case "CS1026":
                    return "Normal parantez kapatma ()) eksik.";
                case "CS1061":
                case "CS0117":
                {
                    string sym = ExtractSecondSingleQuoted(msg) ?? ExtractFirstSingleQuoted(msg);
                    return !string.IsNullOrEmpty(sym)
                        ? $"Bu sınıfın içinde '{sym}' adında bir metot veya değişken yok (Yazım hatası olabilir)."
                        : $"Sınıf içinde böyle bir üye/metot bulunamadı ({msg})";
                }
                case "CS0029":
                    return $"Tip uyuşmazlığı — bir veri tipini diğerine doğrudan atayamazsın ({msg})";
                case "CS0161":
                    return $"Metot değer döndürmeli ama tüm durumlarda 'return' yazılmamış ({msg})";
                case "CS0120":
                {
                    string sym = ExtractFirstSingleQuoted(msg);
                    return !string.IsNullOrEmpty(sym)
                        ? $"'{sym}' statik olmadığı için doğrudan sınıf adıyla çağrılamaz, bir obje referansı gerekli."
                        : $"Statik olmayan üye için obje referansı gerekli ({msg})";
                }
                case "CS7036":
                case "CS1501":
                    return $"Metot çağrısında eksik veya fazla parametre var ({msg})";
                case "CS1503":
                    return $"Metoda gönderilen parametrenin tipi uyuşmuyor ({msg})";
                default:
                    return msg;
            }
        }

        private static string ExtractFirstSingleQuoted(string text)
        {
            int first = text.IndexOf('\'');
            if (first < 0 || first + 1 >= text.Length) return null;
            int second = text.IndexOf('\'', first + 1);
            if (second < 0) return null;
            return text.Substring(first + 1, second - first - 1);
        }

        private static string ExtractSecondSingleQuoted(string text)
        {
            int first = text.IndexOf('\'');
            if (first < 0 || first + 1 >= text.Length) return null;
            int second = text.IndexOf('\'', first + 1);
            if (second < 0 || second + 1 >= text.Length) return null;
            int third = text.IndexOf('\'', second + 1);
            if (third < 0 || third + 1 >= text.Length) return null;
            int fourth = text.IndexOf('\'', third + 1);
            if (fourth < 0) return null;
            return text.Substring(third + 1, fourth - third - 1);
        }
    }
}

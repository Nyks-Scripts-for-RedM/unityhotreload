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
        private const string PREF_SHOW_TOAST = "Nyks.HotReload.ShowToast";
        private const string PREF_TRIGGER_MODE = "Nyks.HotReload.TriggerMode";
        private const string PREF_LANGUAGE = "Nyks.HotReload.Language";
        private const string PREF_SAVED_UNITY_AUTOREFRESH = "Nyks.HotReload.SavedUnityAutoRefresh";

        private const string UNITY_PREF_AUTOREFRESH = "kAutoRefresh";
        private const string UNITY_PREF_AUTOREFRESH_MODE = "kAutoRefreshMode";

        private const double DEBOUNCE_SECONDS = 0.08;
        private const double POLL_INTERVAL_SECONDS = 0.30;

        private static readonly Dictionary<string, long> _scriptLastWriteTicks =
            new Dictionary<string, long>(256, StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, double> _pendingFiles =
            new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<HotReloadPatchEvent> _history = new List<HotReloadPatchEvent>(64);
        private static readonly HashSet<string> _dirtyScriptsSinceDomainReload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _loggedReloadAdvisories = new HashSet<string>(StringComparer.Ordinal);

        private sealed class DynamicLifecycleHook
        {
            public Type OriginalComponentType;
            public string MethodName;
            public IntPtr FunctionPointer;
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

        private static readonly Dictionary<string, DynamicLifecycleHook> _dynamicLifecycleHooks =
            new Dictionary<string, DynamicLifecycleHook>(StringComparer.Ordinal);

        private static readonly Dictionary<string, DynamicLifecycleHook> _dynamicAddedMethods =
            new Dictionary<string, DynamicLifecycleHook>(StringComparer.Ordinal);

        private static readonly List<DynamicScheduledInvoke> _dynamicScheduledInvokes =
            new List<DynamicScheduledInvoke>(16);

        private static readonly HashSet<string> _unityNativeMessagesRequiringReload = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy",
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
        /// When false (default), Edit Mode uses Unity's standard compilation so new classes, interfaces, and serialized fields
        /// work out-of-the-box, while Play Mode automatically locks Domain Reload and hot-patches live changes.
        /// </summary>
        public static bool AllowEditModePatching
        {
            get => EditorPrefs.GetBool(PREF_ALLOW_EDITMODE, false);
            set
            {
                if (EditorPrefs.GetBool(PREF_ALLOW_EDITMODE, false) != value)
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

            HotReloadStateStore.OnScheduleDynamicInvoke = ScheduleDynamicInvoke;
            HotReloadStateStore.OnCancelDynamicInvoke = CancelDynamicInvoke;
            HotReloadStateStore.OnCheckDynamicIsInvoking = CheckDynamicIsInvoking;

            // Immediately initialize file baseline and lock kAutoRefresh preferences during InitializeOnLoad
            // so scripts edited in an external IDE while Unity is unfocused in the background are never missed
            // when delayCall finally fires upon window focus.
            StartFileWatcher();
            UpdateAutoRefreshLockState();

            EditorApplication.delayCall += () =>
            {
                if (IsBackgroundWorkerOrBatchProcess())
                {
                    return;
                }
                if (!_initialBaselineComplete)
                {
                    StartFileWatcher();
                }
                UpdateAutoRefreshLockState();
                EnsureIdeProjectFilesSynced(force: false);
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

            totalSw.Stop();
            evt.Success = true;
            evt.TotalTimeMs = totalSw.Elapsed.TotalMilliseconds;

            // If we are in Edit Mode (not playing) and the user made a structural change
            // (e.g. added `: ICollectible`, added a new class, added a new public/interface method, or added OnTriggerEnter),
            // automatically trigger a clean Unity compilation so the user never has to manually click Full Reload!
            if (!EditorApplication.isPlaying && evt.RequiresDomainReload)
            {
                Debug.Log(IsTurkish
                    ? $"<color=#38BDF8><b>[⚡ Hot Reload Pro • Edit Mode]</b></color> <b>{evt.ScriptName}</b> içinde yapısal değişiklik algılandı ({evt.ReloadReason}). Unity derleyicisiyle otomatik eşitleniyor..."
                    : $"<color=#38BDF8><b>[⚡ Hot Reload Pro • Edit Mode]</b></color> Structural change detected in <b>{evt.ScriptName}</b> ({evt.ReloadReason}). Automatically syncing with Unity compiler...");
                ForceFullUnityReload();
                return evt;
            }

            if (patchedCount > 0 || evt.AddedFields.Count > 0 || evt.RequiresDomainReload)
            {
                _dirtyScriptsSinceDomainReload.Add(scriptPath);
                RecordEvent(evt);
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
            if (_dynamicLifecycleHooks.Count > 0 || _dynamicAddedMethods.Count > 0 || _dynamicScheduledInvokes.Count > 0)
            {
                Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
                _dynamicLifecycleHooks.Clear();
                _dynamicAddedMethods.Clear();
                _dynamicScheduledInvokes.Clear();
            }
            int count = NativeMethodDetour.RevertAll();
            HotReloadStateStore.ClearAll();
            OnStatusChanged?.Invoke();
            return count;
        }

        /// <summary>
        /// Unlocks Auto-Refresh and triggers a clean Unity AssetDatabase Refresh outside the IMGUI loop.
        /// Never re-locks Auto-Refresh until the Domain Reload has completed.
        /// </summary>
        public static void ForceFullUnityReload()
        {
            _dirtyScriptsSinceDomainReload.Clear();
            _loggedReloadAdvisories.Clear();
            _pendingFiles.Clear();
            _isPerformingFullReload = true;
            ReleaseAutoRefreshLock();

            if (_dynamicLifecycleHooks.Count > 0 || _dynamicAddedMethods.Count > 0 || _dynamicScheduledInvokes.Count > 0)
            {
                Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
                _dynamicLifecycleHooks.Clear();
                _dynamicAddedMethods.Clear();
                _dynamicScheduledInvokes.Clear();
            }
            NativeMethodDetour.RevertAll();
            HotReloadStateStore.ClearAll();

            EditorApplication.delayCall += () =>
            {
                AssetDatabase.Refresh(ImportAssetOptions.Default);
                EnsureIdeProjectFilesSynced(force: true);

                // AssetDatabase.Refresh synchronously sets EditorApplication.isCompiling = true if any .cs file changed on disk.
                // Only call RequestScriptReload when no script compilation was queued; otherwise RequestScriptReload
                // forces a redundant second StopAssetImportingV2(ForceSynchronousImport | ForceDomainReload) pass!
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
                {
                    EditorUtility.RequestScriptReload();
                }
            };
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
                _scriptLastWriteTicks.Clear();
                string assetsPath = Application.dataPath;
                if (!Directory.Exists(assetsPath))
                {
                    return;
                }

                foreach (string filePath in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
                {
                    try
                    {
                        _scriptLastWriteTicks[filePath] = File.GetLastWriteTimeUtc(filePath).Ticks;
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
            _scriptLastWriteTicks.Clear();
            _pendingFiles.Clear();
        }

        private static void PollModifiedScriptsMainThread()
        {
            if (!_initialBaselineComplete || !IsEnabled || _isPerformingFullReload || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastPollTime < POLL_INTERVAL_SECONDS)
            {
                return;
            }
            _lastPollTime = EditorApplication.timeSinceStartup;

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

                    if (_scriptLastWriteTicks.TryGetValue(filePath, out long previousTicks))
                    {
                        if (currentTicks > previousTicks)
                        {
                            _scriptLastWriteTicks[filePath] = currentTicks;
                            if (EditorApplication.isPlaying || AllowEditModePatching)
                            {
                                QueueScriptChange(filePath);
                            }
                        }
                    }
                    else
                    {
                        // Newly created script file: record baseline and let Unity register new MonoScript if in Edit Mode
                        _scriptLastWriteTicks[filePath] = currentTicks;
                        if (EditorApplication.isPlaying || AllowEditModePatching)
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

            if (!EditorApplication.isPlaying && !AllowEditModePatching)
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

            // Pre-resolve DotNetSdk paths 1.0s after Editor domain reload
            if (!_warmedUp && EditorApplication.timeSinceStartup - _startupTime > 1.0)
            {
                _warmedUp = true;
                HotReloadCompilerService.WarmUp();
                UpdateAutoRefreshLockState();
            }

            PollModifiedScriptsMainThread();

            if (_pendingFiles.IsEmpty)
            {
                return;
            }

            if (!IsEnabled || EditorApplication.isCompiling)
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
                case PlayModeStateChange.ExitingEditMode:
                    // Clear any leftover Edit Mode queue so entering Play Mode never triggers a false Play-Mode patch
                    _pendingFiles.Clear();
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    _pendingFiles.Clear();
                    StartFileWatcher();
                    UpdateAutoRefreshLockState();
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    _pendingFiles.Clear();
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
            if (_dynamicLifecycleHooks.Count > 0 || _dynamicAddedMethods.Count > 0 || _dynamicScheduledInvokes.Count > 0)
            {
                Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
                _dynamicLifecycleHooks.Clear();
                _dynamicAddedMethods.Clear();
                _dynamicScheduledInvokes.Clear();
            }
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
            OnBeforeAssemblyReload();
            ReleaseAutoRefreshLock();
        }

        private static void UpdateAutoRefreshLockState()
        {
            if (_isPerformingFullReload)
            {
                return;
            }

            // Lock kAutoRefresh ONLY when actively in Play Mode (or if user explicitly enabled Edit-Mode Patching).
            // Never lock during ExitingEditMode before Play Mode starts, so Unity can always compile Edit-Mode script changes first!
            bool shouldDisableAutoRefreshPref = IsEnabled && LockPlayModeAutoRefresh &&
                                                (EditorApplication.isPlaying || AllowEditModePatching);

            if (shouldDisableAutoRefreshPref)
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

            // Only use C++ AssetDatabase.DisallowAutoRefresh() during active Play Mode
            bool shouldDisallowInPlayMode = IsEnabled && LockPlayModeAutoRefresh && EditorApplication.isPlaying;
            if (shouldDisallowInPlayMode && !_autoRefreshLockedByUs)
            {
                AssetDatabase.DisallowAutoRefresh();
                _autoRefreshLockedByUs = true;
            }
            else if (!shouldDisallowInPlayMode && _autoRefreshLockedByUs)
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
                // Ensure Unity Auto-Refresh is never left disabled in Edit Mode
                if (EditorPrefs.GetInt(UNITY_PREF_AUTOREFRESH_MODE, 1) == 0)
                {
                    EditorPrefs.SetBool(UNITY_PREF_AUTOREFRESH, true);
                    EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH, 1);
                    EditorPrefs.SetInt(UNITY_PREF_AUTOREFRESH_MODE, 1);
                }
            }
        }

        private static int ApplyAssemblyDetours(
            Assembly patchAssembly,
            string scriptName,
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

                if (patchType.IsNested && patchType.Name.StartsWith("<", StringComparison.Ordinal))
                {
                    continue;
                }

                Type originalType = FindOriginalRuntimeType(patchType);
                if (originalType == null)
                {
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
                    requiresDomainReload = true;
                    reloadReason = $"Struct '{patchType.Name}' modified";
                    LogDomainReloadAdvisoryOnce(
                        $"StructType:{patchType.FullName}",
                        $"Struct (<b>{patchType.Name}</b>) was modified in <b>{scriptName}</b>. " +
                        "To realign value-type memory layouts, click <b>Tools > Hot Reload Pro > Full Domain Reload</b> once.");
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
                        continue;
                    }

                    MethodInfo originalMethod = FindMatchingOriginalMethod(originalType, patchMethod, flags);
                    if (originalMethod == null)
                    {
                        if (!patchMethod.IsStatic && patchMethod.GetParameters().Length == 0 &&
                            typeof(MonoBehaviour).IsAssignableFrom(originalType))
                        {
                            if (patchMethod.Name == "Update" || patchMethod.Name == "LateUpdate" || patchMethod.Name == "FixedUpdate")
                            {
                                if (TryRegisterDynamicLifecycleHook(originalType, patchMethod))
                                {
                                    patchedCount++;
                                    patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name} (Dynamic Bridge)");
                                }
                                continue;
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

                            if (patchMethod.IsPrivate &&
                                patchMethod.ReturnType == typeof(void) &&
                                TryRegisterDynamicAddedMethod(originalType, patchMethod))
                            {
                                patchedCount++;
                                patchedMethodNamesOut.Add($"{originalType.Name}.{patchMethod.Name} (Dynamic Method)");
                                continue;
                            }
                        }

                        // Any new non-private method (e.g. interface implementation `public void Collect()`)
                        // or method with parameters/return value requires a Domain Reload to exist in runtime metadata
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

        private static unsafe bool TryRegisterDynamicLifecycleHook(Type originalType, MethodInfo patchMethod)
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
                _dynamicLifecycleHooks[key] = new DynamicLifecycleHook
                {
                    OriginalComponentType = originalType,
                    MethodName = patchMethod.Name,
                    FunctionPointer = fnPtr
                };
                Application.onBeforeRender -= OnBeforeRenderDynamicUpdates;
                Application.onBeforeRender += OnBeforeRenderDynamicUpdates;
                return true;
            }
            catch
            {
                return false;
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

            foreach (var kvp in _dynamicLifecycleHooks)
            {
                DynamicLifecycleHook hook = kvp.Value;
                if (hook == null || hook.OriginalComponentType == null || hook.FunctionPointer == IntPtr.Zero)
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
                if (tr)
                {
                    errSb.AppendLine($"<color=#F87171><b>[📝 Kod Hatası • {evt.ScriptName}]</b></color> <i>(Eklenti hatası değildir — yazdığın C# kodunda hata var)</i>:");
                }
                else
                {
                    errSb.AppendLine($"<color=#F87171><b>[📝 C# Script Error • {evt.ScriptName}]</b></color> <i>(Code error in your C# script, not a plugin error)</i>:");
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

            if (!turkish)
            {
                return d.ToString();
            }

            string loc = d.Line > 0 ? $"(Satır {d.Line}, Sütun {d.Column}) " : string.Empty;
            string translated = TranslateCompilerErrorToTurkish(d.Id, d.Message);
            return $"{loc}<b>{d.Id}</b>: {translated}";
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

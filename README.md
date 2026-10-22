# Nyks Hot Reload Pro

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-2ea44f?style=for-the-badge&logo=apache&logoColor=white)](LICENSE)
[![Unity 2021.3+ | 6000.x](https://img.shields.io/badge/Unity-2021.3%2B_%7C_Unity_6_(6000.x)-000000?style=for-the-badge&logo=unity&logoColor=white)](https://unity.com/)
[![Runtime: x64 & ARM64](https://img.shields.io/badge/JIT_Detour-x86__64_%7C_ARM64-007ec6?style=for-the-badge)](#architecture)
[![Build Footprint: Zero](https://img.shields.io/badge/Player_Build_Footprint-0_Bytes-6e5494?style=for-the-badge)](#overview)

**Nyks Hot Reload Pro** (`com.nyks.unityhotreload`) is a standalone, zero-dependency C# hot-patching engine for the Unity Editor. It compiles modified scripts in the background using Roslyn and detours native JIT method pointers in memory, eliminating the need to wait for full Unity Domain Reloads during iteration in both **Edit Mode** and **Play Mode**.

---

## Overview

- **Editor-Only UPM Package** — Isolated via `Nyks.HotReload.Editor.asmdef` (`includePlatforms: ["Editor"]`). Zero runtime overhead and zero bytes included in standalone player builds.
- **Dual-Tier Roslyn Compilation**
  - **Primary (Warm In-Memory):** Uses Unity's embedded `Microsoft.CodeAnalysis.CSharp` compiler with cached `MetadataReference` graphs for sub-100ms in-memory emission.
  - **Fallback (.NET SDK Roslyn):** Automatically invokes Unity's bundled `csc.dll` compiler when C# 10, 11, or 12 syntax constructs are detected.
- **Native JIT Method Detouring (`x86_64` & `ARM64`)** — Rewrites compiled method prologues directly in memory (`VirtualProtect` on Windows, `mprotect` on macOS/Linux) while retaining a 16-byte snapshot of original instructions so patches can be reverted cleanly at any time.
- **Virtual Field Store (`ConditionalWeakTable`)** — Newly added fields in existing classes are rewritten into zero-layout-footprint properties backed by `HotReloadStateStore`. This preserves field offsets on live `MonoBehaviour` instances and guarantees zero memory leaks when objects are destroyed.
- **Cross-Assembly Visibility Bypass** — Emits `[assembly: IgnoresAccessChecksTo(...)]` for project assemblies so patched methods can access `private` and `internal` members without compilation errors.
- **Configurable Trigger Modes** — Patch immediately on file save (`Ctrl + S`) or queue changes silently and patch only when the Unity Editor window regains focus.

---

## Installation

### Option 1 — Install via Git URL (Unity Package Manager)

1. In Unity, open **Window > Package Manager**.
2. Click **`+`** in the top-left corner and select **Add package from git URL...**.
3. Enter the repository URL:

```text
https://github.com/Nyks-Scripts-for-RedM/unityhotreload.git
```

### Option 2 — Add to `Packages/manifest.json`

Add the package reference under `"dependencies"` in your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.nyks.unityhotreload": "https://github.com/Nyks-Scripts-for-RedM/unityhotreload.git"
  }
}
```

For a local clone on disk:

```json
{
  "dependencies": {
    "com.nyks.unityhotreload": "file:../../unityhotreload"
  }
}
```

---

## Usage

1. Open **Tools > Hot Reload Pro** (`Ctrl + Shift + H`).
2. Choose your preferred **Patch Trigger Mode**:
   - **Instant on Save (`Ctrl + S`):** Compiles and patches modified `.cs` files immediately when saved in your IDE.
   - **On Unity Focus (When Clicked):** Queues modified `.cs` files while you work in your IDE and patches them the instant you click back into the Unity Editor window.
3. Edit any C# method body or add new methods/fields in **Edit Mode** or **Play Mode**. Changes are compiled and detoured in memory without triggering a Unity Domain Reload.
4. When a structural change requires a full Unity compilation pass, press **`Ctrl + Alt + R`** (or click **Tools > Hot Reload Pro — Full Domain Reload**).

---

## Technical Limitations & When to Run a Full Domain Reload

> [!IMPORTANT]
> **95% of day-to-day gameplay, physics, UI, and systems iteration** works immediately without a Domain Reload. However, certain structural changes in C# and Unity's native C++ engine require a standard Unity compilation pass.

> [!WARNING]
> Click **Full Domain Reload** in the **Hot Reload Pro** window when performing any of the structural operations marked below:

| Operation | Hot-Patchable | Action Required |
| :--- | :---: | :--- |
| Modifying existing method bodies (`Update`, `FixedUpdate`, `Awake`, `Start`, custom methods) | ![Yes](https://img.shields.io/badge/Supported-2ea44f?style=flat-square) | **None** — patched automatically in memory |
| Adding new methods or new instance/static fields to an existing class | ![Yes](https://img.shields.io/badge/Supported-2ea44f?style=flat-square) | **None** — routed through `HotReloadStateStore` |
| Creating a brand-new `MonoBehaviour` or `ScriptableObject` `.cs` file to attach via `Add Component` | ![Reload](https://img.shields.io/badge/Requires_Reload-d97706?style=flat-square) | Click **Full Domain Reload** once so Unity's native C++ type database registers the new class |
| Changing class inheritance (`: BaseClass`), implemented interfaces, or `enum` values | ![Reload](https://img.shields.io/badge/Requires_Reload-d97706?style=flat-square) | Click **Full Domain Reload** once |
| Assigning newly added `[SerializeField]` Unity `Object` references via Inspector drag-and-drop | ![Partial](https://img.shields.io/badge/Partial-007ec6?style=flat-square) | Primitive/struct values (`float`, `int`, `bool`, `string`, `Vector3`, `Color`) are editable live; `Object` drag-and-drop requires **Full Domain Reload** |
| Modifying generic type/method signatures (`class Foo<T>`) or explicit `[StructLayout]` | ![Reload](https://img.shields.io/badge/Requires_Reload-d97706?style=flat-square) | Click **Full Domain Reload** once |

---

## Architecture

```text
com.nyks.unityhotreload/
├── package.json
├── LICENSE                                  # Apache License 2.0
├── NOTICE
└── Editor/
    ├── Nyks.HotReload.Editor.asmdef         # Editor-only isolated assembly
    ├── Core/
    │   ├── HotReloadSessionManager.cs       # FileSystemWatcher, focus queue & Auto-Refresh lock
    │   ├── HotReloadCompilerService.cs      # Warm in-memory Roslyn + DotNetSdk csc fallback
    │   └── HotReloadSourceRewriter.cs       # Virtual field rewriting & IgnoresAccessChecksTo injection
    ├── Detour/
    │   └── NativeMethodDetour.cs            # x86_64 (12-byte) & ARM64 (16-byte) JIT prologue patching
    ├── State/
    │   └── HotReloadStateStore.cs           # ConditionalWeakTable backing store for dynamic fields
    └── UI/
        ├── HotReloadWindow.cs               # Editor control window & live patch feed
        └── HotReloadSceneOverlay.cs         # SceneView notification banner & Inspector hook
```

---

## License

[![Apache License 2.0](https://img.shields.io/badge/Licensed_Under-Apache_2.0-2ea44f?style=flat-square&logo=apache)](LICENSE)

Copyright © 2026 **Togan (`nyks000`)**.  
Licensed under the **Apache License, Version 2.0**. See [LICENSE](LICENSE) and [NOTICE](NOTICE) for full license terms and attribution requirements.

// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nyks.HotReload.Editor.Detour
{
    /// <summary>
    /// Represents a live native machine-code detour applied to a managed method in Mono/CLR memory.
    /// </summary>
    public sealed class DetourRecord
    {
        public MethodBase OriginalMethod { get; internal set; }
        public MethodBase ReplacementMethod { get; internal set; }
        public IntPtr OriginalSite { get; internal set; }
        public IntPtr TargetSite { get; internal set; }
        public byte[] OriginalBytes { get; internal set; }
        public int PatchCount { get; internal set; }
        public DateTime LastPatchedUtc { get; internal set; }
    }

    /// <summary>
    /// Zero-dependency native JIT method detour engine supporting x86_64 and ARM64 architectures.
    /// Overwrites the native prologue of compiled methods with an unconditional jump to the newly compiled method.
    /// </summary>
    public static unsafe class NativeMethodDetour
    {
        private const int PATCH_SIZE_X64 = 12;
        private const int PATCH_SIZE_ARM64 = 16;
        private const int BACKUP_BYTES_LENGTH = 16;

        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;

        private static readonly Dictionary<IntPtr, DetourRecord> _activeDetours = new Dictionary<IntPtr, DetourRecord>(128);
        private static readonly object _syncRoot = new object();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr addr, UIntPtr len, int prot);

        /// <summary>
        /// Gets a snapshot of all currently active method detours.
        /// </summary>
        public static IReadOnlyCollection<DetourRecord> ActiveDetours
        {
            get
            {
                lock (_syncRoot)
                {
                    var list = new List<DetourRecord>(_activeDetours.Count);
                    foreach (var kvp in _activeDetours)
                    {
                        list.Add(kvp.Value);
                    }
                    return list;
                }
            }
        }

        /// <summary>
        /// Gets the total number of currently patched methods.
        /// </summary>
        public static int ActiveDetourCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _activeDetours.Count;
                }
            }
        }

        /// <summary>
        /// Applies a native machine-code jump from <paramref name="originalMethod"/> to <paramref name="replacementMethod"/>.
        /// </summary>
        public static bool TryDetour(MethodBase originalMethod, MethodBase replacementMethod, out string error)
        {
            error = null;

            if (originalMethod == null)
            {
                error = "Original method is null.";
                return false;
            }

            if (replacementMethod == null)
            {
                error = "Replacement method is null.";
                return false;
            }

            if (originalMethod.IsAbstract || replacementMethod.IsAbstract)
            {
                error = "Cannot detour abstract methods.";
                return false;
            }

            try
            {
                PrepareMethodSafe(originalMethod);
                PrepareMethodSafe(replacementMethod);

                IntPtr originalSite = originalMethod.MethodHandle.GetFunctionPointer();
                IntPtr targetSite = replacementMethod.MethodHandle.GetFunctionPointer();

                if (originalSite == IntPtr.Zero || targetSite == IntPtr.Zero)
                {
                    error = "JIT returned a null function pointer.";
                    return false;
                }

                if (originalSite == targetSite)
                {
                    return true;
                }

                lock (_syncRoot)
                {
                    if (!TryMakeMemoryWritable(originalSite, BACKUP_BYTES_LENGTH, out uint oldProtect, out error))
                    {
                        return false;
                    }

                    try
                    {
                        byte* srcPtr = (byte*)originalSite.ToPointer();

                        if (!_activeDetours.TryGetValue(originalSite, out DetourRecord record))
                        {
                            byte[] backup = new byte[BACKUP_BYTES_LENGTH];
                            for (int i = 0; i < BACKUP_BYTES_LENGTH; i++)
                            {
                                backup[i] = srcPtr[i];
                            }

                            record = new DetourRecord
                            {
                                OriginalMethod = originalMethod,
                                ReplacementMethod = replacementMethod,
                                OriginalSite = originalSite,
                                TargetSite = targetSite,
                                OriginalBytes = backup,
                                PatchCount = 1,
                                LastPatchedUtc = DateTime.UtcNow
                            };
                            _activeDetours[originalSite] = record;
                        }
                        else
                        {
                            record.ReplacementMethod = replacementMethod;
                            record.TargetSite = targetSite;
                            record.PatchCount++;
                            record.LastPatchedUtc = DateTime.UtcNow;
                        }

                        WriteAbsoluteJump(srcPtr, targetSite);
                        FlushCpuCache(originalSite, BACKUP_BYTES_LENGTH);
                    }
                    finally
                    {
                        RestoreMemoryProtection(originalSite, BACKUP_BYTES_LENGTH, oldProtect);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Detour exception on {originalMethod.Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Reverts all active native method detours back to their original machine code bytes.
        /// </summary>
        public static int RevertAll()
        {
            int reverted = 0;
            lock (_syncRoot)
            {
                foreach (var kvp in _activeDetours)
                {
                    DetourRecord record = kvp.Value;
                    if (record.OriginalSite != IntPtr.Zero && record.OriginalBytes != null)
                    {
                        if (TryMakeMemoryWritable(record.OriginalSite, BACKUP_BYTES_LENGTH, out uint oldProtect, out _))
                        {
                            try
                            {
                                byte* ptr = (byte*)record.OriginalSite.ToPointer();
                                for (int i = 0; i < record.OriginalBytes.Length; i++)
                                {
                                    ptr[i] = record.OriginalBytes[i];
                                }
                                FlushCpuCache(record.OriginalSite, BACKUP_BYTES_LENGTH);
                                reverted++;
                            }
                            finally
                            {
                                RestoreMemoryProtection(record.OriginalSite, BACKUP_BYTES_LENGTH, oldProtect);
                            }
                        }
                    }
                }
                _activeDetours.Clear();
            }
            return reverted;
        }

        private static void PrepareMethodSafe(MethodBase method)
        {
            Type declaringType = method.DeclaringType;
            if (declaringType != null && declaringType.IsGenericType)
            {
                Type[] typeArgs = declaringType.GetGenericArguments();
                RuntimeTypeHandle[] typeHandles = new RuntimeTypeHandle[typeArgs.Length];
                for (int i = 0; i < typeArgs.Length; i++)
                {
                    typeHandles[i] = typeArgs[i].TypeHandle;
                }
                RuntimeHelpers.PrepareMethod(method.MethodHandle, typeHandles);
            }
            else
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
        }

        private static void WriteAbsoluteJump(byte* site, IntPtr target)
        {
            Architecture arch = RuntimeInformation.ProcessArchitecture;
            if (arch == Architecture.Arm64)
            {
                // ARM64 16-byte absolute branch:
                // ldr x16, #8   (0x58000050)
                // br x16        (0xD61F0200)
                // .quad <64-bit target>
                *(uint*)(site + 0) = 0x58000050u;
                *(uint*)(site + 4) = 0xD61F0200u;
                *(ulong*)(site + 8) = (ulong)target.ToInt64();
            }
            else if (IntPtr.Size == 8)
            {
                // x86_64 12-byte absolute jump:
                // 48 B8 <8-byte-addr>  -> movabs rax, <target>
                // FF E0                -> jmp rax
                *(ulong*)(site + 2) = (ulong)target.ToInt64();
                site[10] = 0xFF;
                site[11] = 0xE0;
                site[0] = 0x48;
                site[1] = 0xB8;
            }
            else
            {
                // x86 32-bit relative jump: E9 <rel32>
                int rel = (int)target.ToInt64() - ((int)new IntPtr(site).ToInt64() + 5);
                *(int*)(site + 1) = rel;
                site[0] = 0xE9;
            }
        }

        private static bool TryMakeMemoryWritable(IntPtr address, int size, out uint oldProtect, out string error)
        {
            oldProtect = 0;
            error = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!VirtualProtect(address, (UIntPtr)size, PAGE_EXECUTE_READWRITE, out oldProtect))
                {
                    int winErr = Marshal.GetLastWin32Error();
                    error = $"VirtualProtect failed with Win32 error code {winErr}.";
                    return false;
                }
                return true;
            }
            else
            {
                long pageSize = Environment.SystemPageSize > 0 ? Environment.SystemPageSize : 4096;
                long rawAddr = address.ToInt64();
                long alignedAddr = rawAddr & ~(pageSize - 1);
                long endAddr = rawAddr + size;
                long totalLen = ((endAddr - alignedAddr + pageSize - 1) / pageSize) * pageSize;

                int res = mprotect(new IntPtr(alignedAddr), (UIntPtr)totalLen, PROT_READ | PROT_WRITE | PROT_EXEC);
                if (res != 0)
                {
                    int errno = Marshal.GetLastWin32Error();
                    error = $"mprotect failed with errno {errno}.";
                    return false;
                }
                return true;
            }
        }

        private static void RestoreMemoryProtection(IntPtr address, int size, uint oldProtect)
        {
            // Keep page executable + writable for subsequent fast patches on Windows, or restore if desired.
            // Leaving RWX on JIT pages is standard in Mono/Harmony so active CPU threads never fault during execution.
        }

        private static void FlushCpuCache(IntPtr address, int size)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)size);
            }
        }
    }
}

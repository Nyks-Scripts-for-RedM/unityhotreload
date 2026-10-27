// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#pragma warning disable UAC0005
#pragma warning disable UAC0007
#pragma warning disable UAC0020

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;

namespace Nyks.HotReload.Editor.Core
{
    /// <summary>
    /// Diagnostic message emitted during dynamic compilation.
    /// </summary>
    public sealed class CompilerDiagnosticEntry
    {
        public string Id { get; set; }
        public string Message { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public bool IsError { get; set; }

        public override string ToString()
        {
            return Line > 0
                ? $"[{Id}] (Line {Line}, Col {Column}): {Message}"
                : $"[{Id}] {Message}";
        }
    }

    /// <summary>
    /// Result of compiling a single modified C# script via the Hot Reload compiler pipeline.
    /// </summary>
    public sealed class HotReloadCompileResult
    {
        public bool Success { get; set; }
        public System.Reflection.Assembly CompiledAssembly { get; set; }
        public string EngineUsed { get; set; }
        public double CompileTimeMs { get; set; }
        public List<CompilerDiagnosticEntry> Diagnostics { get; } = new List<CompilerDiagnosticEntry>();
        public SourceRewriteResult RewriteInfo { get; set; }
    }

    /// <summary>
    /// Dual-engine C# compiler service for Unity 6 (6000.x) and Unity 2021.3+:
    /// 1. Tier 1: Warm In-Memory Roslyn (<see cref="TryCompileInMemoryRoslyn"/>) with cached MetadataReferences (~20-60ms).
    /// 2. Tier 2: Unity DotNetSdk Roslyn csc.dll process compiler (<see cref="TryCompileDotNetSdkCsc"/>) supporting C# 10/11/12+.
    /// </summary>
    public static class HotReloadCompilerService
    {
        private static int _patchSequenceId = 1;
        private static bool _roslynInitialized;
        private static bool _roslynAvailable;

        private static System.Reflection.Assembly _codeAnalysisAsm;
        private static System.Reflection.Assembly _codeAnalysisCSharpAsm;

        private static Type _syntaxTreeType;
        private static Type _csharpSyntaxTreeType;
        private static Type _csharpParseOptionsType;
        private static Type _languageVersionType;
        private static Type _metadataReferenceType;
        private static Type _portableExecutableReferenceType;
        private static Type _csharpCompilationType;
        private static Type _csharpCompilationOptionsType;
        private static Type _outputKindType;
        private static Type _optimizationLevelType;
        private static Type _metadataImportOptionsType;
        private static Type _diagnosticSeverityType;

        private static MethodInfo _createFromFileMethod;
        private static readonly Dictionary<string, object> _cachedMetadataReferences = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _cachedReferenceTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static string _cachedDotNetExePath;
        private static string _cachedCscDllPath;

        private static bool _compilationContextCached;
        private static List<string> _cachedBaseReferences;
        private static List<string> _cachedProjectAssemblyNames;
        private static HashSet<string> _cachedBaseDefines;
        private static Dictionary<string, string[]> _cachedScriptDefinesByPath;

        // Compiler-infrastructure diagnostics: never caused by user code. Trigger Tier 2 fallback instead of reporting.
        private static readonly HashSet<string> _infrastructureDiagnosticIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "CS0041", // Unexpected error writing debug information
            "CS0016", // Could not write to output file
            "CS7038", // Failed to emit module
            "CS0009", // Metadata file could not be opened
            "CS0006", // Metadata file could not be found
        };

        internal static bool IsInfrastructureDiagnostic(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            return _infrastructureDiagnosticIds.Contains(id) || id.StartsWith("HR", StringComparison.Ordinal);
        }

        /// <summary>
        /// Compiles the specified C# script file into an in-memory dynamic assembly.
        /// </summary>
        public static HotReloadCompileResult CompileScriptFile(string scriptFilePath)
        {
            if (string.IsNullOrEmpty(scriptFilePath))
            {
                var emptyRes = new HotReloadCompileResult();
                emptyRes.Diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0001",
                    Message = "Script file path is empty.",
                    IsError = true
                });
                return emptyRes;
            }

            return CompileScriptBatch(new[] { scriptFilePath });
        }

        /// <summary>
        /// Compiles one or more modified C# script files together into a single in-memory dynamic assembly.
        /// Compiling multiple modified files in one batch prevents sequential multi-second compiles and resolves
        /// cross-file dependencies (e.g. a new public method added in Script A and called in Script B).
        /// </summary>
        public static HotReloadCompileResult CompileScriptBatch(IReadOnlyList<string> scriptFilePaths)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new HotReloadCompileResult();

            if (scriptFilePaths == null || scriptFilePaths.Count == 0)
            {
                result.Diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0001",
                    Message = "No script files provided for compilation.",
                    IsError = true
                });
                return result;
            }

            string primaryScriptPath = scriptFilePaths[0];
            CollectCompilationContext(
                primaryScriptPath,
                out List<string> referencePaths,
                out List<string> defineSymbols,
                out List<string> projectAssemblyNames);

            var combinedRewrite = new SourceRewriteResult();
            var filePathsList = new List<string>(scriptFilePaths.Count);
            var rewrittenSourcesList = new List<string>(scriptFilePaths.Count);

            for (int i = 0; i < scriptFilePaths.Count; i++)
            {
                string scriptFilePath = scriptFilePaths[i];
                if (string.IsNullOrEmpty(scriptFilePath) || !File.Exists(scriptFilePath))
                {
                    continue;
                }

                string rawSource;
                try
                {
                    rawSource = ReadFileWithRetry(scriptFilePath);
                }
                catch (Exception ex)
                {
                    result.Diagnostics.Add(new CompilerDiagnosticEntry
                    {
                        Id = "HR0002",
                        FilePath = scriptFilePath,
                        Message = $"Failed to read script file: {ex.Message}",
                        IsError = true
                    });
                    return result;
                }

                bool emitBoilerplate = rewrittenSourcesList.Count == 0;
                SourceRewriteResult singleRewrite = HotReloadSourceRewriter.Rewrite(
                    rawSource,
                    projectAssemblyNames,
                    ResolveLoadedTypeByName,
                    emitSharedAssemblyBoilerplate: emitBoilerplate);

                filePathsList.Add(scriptFilePath);
                rewrittenSourcesList.Add(singleRewrite.RewrittenSource);

                if (rewrittenSourcesList.Count == 1)
                {
                    combinedRewrite.RewrittenSource = singleRewrite.RewrittenSource;
                }

                foreach (var addedField in singleRewrite.AddedFields)
                {
                    combinedRewrite.AddedFields.Add(addedField);
                }
                foreach (string declType in singleRewrite.DeclaredTypeNames)
                {
                    if (!combinedRewrite.DeclaredTypeNames.Contains(declType))
                    {
                        combinedRewrite.DeclaredTypeNames.Add(declType);
                    }
                }
            }

            if (rewrittenSourcesList.Count == 0)
            {
                result.Diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0001",
                    Message = "Script file not found on disk.",
                    IsError = true
                });
                return result;
            }

            result.RewriteInfo = combinedRewrite;

            string patchAssemblyName = $"NyksHotReload_Patch_{_patchSequenceId++}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            // Tier 1: Fast Warm In-Memory Roslyn (~40-100ms) with cached MetadataReferences.
            if (TryCompileInMemoryRoslyn(
                patchAssemblyName,
                filePathsList,
                rewrittenSourcesList,
                referencePaths,
                defineSymbols,
                out System.Reflection.Assembly inMemAssembly,
                out List<CompilerDiagnosticEntry> inMemDiagnostics,
                out bool shouldFallbackToDotNetSdk))
            {
                stopwatch.Stop();
                result.Success = true;
                result.CompiledAssembly = inMemAssembly;
                result.EngineUsed = "In-Memory Roslyn";
                result.CompileTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                result.Diagnostics.AddRange(inMemDiagnostics);
                return result;
            }

            // If In-Memory Roslyn reported a clean user syntax error (and not a compiler/language-version limitation),
            // return the error immediately in ~50ms without spawning an external process!
            if (_roslynAvailable && !shouldFallbackToDotNetSdk && inMemDiagnostics != null && inMemDiagnostics.Count > 0)
            {
                stopwatch.Stop();
                result.Success = false;
                result.EngineUsed = "In-Memory Roslyn";
                result.CompileTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                result.Diagnostics.AddRange(inMemDiagnostics);
                return result;
            }

            // Tier 2: Unity DotNetSdk Roslyn (csc.dll) out-of-process compiler for C# 10/11/12+ syntax or fallback.
            if (TryCompileDotNetSdkCsc(
                patchAssemblyName,
                filePathsList,
                rewrittenSourcesList,
                referencePaths,
                defineSymbols,
                out System.Reflection.Assembly cscAssembly,
                out List<CompilerDiagnosticEntry> cscDiagnostics))
            {
                stopwatch.Stop();
                result.Success = true;
                result.CompiledAssembly = cscAssembly;
                result.EngineUsed = "Unity DotNetSdk Roslyn (csc)";
                result.CompileTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                result.Diagnostics.AddRange(cscDiagnostics);
                return result;
            }

            stopwatch.Stop();
            result.Success = false;
            result.CompileTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            if (cscDiagnostics != null && cscDiagnostics.Count > 0)
            {
                result.Diagnostics.AddRange(cscDiagnostics);
            }
            else if (inMemDiagnostics != null && inMemDiagnostics.Count > 0)
            {
                result.Diagnostics.AddRange(inMemDiagnostics);
            }
            else
            {
                result.Diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0003",
                    Message = "Compilation failed with unknown compiler error.",
                    IsError = true
                });
            }

            return result;
        }

        /// <summary>
        /// Pre-resolves the compiler paths and caches assembly references so the first Hot Reload is instantaneous.
        /// </summary>
        public static void WarmUp()
        {
            try
            {
                ResolveDotNetAndCscPaths(out _, out _);
                EnsureInMemoryRoslynLoaded();
                CollectCompilationContext(null, out List<string> refs, out _, out _);
                if (_roslynAvailable && refs != null && refs.Count > 0)
                {
                    GetOrCreateMetadataReferences(refs);
                }
            }
            catch
            {
                // Non-fatal during warmup
            }
        }

        private static bool TryCompileInMemoryRoslyn(
            string assemblyName,
            IReadOnlyList<string> filePaths,
            IReadOnlyList<string> rewrittenSources,
            List<string> referencePaths,
            List<string> defineSymbols,
            out System.Reflection.Assembly compiledAssembly,
            out List<CompilerDiagnosticEntry> diagnostics,
            out bool shouldFallbackToDotNetSdk)
        {
            compiledAssembly = null;
            diagnostics = new List<CompilerDiagnosticEntry>();
            shouldFallbackToDotNetSdk = true;

            EnsureInMemoryRoslynLoaded();
            if (!_roslynAvailable)
            {
                return false;
            }

            try
            {
                // 1. Create CSharpParseOptions with LanguageVersion.Preview / Latest & preprocessor symbols
                object langVersion = Enum.IsDefined(_languageVersionType, "Preview")
                    ? Enum.Parse(_languageVersionType, "Preview")
                    : Enum.Parse(_languageVersionType, "Latest");

                object parseOptions = null;
                foreach (ConstructorInfo ctor in _csharpParseOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] ps = ctor.GetParameters();
                    if (ps.Length >= 4 && ps[0].ParameterType == _languageVersionType)
                    {
                        object[] args = new object[ps.Length];
                        args[0] = langVersion;
                        for (int i = 1; i < ps.Length; i++)
                        {
                            if (ps[i].Name == "preprocessorSymbols")
                            {
                                args[i] = defineSymbols;
                            }
                            else if (ps[i].HasDefaultValue && ps[i].DefaultValue != null)
                            {
                                args[i] = ps[i].DefaultValue;
                            }
                            else if (ps[i].ParameterType.IsValueType)
                            {
                                args[i] = Activator.CreateInstance(ps[i].ParameterType);
                            }
                            else
                            {
                                args[i] = null;
                            }
                        }
                        parseOptions = ctor.Invoke(args);
                        break;
                    }
                }

                if (parseOptions == null)
                {
                    return false;
                }

                // 2. Parse SyntaxTrees for all files in the batch
                MethodInfo parseTextMethod = null;
                foreach (MethodInfo m in _csharpSyntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "ParseText")
                    {
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length >= 4 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == _csharpParseOptionsType)
                        {
                            parseTextMethod = m;
                            break;
                        }
                    }
                }

                if (parseTextMethod == null)
                {
                    return false;
                }

                ParameterInfo[] parseParams = parseTextMethod.GetParameters();
                Array syntaxTreeArray = Array.CreateInstance(_syntaxTreeType, rewrittenSources.Count);

                for (int s = 0; s < rewrittenSources.Count; s++)
                {
                    object[] parseArgs = new object[parseParams.Length];
                    parseArgs[0] = rewrittenSources[s];
                    parseArgs[1] = parseOptions;
                    parseArgs[2] = (filePaths != null && s < filePaths.Count && filePaths[s] != null) ? filePaths[s] : string.Empty;
                    parseArgs[3] = Encoding.UTF8;
                    for (int i = 4; i < parseParams.Length; i++)
                    {
                        parseArgs[i] = parseParams[i].DefaultValue;
                    }

                    object syntaxTree = parseTextMethod.Invoke(null, parseArgs);
                    syntaxTreeArray.SetValue(syntaxTree, s);
                }

                // 3. Build CSharpCompilationOptions (OutputKind.DynamicallyLinkedLibrary = 2, AllowUnsafe = true)
                object outputKindDll = Enum.ToObject(_outputKindType, 2);
                object compilationOptions = null;

                foreach (ConstructorInfo ctor in _csharpCompilationOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] ctorParams = ctor.GetParameters();
                    if (ctorParams.Length > 0 && ctorParams[0].ParameterType == _outputKindType)
                    {
                        object[] ctorArgs = new object[ctorParams.Length];
                        ctorArgs[0] = outputKindDll;
                        for (int i = 1; i < ctorParams.Length; i++)
                        {
                            ParameterInfo p = ctorParams[i];
                            if (p.Name == "allowUnsafe" && p.ParameterType == typeof(bool))
                            {
                                ctorArgs[i] = true;
                            }
                            else if (p.Name == "optimizationLevel" && p.ParameterType == _optimizationLevelType)
                            {
                                ctorArgs[i] = Enum.ToObject(_optimizationLevelType, 0); // Debug
                            }
                            else if (p.Name == "metadataImportOptions" && _metadataImportOptionsType != null && p.ParameterType == _metadataImportOptionsType)
                            {
                                ctorArgs[i] = Enum.ToObject(_metadataImportOptionsType, 2); // MetadataImportOptions.All
                            }
                            else if (p.HasDefaultValue)
                            {
                                ctorArgs[i] = p.DefaultValue;
                            }
                            else if (p.ParameterType.IsValueType)
                            {
                                ctorArgs[i] = Activator.CreateInstance(p.ParameterType);
                            }
                            else
                            {
                                ctorArgs[i] = null;
                            }
                        }

                        compilationOptions = ctor.Invoke(ctorArgs);
                        break;
                    }
                }

                if (compilationOptions == null)
                {
                    return false;
                }

                // Ensure MetadataImportOptions.All is active so IgnoresAccessChecksTo can bind to private members
                if (_metadataImportOptionsType != null)
                {
                    MethodInfo withMetaImport = _csharpCompilationOptionsType.GetMethod(
                        "WithMetadataImportOptions",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (withMetaImport != null)
                    {
                        compilationOptions = withMetaImport.Invoke(
                            compilationOptions,
                            new object[] { Enum.ToObject(_metadataImportOptionsType, 2) });
                    }
                }

                // 4. Get cached MetadataReferences
                Array metadataRefsArray = GetOrCreateMetadataReferences(referencePaths);

                // 5. CSharpCompilation.Create(assemblyName, syntaxTrees, references, options)
                MethodInfo createCompilationMethod = null;
                foreach (MethodInfo m in _csharpCompilationType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Create" && m.GetParameters().Length == 4)
                    {
                        createCompilationMethod = m;
                        break;
                    }
                }

                if (createCompilationMethod == null)
                {
                    return false;
                }

                object compilation = createCompilationMethod.Invoke(
                    null,
                    new object[] { assemblyName, syntaxTreeArray, metadataRefsArray, compilationOptions });

                // 6. Emit to MemoryStream.
                // pdbStream is intentionally null: with EmitOptions=null Roslyn defaults to native Windows PDB,
                // which requires COM DiaSymReader and throws PlatformNotSupportedException on Unity's Mono (CS0041).
                // PDB bytes are not loaded anyway (Assembly.Load(peBytes) only).
                using (var peStream = new MemoryStream(65536))
                {
                    MethodInfo emitMethod = null;
                    foreach (MethodInfo m in _csharpCompilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name == "Emit")
                        {
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length >= 2 && ps[0].ParameterType == typeof(Stream) && ps[1].ParameterType == typeof(Stream))
                            {
                                emitMethod = m;
                                break;
                            }
                        }
                    }

                    if (emitMethod == null)
                    {
                        return false;
                    }

                    ParameterInfo[] emitParams = emitMethod.GetParameters();
                    object[] emitArgs = new object[emitParams.Length];
                    emitArgs[0] = peStream;
                    emitArgs[1] = null;
                    for (int i = 2; i < emitParams.Length; i++)
                    {
                        emitArgs[i] = emitParams[i].HasDefaultValue
                            ? emitParams[i].DefaultValue
                            : (emitParams[i].ParameterType.IsValueType ? Activator.CreateInstance(emitParams[i].ParameterType) : null);
                    }

                    object emitResult = emitMethod.Invoke(compilation, emitArgs);
                    PropertyInfo successProp = emitResult.GetType().GetProperty("Success");
                    PropertyInfo diagProp = emitResult.GetType().GetProperty("Diagnostics");

                    bool isSuccess = (bool)successProp.GetValue(emitResult);
                    IEnumerable rawDiags = diagProp.GetValue(emitResult) as IEnumerable;
                    shouldFallbackToDotNetSdk = false;

                    if (rawDiags != null)
                    {
                        foreach (object d in rawDiags)
                        {
                            Type dType = d.GetType();
                            string id = dType.GetProperty("Id")?.GetValue(d)?.ToString() ?? "";
                            if (id == "CS0436" || id == "CS1701" || id == "CS1702")
                            {
                                continue;
                            }

                            object severityObj = dType.GetProperty("Severity")?.GetValue(d);
                            int severityInt = severityObj != null ? Convert.ToInt32(severityObj) : 0;
                            if (severityInt < 2) // Hidden (0) or Info (1)
                            {
                                continue;
                            }

                            bool isErr = severityInt == 3; // Error = 3
                            if (isErr && _infrastructureDiagnosticIds.Contains(id))
                            {
                                shouldFallbackToDotNetSdk = true;
                            }
                            if (isErr && (id == "CS8703" || id == "CS8652" || id == "CS8400" || id == "CS8773" || id == "CS8936" || id == "CS0518"))
                            {
                                // Language version / C# 10+ syntax feature not supported by Mono's Roslyn 3.7: fall back to DotNetSdk Roslyn
                                shouldFallbackToDotNetSdk = true;
                            }

                            MethodInfo getMessage = dType.GetMethod("GetMessage", new[] { typeof(IFormatProvider) });
                            string msg = getMessage != null ? (string)getMessage.Invoke(d, new object[] { null }) : d.ToString();

                            int line = 0;
                            int col = 0;
                            string diagFilePath = null;
                            object location = dType.GetProperty("Location")?.GetValue(d);
                            if (location != null)
                            {
                                MethodInfo getLineSpan = location.GetType().GetMethod("GetLineSpan");
                                if (getLineSpan != null)
                                {
                                    object lineSpan = getLineSpan.Invoke(location, null);
                                    diagFilePath = lineSpan.GetType().GetProperty("Path")?.GetValue(lineSpan) as string;
                                    object startPos = lineSpan.GetType().GetProperty("StartLinePosition")?.GetValue(lineSpan);
                                    if (startPos != null)
                                    {
                                        line = (int)startPos.GetType().GetProperty("Line").GetValue(startPos) + 1;
                                        col = (int)startPos.GetType().GetProperty("Character").GetValue(startPos) + 1;
                                    }
                                }
                            }

                            diagnostics.Add(new CompilerDiagnosticEntry
                            {
                                Id = id,
                                Message = msg,
                                FilePath = diagFilePath,
                                Line = line,
                                Column = col,
                                IsError = isErr
                            });
                        }
                    }

                    if (!isSuccess)
                    {
                        if (diagnostics.Count == 0)
                        {
                            shouldFallbackToDotNetSdk = true;
                        }
                        return false;
                    }

                    byte[] peBytes = peStream.ToArray();
                    compiledAssembly = System.Reflection.Assembly.Load(peBytes);
                    return compiledAssembly != null;
                }
            }
            catch
            {
                shouldFallbackToDotNetSdk = true;
                return false;
            }
        }

        private static bool TryCompileDotNetSdkCsc(
            string assemblyName,
            IReadOnlyList<string> originalFilePaths,
            IReadOnlyList<string> rewrittenSources,
            List<string> referencePaths,
            List<string> defineSymbols,
            out System.Reflection.Assembly compiledAssembly,
            out List<CompilerDiagnosticEntry> diagnostics)
        {
            compiledAssembly = null;
            diagnostics = new List<CompilerDiagnosticEntry>();

            if (!ResolveDotNetAndCscPaths(out string dotnetExe, out string cscDll))
            {
                diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0004",
                    Message = "Could not locate Unity's bundled DotNetSdk / Roslyn csc.dll compiler.",
                    IsError = true
                });
                return false;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "NyksHotReload");
            Directory.CreateDirectory(tempDir);

            var tempSrcFiles = new List<string>(rewrittenSources.Count);
            var tempToOriginalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string rspFile = Path.Combine(tempDir, $"{assemblyName}.rsp");
            string outDll = Path.Combine(tempDir, $"{assemblyName}.dll");

            try
            {
                for (int i = 0; i < rewrittenSources.Count; i++)
                {
                    string origName = (originalFilePaths != null && i < originalFilePaths.Count && !string.IsNullOrEmpty(originalFilePaths[i]))
                        ? Path.GetFileNameWithoutExtension(originalFilePaths[i])
                        : $"Script_{i}";
                    string tempSrc = Path.Combine(tempDir, $"{assemblyName}_{i}_{origName}.cs");
                    File.WriteAllText(tempSrc, rewrittenSources[i], Encoding.UTF8);
                    tempSrcFiles.Add(tempSrc);
                    if (originalFilePaths != null && i < originalFilePaths.Count && !string.IsNullOrEmpty(originalFilePaths[i]))
                    {
                        tempToOriginalMap[Path.GetFileName(tempSrc)] = originalFilePaths[i];
                    }
                }

                var rsp = new StringBuilder(16384);
                rsp.AppendLine("-target:library");
                rsp.AppendLine("-nostdlib+");
                rsp.AppendLine("-unsafe+");
                rsp.AppendLine("-optimize-");
                rsp.AppendLine("-debug-");
                rsp.AppendLine("-utf8output");
                rsp.AppendLine("-preferreduilang:en-US");
                rsp.AppendLine("-langversion:latest");
                rsp.AppendLine("-nowarn:0436,0169,0649,0414,1701,1702");
                rsp.AppendLine($"-out:\"{outDll}\"");

                foreach (string def in defineSymbols)
                {
                    if (!string.IsNullOrWhiteSpace(def))
                    {
                        rsp.AppendLine($"-define:{def}");
                    }
                }

                foreach (string refPath in referencePaths)
                {
                    if (File.Exists(refPath))
                    {
                        rsp.AppendLine($"-reference:\"{refPath}\"");
                    }
                }

                for (int i = 0; i < tempSrcFiles.Count; i++)
                {
                    rsp.AppendLine($"\"{tempSrcFiles[i]}\"");
                }
                File.WriteAllText(rspFile, rsp.ToString(), Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = dotnetExe,
                    Arguments = $"exec \"{cscDll}\" -noconfig @\"{rspFile}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(10000);

                    ParseCscOutput(stdout + "\n" + stderr, diagnostics, tempToOriginalMap);

                    if (proc.ExitCode != 0 || !File.Exists(outDll))
                    {
                        return false;
                    }

                    byte[] peBytes = File.ReadAllBytes(outDll);
                    compiledAssembly = System.Reflection.Assembly.Load(peBytes);

                    return compiledAssembly != null;
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0005",
                    Message = $"DotNetSdk compiler exception: {ex.Message}",
                    IsError = true
                });
                return false;
            }
            finally
            {
                for (int i = 0; i < tempSrcFiles.Count; i++)
                {
                    TryDeleteFile(tempSrcFiles[i]);
                }
                TryDeleteFile(rspFile);
                TryDeleteFile(outDll);
            }
        }

        private static void EnsureInMemoryRoslynLoaded()
        {
            if (_roslynInitialized)
            {
                return;
            }
            _roslynInitialized = true;

            try
            {
                string editorDataPath = EditorApplication.applicationContentsPath;
                string mono45Dir = Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5");
                string roslynDir = Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn");

                string probeDir = Directory.Exists(mono45Dir) && File.Exists(Path.Combine(mono45Dir, "Microsoft.CodeAnalysis.CSharp.dll"))
                    ? mono45Dir
                    : roslynDir;

                if (!Directory.Exists(probeDir))
                {
                    _roslynAvailable = false;
                    return;
                }

                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                {
                    string shortName = new AssemblyName(args.Name).Name;
                    string candidate = Path.Combine(probeDir, shortName + ".dll");
                    if (File.Exists(candidate))
                    {
                        return System.Reflection.Assembly.LoadFrom(candidate);
                    }
                    string facadesCandidate = Path.Combine(mono45Dir, "Facades", shortName + ".dll");
                    if (File.Exists(facadesCandidate))
                    {
                        return System.Reflection.Assembly.LoadFrom(facadesCandidate);
                    }
                    return null;
                };

                string caPath = Path.Combine(probeDir, "Microsoft.CodeAnalysis.dll");
                string csPath = Path.Combine(probeDir, "Microsoft.CodeAnalysis.CSharp.dll");

                if (!File.Exists(caPath) || !File.Exists(csPath))
                {
                    _roslynAvailable = false;
                    return;
                }

                _codeAnalysisAsm = System.Reflection.Assembly.LoadFrom(caPath);
                _codeAnalysisCSharpAsm = System.Reflection.Assembly.LoadFrom(csPath);

                _syntaxTreeType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree");
                _metadataReferenceType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
                _portableExecutableReferenceType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.PortableExecutableReference");
                _outputKindType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.OutputKind");
                _optimizationLevelType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.OptimizationLevel");
                _metadataImportOptionsType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.MetadataImportOptions");
                _diagnosticSeverityType = _codeAnalysisAsm.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity");

                _csharpSyntaxTreeType = _codeAnalysisCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                _csharpParseOptionsType = _codeAnalysisCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
                _languageVersionType = _codeAnalysisCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion");
                _csharpCompilationType = _codeAnalysisCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                _csharpCompilationOptionsType = _codeAnalysisCSharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");

                foreach (MethodInfo m in _metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "CreateFromFile" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    {
                        _createFromFileMethod = m;
                        break;
                    }
                }

                _roslynAvailable =
                    _syntaxTreeType != null &&
                    _csharpSyntaxTreeType != null &&
                    _csharpCompilationType != null &&
                    _createFromFileMethod != null;
            }
            catch
            {
                _roslynAvailable = false;
            }
        }

        private static Array GetOrCreateMetadataReferences(List<string> referencePaths)
        {
            var validRefs = new List<object>(referencePaths.Count);
            ParameterInfo[] createParams = _createFromFileMethod.GetParameters();

            foreach (string path in referencePaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }

                DateTime lastWrite = File.GetLastWriteTimeUtc(path);
                if (!_cachedMetadataReferences.TryGetValue(path, out object metaRef) ||
                    !_cachedReferenceTimestamps.TryGetValue(path, out DateTime cachedTime) ||
                    cachedTime != lastWrite)
                {
                    try
                    {
                        object[] args = new object[createParams.Length];
                        args[0] = path;
                        for (int i = 1; i < createParams.Length; i++)
                        {
                            args[i] = createParams[i].HasDefaultValue
                                ? createParams[i].DefaultValue
                                : (createParams[i].ParameterType.IsValueType ? Activator.CreateInstance(createParams[i].ParameterType) : null);
                        }

                        metaRef = _createFromFileMethod.Invoke(null, args);
                        _cachedMetadataReferences[path] = metaRef;
                        _cachedReferenceTimestamps[path] = lastWrite;
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (metaRef != null)
                {
                    validRefs.Add(metaRef);
                }
            }

            Array arr = Array.CreateInstance(_metadataReferenceType, validRefs.Count);
            for (int i = 0; i < validRefs.Count; i++)
            {
                arr.SetValue(validRefs[i], i);
            }
            return arr;
        }

        private static void CollectCompilationContext(
            string scriptFilePath,
            out List<string> referencePaths,
            out List<string> defineSymbols,
            out List<string> projectAssemblyNames)
        {
            if (!_compilationContextCached || _cachedBaseReferences == null || _cachedProjectAssemblyNames == null || _cachedBaseDefines == null)
            {
                _cachedBaseReferences = new List<string>(128);
                _cachedProjectAssemblyNames = new List<string>(32);
                _cachedBaseDefines = new HashSet<string>(StringComparer.Ordinal);
                _cachedScriptDefinesByPath = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

                foreach (string def in EditorUserBuildSettings.activeScriptCompilationDefines)
                {
                    _cachedBaseDefines.Add(def);
                }
                _cachedBaseDefines.Add("UNITY_EDITOR");
                _cachedBaseDefines.Add("DEBUG");
                _cachedBaseDefines.Add("TRACE");

                var pipelineRefCandidates = new List<string>(256);

                try
                {
                    UnityEditor.Compilation.Assembly[] unityAssemblies = CompilationPipeline.GetAssemblies();
                    foreach (UnityEditor.Compilation.Assembly uAsm in unityAssemblies)
                    {
                        if (!string.IsNullOrEmpty(uAsm.name) && !_cachedProjectAssemblyNames.Contains(uAsm.name))
                        {
                            _cachedProjectAssemblyNames.Add(uAsm.name);
                        }

                        if (!string.IsNullOrEmpty(uAsm.outputPath))
                        {
                            pipelineRefCandidates.Add(uAsm.outputPath);
                        }

                        if (uAsm.compiledAssemblyReferences != null)
                        {
                            foreach (string compRef in uAsm.compiledAssemblyReferences)
                            {
                                if (!string.IsNullOrEmpty(compRef))
                                {
                                    pipelineRefCandidates.Add(compRef);
                                }
                            }
                        }

                        if (uAsm.sourceFiles != null && uAsm.defines != null && uAsm.defines.Length > 0)
                        {
                            foreach (string src in uAsm.sourceFiles)
                            {
                                if (!string.IsNullOrEmpty(src))
                                {
                                    string fullSrc = Path.GetFullPath(src).Replace('\\', '/');
                                    _cachedScriptDefinesByPath[fullSrc] = uAsm.defines;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore if CompilationPipeline is unavailable
                }

                if (!_cachedProjectAssemblyNames.Contains("Assembly-CSharp"))
                {
                    _cachedProjectAssemblyNames.Add("Assembly-CSharp");
                }

                var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenFullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void TryAddReferencePath(string candidatePath)
                {
                    if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
                    {
                        return;
                    }

                    string full = Path.GetFullPath(candidatePath);
                    string fileName = Path.GetFileName(full);

                    if (fileName.StartsWith("NyksHotReload_Patch_", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase) ||
                        full.IndexOf("NetStandard\\ref\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        full.IndexOf("NetStandard/ref/", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return;
                    }

                    if (seenFileNames.Contains(fileName) || seenFullPaths.Contains(full))
                    {
                        return;
                    }

                    seenFileNames.Add(fileName);
                    seenFullPaths.Add(full);
                    _cachedBaseReferences.Add(full);
                }

                foreach (System.Reflection.Assembly loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loadedAsm == null || loadedAsm.IsDynamic)
                    {
                        continue;
                    }

                    try
                    {
                        TryAddReferencePath(loadedAsm.Location);
                    }
                    catch
                    {
                        continue;
                    }
                }

                foreach (string pipeRef in pipelineRefCandidates)
                {
                    try
                    {
                        TryAddReferencePath(pipeRef);
                    }
                    catch
                    {
                        continue;
                    }
                }

                try
                {
                    string scriptAssembliesDir = Path.GetFullPath("Library/ScriptAssemblies");
                    if (Directory.Exists(scriptAssembliesDir))
                    {
                        foreach (string dllPath in Directory.GetFiles(scriptAssembliesDir, "*.dll"))
                        {
                            TryAddReferencePath(dllPath);
                        }
                    }
                }
                catch
                {
                    // Ignore if Library/ScriptAssemblies is inaccessible
                }

                try
                {
                    string assetsPath = UnityEngine.Application.dataPath;
                    if (!string.IsNullOrEmpty(assetsPath) && Directory.Exists(assetsPath))
                    {
                        foreach (string pluginDll in Directory.GetFiles(assetsPath, "*.dll", SearchOption.AllDirectories))
                        {
                            TryAddReferencePath(pluginDll);
                        }
                    }
                }
                catch
                {
                    // Ignore if plugin DLL enumeration fails
                }

                _compilationContextCached = true;
            }

            referencePaths = _cachedBaseReferences;
            projectAssemblyNames = _cachedProjectAssemblyNames;

            var definesSet = new HashSet<string>(_cachedBaseDefines, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(scriptFilePath) && _cachedScriptDefinesByPath != null)
            {
                try
                {
                    string normalizedScript = Path.GetFullPath(scriptFilePath).Replace('\\', '/');
                    if (_cachedScriptDefinesByPath.TryGetValue(normalizedScript, out string[] asmDefines) && asmDefines != null)
                    {
                        for (int i = 0; i < asmDefines.Length; i++)
                        {
                            definesSet.Add(asmDefines[i]);
                        }
                    }
                }
                catch
                {
                    // Ignore path normalization errors
                }
            }

            defineSymbols = new List<string>(definesSet);
        }

        private static bool ResolveDotNetAndCscPaths(out string dotnetExePath, out string cscDllPath)
        {
            if (!string.IsNullOrEmpty(_cachedDotNetExePath) &&
                !string.IsNullOrEmpty(_cachedCscDllPath) &&
                File.Exists(_cachedDotNetExePath) &&
                File.Exists(_cachedCscDllPath))
            {
                dotnetExePath = _cachedDotNetExePath;
                cscDllPath = _cachedCscDllPath;
                return true;
            }

            string editorData = EditorApplication.applicationContentsPath;
            string exeName = UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";

            string[] dotnetCandidates =
            {
                Path.Combine(editorData, "DotNetSdk", exeName),
                Path.Combine(editorData, "NetCoreRuntime", exeName)
            };

            dotnetExePath = null;
            foreach (string candidate in dotnetCandidates)
            {
                if (File.Exists(candidate))
                {
                    dotnetExePath = candidate;
                    break;
                }
            }

            cscDllPath = null;
            string sdkRoot = Path.Combine(editorData, "DotNetSdk", "sdk");
            if (Directory.Exists(sdkRoot))
            {
                foreach (string sdkVersionDir in Directory.GetDirectories(sdkRoot))
                {
                    string candidateCsc = Path.Combine(sdkVersionDir, "Roslyn", "bincore", "csc.dll");
                    if (File.Exists(candidateCsc))
                    {
                        cscDllPath = candidateCsc;
                        break;
                    }
                }
            }

            if (cscDllPath == null)
            {
                string legacyRoslynCsc = Path.Combine(editorData, "DotNetSdkRoslyn", "csc.dll");
                if (File.Exists(legacyRoslynCsc))
                {
                    cscDllPath = legacyRoslynCsc;
                }
            }

            if (!string.IsNullOrEmpty(dotnetExePath) && !string.IsNullOrEmpty(cscDllPath))
            {
                _cachedDotNetExePath = dotnetExePath;
                _cachedCscDllPath = cscDllPath;
                return true;
            }

            return false;
        }

        private static void ParseCscOutput(
            string output,
            List<CompilerDiagnosticEntry> diagnostics,
            Dictionary<string, string> tempToOriginalMap = null)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                bool isError = line.Contains(": error ");
                bool isWarning = line.Contains(": warning ");

                if (!isError && !isWarning)
                {
                    continue;
                }

                if (line.Contains("CS0436") || line.Contains("CS1701") || line.Contains("CS1702"))
                {
                    continue;
                }

                int lineNum = 0;
                int colNum = 0;
                string diagFilePath = null;
                int parenOpen = line.IndexOf('(');
                int parenClose = line.IndexOf(')', parenOpen + 1);
                if (parenOpen > 0 && parenClose > parenOpen)
                {
                    string filePrefix = line.Substring(0, parenOpen).Trim();
                    if (!string.IsNullOrEmpty(filePrefix))
                    {
                        string tempFileName = Path.GetFileName(filePrefix);
                        if (tempToOriginalMap != null && !string.IsNullOrEmpty(tempFileName) &&
                            tempToOriginalMap.TryGetValue(tempFileName, out string mappedPath))
                        {
                            diagFilePath = mappedPath;
                        }
                        else
                        {
                            diagFilePath = filePrefix;
                        }
                    }

                    string coords = line.Substring(parenOpen + 1, parenClose - parenOpen - 1);
                    string[] parts = coords.Split(',');
                    if (parts.Length >= 1) int.TryParse(parts[0], out lineNum);
                    if (parts.Length >= 2) int.TryParse(parts[1], out colNum);
                }

                int colonIdx = line.IndexOf(": error ", StringComparison.Ordinal);
                if (colonIdx < 0) colonIdx = line.IndexOf(": warning ", StringComparison.Ordinal);

                string messagePart = colonIdx >= 0 ? line.Substring(colonIdx + 2) : line;
                // messagePart looks like "error CS1002: ; expected" → extract "CS1002"
                string diagId = isError ? "ERROR" : "WARN";
                int csIdx = messagePart.IndexOf("CS", StringComparison.Ordinal);
                int idEnd = csIdx >= 0 ? messagePart.IndexOf(':', csIdx) : -1;
                if (csIdx >= 0 && idEnd > csIdx)
                {
                    diagId = messagePart.Substring(csIdx, idEnd - csIdx).Trim();
                    messagePart = messagePart.Substring(idEnd + 1).Trim();
                }

                diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = diagId,
                    Message = messagePart,
                    FilePath = diagFilePath,
                    Line = lineNum,
                    Column = colNum,
                    IsError = isError
                });
            }
        }

        private static Type ResolveLoadedTypeByName(string typeFullNameOrShortName)
        {
            if (string.IsNullOrEmpty(typeFullNameOrShortName))
            {
                return null;
            }

            System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            // Search non-patch assemblies first (so we always match the original Type from Assembly-CSharp / .asmdef)
            for (int i = 0; i < assemblies.Length; i++)
            {
                System.Reflection.Assembly asm = assemblies[i];
                if (asm == null || asm.IsDynamic)
                {
                    continue;
                }

                string asmName = asm.GetName().Name;
                if (asmName.StartsWith("NyksHotReload_Patch_", StringComparison.Ordinal))
                {
                    continue;
                }

                Type exact = asm.GetType(typeFullNameOrShortName, false);
                if (exact != null)
                {
                    return exact;
                }
            }

            return null;
        }

        private static string ReadFileWithRetry(string path)
        {
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(25);
                }
            }
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore locked temporary file cleanup errors
            }
        }
    }
}

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

        /// <summary>
        /// Compiles the specified C# script file into an in-memory dynamic assembly.
        /// </summary>
        public static HotReloadCompileResult CompileScriptFile(string scriptFilePath)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new HotReloadCompileResult();

            if (string.IsNullOrEmpty(scriptFilePath) || !File.Exists(scriptFilePath))
            {
                result.Diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = "HR0001",
                    Message = $"Script file not found: {scriptFilePath}",
                    IsError = true
                });
                return result;
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
                    Message = $"Failed to read script file: {ex.Message}",
                    IsError = true
                });
                return result;
            }

            CollectCompilationContext(
                scriptFilePath,
                out List<string> referencePaths,
                out List<string> defineSymbols,
                out List<string> projectAssemblyNames);

            SourceRewriteResult rewriteResult = HotReloadSourceRewriter.Rewrite(
                rawSource,
                projectAssemblyNames,
                ResolveLoadedTypeByName);

            result.RewriteInfo = rewriteResult;

            string patchAssemblyName = $"NyksHotReload_Patch_{_patchSequenceId++}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            // Attempt Tier 1: Warm In-Memory Roslyn first for sub-60ms compilation
            if (TryCompileInMemoryRoslyn(
                patchAssemblyName,
                scriptFilePath,
                rewriteResult.RewrittenSource,
                referencePaths,
                defineSymbols,
                out System.Reflection.Assembly inMemAssembly,
                out List<CompilerDiagnosticEntry> inMemDiagnostics))
            {
                stopwatch.Stop();
                result.Success = true;
                result.CompiledAssembly = inMemAssembly;
                result.EngineUsed = "Warm In-Memory Roslyn";
                result.CompileTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                result.Diagnostics.AddRange(inMemDiagnostics);
                return result;
            }

            // Attempt Tier 2: Unity 6 DotNetSdk Roslyn (csc.dll) which supports C# 10/11/12+
            if (TryCompileDotNetSdkCsc(
                patchAssemblyName,
                 rewriteResult.RewrittenSource,
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
        /// Pre-warms the in-memory Roslyn compiler and metadata reference cache so the first save is instantaneous.
        /// </summary>
        public static void WarmUp()
        {
            try
            {
                EnsureInMemoryRoslynLoaded();
                if (_roslynAvailable)
                {
                    CollectCompilationContext(null, out List<string> refs, out _, out _);
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
            string filePath,
            string rewrittenSource,
            List<string> referencePaths,
            List<string> defineSymbols,
            out System.Reflection.Assembly compiledAssembly,
            out List<CompilerDiagnosticEntry> diagnostics)
        {
            compiledAssembly = null;
            diagnostics = new List<CompilerDiagnosticEntry>();

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

                // 2. Parse SyntaxTree
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
                object[] parseArgs = new object[parseParams.Length];
                parseArgs[0] = rewrittenSource;
                parseArgs[1] = parseOptions;
                parseArgs[2] = filePath ?? string.Empty;
                parseArgs[3] = Encoding.UTF8;
                for (int i = 4; i < parseParams.Length; i++)
                {
                    parseArgs[i] = parseParams[i].DefaultValue;
                }

                object syntaxTree = parseTextMethod.Invoke(null, parseArgs);
                Array syntaxTreeArray = Array.CreateInstance(_syntaxTreeType, 1);
                syntaxTreeArray.SetValue(syntaxTree, 0);

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

                // 6. Emit to MemoryStream
                using (var peStream = new MemoryStream(65536))
                using (var pdbStream = new MemoryStream(32768))
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
                    emitArgs[1] = pdbStream;
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
                            MethodInfo getMessage = dType.GetMethod("GetMessage", new[] { typeof(IFormatProvider) });
                            string msg = getMessage != null ? (string)getMessage.Invoke(d, new object[] { null }) : d.ToString();

                            int line = 0;
                            int col = 0;
                            object location = dType.GetProperty("Location")?.GetValue(d);
                            if (location != null)
                            {
                                MethodInfo getLineSpan = location.GetType().GetMethod("GetLineSpan");
                                if (getLineSpan != null)
                                {
                                    object lineSpan = getLineSpan.Invoke(location, null);
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
                                Line = line,
                                Column = col,
                                IsError = isErr
                            });
                        }
                    }

                    if (!isSuccess)
                    {
                        return false;
                    }

                    byte[] peBytes = peStream.ToArray();
                    byte[] pdbBytes = pdbStream.ToArray();
                    compiledAssembly = System.Reflection.Assembly.Load(peBytes, pdbBytes);
                    return compiledAssembly != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCompileDotNetSdkCsc(
            string assemblyName,
            string rewrittenSource,
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

            string tempDir = Path.Combine( Path.GetTempPath(), "NyksHotReload");
            Directory.CreateDirectory(tempDir);

            string srcFile = Path.Combine(tempDir, $"{assemblyName}.cs");
            string rspFile = Path.Combine(tempDir, $"{assemblyName}.rsp");
            string outDll = Path.Combine(tempDir, $"{assemblyName}.dll");
            string outPdb = Path.Combine(tempDir, $"{assemblyName}.pdb");

            try
            {
                File.WriteAllText(srcFile, rewrittenSource, Encoding.UTF8);

                var rsp = new StringBuilder(16384);
                rsp.AppendLine("-target:library");
                rsp.AppendLine("-nostdlib+");
                rsp.AppendLine("-noconfig");
                rsp.AppendLine("-unsafe+");
                rsp.AppendLine("-optimize-");
                rsp.AppendLine("-debug:portable");
                rsp.AppendLine("-langversion:latest");
                rsp.AppendLine("-nowarn:0436,0169,0649,0414,1701,1702");
                rsp.AppendLine($"-out:\"{outDll}\"");
                rsp.AppendLine($"-pdb:\"{outPdb}\"");

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

                rsp.AppendLine($"\"{srcFile}\"");
                File.WriteAllText(rspFile, rsp.ToString(), Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = dotnetExe,
                    Arguments = $"exec \"{cscDll}\" @\"{rspFile}\"",
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

                    ParseCscOutput(stdout + "\n" + stderr, diagnostics);

                    if (proc.ExitCode != 0 || !File.Exists(outDll))
                    {
                        return false;
                    }

                    byte[] peBytes = File.ReadAllBytes(outDll);
                    byte[] pdbBytes = File.Exists(outPdb) ? File.ReadAllBytes(outPdb) : null;
                    compiledAssembly = pdbBytes != null
                        ? System.Reflection.Assembly.Load(peBytes, pdbBytes)
                        : System.Reflection.Assembly.Load(peBytes);

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
                TryDeleteFile(srcFile);
                TryDeleteFile(rspFile);
                TryDeleteFile(outDll);
                TryDeleteFile(outPdb);
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
            referencePaths = new List<string>(128);
            defineSymbols = new List<string>(64);
            projectAssemblyNames = new List<string>(32);

            var definesSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string def in EditorUserBuildSettings.activeScriptCompilationDefines)
            {
                definesSet.Add(def);
            }
            definesSet.Add("UNITY_EDITOR");
            definesSet.Add("DEBUG");
            definesSet.Add("TRACE");

            // Check Unity CompilationPipeline for the owning assembly's exact defines & references
            try
            {
                UnityEditor.Compilation.Assembly[] unityAssemblies = CompilationPipeline.GetAssemblies();
                string normalizedScript = !string.IsNullOrEmpty(scriptFilePath)
                    ? Path.GetFullPath(scriptFilePath).Replace('\\', '/')
                    : null;

                foreach (UnityEditor.Compilation.Assembly uAsm in unityAssemblies)
                {
                    if (!string.IsNullOrEmpty(uAsm.name))
                    {
                        projectAssemblyNames.Add(uAsm.name);
                    }

                    if (normalizedScript != null && uAsm.sourceFiles != null)
                    {
                        foreach (string src in uAsm.sourceFiles)
                        {
                            string fullSrc = Path.GetFullPath(src).Replace('\\', '/');
                            if (string.Equals(fullSrc, normalizedScript, StringComparison.OrdinalIgnoreCase))
                            {
                                if (uAsm.defines != null)
                                {
                                    foreach (string d in uAsm.defines)
                                    {
                                        definesSet.Add(d);
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore if CompilationPipeline is unavailable
            }

            if (!projectAssemblyNames.Contains("Assembly-CSharp"))
            {
                projectAssemblyNames.Add("Assembly-CSharp");
            }

            defineSymbols.AddRange(definesSet);

            // Collect loaded AppDomain assemblies AND CompilationPipeline output assemblies, deduplicating by file name
            var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collectedRefs = referencePaths;

            void TryAddReferencePath(string candidatePath)
            {
                if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
                {
                    return;
                }

                string full = Path.GetFullPath(candidatePath);
                string fileName = Path.GetFileName(full);

                if (fileName.StartsWith("NyksHotReload_Patch_", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (seenFileNames.Contains(fileName) || seenFullPaths.Contains(full))
                {
                    return;
                }

                seenFileNames.Add(fileName);
                seenFullPaths.Add(full);
                collectedRefs.Add(full);
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

        private static void ParseCscOutput(string output, List<CompilerDiagnosticEntry> diagnostics)
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
                int parenOpen = line.IndexOf('(');
                int parenClose = line.IndexOf(')', parenOpen + 1);
                if (parenOpen > 0 && parenClose > parenOpen)
                {
                    string coords = line.Substring(parenOpen + 1, parenClose - parenOpen - 1);
                    string[] parts = coords.Split(',');
                    if (parts.Length >= 1) int.TryParse(parts[0], out lineNum);
                    if (parts.Length >= 2) int.TryParse(parts[1], out colNum);
                }

                int colonIdx = line.IndexOf(": error ", StringComparison.Ordinal);
                if (colonIdx < 0) colonIdx = line.IndexOf(": warning ", StringComparison.Ordinal);

                string messagePart = colonIdx >= 0 ? line.Substring(colonIdx + 2) : line;

                diagnostics.Add(new CompilerDiagnosticEntry
                {
                    Id = isError ? "ERROR" : "WARN",
                    Message = messagePart,
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

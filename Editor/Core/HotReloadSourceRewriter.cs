// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Nyks.HotReload.Editor.State;

namespace Nyks.HotReload.Editor.Core
{
    /// <summary>
    /// Result of rewriting a C# source file prior to dynamic Hot Reload compilation.
    /// </summary>
    public sealed class SourceRewriteResult
    {
        public string RewrittenSource { get; set; }
        public List<DynamicFieldDescriptor> AddedFields { get; } = new List<DynamicFieldDescriptor>();
        public List<string> DeclaredTypeNames { get; } = new List<string>();
    }

    /// <summary>
    /// Pre-processes modified C# source files before compilation:
    /// 1. Injects [IgnoresAccessChecksTo] attributes for all project assemblies so private/internal members remain accessible.
    /// 2. Detects newly added fields that do not exist on the live runtime Type and converts them into
    ///    zero-layout-footprint properties backed by <see cref="HotReloadStateStore"/> so existing field offsets never shift.
    /// </summary>
    public static class HotReloadSourceRewriter
    {
        private static readonly Regex _namespaceRegex = new Regex(
            @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex _classDeclRegex = new Regex(
            @"\b(?:public|internal|private|protected|sealed|abstract|partial|static|\s)*\bclass\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

        // Matches single-line instance/static field declarations inside a class body.
        private static readonly Regex _fieldLineRegex = new Regex(
            @"^(?<indent>\s*)(?<attrs>(?:\[[^\]]+\]\s*)*)(?<mods>(?:(?:public|private|protected|internal|static|readonly|volatile|new)\s+)+)(?<type>[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?\s]*?)\s+(?<decls>[A-Za-z_][A-Za-z0-9_]*\s*(?:=\s*[^;]+)?(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*\s*(?:=\s*[^;]+)?)*)\s*;\s*(?<comment>\/\/.*)?$",
            RegexOptions.Compiled);

        /// <summary>
        /// Rewrites the C# source code to preserve memory layout and bypass cross-assembly visibility checks.
        /// </summary>
        public static SourceRewriteResult Rewrite(
            string sourceCode,
            IReadOnlyCollection<string> targetAssemblyNames,
            Func<string, Type> runtimeTypeResolver)
        {
            var result = new SourceRewriteResult();

            string detectedNamespace = null;
            Match nsMatch = _namespaceRegex.Match(sourceCode);
            if (nsMatch.Success)
            {
                detectedNamespace = nsMatch.Groups[1].Value.Trim();
            }

            foreach (Match classMatch in _classDeclRegex.Matches(sourceCode))
            {
                string shortClassName = classMatch.Groups[1].Value.Trim();
                string fullTypeName = string.IsNullOrEmpty(detectedNamespace)
                    ? shortClassName
                    : $"{detectedNamespace}.{shortClassName}";

                if (!result.DeclaredTypeNames.Contains(fullTypeName))
                {
                    result.DeclaredTypeNames.Add(fullTypeName);
                }
            }

            // Build lookup of existing fields for the primary declared type so we know if the user added a new field.
            Type primaryOriginalType = null;
            HashSet<string> existingFieldNames = null;
            string primaryTypeFullName = null;

            if (runtimeTypeResolver != null && result.DeclaredTypeNames.Count > 0)
            {
                foreach (string candidateName in result.DeclaredTypeNames)
                {
                    Type resolved = runtimeTypeResolver(candidateName);
                    if (resolved != null)
                    {
                        primaryOriginalType = resolved;
                        primaryTypeFullName = resolved.FullName ?? candidateName;
                        existingFieldNames = GetExistingFieldNames(resolved);
                        break;
                    }
                }
            }

            string[] lines = sourceCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder(sourceCode.Length + 4096);

            // 1. Emit [assembly: IgnoresAccessChecksTo("...")] before any namespace/type declarations,
            //    immediately after top-level using directives and #define/#pragma directives.
            int headerInsertIndex = FindHeaderInsertionLine(lines);

            int braceDepth = 0;
            bool insideClass = false;
            int classBraceDepth = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                if (i == headerInsertIndex)
                {
                    EmitAccessChecksAttributes(sb, targetAssemblyNames);
                }

                string line = lines[i];
                string trimmed = line.Trim();

                // Track class scope depth so we only inspect class-level field declarations (not local variables inside methods).
                if (!insideClass && _classDeclRegex.IsMatch(line) && !trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    insideClass = true;
                    classBraceDepth = braceDepth + CountChar(line, '{') - CountChar(line, '}');
                    if (!line.Contains("{"))
                    {
                        classBraceDepth = braceDepth + 1;
                    }
                }

                int openBraces = CountChar(line, '{');
                int closeBraces = CountChar(line, '}');

                bool isAtClassFieldLevel = insideClass && (braceDepth == classBraceDepth) && openBraces == 0 && closeBraces == 0;

                if (isAtClassFieldLevel && primaryOriginalType != null && existingFieldNames != null)
                {
                    if (TryRewriteNewFieldLine(
                        line,
                        primaryTypeFullName,
                        existingFieldNames,
                        result.AddedFields,
                        out string rewrittenLine))
                    {
                        sb.AppendLine(rewrittenLine);
                        braceDepth += openBraces - closeBraces;
                        continue;
                    }
                }

                sb.AppendLine(line);
                braceDepth += openBraces - closeBraces;

                if (insideClass && braceDepth < classBraceDepth && closeBraces > 0)
                {
                    insideClass = false;
                }
            }

            if (headerInsertIndex >= lines.Length)
            {
                EmitAccessChecksAttributes(sb, targetAssemblyNames);
            }

            // Append the internal IgnoresAccessChecksToAttribute definition at the very end of the compilation unit.
            sb.AppendLine();
            sb.AppendLine("namespace System.Runtime.CompilerServices");
            sb.AppendLine("{");
            sb.AppendLine("    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]");
            sb.AppendLine("    internal sealed class IgnoresAccessChecksToAttribute : System.Attribute");
            sb.AppendLine("    {");
            sb.AppendLine("        public IgnoresAccessChecksToAttribute(string assemblyName) { AssemblyName = assemblyName; }");
            sb.AppendLine("        public string AssemblyName { get; }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            result.RewrittenSource = sb.ToString();
            return result;
        }

        private static bool TryRewriteNewFieldLine(
            string line,
            string ownerTypeFullName,
            HashSet<string> existingFields,
            List<DynamicFieldDescriptor> addedFieldsOut,
            out string rewrittenLine)
        {
            rewrittenLine = null;
            string trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed) ||
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.Contains(" event ") ||
                trimmed.Contains(" const ") ||
                trimmed.Contains("(") && !trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return false;
            }

            Match match = _fieldLineRegex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            string indent = match.Groups["indent"].Value;
            string attrs = match.Groups["attrs"].Value;
            string mods = match.Groups["mods"].Value.Trim();
            string typeName = match.Groups["type"].Value.Trim();
            string decls = match.Groups["decls"].Value.Trim();

            if (string.Equals(typeName, "void", StringComparison.Ordinal) ||
                string.Equals(typeName, "class", StringComparison.Ordinal) ||
                string.Equals(typeName, "struct", StringComparison.Ordinal) ||
                string.Equals(typeName, "enum", StringComparison.Ordinal) ||
                string.Equals(typeName, "interface", StringComparison.Ordinal) ||
                string.Equals(typeName, "return", StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = SplitTopLevelCommas(decls);
            bool anyNewField = false;
            bool allExisting = true;

            foreach (string part in parts)
            {
                string varName = ExtractVariableName(part);
                if (string.IsNullOrEmpty(varName))
                {
                    return false;
                }

                if (existingFields.Contains(varName))
                {
                    // Existing field on the original Type
                }
                else
                {
                    anyNewField = true;
                    allExisting = false;
                }
            }

            if (allExisting || !anyNewField)
            {
                return false;
            }

            bool isStatic = mods.Contains("static");
            bool isPublicOrSerialized = mods.Contains("public") || attrs.Contains("SerializeField");
            string cleanMods = mods.Replace("readonly", "").Replace("volatile", "").Trim();

            var propBuilder = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                string varName = ExtractVariableName(part);
                string defaultExpr = ExtractInitializerExpression(part, typeName);

                if (existingFields.Contains(varName))
                {
                    // Preserve existing field intact so original layout never changes
                    propBuilder.Append($"{indent}{attrs}{mods} {typeName} {part};");
                }
                else
                {
                    string fieldKey = $"{ownerTypeFullName}.{varName}";
                    if (isStatic)
                    {
                        propBuilder.Append(
                            $"{indent}{cleanMods} {typeName} {varName} {{ " +
                            $"get => global::Nyks.HotReload.Editor.State.HotReloadStateStore.GetStatic<{typeName}>(\"{fieldKey}\", {defaultExpr}); " +
                            $"set => global::Nyks.HotReload.Editor.State.HotReloadStateStore.SetStatic<{typeName}>(\"{fieldKey}\", value); }}");
                    }
                    else
                    {
                        propBuilder.Append(
                            $"{indent}{cleanMods} {typeName} {varName} {{ " +
                            $"get => global::Nyks.HotReload.Editor.State.HotReloadStateStore.Get<{typeName}>(this, \"{fieldKey}\", {defaultExpr}); " +
                            $"set => global::Nyks.HotReload.Editor.State.HotReloadStateStore.Set<{typeName}>(this, \"{fieldKey}\", value); }}");
                    }

                    var descriptor = new DynamicFieldDescriptor
                    {
                        OwnerTypeFullName = ownerTypeFullName,
                        FieldName = varName,
                        FieldKey = fieldKey,
                        FieldType = ResolvePrimitiveOrUnityType(typeName),
                        DefaultValue = EvaluateSimpleLiteral(defaultExpr, typeName),
                        IsStatic = isStatic,
                        IsPublicOrSerialized = isPublicOrSerialized
                    };
                    addedFieldsOut.Add(descriptor);
                    HotReloadStateStore.RegisterDynamicField(descriptor);
                }

                if (i < parts.Length - 1)
                {
                    propBuilder.Append(" ");
                }
            }

            rewrittenLine = propBuilder.ToString();
            return true;
        }

        private static HashSet<string> GetExistingFieldNames(Type originalType)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            FieldInfo[] fields = originalType.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            for (int i = 0; i < fields.Length; i++)
            {
                set.Add(fields[i].Name);
            }
            return set;
        }

        private static int FindHeaderInsertionLine(string[] lines)
        {
            int lastUsingOrDefineLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (string.IsNullOrEmpty(trimmed) ||
                    trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal) ||
                    (trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.EndsWith(";", StringComparison.Ordinal)))
                {
                    lastUsingOrDefineLine = i;
                    continue;
                }

                break;
            }

            return lastUsingOrDefineLine + 1;
        }

        private static void EmitAccessChecksAttributes(StringBuilder sb, IReadOnlyCollection<string> assemblyNames)
        {
            if (assemblyNames == null)
            {
                return;
            }

            foreach (string asmName in assemblyNames)
            {
                if (!string.IsNullOrWhiteSpace(asmName))
                {
                    sb.Append("[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"").Append(asmName).Append("\")] ");
                }
            }
            sb.AppendLine();
        }

        private static int CountChar(string line, char target)
        {
            int count = 0;
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/' && !inString)
                {
                    break;
                }
                if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                {
                    inString = !inString;
                }
                if (!inString && c == target)
                {
                    count++;
                }
            }
            return count;
        }

        private static string[] SplitTopLevelCommas(string input)
        {
            var list = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    list.Add(input.Substring(start, i - start));
                    start = i + 1;
                }
            }
            list.Add(input.Substring(start));
            return list.ToArray();
        }

        private static string ExtractVariableName(string declPart)
        {
            int eqIdx = declPart.IndexOf('=');
            string nameSection = eqIdx >= 0 ? declPart.Substring(0, eqIdx).Trim() : declPart.Trim();
            return nameSection;
        }

        private static string ExtractInitializerExpression(string declPart, string typeName)
        {
            int eqIdx = declPart.IndexOf('=');
            if (eqIdx >= 0 && eqIdx + 1 < declPart.Length)
            {
                return declPart.Substring(eqIdx + 1).Trim();
            }
            return $"default({typeName})";
        }

        private static Type ResolvePrimitiveOrUnityType(string typeName)
        {
            switch (typeName)
            {
                case "float":
                case "Single":
                    return typeof(float);
                case "int":
                case "Int32":
                    return typeof(int);
                case "bool":
                case "Boolean":
                    return typeof(bool);
                case "string":
                case "String":
                    return typeof(string);
                case "double":
                case "Double":
                    return typeof(double);
                case "Vector2":
                    return typeof(UnityEngine.Vector2);
                case "Vector3":
                    return typeof(UnityEngine.Vector3);
                case "Color":
                    return typeof(UnityEngine.Color);
                case "KeyCode":
                    return typeof(UnityEngine.KeyCode);
                default:
                    return typeof(object);
            }
        }

        private static object EvaluateSimpleLiteral(string expr, string typeName)
        {
            string cleaned = expr.Trim();
            if (typeName == "float" || typeName == "Single")
            {
                cleaned = cleaned.TrimEnd('f', 'F');
                if (float.TryParse(cleaned, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    return f;
                return 0f;
            }
            if (typeName == "int" || typeName == "Int32")
            {
                if (int.TryParse(cleaned, out int i))
                    return i;
                return 0;
            }
            if (typeName == "bool" || typeName == "Boolean")
            {
                if (bool.TryParse(cleaned, out bool b))
                    return b;
                return false;
            }
            if (typeName == "string" || typeName == "String")
            {
                if (cleaned.StartsWith("\"", StringComparison.Ordinal) && cleaned.EndsWith("\"", StringComparison.Ordinal) && cleaned.Length >= 2)
                    return cleaned.Substring(1, cleaned.Length - 2);
                return string.Empty;
            }
            return null;
        }
    }
}

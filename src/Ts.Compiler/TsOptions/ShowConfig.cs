// Ported from tsgo internal/tsoptions/showconfig.go (390 LOC) — the --showConfig face:
// ConvertToTSConfig / serializeCompilerOptions / addImpliedOptions.
//
// PORT-NOTE: Go walks core.CompilerOptions with reflection (showconfig.go:169-185) and
// marshals the TSConfig struct with go-json-experiment (showconfig.go:375). Reflection is
// AOT-banned (plan/07 §1.1), so the field walk goes through the hand-written per-field table
// CompilerOptionsFields (the same "统一供给" table standing in for reflection in
// mergeCompilerOptions/optionsHaveChanges), and the marshal goes through a hand-written
// Utf8JsonWriter emitter (MarshalIndentWrite) — the same substitution plan/10 §2.3.2
// (ordered_map JSON face) and plan/07 §4 prescribe. Emission fidelity notes:
//   - field order  = CompilerOptions declaration order (= Go struct order = tsgo's output order);
//   - implied options are appended at the end in impliedOptions order;
//   - indent is 4 spaces, `omitzero` fields are dropped when nil, and string escaping is
//     "relaxed" (JavaScriptEncoder.UnsafeRelaxedJsonEscaping ≈ go-json-experiment defaults:
//     no HTML escaping, raw UTF-8, no trailing newline after the closing brace).

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;
using Ts.TsPath;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.TsOptions;

// showconfig.go:13-19 — computeFn wraps a typed getter method so it can be stored in an
// impliedOption's compute field (which has type func(*core.CompilerOptions) any).
// PORT-NOTE: Go needs the generic wrapper to erase T → any; C# lambdas box to object?
// directly, so the wrapper collapses into the table's lambdas below.

// showconfig.go:21-30 — impliedOption describes a compiler option whose effective value can
// be derived from other options. This mirrors TypeScript's computedOptions concept used in
// convertToTSConfig.
public sealed class ImpliedOption
{
    // showconfig.go:24-26 — name is the Go struct field name of the CompilerOptions field
    // (e.g., "Module").
    public readonly string Name;

    // showconfig.go:27-29 — dependencies lists the Go struct field names that this option
    // depends on.
    public readonly string[] Dependencies;

    // showconfig.go:29-31 — compute returns the effective value of this option given
    // compiler options.
    public readonly Func<CompilerOptions, object?> Compute;

    public ImpliedOption(string name, string[] dependencies, Func<CompilerOptions, object?> compute)
    {
        Name = name;
        Dependencies = dependencies;
        Compute = compute;
    }
}

public static class ShowConfig
{
    // showconfig.go:32-50 — impliedOptions lists the compiler options that may be implied by
    // other options, mirroring TypeScript's computedOptions used in convertToTSConfig.
    // Each compute function delegates directly to an existing core.CompilerOptions getter.
    internal static readonly ImpliedOption[] impliedOptions =
    [
        new ImpliedOption("Module", ["Target"], static o => o.GetEmitModuleKind()),
        new ImpliedOption("ModuleResolution", ["Module", "Target"], static o => o.GetModuleResolutionKind()),
        new ImpliedOption("ModuleDetection", ["Module", "Target"], static o => o.GetEmitModuleDetectionKind()),
        new ImpliedOption("IsolatedModules", ["VerbatimModuleSyntax"], static o => o.GetIsolatedModules()),
        new ImpliedOption("PreserveConstEnums", ["IsolatedModules", "VerbatimModuleSyntax"], static o => o.ShouldPreserveConstEnums()),
        new ImpliedOption("Declaration", ["Composite"], static o => o.GetEmitDeclarations()),
        new ImpliedOption("DeclarationMap", ["Declaration", "Composite"], static o => o.GetAreDeclarationMapsEnabled()),
        new ImpliedOption("Incremental", ["Composite"], static o => o.IsIncremental()),
        new ImpliedOption("UseDefineForClassFields", ["Target", "Module"], static o => o.GetUseDefineForClassFields()),
        new ImpliedOption("ResolvePackageJsonExports", ["ModuleResolution", "Module", "Target"], static o => o.GetResolvePackageJsonExports()),
        new ImpliedOption("ResolvePackageJsonImports", ["ModuleResolution", "ResolvePackageJsonExports", "Module", "Target"], static o => o.GetResolvePackageJsonImports()),
        new ImpliedOption("ResolveJsonModule", ["ModuleResolution", "Module", "Target"], static o => o.GetResolveJsonModule()),
        new ImpliedOption("AllowJs", ["CheckJs"], static o => o.GetAllowJS()),
        new ImpliedOption("AllowImportingTsExtensions", ["RewriteRelativeImportExtensions"], static o => o.GetAllowImportingTsExtensions()),
    ];

    // TSConfig represents the output structure for --showConfig (showconfig.go:52-60).
    // Field order = Go struct field order = emitted JSON key order; Go's `omitzero` tags
    // become null checks in MarshalIndentWrite (References/Files/Include/Exclude/
    // CompileOnSave), while CompilerOptions has no omitzero (nil → JSON null).
    public sealed class TSConfig
    {
        public OrderedMap<string, object?>? CompilerOptions; // json: "compilerOptions"
        public List<object?>? References;                    // json: "references,omitzero"
        public string[]? Files;                              // json: "files,omitzero"
        public string[]? Include;                            // json: "include,omitzero"
        public string[]? Exclude;                            // json: "exclude,omitzero"
        public bool? CompileOnSave;                          // json: "compileOnSave,omitzero"
    }

    // showconfig.go:62-138 — ConvertToTSConfig generates a complete tsconfig representation
    // for --showConfig output, matching the behavior of TypeScript's convertToTSConfig
    // function.
    public static TSConfig ConvertToTSConfig(ParsedCommandLine configParseResult, string configFileName)
    {
        if (configFileName == "")
        {
            configFileName = "tsconfig.json";
        }
        var normalizedConfigPath = TsPathApi.GetNormalizedAbsolutePath(configFileName, configParseResult.GetCurrentDirectory());
        var comparePathsOptions = new ComparePathsOptions
        {
            CurrentDirectory = configParseResult.GetCurrentDirectory(),
            UseCaseSensitiveFileNames = configParseResult.UseCaseSensitiveFileNames(),
        };

        // Build the list of all resolved files as relative paths from the config file.
        var files = new List<string>();
        foreach (var f in configParseResult.FileNames() ?? [])
        {
            var normalizedFilePath = TsPathApi.GetNormalizedAbsolutePath(f, configParseResult.GetCurrentDirectory());
            var relativePath = TsPathApi.GetRelativePathFromFile(normalizedConfigPath, normalizedFilePath, comparePathsOptions);
            files.Add(relativePath);
        }

        // Serialize compiler options
        var optionMap = serializeCompilerOptions(configParseResult.CompilerOptions()!, normalizedConfigPath, comparePathsOptions);

        // Remove command-line-only options from the output
        foreach (var name in new[]
        {
            "showConfig", "configFile", "configFilePath", "help", "init",
            "listFilesOnly", "listEmittedFiles", "project", "build", "version",
        })
        {
            optionMap.TryDelete(name, out _);
        }

        // Add implied compiler options (options that are derived from explicitly set options,
        // such as moduleResolution implied by module, or useDefineForClassFields implied by target).
        // This mirrors TypeScript's convertToTSConfig computedOptions logic.
        addImpliedOptions(optionMap, configParseResult.CompilerOptions()!, normalizedConfigPath, comparePathsOptions);

        var config = new TSConfig
        {
            CompilerOptions = optionMap,
        };

        // Add references
        var refs = configParseResult.ProjectReferences();
        if (refs != null && refs.Length > 0)
        {
            var references = new List<object?>();
            foreach (var r in refs)
            {
                var @ref = new OrderedMap<string, object?>();
                @ref.Set("path", r.OriginalPath);
                if (r.Circular)
                {
                    @ref.Set("circular", true);
                }
                references.Add(@ref);
            }
            config.References = references;
        }

        // Add files
        if (files.Count > 0)
        {
            config.Files = files.ToArray();
        }

        // Add include/exclude from configFileSpecs
        if (configParseResult.ConfigFile != null && configParseResult.ConfigFile.configFileSpecs != null)
        {
            var specs = configParseResult.ConfigFile.configFileSpecs;
            var include = filterSameAsDefaultInclude(specs.validatedIncludeSpecs);
            if (include != null && include.Length > 0)
            {
                config.Include = include;
            }
            config.Exclude = specs.validatedExcludeSpecs;
        }

        // Add compileOnSave
        if (configParseResult.CompileOnSave != null && configParseResult.CompileOnSave.Value)
        {
            config.CompileOnSave = true;
        }

        return config;
    }

    // showconfig.go:140-149 — filterSameAsDefaultInclude returns nil if specs is the default
    // include spec ["**/*"].
    private static string[]? filterSameAsDefaultInclude(string[]? specs)
    {
        if (specs == null || specs.Length == 0)
        {
            return null;
        }
        if (specs.Length == 1 && specs[0] == TsConfigParsing.defaultIncludeSpec)
        {
            return null;
        }
        return specs;
    }

    // showconfig.go:151-160 — getNameOfCompilerOptionValue returns the string key for a given
    // enum value by searching the option's enum map.
    private static string getNameOfCompilerOptionValue(object? value, OrderedMap<string, object?> enumMap)
    {
        // map-iter: 已排序 —— enum maps are insertion-ordered; first key wins (Go interface ==).
        foreach (var (k, v) in enumMap.Entries())
        {
            if (Equals(v, value))
            {
                return k;
            }
        }
        return "";
    }

    // showconfig.go:162-276 — serializeCompilerOptions converts CompilerOptions to an ordered
    // map with string names as keys and serialized values (enums as strings, paths as
    // relative paths, etc.) matching the output of tsc --showConfig.
    //
    // PORT-NOTE: the Go reflect field walk (showconfig.go:169-185) becomes the
    // CompilerOptionsFields table, whose order mirrors the Go struct declaration order —
    // that order IS the emitted compilerOptions key order.
    private static OrderedMap<string, object?> serializeCompilerOptions(CompilerOptions options, string configFilePath, ComparePathsOptions comparePathsOptions)
    {
        var result = new OrderedMap<string, object?>(32);
        var configDir = TsPathApi.GetDirectoryPath(configFilePath);

        foreach (var field in CompilerOptionsFields.Fields)
        {
            var optionDecl = TsConfigParsing.CommandLineCompilerOptionsMap.Get(field.Name);
            if (optionDecl == null)
            {
                continue;
            }

            // Skip command-line-only and output formatting options
            if (ReferenceEquals(optionDecl.Category, global::Ts.Diagnostics.Diagnostics.Command_line_Options) || ReferenceEquals(optionDecl.Category, global::Ts.Diagnostics.Diagnostics.Output_Formatting))
            {
                continue;
            }

            var value = field.Get(options);

            // Skip zero values (unset options)
            if (field.IsZero(options))
            {
                continue;
            }

            var name = optionDecl.Name;

            var enumMap = optionDecl.EnumMap();
            if (enumMap != null)
            {
                // Enum option - convert numeric value to string name
                var serialized = serializeEnumValue(value, enumMap);
                if (serialized != "")
                {
                    result.Set(name, serialized);
                }
                continue;
            }

            switch (optionDecl.Kind)
            {
                case CommandLineOptionKind.CommandLineOptionTypeListOrElement:
                    global::Ts.Core.Debug.Assert(false, "listOrElement option should not reach serialization");
                    break;

                case CommandLineOptionKind.CommandLineOptionTypeList:
                    {
                        var elem = optionDecl.Elements();
                        if (elem != null && elem.IsFilePath)
                        {
                            // List of file paths - make relative
                            if (value is string[] strs)
                            {
                                var relPaths = new string[strs.Length];
                                for (var j = 0; j < strs.Length; j++)
                                {
                                    var absPath = TsPathApi.GetNormalizedAbsolutePath(strs[j], configDir);
                                    relPaths[j] = TsPathApi.GetRelativePathFromFile(configFilePath, absPath, comparePathsOptions);
                                }
                                result.Set(name, relPaths);
                                continue;
                            }
                        }
                        if (elem != null && elem.EnumMap() != null)
                        {
                            // List of enum values (e.g., lib)
                            var elemMap = elem.EnumMap()!;
                            if (value is string[] strs)
                            {
                                var serialized = new List<string>(strs.Length);
                                foreach (var s in strs)
                                {
                                    // lib values are already stored as the d.ts filename, need to find original key
                                    var found = getNameOfCompilerOptionValue(s, elemMap);
                                    if (found != "")
                                    {
                                        serialized.Add(found);
                                    }
                                    else
                                    {
                                        serialized.Add(s);
                                    }
                                }
                                result.Set(name, serialized.ToArray());
                                continue;
                            }
                        }
                        result.Set(name, value);
                        break;
                    }

                case CommandLineOptionKind.CommandLineOptionTypeString:
                    if (optionDecl.IsFilePath)
                    {
                        // File path option - make relative to config
                        if (value is string s && s != "")
                        {
                            var absPath = TsPathApi.GetNormalizedAbsolutePath(s, configDir);
                            result.Set(name, TsPathApi.GetRelativePathFromFile(configFilePath, absPath, comparePathsOptions));
                            continue;
                        }
                    }
                    result.Set(name, value);
                    break;

                case CommandLineOptionKind.CommandLineOptionTypeBoolean:
                    if (value is Tristate t)
                    {
                        if (t.IsTrue())
                        {
                            result.Set(name, true);
                        }
                        else if (t.IsFalse())
                        {
                            result.Set(name, false);
                        }
                    }
                    else
                    {
                        result.Set(name, value);
                    }
                    break;

                case CommandLineOptionKind.CommandLineOptionTypeNumber:
                    result.Set(name, value);
                    break;

                default:
                    result.Set(name, value);
                    break;
            }
        }

        return result;
    }

    // showconfig.go:278-295 — serializeEnumValue converts an enum field value to its
    // corresponding string key using the option's enum map. It handles int32-based enum
    // types.
    private static string serializeEnumValue(object? value, OrderedMap<string, object?> enumMap)
    {
        // The enum maps store values as core.ModuleKind, core.ScriptTarget, etc.
        // But those are all int32 underneath. We need to compare by the underlying int32 value.
        if (value is Enum || value is int || value is long || value is short || value is sbyte || value is byte || value is uint || value is ushort)
        {
            var intVal = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            // map-iter: 已排序 —— first matching key wins, mirroring Go's entry scan.
            foreach (var (k, v) in enumMap.Entries())
            {
                if (v is Enum || v is int || v is long || v is short || v is sbyte || v is byte || v is uint || v is ushort)
                {
                    var ev = Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
                    if (ev == intVal)
                    {
                        return k;
                    }
                }
            }
        }
        // Fallback: direct comparison
        return getNameOfCompilerOptionValue(value, enumMap);
    }

    // showconfig.go:297-348 — addImpliedOptions adds compiler options that are implied by
    // other explicitly-set options, mirroring TypeScript's convertToTSConfig behavior for
    // computedOptions. For example, when module: nodenext is set, moduleResolution: nodenext
    // is implied.
    private static void addImpliedOptions(
        OrderedMap<string, object?> optionMap,
        CompilerOptions options,
        string configFilePath,
        ComparePathsOptions comparePathsOptions)
    {
        _ = configFilePath;     // Go: unused `_ string`
        _ = comparePathsOptions; // Go: unused `_ tspath.ComparePathsOptions`

        // Build the set of explicitly provided option JSON names (e.g., "module", "target").
        // map-iter: 已排序 —— Keys() is insertion-ordered; only membership is consumed.
        var provided = new HashSet<string>(optionMap.Size());
        foreach (var k in optionMap.Keys())
        {
            provided.Add(k);
        }

        var defaultOpts = new CompilerOptions();

        // map-iter: 已排序 —— fixed declaration-ordered table.
        foreach (var entry in impliedOptions)
        {
            // Get the option declaration for this implied option (using case-insensitive lookup).
            var optionDecl = TsConfigParsing.CommandLineCompilerOptionsMap.Get(entry.Name);
            if (optionDecl == null)
            {
                continue;
            }

            // Skip if this option is already explicitly provided.
            if (provided.Contains(optionDecl.Name))
            {
                continue;
            }

            // Check if any direct dependency is in the provided set.
            // This mirrors TypeScript's optionDependsOn check.
            if (!anyDependencyProvided(entry.Dependencies, provided))
            {
                continue;
            }

            // Compute the effective value with current options and the default value with empty options.
            var implied = entry.Compute(options);
            var defaultVal = entry.Compute(defaultOpts);

            // If the implied value equals the default, this option doesn't add useful information.
            if (CompilerOptionsFields.DeepEqual(implied, defaultVal))
            {
                continue;
            }

            // Serialize the implied value and add it to the option map.
            var serialized = serializeImpliedOptionValue(optionDecl, implied);
            if (serialized == null)
            {
                continue;
            }
            optionMap.Set(optionDecl.Name, serialized);
        }
    }

    // showconfig.go:350-360 — anyDependencyProvided returns true if any of the given
    // dependency names (using Go field names like "Target") corresponds to an option in the
    // provided set.
    private static bool anyDependencyProvided(string[] dependencies, HashSet<string> provided)
    {
        foreach (var dep in dependencies)
        {
            var depDecl = TsConfigParsing.CommandLineCompilerOptionsMap.Get(dep);
            if (depDecl != null && provided.Contains(depDecl.Name))
            {
                return true;
            }
        }
        return false;
    }

    // showconfig.go:362-389 — serializeImpliedOptionValue converts a computed implied option
    // value to its serializable form. For enum options, it converts numeric values to their
    // string names. For boolean options, it returns the bool directly.
    private static object? serializeImpliedOptionValue(CommandLineOption optionDecl, object? value)
    {
        if (value == null)
        {
            return null;
        }
        var enumMap = optionDecl.EnumMap();
        if (enumMap != null)
        {
            var s = serializeEnumValue(value, enumMap);
            if (s != "")
            {
                return s;
            }
            return null;
        }
        switch (value)
        {
            case bool v:
                return v;
            case Tristate v:
                if (v.IsTrue())
                {
                    return true;
                }
                else if (v.IsFalse())
                {
                    return false;
                }
                return null;
        }
        return value;
    }

    // MarshalIndentWrite reproduces Go `json.MarshalIndentWrite(w, tsConfig, "", "    ")`
    // (showconfig.go:375, consumed by execute/tsc.go showConfig). Reflection marshal is
    // AOT-banned (plan/07 §1.1); the value shapes reaching here are closed:
    // string | bool | int? | string[] | OrderedMap<string, object?> (nested reference maps)
    // | OrderedMap<string, string[]> (paths), so a hand-written Utf8JsonWriter emitter keeps
    // byte-shape fidelity (4-space indent, omitzero drops, no trailing newline —
    // go-json-experiment appends none; HTML-sensitive characters are not escaped).
    public static void MarshalIndentWrite(System.IO.TextWriter w, TSConfig config)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
        {
            Indented = true,
            IndentSize = 4,
            IndentCharacter = ' ',
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteTSConfig(writer, config);
        }
        w.Write(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    // TSConfig field order = emitted key order (compilerOptions, references, files, include,
    // exclude, compileOnSave); omitzero fields are skipped when null.
    private static void WriteTSConfig(System.Text.Json.Utf8JsonWriter w, TSConfig config)
    {
        w.WriteStartObject();

        w.WritePropertyName("compilerOptions");
        if (config.CompilerOptions != null)
        {
            WriteOptionsMap(w, config.CompilerOptions);
        }
        else
        {
            w.WriteNullValue();
        }

        if (config.References != null)
        {
            w.WritePropertyName("references");
            w.WriteStartArray();
            foreach (var @ref in config.References)
            {
                WriteValue(w, @ref);
            }
            w.WriteEndArray();
        }

        if (config.Files != null)
        {
            w.WritePropertyName("files");
            w.WriteStartArray();
            foreach (var file in config.Files)
            {
                w.WriteStringValue(file);
            }
            w.WriteEndArray();
        }

        if (config.Include != null)
        {
            w.WritePropertyName("include");
            w.WriteStartArray();
            foreach (var include in config.Include)
            {
                w.WriteStringValue(include);
            }
            w.WriteEndArray();
        }

        if (config.Exclude != null)
        {
            w.WritePropertyName("exclude");
            w.WriteStartArray();
            foreach (var exclude in config.Exclude)
            {
                w.WriteStringValue(exclude);
            }
            w.WriteEndArray();
        }

        if (config.CompileOnSave != null)
        {
            w.WritePropertyName("compileOnSave");
            w.WriteBooleanValue(config.CompileOnSave.Value);
        }

        w.WriteEndObject();
    }

    private static void WriteOptionsMap(System.Text.Json.Utf8JsonWriter w, OrderedMap<string, object?> options)
    {
        w.WriteStartObject();
        // map-iter: 已排序 —— insertion order is the CompilerOptions declaration order +
        // appended implied options; that order is the contract.
        foreach (var (key, value) in options.Entries())
        {
            w.WritePropertyName(key);
            WriteValue(w, value);
        }
        w.WriteEndObject();
    }

    private static void WriteValue(System.Text.Json.Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                // Go *int (MaxNodeModuleJsDepth) boxes to a plain int here; null reaches
                // `case null` above.
                w.WriteNumberValue(i);
                break;
            case string[] arr:
                w.WriteStartArray();
                foreach (var item in arr)
                {
                    w.WriteStringValue(item);
                }
                w.WriteEndArray();
                break;
            case OrderedMap<string, object?> nested:
                WriteOptionsMap(w, nested);
                break;
            case OrderedMap<string, string[]> paths:
                w.WriteStartObject();
                // map-iter: 已排序 —— paths preserves its config-file insertion order.
                foreach (var (key, targets) in paths.Entries())
                {
                    w.WritePropertyName(key);
                    w.WriteStartArray();
                    foreach (var target in targets)
                    {
                        w.WriteStringValue(target);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException(
                    "unsupported --showConfig value type " + value.GetType().ToString());
        }
    }
}

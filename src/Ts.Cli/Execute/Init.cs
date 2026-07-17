// Ported from tsgo internal/execute/tsc/init.go — `tsc --init` tsconfig.json generation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ts.Core;
using Ts.Diagnostics;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Execute;

public static class Init
{
    // init.go:19-31
    public static void WriteConfigFile(ISystem sys, global::Ts.Locale.Locale locale, DiagnosticReporter reportDiagnostic, global::Ts.Collections.OrderedMap<string, object?> options)
    {
        var getCurrentDirectory = sys.GetCurrentDirectory();
        var file = global::Ts.TsPath.TsPath.NormalizePath(global::Ts.TsPath.TsPath.CombinePaths(getCurrentDirectory, "tsconfig.json"));
        if (sys.FS().FileExists(file))
        {
            reportDiagnostic(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.A_tsconfig_json_file_is_already_defined_at_Colon_0, file));
        }
        else
        {
            sys.FS().WriteFile(file, GenerateTSConfig(options, locale));
            var output = new List<string> { "\n" };
            output.AddRange(Help.GetHeader(sys, "Created a new tsconfig.json"));
            output.Add("You can learn more at https://aka.ms/tsconfig");
            output.Add("\n");
            sys.Writer().Write(string.Concat(output));
        }
    }

    // json.Marshal of a string — Utf8JsonWriter with relaxed escaping (AOT-safe, no reflection STJ).
    private static string JsonQuote(string s)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStringValue(s);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // init.go:33-214
    internal static string GenerateTSConfig(global::Ts.Collections.OrderedMap<string, object?> options, global::Ts.Locale.Locale locale)
    {
        const string tab = "  ";
        var result = new List<string>();

        var allSetOptions = new List<string>();
        foreach (var (k, _) in global::Ts.Collections.OrderedMapExtensions.Entries(options))
        {
            if (k != "init" && k != "help" && k != "watch")
            {
                allSetOptions.Add(k);
            }
        }

        void EmitHeader(global::Ts.Diagnostics.Message header) => result.Add(tab + tab + "// " + header.Localize(locale));
        void Newline() => result.Add("");
        void Push(params string[] args) => result.AddRange(args);

        string FormatSingleValue(object? value, global::Ts.Collections.OrderedMap<string, object?>? enumMap)
        {
            if (enumMap != null)
            {
                var found = false;
                foreach (var (k, v) in global::Ts.Collections.OrderedMapExtensions.Entries(enumMap))
                {
                    if (Equals(value, v))
                    {
                        value = k;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    throw new InvalidOperationException($"No matching value of {value}");
                }
            }

            // json.MarshalIndent of a scalar: strings quoted (JSON escaping), numbers/bools plain.
            return value switch
            {
                null => "null",
                bool b => b ? "true" : "false",
                string s => JsonQuote(s),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
        }

        string FormatValueOrArray(string settingName, object? value)
        {
            global::Ts.TsOptions.CommandLineOption? option = null;
            foreach (var decl in global::Ts.TsOptions.DeclsCompiler.OptionsDeclarations)
            {
                if (decl.Name == settingName)
                {
                    option = decl;
                }
            }
            if (option == null)
            {
                throw new InvalidOperationException("No option named " + settingName);
            }

            if (value is IEnumerable enumerable and not string)
            {
                global::Ts.Collections.OrderedMap<string, object?>? enumMap = null;
                if (option.Elements() is { } elemOption)
                {
                    enumMap = elemOption.EnumMap();
                }

                var elems = new List<string>();
                foreach (var item in enumerable)
                {
                    elems.Add(FormatSingleValue(item, enumMap));
                }
                return "[" + string.Join(", ", elems) + "]";
            }
            return FormatSingleValue(value, option.EnumMap());
        }

        // commentedNever: Never comment this out
        // commentedAlways: Always comment this out, even if it's on commandline
        // commentedOptional: Comment out unless it's on commandline
        const int commentedNever = 0;
        const int commentedAlways = 1;
        const int commentedOptional = 2;
        _ = commentedAlways;
        void EmitOption(string setting, object? defaultValue, int commented)
        {
            if (commented > 2)
            {
                throw new InvalidOperationException("should not happen: invalid `commented`, must be a bug.");
            }

            var existingOptionIndex = allSetOptions.IndexOf(setting);
            if (existingOptionIndex >= 0)
            {
                allSetOptions.RemoveAt(existingOptionIndex);
            }

            var comment = commented switch
            {
                commentedAlways => true,
                commentedNever => false,
                _ => !options.Has(setting),
            };

            if (!options.TryGet(setting, out var value))
            {
                value = defaultValue;
            }

            if (comment)
            {
                Push(tab + tab + "// \"" + setting + "\": " + FormatValueOrArray(setting, value) + ",");
            }
            else
            {
                Push(tab + tab + "\"" + setting + "\": " + FormatValueOrArray(setting, value) + ",");
            }
        }

        Push("{");
        Push(tab + "// " + Diag.Visit_https_Colon_Slash_Slashaka_ms_Slashtsconfig_to_read_more_about_this_file.Localize(locale));
        Push(tab + "\"compilerOptions\": {");

        EmitHeader(Diag.File_Layout);
        EmitOption("rootDir", "./src", commentedOptional);
        EmitOption("outDir", "./dist", commentedOptional);

        Newline();

        EmitHeader(Diag.Environment_Settings);
        EmitHeader(Diag.See_also_https_Colon_Slash_Slashaka_ms_Slashtsconfig_Slashmodule);
        EmitOption("module", ModuleKind.NodeNext, commentedNever);
        EmitOption("target", ScriptTarget.ESNext, commentedNever);
        EmitOption("types", Array.Empty<object?>(), commentedNever);
        if (options.TryGet("lib", out var lib))
        {
            EmitOption("lib", lib, commentedNever);
        }
        EmitHeader(Diag.For_nodejs_Colon);
        Push(tab + tab + "// \"lib\": [\"esnext\"],");
        Push(tab + tab + "// \"types\": [\"node\"],");
        EmitHeader(Diag.X_and_npm_install_D_types_Slashnode);

        Newline();

        EmitHeader(Diag.Other_Outputs);
        EmitOption("sourceMap", /*defaultValue*/ true, commentedNever);
        EmitOption("declaration", /*defaultValue*/ true, commentedNever);
        EmitOption("declarationMap", /*defaultValue*/ true, commentedNever);

        Newline();

        EmitHeader(Diag.Stricter_Typechecking_Options);
        EmitOption("noUncheckedIndexedAccess", /*defaultValue*/ true, commentedNever);
        EmitOption("exactOptionalPropertyTypes", /*defaultValue*/ true, commentedNever);

        Newline();

        EmitHeader(Diag.Style_Options);
        EmitOption("noImplicitReturns", /*defaultValue*/ true, commentedOptional);
        EmitOption("noImplicitOverride", /*defaultValue*/ true, commentedOptional);
        EmitOption("noUnusedLocals", /*defaultValue*/ true, commentedOptional);
        EmitOption("noUnusedParameters", /*defaultValue*/ true, commentedOptional);
        EmitOption("noFallthroughCasesInSwitch", /*defaultValue*/ true, commentedOptional);
        EmitOption("noPropertyAccessFromIndexSignature", /*defaultValue*/ true, commentedOptional);

        Newline();

        EmitHeader(Diag.Recommended_Options);
        EmitOption("strict", /*defaultValue*/ true, commentedNever);
        EmitOption("jsx", JsxEmit.ReactJSX, commentedNever);
        EmitOption("verbatimModuleSyntax", /*defaultValue*/ true, commentedNever);
        EmitOption("isolatedModules", /*defaultValue*/ true, commentedNever);
        EmitOption("noUncheckedSideEffectImports", /*defaultValue*/ true, commentedNever);
        EmitOption("moduleDetection", ModuleDetectionKind.Force, commentedNever);
        EmitOption("skipLibCheck", /*defaultValue*/ true, commentedNever);

        // Write any user-provided options we haven't already
        if (allSetOptions.Count > 0)
        {
            Newline();
            while (allSetOptions.Count > 0)
            {
                EmitOption(allSetOptions[0], options.GetOrZero(allSetOptions[0]), commentedNever);
            }
        }

        Push(tab + "}");
        Push("}");
        Push("");

        return string.Join("\n", result);
    }
}

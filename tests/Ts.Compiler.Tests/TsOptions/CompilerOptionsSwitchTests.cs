// Ported from tsgo internal/tsoptions/parsinghelpers_test.go (TestParseCompilerOptionNoMissingFields)
// and decls_test.go (TestCompilerOptionsDeclaration).
//
// PORT-NOTE: the Go tests reflect over core.CompilerOptions fields + json tags. Reflection is
// AOT-banned (plan/07), so both tests instead drive the hand-written per-field table
// CompilerOptionsFields (the reflection replacement, Ts.TsOptions.CompilerOptionsReflect) — which
// carries each field's Go name + json-tag name. If the table drifts from the parse switch or the
// declaration table, these tests fail exactly as the Go originals would.

using System.Collections.Generic;
using System.Linq;
using Ts.Core;
using Ts.TsOptions;
using Xunit;

namespace Ts.TsOptions.Tests;

public class CompilerOptionsSwitchTests
{
    [Fact]
    public void ParseCompilerOptionNoMissingFields()
    {
        var missingKeys = new List<string>();
        foreach (var field in CompilerOptionsFields.Fields)
        {
            // reflect.Zero(field.Type).Interface() → the zero value read off a fresh CompilerOptions.
            object? val = field.Get(new CompilerOptions());
            var co = new CompilerOptions();
            var found = ParsingHelpers.parseCompilerOptions(field.JsonName, val, co);
            if (!found)
            {
                missingKeys.Add(field.JsonName);
            }
        }
        Assert.True(missingKeys.Count == 0,
            "The following keys are missing entries in the ParseCompilerOptions switch statement:\n" + string.Join(", ", missingKeys));
    }

    [Fact]
    public void CompilerOptionsDeclaration()
    {
        var decls = new Dictionary<string, CommandLineOption>(System.StringComparer.Ordinal);
        foreach (var decl in DeclsCompiler.OptionsDeclarations)
        {
            decls[OptionsUtil.ToLower(decl.Name)] = decl;
        }

        var internalOptions = new[]
        {
            "allowNonTsExtensions",
            "build",
            "configFilePath",
            "noDtsResolution",
            "noEmitForJsFiles",
            "pathsBasePath",
            "suppressOutputPathCheck",
        };
        var internalOptionsMap = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var opt in internalOptions)
        {
            internalOptionsMap[OptionsUtil.ToLower(opt)] = opt;
        }

        var errors = new List<string>();
        foreach (var field in CompilerOptionsFields.Fields)
        {
            var lowerName = OptionsUtil.ToLower(field.Name);
            if (decls.TryGetValue(lowerName, out var decl))
            {
                // The option declaration name equals the field's json-tag name (both camelCase).
                Assert.Equal(field.JsonName, decl.Name);
                decls.Remove(lowerName);
            }
            else if (internalOptionsMap.TryGetValue(lowerName, out var internalName))
            {
                Assert.Equal(field.JsonName, internalName);
            }
            else
            {
                errors.Add($"CompilerOptions.{field.Name} has no options declaration");
            }
        }

        // Declarations that intentionally have no CompilerOptions field.
        decls.Remove(OptionsUtil.ToLower("plugins"));

        foreach (var decl in decls.Values)
        {
            errors.Add($"Option declaration {decl.Name} is not present in CompilerOptions");
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void FieldTableMatchesCompilerOptionsFieldCount()
    {
        // Sanity: the per-field table has exactly the 130 CompilerOptions value fields.
        Assert.Equal(130, CompilerOptionsFields.Fields.Length);
        // JsonName values are unique.
        Assert.Equal(CompilerOptionsFields.Fields.Length,
            CompilerOptionsFields.Fields.Select(f => f.JsonName).Distinct().Count());
    }
}

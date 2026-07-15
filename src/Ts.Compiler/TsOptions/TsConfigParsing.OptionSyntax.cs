// tsconfigparsing.go — CreateDiagnosticAtReferenceSyntax: place a project-reference diagnostic on the
// originating "references" array element in the tsconfig source, falling back to a file-less compiler
// diagnostic (in the caller) when there is no tsconfig source file. The ForEach* navigation helpers and
// getTsConfigObjectLiteralExpression already live in TsConfigParsing.Worker.cs.

using System;
using Ts.Ast;

namespace Ts.TsOptions;

public static partial class TsConfigParsing
{
    // tsconfigparsing.go:CreateDiagnosticAtReferenceSyntax
    public static Diagnostic? CreateDiagnosticAtReferenceSyntax(ParsedCommandLine config, int index, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        return ForEachTsConfigPropArray<Diagnostic>(config.ConfigFile?.SourceFile, "references", property =>
        {
            var initializer = property.AsPropertyAssignment().Initializer;
            if (initializer is not null && NodeTests.IsArrayLiteralExpression(initializer))
            {
                var value = initializer.Elements();
                if (value is not null && value.Length > index)
                {
                    return Errors.CreateDiagnosticForNodeInSourceFile(config.ConfigFile?.SourceFile, value[index], message, args);
                }
            }
            return null;
        });
    }
}

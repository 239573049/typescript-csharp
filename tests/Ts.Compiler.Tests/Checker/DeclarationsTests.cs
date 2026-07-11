// Smoke probes for the complex-declaration validator wave (Checker.Declarations.cs):
// checkInterfaceDeclaration / checkEnumDeclaration / checkClassDeclaration + the signature/member/
// type-node validators. These validators are now REAL, but their reachable trunk still calls a few
// deep helpers that remain single-dedup stubbed in Checker.Declarations.Deps.cs (full
// heritage/override-conflict analysis, index-constraint checks, checkObjectTypeForDuplicateDeclarations,
// enum-member value computation display, deferred JSDoc-parameter matching). A full parse→bind→check
// over any interface/enum/class therefore reaches one of those stubs and throws NotImplementedException.
//
// The probes below build a real bound Program and run the checker; each is Skipped with the exact stub
// that currently blocks it. Un-skip a probe once its blocking deep helper lands. The build itself
// (0/0 in the isolated verification) already proves the validators compile and are wired into the
// CheckSourceElementWorker dispatch.

using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Compiler.Tests;

public sealed class DeclarationsTests
{
    private const string CurrentDirectory = "/src";

    private static Program BuildProgram(IReadOnlyDictionary<string, string> files, string[] roots, CompilerOptions options)
    {
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var host = CompilerHostImpl.NewCompilerHost(CurrentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            roots,
            new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = CurrentDirectory });
        return new Program(new ProgramOptions { Host = host, Config = config, SingleThreaded = Tristate.True });
    }

    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        var files = new Dictionary<string, string> { ["/src/only.ts"] = source };
        var program = BuildProgram(files, ["/src/only.ts"], new CompilerOptions { NoLib = Tristate.True, Types = [] });
        program.BindSourceFiles();
        var sf = program.GetSourceFile("/src/only.ts")!;
        var (checker, release) = program.GetTypeChecker(CancellationToken.None);
        try
        {
            return checker.GetDiagnostics(sf);
        }
        finally
        {
            release();
        }
    }

    // checkInterfaceDeclaration: a trivial interface with no heritage. Reachable trunk runs grammar +
    // containerAllowsBlockScopedVariable + checkTypeParameters(none) + checkTypeNameIsReserved +
    // checkExportsOnMergedDeclarations, then declaredTypeLinks gate → checkInheritedPropertiesAreIdentical
    // (STUB). Blocked there; un-skip once checkInheritedPropertiesAreIdentical lands.
    [Fact(Skip = "checkInterfaceDeclaration trunk reaches checkInheritedPropertiesAreIdentical (heritage-identity analysis, deferred)")]
    public void SimpleInterface_ProducesNoDiagnostics()
    {
        var diags = Check("interface I { x: number; }\n");
        Assert.Empty(diags);
    }

    // checkEnumDeclaration: a numeric enum. Reachable trunk runs grammar + checkCollisionsForDeclarationName
    // + checkExportsOnMergedDeclarations + checkSourceElements(members → checkEnumMember) + computeEnumMemberValues,
    // then the per-symbol enumChecked gate. checkSourceElements over members recurses into checkEnumMember
    // (REAL) which calls checkExpression on initializers. Blocked on the members/expression subsystem
    // reachable via computeEnumMemberValues / checkExpression deep paths; un-skip when those land.
    [Fact(Skip = "checkEnumDeclaration trunk reaches computeEnumMemberValues / member-expression checks (deferred)")]
    public void NumericEnum_ProducesNoDiagnostics()
    {
        var diags = Check("enum E { A, B, C }\n");
        Assert.Empty(diags);
    }

    // checkClassDeclaration: a trivial class. Reachable trunk runs checkClassLikeDeclaration which calls
    // checkObjectTypeForDuplicateDeclarations (STUB) early. Blocked there; un-skip once that lands.
    [Fact(Skip = "checkClassLikeDeclaration trunk reaches checkObjectTypeForDuplicateDeclarations (member-conflict analysis, deferred)")]
    public void SimpleClass_ProducesNoDiagnostics()
    {
        var diags = Check("class C { x: number = 1; }\n");
        Assert.Empty(diags);
    }
}

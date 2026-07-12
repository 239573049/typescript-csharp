// Smoke tests for the phase-5 awaited-types wave (#22) — the getAwaitedType chain
// (checker.go:28679-28767 + 30990-31226 → Checker.AwaitedTypes.cs).
//
// These tests exercise only the wave's stub-free surface:
//   - Non-thenable types (intrinsics, literals, unions of them) flow through
//     getAwaitedTypeNoAliasEx's primitive early-outs (getPromisedTypeOfPromiseEx and isThenableType
//     both bail on allTypesAssignableToKind(Primitive|Never)) and come back unchanged — never touching
//     the GetTypeWithFacts stub, which only sits on the `then`-signature (thenable) path.
//   - A real `Promise<number>` reference (E2E program with an inline global `interface Promise<T>`)
//     unwraps through the isReferenceToType fast path + the recursive promised-type walk to `number`.
//   - The CachedTypeKindAwaitedType cache returns the identical instance on a second query.

using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Checker;
using Ts.Core;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Checker.Tests;

public sealed class AwaitedTypesTests
{
    // ===== pure (FakeProgram) surface =====

    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    [Fact]
    public void GetAwaitedType_ReturnsNonThenableIntrinsicUnchanged()
    {
        var c = NewChecker();
        Assert.Same(c.numberType, c.GetAwaitedType(c.numberType!));
        Assert.Same(c.stringType, c.GetAwaitedType(c.stringType!));
        Assert.Same(c.booleanType, c.GetAwaitedType(c.booleanType!));
    }

    [Fact]
    public void GetAwaitedTypeNoAlias_ReturnsLiteralTypeUnchanged_AndCachesIt()
    {
        var c = NewChecker();
        var lit = c.GetStringLiteralType("hello");
        var first = c.GetAwaitedTypeNoAlias(lit);
        Assert.Same(lit, first);
        // Second query hits the CachedTypeKindAwaitedType cache and must return the same instance.
        Assert.Same(first, c.GetAwaitedTypeNoAlias(lit));
    }

    [Fact]
    public void GetAwaitedType_ReturnsUnionOfNonThenablesUnchanged()
    {
        var c = NewChecker();
        var union = c.GetUnionType([c.numberType!, c.stringType!]);
        // Each constituent awaits to itself, so mapType reconstructs the identical union.
        Assert.Same(union, c.GetAwaitedType(union));
    }

    [Fact]
    public void AwaitedPredicates_AreFalseForNonThenableIntrinsics()
    {
        var c = NewChecker();
        Assert.False(c.IsThenableType(c.numberType!));
        Assert.False(c.IsAwaitedTypeInstantiation(c.numberType!));
        Assert.False(c.IsAwaitedTypeNeeded(c.numberType!));
        // unwrapAwaitedType on a non-union, non-Awaited<T> type is the identity.
        Assert.Same(c.numberType, c.UnwrapAwaitedType(c.numberType!));
    }

    // ===== E2E: Promise<number> unwrap over a real bound program =====

    private const string CurrentDirectory = "/src";

    private static global::Ts.Compiler.Program BuildProgram(IReadOnlyDictionary<string, string> files, string[] roots, CompilerOptions options)
    {
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var host = global::Ts.Compiler.CompilerHostImpl.NewCompilerHost(CurrentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            roots,
            new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = CurrentDirectory });
        return new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions { Host = host, Config = config, SingleThreaded = Tristate.True });
    }

    private static Node? FindFirst(Node node, SyntaxKind kind)
    {
        Node? found = null;
        void Visit(Node n)
        {
            if (found is not null)
            {
                return;
            }
            if (n.Kind == kind)
            {
                found = n;
                return;
            }
            n.ForEachChild(child =>
            {
                Visit(child);
                return found is not null;
            });
        }
        Visit(node);
        return found;
    }

    [Fact]
    public void GetAwaitedType_UnwrapsGlobalPromiseReference()
    {
        // The inline `interface Promise<T> {}` makes isReferenceToType(t, getGlobalPromiseType()) hit
        // the fast path: promised type = the type argument (`number`), which awaits to itself.
        var files = new Dictionary<string, string>
        {
            ["/src/only.ts"] = "interface Promise<T> {}\nlet p: Promise<number>;\n",
        };
        var program = BuildProgram(files, ["/src/only.ts"], new CompilerOptions { NoLib = Tristate.True, Types = [] });
        program.BindSourceFiles();
        var sf = program.GetSourceFile("/src/only.ts")!;
        var (checker, release) = program.GetTypeChecker(CancellationToken.None);
        try
        {
            var typeRef = FindFirst(sf, SyntaxKind.TypeReference);
            Assert.NotNull(typeRef);
            var promiseOfNumber = checker.GetTypeFromTypeNode(typeRef!);

            Assert.Same(checker.numberType, checker.GetPromisedTypeOfPromise(promiseOfNumber));
            Assert.Same(checker.numberType, checker.GetAwaitedType(promiseOfNumber));
            Assert.Same(checker.numberType, checker.GetAwaitedTypeOfPromise(promiseOfNumber));
        }
        finally
        {
            release();
        }
    }
}

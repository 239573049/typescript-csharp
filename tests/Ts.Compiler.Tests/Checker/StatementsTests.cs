// Smoke tests for the phase-5 checker statements dispatch wave (plan/03 §4.2 #5; checker.go:2182-7016).
//
// The full statement/declaration check* paths route through cross-cluster stubs (grammar checks,
// checkExpression, error reporting, name lookup — Checker.Statements.Deps.cs + sibling dedup surfaces)
// that throw NotImplementedException until their owning waves land. So this file exercises only the
// wave's reachable, stub-free surface: the dispatch guards that early-out without touching a stub
// (CheckSourceElement(null)), and CheckTypeNameIsReserved on a non-reserved name (the switch falls
// through with no Error() call). Deeper cases (e.g. a full check of `const x: number = 1;`) are
// Skip-marked with a note because they immediately hit stubbed grammar/expression helpers.

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Checker.Tests;

public class StatementsTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile ParseAndBind(string text)
    {
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(sf);
        return sf;
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

    // checker.go:2229 — checkSourceElement guards on a nil node and returns false without dispatching.
    [Fact]
    public void CheckSourceElement_Null_ReturnsFalseWithoutThrowing()
    {
        var checker = NewChecker();
        Assert.False(checker.CheckSourceElement(null));
    }

    // checker.go:6878 — checkTypeNameIsReserved only calls Error() for a reserved keyword name;
    // a user identifier ("Foo") falls through the switch, touching no stub.
    [Fact]
    public void CheckTypeNameIsReserved_NonReservedName_DoesNotThrow()
    {
        var checker = NewChecker();
        var sf = ParseAndBind("type Foo = number;");
        var alias = FindFirst(sf.AsNode(), SyntaxKind.TypeAliasDeclaration)!;
        var name = alias.Name()!; // identifier "Foo"

        // No reserved keyword => switch falls through, Error() is never reached (stub-free path).
        checker.CheckTypeNameIsReserved(name, global::Ts.Diagnostics.Diagnostics.Type_alias_name_cannot_be_0);
    }

    // A full checkVariableStatement over `const x: number = 1;` immediately reaches stubbed grammar
    // and expression helpers (checkGrammarModifiers / checkExpressionCached / …), which throw
    // NotImplementedException. Re-enable once the grammar + expressions waves land.
    [Fact(Skip = "checkVariableStatement routes through stubbed grammar/expression helpers (Checker.Statements.Deps.cs); not yet implemented.")]
    public void CheckVariableStatement_SimpleConst_NoDiagnostics()
    {
        var checker = NewChecker();
        var sf = ParseAndBind("const x: number = 1;");
        var stmt = FindFirst(sf.AsNode(), SyntaxKind.VariableStatement)!;
        checker.CheckVariableStatement(stmt);
    }
}

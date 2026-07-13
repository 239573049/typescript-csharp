// Single dedup surface for the expression-core wave (#7/#8). Methods here fall into two groups:
//
//   (a) Leaf expression checkers OWNED by #7 (checker.go:7494-12292) but not yet ported in this
//       breadth-first pass — the dispatch trunk (Checker.Expressions.cs) routes to them so the
//       assembly compiles and literal/binary expressions type-check for real. When a follow-up pass
//       ports a leaf, it moves the body into Checker.Expressions.cs and DELETES the stub here.
//
//   (b) Cross-cluster helpers that #7/#8 CALL but do not own — they live in not-yet-landed waves
//       (grammar checks, contextual typing, JSX links, symbol/reference marking, awaited types,
//       falsy-type extraction). Each throws NotImplementedException so the call sites compile; the
//       owning wave DELETES the corresponding stub when it lands.
//
// Stubs already declared by other dedup surfaces (Statements.Deps / MembersSignatures.Deps /
// Flow.Deps / SymbolResolution / Stubs) are intentionally NOT redeclared here to avoid duplicate
// members (e.g. CheckSuperExpression, CheckArrayLiteral, CheckDestructuringAssignment,
// PushContextualType/PopContextualType, GetTypeOfExpression, GetContextualType, IsImportCall,
// GetAwaitedTypeNoAlias, ErrorAndMaybeSuggestAwait, GrammarErrorOnNode, CheckExternalEmitHelpers,
// GetAwaitedTypeOfPromise, CheckExpressionForMutableLocation, EntityNameToString,
// CreateDiagnosticForNode). **Do NOT implement whole missing subsystems here.**

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ===== (a) #7 leaf expression checkers (owned; body pending a follow-up breadth pass) =====

    // checker.go:7814 / 7823
    internal Type CheckPrivateIdentifierExpression(Node node) => throw new NotImplementedException("checkPrivateIdentifierExpression");

    // checkIdentifier (checker.go:11014) landed in Checker.CheckIdentifier.cs.

    // checker.go:12046
    internal Type CheckThisExpression(Node node) => throw new NotImplementedException("checkThisExpression");

    // checker.go:7953
    internal Type CheckTemplateExpression(Node node) => throw new NotImplementedException("checkTemplateExpression");

    // checker.go:7989
    internal Type CheckRegularExpressionLiteral(Node node) => throw new NotImplementedException("checkRegularExpressionLiteral");

    // checker.go:13094 — checkObjectLiteral: real body landed in Checker.ObjectLiteral.cs.

    // checker.go:11216 — checkPropertyAccessExpression: real body landed in Checker.PropertyAccess.cs.

    // checker.go:8099
    internal Type CheckQualifiedName(Node node, CheckMode checkMode) => throw new NotImplementedException("checkQualifiedName");

    // checker.go:8110
    internal Type CheckIndexedAccess(Node node, CheckMode checkMode) => throw new NotImplementedException("checkIndexedAccess");

    // checker.go:8244
    internal Type CheckImportCallExpression(Node node) => throw new NotImplementedException("checkImportCallExpression");

    // checker.go:8298 — checkCallExpression: real body landed in Checker.CallResolution.cs.

    // checker.go:10010
    internal Type CheckTaggedTemplateExpression(Node node) => throw new NotImplementedException("checkTaggedTemplateExpression");

    // checker.go:10019 — checkParenthesizedExpression: real body landed in Checker.Expressions.cs.

    // checker.go:10023
    internal Type CheckClassExpression(Node node) => throw new NotImplementedException("checkClassExpression");

    // CheckFunctionExpressionOrObjectLiteralMethod (+ contextual/deferred/parameter-assignment family)
    // is now REAL in Checker.Expressions.cs (checker.go:10090-10433).

    // checker.go:12256
    internal Type CheckAssertion(Node node, CheckMode checkMode) => throw new NotImplementedException("checkAssertion");

    // checker.go:10593
    // checker.go:10593
    internal Type CheckTypeOfExpression(Node node)
    {
        CheckExpression(node.Expression()!);
        return typeofType!;
    }

    // checker.go:10598
    internal Type CheckNonNullAssertion(Node node) => throw new NotImplementedException("checkNonNullAssertion");

    // checker.go:10715
    internal Type CheckSatisfiesExpression(Node node) => throw new NotImplementedException("checkSatisfiesExpression");

    // checker.go:10727
    internal Type CheckMetaProperty(Node node) => throw new NotImplementedException("checkMetaProperty");

    // checker.go:10778
    internal Type CheckDeleteExpression(Node node) => throw new NotImplementedException("checkDeleteExpression");

    // checker.go:10814
    internal Type CheckVoidExpression(Node node) => throw new NotImplementedException("checkVoidExpression");

    // checker.go:10819
    internal Type CheckAwaitExpression(Node node) => throw new NotImplementedException("checkAwaitExpression");

    // checker.go:10829
    internal Type CheckPrefixUnaryExpression(Node node) => throw new NotImplementedException("checkPrefixUnaryExpression");

    // checker.go:10883
    internal Type CheckPostfixUnaryExpression(Node node) => throw new NotImplementedException("checkPostfixUnaryExpression");

    // checker.go:10908
    internal Type CheckConditionalExpression(Node node, CheckMode checkMode) => throw new NotImplementedException("checkConditionalExpression");

    // checker.go:10921
    internal Type CheckSpreadExpression(Node node, CheckMode checkMode) => throw new NotImplementedException("checkSpreadExpression");

    // checker.go:10926
    internal Type CheckYieldExpression(Node node) => throw new NotImplementedException("checkYieldExpression");

    // checker.go:11006
    internal Type CheckSyntheticExpression(Node node) => throw new NotImplementedException("checkSyntheticExpression");

    // checker.go:7576 — instantiate an expression's single generic call signature against its
    // contextual type. The guard early-return (non-inferential, non-skip-generic check modes) is REAL;
    // the deep inference-context body (getSingleSignature / getApparentTypeOfContextualType /
    // getInferenceContext / applyToParameterTypes / …) is a large inference-engine surface reached only
    // during type-argument inference and remains deferred (single stub below).
    internal Type InstantiateTypeWithSingleGenericCallSignature(Node node, Type t, CheckMode checkMode)
    {
        if ((checkMode & (CheckMode.Inferential | CheckMode.SkipGenericFunctions)) == 0)
        {
            return t;
        }
        throw new NotImplementedException("instantiateTypeWithSingleGenericCallSignature (inferential body)");
    }

    // checker.go:7550 — const-enum access-site validation (needs program/verbatim-module wiring).
    internal void CheckConstEnumAccess(Node node, Type t) => throw new NotImplementedException("checkConstEnumAccess");

    // ===== (b) cross-cluster helpers called by #7/#8 (owned elsewhere) =====

    // jsx.go — JSX expression checkers: real bodies landed in Checker.Jsx.cs (jsx wave).

    // grammar.go — CheckGrammarNumericLiteral/CheckGrammarBigIntLiteral are now REAL in Checker.Grammar.cs (grammar wave).

    // checker.go — global-symbol resolution (Construction / NameLookup clusters).
    internal Symbol? GetGlobalNaNSymbolOrNil() => throw new NotImplementedException("getGlobalNaNSymbolOrNil");

    // checker.go:11499 — nonexistent-property diagnostics (property-access cluster, #7).
    internal void ReportNonexistentProperty(Node propNode, Type containingType, bool isUncheckedJS) => throw new NotImplementedException("reportNonexistentProperty");

    // checker.go — unchecked-JS suggestion heuristic (utilities cluster).
    // utilities.go:1700
    internal bool IsUncheckedJSSuggestion(Node? node, Symbol? suggestion, bool excludeClasses)
    {
        var file = AstUtilities.GetSourceFileOfNode(node);
        if (file != null)
        {
            if (compilerOptions.CheckJs.IsUnknown() && file.CheckJsDirective == null && (file.ScriptKind == global::Ts.Core.ScriptKind.JS || file.ScriptKind == global::Ts.Core.ScriptKind.Jsx))
            {
                SourceFile? declarationFile = null;
                if (suggestion != null)
                {
                    var firstDeclaration = CoreUtil.FirstOrNil(suggestion.Declarations);
                    if (firstDeclaration != null)
                    {
                        declarationFile = AstUtilities.GetSourceFileOfNode(firstDeclaration);
                    }
                }
                var suggestionHasNoExtendsOrDecorators = suggestion == null ||
                    suggestion.ValueDeclaration == null ||
                    !AstUtilities.IsClassLike(suggestion.ValueDeclaration) ||
                    AstUtilities.GetExtendsHeritageClauseElements(suggestion.ValueDeclaration).Length != 0 ||
                    AstUtilities.ClassOrConstructorParameterIsDecorated(false, suggestion.ValueDeclaration);
                return !(file != declarationFile && declarationFile != null && AstUtilities.IsGlobalSourceFile(declarationFile.AsNode())) &&
                    !(excludeClasses && suggestion != null && (suggestion.Flags & SymbolFlags.Class) != 0 && suggestionHasNoExtendsOrDecorators) &&
                    !(node != null && excludeClasses && NodeTests.IsPropertyAccessExpression(node) && node.Expression()!.Kind == SyntaxKind.ThisKeyword && suggestionHasNoExtendsOrDecorators);
            }
        }
        return false;
    }

    // checker.go — definitely-falsy type extraction (type-facts cluster).
    internal Type ExtractDefinitelyFalsyTypes(Type type) => throw new NotImplementedException("extractDefinitelyFalsyTypes");
    // checker.go:28863
    internal Type RemoveDefinitelyFalsyTypes(Type t) => FilterType(t, x => HasTypeFacts(x, TypeFacts.Truthy));

    // checker.go:25362
    // This like getBaseTypeOfLiteralType, but instead treats enum literals as strings/numbers instead
    // of returning their enum base type (which depends on the types of other literals in the enum).
    internal Type GetBaseTypeOfLiteralTypeForComparison(Type t)
    {
        if ((t.flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
        {
            return stringType!;
        }
        if ((t.flags & (TypeFlags.NumberLiteral | TypeFlags.Enum)) != 0)
        {
            return numberType!;
        }
        if ((t.flags & TypeFlags.BigIntLiteral) != 0)
        {
            return bigintType!;
        }
        if ((t.flags & TypeFlags.BooleanLiteral) != 0)
        {
            return booleanType!;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return MapType(t, GetBaseTypeOfLiteralTypeForComparison);
        }
        return t;
    }

    // IsWriteOnlyAccess (ast.go:1265) landed on AstUtilities (Utilities.AccessKind.cs); call sites use
    // AstUtilities.IsWriteOnlyAccess directly.

    // checker.go/utilities.go:1041 — literal-expression-of-object predicate (utilities cluster free func).
    internal static bool IsLiteralExpressionOfObject(Node node) => throw new NotImplementedException("isLiteralExpressionOfObject");
}

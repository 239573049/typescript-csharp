// Single dedup surface for cross-cluster methods that flow.go CALLS but does not own — they live in
// not-yet-landed waves (expressions/#7-#8, symbol resolution/#10-#11, contextual/#21, type creation,
// facts, evaluator utilities, grammar). Each throws NotImplementedException so flow's call sites
// compile; deep runtime paths run only once the owning wave lands its real implementation, at which
// point that wave DELETES the corresponding stub here. **Do NOT implement whole missing subsystems.**

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ---- expressions (#7/#8) ----
    // getTypeOfExpression / checkExpression / checkExpressionCached / checkNonNullExpression /
    // getResolvedSymbol / getResolvedSymbolOrNil / checkRightHandSideOfForOf / checkIteratedTypeOrElementType
    // are stubbed by other Members/Signatures/Stubs dedup surfaces (already present).
    // getContextFreeTypeOfExpression (checker.go:7519) — real body landed in Checker.Expressions.cs (#7 wave).
    internal Type GetContextFreeTypeOfExpression(Node node) => GetContextFreeTypeOfExpressionCore(node);
    internal Type CheckNonNullType(Type t, Node node) => throw new NotImplementedException("checkNonNullType");
    internal Type GetOptionalExpressionType(Type exprType, Node expression) => throw new NotImplementedException("getOptionalExpressionType");
    internal Type CheckSuperExpression(Node node) => throw new NotImplementedException("checkSuperExpression");
    internal Signature GetResolvedSignature(Node node, Signature[]? candidatesOutArray, CheckMode checkMode) => throw new NotImplementedException("getResolvedSignature");
    internal AssignmentKind GetAssignmentTargetKind(Node node) => throw new NotImplementedException("getAssignmentTargetKind");
    internal bool IsInCompoundLikeAssignment(Node node) => throw new NotImplementedException("isInCompoundLikeAssignment");

    // ---- symbol resolution / symbol predicates (#10/#11) ----
    // resolveSymbol / resolveEntityName / getExportSymbolOfValueSymbolIfExported / getSymbolOfDeclaration /
    // getResolvedSymbol / isConstantVariable / isReadonlySymbol already stubbed elsewhere.
    internal bool IsParameterOrMutableLocalVariable(Symbol symbol) => throw new NotImplementedException("isParameterOrMutableLocalVariable");
    internal bool IsSomeSymbolAssigned(Node rootDeclaration) => throw new NotImplementedException("isSomeSymbolAssigned");
    internal Symbol? GetSymbolForPrivateIdentifierExpression(Node privId) => throw new NotImplementedException("getSymbolForPrivateIdentifierExpression");
    internal Symbol? GetGlobalRecordSymbol() => throw new NotImplementedException("getGlobalRecordSymbol");
    internal Symbol? GetGlobalESSymbolConstructorSymbolOrNil() => throw new NotImplementedException("getGlobalESSymbolConstructorSymbolOrNil");

    // ---- type creation / operations / facts ----
    // checker.go:30973-30981
    internal Type ConvertAutoToAny(Type t)
    {
        if (t == autoType)
        {
            return anyType!;
        }
        if (t == autoArrayType)
        {
            return anyArrayType!;
        }
        return t;
    }
    internal Type RecombineUnknownType(Type t) => throw new NotImplementedException("recombineUnknownType");
    internal Type ExtractUnitType(Type t) => throw new NotImplementedException("extractUnitType");
    // getFreshTypeOfLiteralType is real in Checker.TypeCreation.cs.
    internal Type GetNarrowableTypeForReference(Type t, Node reference, CheckMode checkMode) => throw new NotImplementedException("getNarrowableTypeForReference");
    internal bool IsUnitLikeType(Type t) => throw new NotImplementedException("isUnitLikeType");
    internal Type GetFlowTypeOfProperty(Node reference, Symbol? prop) => throw new NotImplementedException("getFlowTypeOfProperty");

    // ---- type→property-name utilities (checker.go free funcs) ----
    // isTypeUsableAsPropertyName / getPropertyNameFromType already stubbed in Checker.SymbolResolution.cs.
    internal static bool IsNeitherUnitTypeNorNever(Type t) => throw new NotImplementedException("isNeitherUnitTypeNorNever");
    internal static bool IsNonNullAccess(Node access) => throw new NotImplementedException("isNonNullAccess");
    // IsTypeAny already stubbed in Checker.SymbolTypes.cs.
}

// checker.go — assignment-target kind (getAssignmentTargetKind). Owned by the operator-expressions
// cluster (#8, not yet landed); the enum lives here alongside the getAssignmentTargetKind stub so
// flow's call sites compile. When #8 lands it defines the enum and DELETES this + the stub above.
internal enum AssignmentKind
{
    None,
    Definite,
    Compound,
}

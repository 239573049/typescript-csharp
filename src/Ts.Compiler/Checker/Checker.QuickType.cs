// Ported from tsgo internal/checker/checker.go (getQuickTypeOfExpression 7338 and its single-signature
// return-type helpers + isSymbolOrSymbolForCall 8357 + propagateOptionalTypeMarker 28839), plan/03 §4.2 #7.
// Faithful per-function translation: same function order as Go, same branch structure; Go comments
// and `// !!!` markers preserved.
//
// §1.5: the no-collision hosts (NodeTests/AstUtilities) are used directly; `Diag` alias for messages.
// These are the helpers checkDeclarationInitializer (checker.go:16710) reaches for the common
// literal/identifier initializer path; the await/call/new leaves route through cross-cluster stubs
// (getAwaitedType/checkNonNullType/getOptionalExpressionType/getGlobalESSymbolConstructorSymbolOrNil)
// that land with their owning waves.

using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:7338
    internal Type? GetQuickTypeOfExpression(Node node)
    {
        var expr = AstUtilities.SkipParentheses(node);
        if (NodeTests.IsAwaitExpression(expr))
        {
            var awaited = GetQuickTypeOfExpression(expr.Expression()!);
            if (awaited != null)
            {
                return GetAwaitedType(awaited);
            }
            return null;
        }
        // Optimize for the common case of a call to a function with a single non-generic call
        // signature where we can just fetch the return type without checking the arguments.
        if (NodeTests.IsCallExpression(expr) && expr.Expression()!.Kind != SyntaxKind.SuperKeyword && !AstUtilities.IsRequireCall(expr, true /*requireStringLiteralLikeArgument*/) && !IsSymbolOrSymbolForCall(expr) && !IsImportCall(expr))
        {
            if (IsCallChain(expr))
            {
                return GetReturnTypeOfSingleNonGenericSignatureOfCallChain(expr);
            }
            return GetReturnTypeOfSingleNonGenericSignature(CheckNonNullExpression(expr.Expression()!), SignatureKind.Call);
        }
        if (NodeTests.IsNewExpression(expr))
        {
            return GetReturnTypeOfSingleNonGenericSignature(CheckNonNullExpression(expr.Expression()!), SignatureKind.Construct);
        }
        if (AstUtilities.IsAssertionExpression(expr) && !AstUtilities.IsConstTypeReference(expr.Type()!))
        {
            return GetTypeFromTypeNode(expr.Type()!);
        }
        if (AstUtilities.IsLiteralExpression(node) || AstUtilities.IsBooleanLiteral(node))
        {
            return CheckExpression(node);
        }
        return null;
    }

    // checker.go:7362
    internal Type? GetReturnTypeOfSingleNonGenericSignature(Type funcType, SignatureKind kind)
    {
        var signature = GetSingleSignature(funcType, kind, true /*allowMembers*/);
        if (signature != null && signature.typeParameters.Len() == 0)
        {
            return GetReturnTypeOfSignature(signature);
        }
        return null;
    }

    // checker.go:7370
    internal Type? GetReturnTypeOfSingleNonGenericSignatureOfCallChain(Node expr)
    {
        var funcType = CheckExpression(expr.Expression()!);
        var nonOptionalType = GetOptionalExpressionType(funcType, expr.Expression()!);
        var returnType = GetReturnTypeOfSingleNonGenericSignature(funcType, SignatureKind.Call);
        if (returnType != null)
        {
            return PropagateOptionalTypeMarker(returnType, expr, nonOptionalType != funcType);
        }
        return null;
    }

    // isSymbolOrSymbolForCall (checker.go:8357) implemented in Checker.CallResolution.cs.

    // checker.go:28839
    internal Type PropagateOptionalTypeMarker(Type t, Node node, bool wasOptional)
    {
        if (wasOptional)
        {
            if (AstUtilities.IsOutermostOptionalChain(node))
            {
                return GetOptionalType(t, false);
            }
            return AddOptionalTypeMarker(t);
        }
        return t;
    }
}

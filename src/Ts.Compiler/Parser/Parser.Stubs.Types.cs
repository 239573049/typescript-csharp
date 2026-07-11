// Partitioned stub file (cluster: Types). Replace each stub with the real body in place.
// Build stays green; when all stubs in a cluster are implemented, delete this file.
#nullable enable
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    private Node CreateUnionOrIntersectionTypeNode(SyntaxKind @operator, NodeList? types) => throw new System.NotImplementedException("createUnionOrIntersectionTypeNode");
    private Node ParseAssertsTypePredicate() => throw new System.NotImplementedException("parseAssertsTypePredicate");
    private Node ParseEntityNameOfTypeReference() => throw new System.NotImplementedException("parseEntityNameOfTypeReference");
    private Node ParseInferType() => throw new System.NotImplementedException("parseInferType");
    private Node ParseIntersectionTypeOrHigher() => throw new System.NotImplementedException("parseIntersectionTypeOrHigher");
    private Node ParseKeywordTypeNode() => throw new System.NotImplementedException("parseKeywordTypeNode");
    private Node ParseLiteralTypeNode(bool negative) => throw new System.NotImplementedException("parseLiteralTypeNode");
    private Node ParseMappedType() => throw new System.NotImplementedException("parseMappedType");
    private Node ParseNestedTypeLiteral(Node? typeExpression, Node? name, PropertyLikeParse target, int indent) => throw new System.NotImplementedException("parseNestedTypeLiteral");
    private Node ParseNonArrayType() => throw new System.NotImplementedException("parseNonArrayType");
    private Node ParseParenthesizedType() => throw new System.NotImplementedException("parseParenthesizedType");
    private Node ParsePostfixTypeOrHigher() => throw new System.NotImplementedException("parsePostfixTypeOrHigher");
    private Node ParseReturnType(SyntaxKind returnToken, bool isType) => throw new System.NotImplementedException("parseReturnType");
    private Node ParseTemplateType() => throw new System.NotImplementedException("parseTemplateType");
    private Node ParseTemplateTypeSpan() => throw new System.NotImplementedException("parseTemplateTypeSpan");
    private Node ParseThisTypeNode() => throw new System.NotImplementedException("parseThisTypeNode");
    private Node ParseThisTypePredicate(Node? lhs) => throw new System.NotImplementedException("parseThisTypePredicate");
    private Node ParseTupleElementNameOrTupleElementType() => throw new System.NotImplementedException("parseTupleElementNameOrTupleElementType");
    private Node ParseTupleElementType() => throw new System.NotImplementedException("parseTupleElementType");
    private Node ParseTupleType() => throw new System.NotImplementedException("parseTupleType");
    private Node ParseType() => throw new System.NotImplementedException("parseType");
    private Node ParseTypeAnnotation() => throw new System.NotImplementedException("parseTypeAnnotation");
    private Node ParseTypeAssertion() => throw new System.NotImplementedException("parseTypeAssertion");
    private Node ParseTypeLiteral() => throw new System.NotImplementedException("parseTypeLiteral");
    private Node ParseTypeOfExpression() => throw new System.NotImplementedException("parseTypeOfExpression");
    private Node ParseTypeOperator(SyntaxKind @operator) => throw new System.NotImplementedException("parseTypeOperator");
    private Node ParseTypeOperatorOrHigher() => throw new System.NotImplementedException("parseTypeOperatorOrHigher");
    private Node ParseTypeOrTypePredicate() => throw new System.NotImplementedException("parseTypeOrTypePredicate");
    private Node ParseTypeQuery() => throw new System.NotImplementedException("parseTypeQuery");
    private Node ParseTypeReference() => throw new System.NotImplementedException("parseTypeReference");
    private Node ParseUnionTypeOrHigher() => throw new System.NotImplementedException("parseUnionTypeOrHigher");
    private Node TryParseConstraintOfInferType() => throw new System.NotImplementedException("tryParseConstraintOfInferType");
    private Node TryParseTypeExpression() => throw new System.NotImplementedException("tryParseTypeExpression");
    private NodeList ParseTemplateTypeSpans() => throw new System.NotImplementedException("parseTemplateTypeSpans");
    private bool IsStartOfType(bool inStartOfParameter) => throw new System.NotImplementedException("isStartOfType");
    private bool NextIsParenthesizedOrFunctionType() => throw new System.NotImplementedException("nextIsParenthesizedOrFunctionType");
    private bool NextIsStartOfMappedType() => throw new System.NotImplementedException("nextIsStartOfMappedType");
    private bool NextIsStartOfType() => throw new System.NotImplementedException("nextIsStartOfType");
    private bool NextIsUnambiguouslyStartOfFunctionType() => throw new System.NotImplementedException("nextIsUnambiguouslyStartOfFunctionType");
    private bool ShouldParseReturnType(SyntaxKind returnToken, bool isType) => throw new System.NotImplementedException("shouldParseReturnType");
}

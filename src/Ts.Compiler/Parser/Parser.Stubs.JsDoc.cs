// Partitioned stub file (cluster: JsDoc). Replace each stub with the real body in place.
// Build stays green; when all stubs in a cluster are implemented, delete this file.
#nullable enable
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    private (Node, bool) ParseBracketNameInPropertyAndParamTag(PropertyLikeParse target) => throw new System.NotImplementedException("parseBracketNameInPropertyAndParamTag");
    private (string, bool) ParseJSDocLinkPrefix() => throw new System.NotImplementedException("parseJSDocLinkPrefix");
    private Node GetInnermostNameOfJSDocNamespace(Node? fullName) => throw new System.NotImplementedException("getInnermostNameOfJSDocNamespace");
    private Node ParseAugmentsTag(int start, Node? tagName, int margin, string? indentText) => throw new System.NotImplementedException("parseAugmentsTag");
    private Node ParseCallbackTag(int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseCallbackTag");
    private Node ParseChildParameterOrPropertyTag(PropertyLikeParse target, int indent, Node? name) => throw new System.NotImplementedException("parseChildParameterOrPropertyTag");
    private Node ParseChildPropertyTag(int indent) => throw new System.NotImplementedException("parseChildPropertyTag");
    private Node ParseImplementsTag(int start, Node? tagName, int margin, string? indentText) => throw new System.NotImplementedException("parseImplementsTag");
    private Node ParseImportTag(int start, Node? tagName, int margin, string? indentText) => throw new System.NotImplementedException("parseImportTag");
    private Node ParseJSDocAllType() => throw new System.NotImplementedException("parseJSDocAllType");
    private Node ParseJSDocComment(Node? parent, int start, int end, int fullStart) => throw new System.NotImplementedException("parseJSDocComment");
    private Node ParseJSDocCommentWorker(int start, int end, int fullStart, int indent) => throw new System.NotImplementedException("parseJSDocCommentWorker");
    private Node ParseJSDocEntityName(Message? diagnosticMessage) => throw new System.NotImplementedException("parseJSDocEntityName");
    private Node ParseJSDocIdentifierName(Message? diagnosticMessage) => throw new System.NotImplementedException("parseJSDocIdentifierName");
    private Node ParseJSDocLink(int start) => throw new System.NotImplementedException("parseJSDocLink");
    private Node ParseJSDocLinkName() => throw new System.NotImplementedException("parseJSDocLinkName");
    private Node ParseJSDocNameReference() => throw new System.NotImplementedException("parseJSDocNameReference");
    private Node ParseJSDocNonNullableType() => throw new System.NotImplementedException("parseJSDocNonNullableType");
    private Node ParseJSDocNullableType() => throw new System.NotImplementedException("parseJSDocNullableType");
    private Node ParseJSDocSignature(int start, int indent) => throw new System.NotImplementedException("parseJSDocSignature");
    private Node ParseJSDocType() => throw new System.NotImplementedException("parseJSDocType");
    private Node ParseJSDocTypeExpression(bool mayOmitBraces) => throw new System.NotImplementedException("parseJSDocTypeExpression");
    private Node ParseJSDocTypeNameWithNamespace(bool nested) => throw new System.NotImplementedException("parseJSDocTypeNameWithNamespace");
    private Node ParseOverloadTag(int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseOverloadTag");
    private Node ParseParameterOrPropertyTag(int start, Node? tagName, PropertyLikeParse target, int indent) => throw new System.NotImplementedException("parseParameterOrPropertyTag");
    private Node ParseReturnTag(Node[]? previousTags, int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseReturnTag");
    private Node ParseSatisfiesTag(int start, Node? tagName, int margin, string? indentText) => throw new System.NotImplementedException("parseSatisfiesTag");
    private Node ParseSeeTag(int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseSeeTag");
    private Node ParseTag(Node[]? tags, int margin) => throw new System.NotImplementedException("parseTag");
    private Node ParseTemplateTag(int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseTemplateTag");
    private Node ParseThisTag(int start, Node? tagName, int margin, string? indentText) => throw new System.NotImplementedException("parseThisTag");
    private Node ParseThrowsTag(int start, Node? tagName, int margin, string? indentText) => throw new System.NotImplementedException("parseThrowsTag");
    private Node ParseTypeTag(Node[]? previousTags, int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseTypeTag");
    private Node ParseTypedefTag(int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseTypedefTag");
    private Node ParseUnknownTag(int start, Node? tagName, int indent, string? indentText) => throw new System.NotImplementedException("parseUnknownTag");
    private Node ReparseJSDocSignature(Node? jsSignature, Node? fun, Node? jsDoc, Node? tag, ModifierList? modifiers) => throw new System.NotImplementedException("reparseJSDocSignature");
    private Node ReparseJSDocTypeLiteral(Node? t) => throw new System.NotImplementedException("reparseJSDocTypeLiteral");
    private Node ReparseTopLevelAwait(SourceFile? sourceFile) => throw new System.NotImplementedException("reparseTopLevelAwait");
    private Node TryParseChildTag(PropertyLikeParse target, int indent) => throw new System.NotImplementedException("tryParseChildTag");
    private Node WrapInJSDocNamespace(Node? fullName, Node? statement, bool nested) => throw new System.NotImplementedException("wrapInJSDocNamespace");
    private NodeList ParseTagComments(int indent, string? initialMargin) => throw new System.NotImplementedException("parseTagComments");
    private NodeList ParseTrailingTagComments(int pos, int end, int margin, string? indentText) => throw new System.NotImplementedException("parseTrailingTagComments");
    private bool ParseOptionalJsdoc(SyntaxKind t) => throw new System.NotImplementedException("parseOptionalJsdoc");
    private string SkipWhitespaceOrAsterisk() => throw new System.NotImplementedException("skipWhitespaceOrAsterisk");
    private void ReparseHosted(Node? tag, Node? parent, Node? jsDoc) => throw new System.NotImplementedException("reparseHosted");
    private void ReparseJSDocComment(Node? node, Node? tag) => throw new System.NotImplementedException("reparseJSDocComment");
    private void ReparseTags(Node? parent, Node[]? jsDoc) => throw new System.NotImplementedException("reparseTags");
    private void ReparseUnhosted(Node? tag, Node? parent, Node? jsDoc) => throw new System.NotImplementedException("reparseUnhosted");
}

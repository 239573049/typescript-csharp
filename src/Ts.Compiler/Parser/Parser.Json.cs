// Ported from tsgo internal/parser/parser.go (JSON parsing section).
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;


namespace Ts.Parser;

public sealed partial class Parser
{
    private SourceFile ParseJSONText()
    {
        int pos = NodePos();
        NodeList? statements;
        Node? eof;

        if (token == SyntaxKind.EndOfFile)
        {
            statements = NewNodeList(new TextRange(pos, NodePos()), System.Array.Empty<Node>());
            eof = ParseTokenNode();
        }
        else
        {
            object? expressions = null; // Node[] | Node

            while (token != SyntaxKind.EndOfFile)
            {
                Node? expression;
                switch (token)
                {
                    case SyntaxKind.OpenBracketToken:
                        expression = ParseArrayLiteralExpression();
                        break;
                    case SyntaxKind.TrueKeyword:
                    case SyntaxKind.FalseKeyword:
                    case SyntaxKind.NullKeyword:
                        expression = ParseTokenNode();
                        break;
                    case SyntaxKind.MinusToken:
                        if (LookAhead(() =>
                            NextToken() == SyntaxKind.NumericLiteral && NextToken() != SyntaxKind.ColonToken))
                        {
                            expression = ParsePrefixUnaryExpression();
                        }
                        else
                        {
                            expression = ParseObjectLiteralExpression();
                        }
                        break;
                    case SyntaxKind.NumericLiteral:
                    case SyntaxKind.StringLiteral:
                        if (LookAhead(() => NextToken() != SyntaxKind.ColonToken))
                        {
                            expression = ParseLiteralExpression(false);
                            break;
                        }
                        goto default;
                    default:
                        expression = ParseObjectLiteralExpression();
                        break;
                }

                // Error recovery: collect multiple top-level expressions
                if (expressions != null)
                {
                    if (expressions is Node[] es)
                    {
                        var list = new List<Node>(es) { expression! };
                        expressions = list.ToArray();
                    }
                    else
                    {
                        expressions = new Node[] { (Node)expressions, expression! };
                    }
                }
                else
                {
                    expressions = expression;
                    if (token != SyntaxKind.EndOfFile)
                    {
                        ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Unexpected_token);
                    }
                }
            }

            Node exprNode;
            if (expressions is Node[] esArr)
            {
                exprNode = FinishNode(factory.NewArrayLiteralExpression(NewNodeList(new TextRange(pos, NodePos()), esArr), false), pos);
            }
            else
            {
                exprNode = (Node)expressions!;
            }
            Node statement = FinishNode(factory.NewExpressionStatement(exprNode), pos);
            statements = NewNodeList(new TextRange(pos, NodePos()), new[] { statement });
            eof = ParseExpectedToken(SyntaxKind.EndOfFile);
        }
        Node node = FinishNode(factory.NewSourceFile(opts, sourceText, statements, eof), pos);
        SourceFile result = node.AsSourceFile();
        if (result.Statements != null && result.Statements.Nodes.Length > 0)
        {
            ValidateJsonValue(result, result.Statements.Nodes[0].Expression());
        }
        FinishSourceFile(result, false);
        return result;
    }

    private static TextRange GetErrorSpanForNode(string sourceText, Node node) =>
        new(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, node.Pos), node.End);

    private void ValidateJsonValue(SourceFile sourceFile, Node? valueExpression)
    {
        if (valueExpression == null)
        {
            return;
        }
        switch (valueExpression.Kind)
        {
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.NumericLiteral:
                return;
            case SyntaxKind.StringLiteral:
                if (!IsDoubleQuotedString(valueExpression))
                {
                    diagnostics.Add(Diagnostic.NewDiagnostic(sourceFile, GetErrorSpanForNode(sourceText, valueExpression), global::Ts.Diagnostics.Diagnostics.String_literal_with_double_quotes_expected));
                }
                return;
            case SyntaxKind.PrefixUnaryExpression:
                if (valueExpression.AsPrefixUnaryExpression().Operator != SyntaxKind.MinusToken ||
                    valueExpression.AsPrefixUnaryExpression().Operand!.Kind != SyntaxKind.NumericLiteral)
                {
                    break; // not valid JSON syntax
                }
                return;
            case SyntaxKind.ObjectLiteralExpression:
                ValidateJsonObjectLiteral(sourceFile, valueExpression.AsObjectLiteralExpression());
                return;
            case SyntaxKind.ArrayLiteralExpression:
                foreach (Node element in valueExpression.Elements() ?? System.Array.Empty<Node>())
                {
                    ValidateJsonValue(sourceFile, element);
                }
                return;
        }
        diagnostics.Add(Diagnostic.NewDiagnostic(sourceFile, GetErrorSpanForNode(sourceText, valueExpression), global::Ts.Diagnostics.Diagnostics.Property_value_can_only_be_string_literal_numeric_literal_true_false_null_object_literal_or_array_literal));
    }

    private static bool IsDoubleQuotedString(Node node) =>
        NodeTests.IsStringLiteral(node) && (node.AsStringLiteral().TokenFlags & TokenFlags.SingleQuote) == 0;

    private void ValidateJsonObjectLiteral(SourceFile sourceFile, ObjectLiteralExpression node)
    {
        foreach (Node element in node.Properties!.Nodes)
        {
            if (element.Kind != SyntaxKind.PropertyAssignment)
            {
                diagnostics.Add(Diagnostic.NewDiagnostic(sourceFile, GetErrorSpanForNode(sourceText, element), global::Ts.Diagnostics.Diagnostics.Property_assignment_expected));
                continue;
            }
            if (element.Name() != null && !IsDoubleQuotedString(element.Name()!))
            {
                diagnostics.Add(Diagnostic.NewDiagnostic(sourceFile, GetErrorSpanForNode(sourceText, element.Name()!), global::Ts.Diagnostics.Diagnostics.String_literal_with_double_quotes_expected));
            }
            ValidateJsonValue(sourceFile, element.AsPropertyAssignment().Initializer);
        }
    }
}

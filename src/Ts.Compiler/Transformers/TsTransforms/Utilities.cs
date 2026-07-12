// Ported from tsgo internal/transformers/tstransforms/utilities.go.

using System;
using System.Globalization;
using Ts.Ast;

namespace Ts.Transformers.TsTransforms;

internal static class TsTransformUtilities
{
    internal static Expression? ConstantExpression(object? value, global::Ts.Printer.NodeFactory factory)
    {
        switch (value)
        {
            case string text:
                return factory.NewStringLiteral(text, TokenFlags.None);
            case double number when double.IsPositiveInfinity(number):
                return factory.NewIdentifier("Infinity");
            case double number when double.IsNegativeInfinity(number):
                return factory.NewPrefixUnaryExpression(
                    SyntaxKind.MinusToken, factory.NewIdentifier("Infinity"));
            case double number when double.IsNaN(number):
                return factory.NewIdentifier("NaN");
            case double number when number < 0:
                return factory.NewPrefixUnaryExpression(
                    SyntaxKind.MinusToken, ConstantExpression(-number, factory));
            case double number:
                return factory.NewNumericLiteral(number.ToString("R", CultureInfo.InvariantCulture), TokenFlags.None);
            case int number:
                return factory.NewNumericLiteral(number.ToString(CultureInfo.InvariantCulture), TokenFlags.None);
            case long number:
                return factory.NewNumericLiteral(number.ToString(CultureInfo.InvariantCulture), TokenFlags.None);
            case global::Ts.JsNum.Number number:
                if (number.IsInf())
                {
                    return number.Value > 0
                        ? factory.NewIdentifier("Infinity")
                        : factory.NewPrefixUnaryExpression(
                            SyntaxKind.MinusToken, factory.NewIdentifier("Infinity"));
                }
                if (number.IsNaN())
                {
                    return factory.NewIdentifier("NaN");
                }
                if (number.Value < 0)
                {
                    return factory.NewPrefixUnaryExpression(
                        SyntaxKind.MinusToken,
                        ConstantExpression(new global::Ts.JsNum.Number(-number.Value), factory));
                }
                return factory.NewNumericLiteral(number.ToString(), TokenFlags.None);
            default:
                return null;
        }
    }

    internal static ModuleDeclaration GetInnermostModuleDeclarationFromDottedModule(
        ModuleDeclaration moduleDeclaration)
    {
        while (moduleDeclaration.Body is { Kind: SyntaxKind.ModuleDeclaration } body)
        {
            moduleDeclaration = body.AsModuleDeclaration();
        }
        return moduleDeclaration;
    }
}

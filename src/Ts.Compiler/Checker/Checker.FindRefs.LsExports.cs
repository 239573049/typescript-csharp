// Additive public re-exports consumed by the Ts.LanguageService find-all-references /
// import-tracker port (internal/ls/findallreferences.go + importTracker.go).
//
// DEVIATION-REGISTER (AGENTS.md §6, logged in the port report): the dispatcher scoped
// this session to new Ls/* files plus Rename.cs/Definition.cs edits, but the deep
// find-all-references engine calls checker-package exported (capitalized) API —
// services.go / exports.go — whose C# cores landed only as internal members. Every
// member below is a 1:1 passthrough to (or a 1:1 local port of) the existing internal
// implementation: no logic change, additive file (zero merge surface with concurrent
// agents). Same convention as Checker.LsExports.cs and prior wrapper commits
// (9109b7cdf, 4c1c94bc8). getTypeOfAssignmentPattern / getIndexSignaturesAtLocation
// are ported here verbatim because their Go bodies are private to the checker package
// and had no C# port yet.

using Ts.Ast;

namespace Ts.Checker;

public static class CheckerFindRefsLsExports
{
    // exports.go:257 — GetIndexSignaturesAtLocation (checker.go:31521 body).
    public static global::Ts.Ast.Node[] GetIndexSignaturesAtLocation(this Checker c, global::Ts.Ast.Node node) =>
        c.GetIndexSignaturesAtLocationCore(node);

    // services.go:484 — GetShorthandAssignmentValueSymbol.
    public static global::Ts.Ast.Symbol? GetShorthandAssignmentValueSymbol(this Checker c, global::Ts.Ast.Node? location) =>
        c.GetShorthandAssignmentValueSymbolCore(location);

    // services.go:465 — GetExportSpecifierLocalTargetSymbol.
    public static global::Ts.Ast.Symbol? GetExportSpecifierLocalTargetSymbol(this Checker c, global::Ts.Ast.Node node) =>
        c.GetExportSpecifierLocalTargetSymbolCore(node);

    // services.go:497 — GetSymbolsOfParameterPropertyDeclaration.
    public static (global::Ts.Ast.Symbol ParameterSymbol, global::Ts.Ast.Symbol PropertySymbol) GetSymbolsOfParameterPropertyDeclaration(
        this Checker c, global::Ts.Ast.Node parameter, string parameterName) =>
        c.GetSymbolsOfParameterPropertyDeclarationCore(parameter, parameterName);

    // services.go:1060 — GetPropertySymbolOfDestructuringAssignment.
    public static global::Ts.Ast.Symbol? GetPropertySymbolOfDestructuringAssignment(this Checker c, global::Ts.Ast.Node location) =>
        c.GetPropertySymbolOfDestructuringAssignmentCore(location);

    // services.go — GetImmediateAliasedSymbol public face (internal instance core keeps
    // its name; internal call sites inside Ts.Compiler keep resolving to it).
    public static global::Ts.Ast.Symbol? GetImmediateAliasedSymbolEx(this Checker c, global::Ts.Ast.Symbol symbol) =>
        c.GetImmediateAliasedSymbol(symbol);

    // checker/utilities.go — getSuperContainer (internal static core on Checker; already
    // consumed inside Ts.Compiler by Checker.Expressions.cs). Receiver-free static form:
    // the Go original is an ast-package free function.
    public static global::Ts.Ast.Node? GetSuperContainerForFindRefs(global::Ts.Ast.Node node, bool stopOnFunctions) =>
        Checker.GetSuperContainer(node, stopOnFunctions);

    // printer/utilities.go — RangeIsOnSingleLine (internal core on PrinterUtilities; the
    // string-literal reference family needs it to filter multi-line templates).
    public static bool RangeIsOnSingleLine(global::Ts.Core.TextRange r, global::Ts.Ast.SourceFile sourceFile) =>
        global::Ts.Printer.PrinterUtilities.RangeIsOnSingleLine(r, sourceFile);

    // checker/services.go — WalkUpBindingElementsAndPatterns (internal static core on
    // Checker; the Go original is an ast-package free function).
    public static global::Ts.Ast.Node WalkUpBindingElementsAndPatternsForFindRefs(global::Ts.Ast.Node binding) =>
        Checker.WalkUpBindingElementsAndPatterns(binding);
}

public sealed partial class Checker
{
    // checker.go:31521 — getIndexSignaturesAtLocation.
    internal global::Ts.Ast.Node[] GetIndexSignaturesAtLocationCore(global::Ts.Ast.Node node)
    {
        var signatures = new System.Collections.Generic.List<global::Ts.Ast.Node>();
        if (NodeTests.IsIdentifier(node) && NodeTests.IsPropertyAccessExpression(node.Parent!) && node.Parent!.Name() == node)
        {
            var keyType = GetLiteralTypeFromPropertyName(node);
            var objectType = GetTypeOfExpression(node.Parent!.Expression()!);
            foreach (var t in objectType.Distributed())
            {
                foreach (var info in GetApplicableIndexInfos(t, keyType) ?? [])
                {
                    if (info.declaration != null && !signatures.Contains(info.declaration))
                    {
                        // core.AppendIfUnique.
                        signatures.Add(info.declaration);
                    }
                }
            }
        }
        return signatures.ToArray();
    }

    // services.go:484 — getShorthandAssignmentValueSymbol.
    internal global::Ts.Ast.Symbol? GetShorthandAssignmentValueSymbolCore(global::Ts.Ast.Node? location)
    {
        if (location != null && NodeTests.IsShorthandPropertyAssignment(location))
        {
            return ResolveEntityName(location.Name()!, SymbolFlags.Value | SymbolFlags.Alias, true /*ignoreErrors*/, false, null);
        }
        return null;
    }

    // services.go:465 — getExportSpecifierLocalTargetSymbol.
    internal global::Ts.Ast.Symbol? GetExportSpecifierLocalTargetSymbolCore(global::Ts.Ast.Node node)
    {
        // node should be ExportSpecifier | Identifier
        switch (node.Kind)
        {
            case SyntaxKind.ExportSpecifier:
                if (node.Parent!.Parent!.ModuleSpecifier() != null)
                {
                    return GetExternalModuleMember(node.Parent!.Parent!, node, false /*dontResolveAlias*/);
                }
                var name = node.PropertyNameOrName()!;
                if (NodeTests.IsStringLiteral(name))
                {
                    // Skip for invalid syntax like this: export { "x" }
                    return null;
                }
                return ResolveEntityName(name, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias, true /*ignoreErrors*/, false, null);
            case SyntaxKind.Identifier:
                return ResolveEntityName(node, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias, true /*ignoreErrors*/, false, null);
        }
        // Go panic; InvalidOperationException is the repository's panic mapping.
        throw new System.InvalidOperationException("Unhandled case in getExportSpecifierLocalTargetSymbol, node should be ExportSpecifier | Identifier");
    }

    // services.go:497 — getSymbolsOfParameterPropertyDeclaration.
    internal (global::Ts.Ast.Symbol ParameterSymbol, global::Ts.Ast.Symbol PropertySymbol) GetSymbolsOfParameterPropertyDeclarationCore(
        global::Ts.Ast.Node parameter, string parameterName)
    {
        var constructorDeclaration = parameter.Parent!;
        var classDeclaration = parameter.Parent!.Parent!;

        var parameterSymbol = GetSymbol(constructorDeclaration.Locals(), parameterName, SymbolFlags.Value);
        var propertySymbol = GetSymbol(GetMembersOfSymbol(classDeclaration.Symbol()!), parameterName, SymbolFlags.Value);

        if (parameterSymbol != null && propertySymbol != null)
        {
            return (parameterSymbol, propertySymbol);
        }

        // Go panic; InvalidOperationException is the repository's panic mapping.
        throw new System.InvalidOperationException("There should exist two symbols, one as property declaration and one as parameter declaration");
    }

    // services.go:1060 — getPropertySymbolOfDestructuringAssignment.
    internal global::Ts.Ast.Symbol? GetPropertySymbolOfDestructuringAssignmentCore(global::Ts.Ast.Node location)
    {
        if (IsArrayLiteralOrObjectLiteralDestructuringPattern(location.Parent!.Parent!))
        {
            // Get the type of the object or array literal and then look for property of given name in the type
            var typeOfObjectLiteral = GetTypeOfAssignmentPattern(location.Parent!.Parent!);
            if (typeOfObjectLiteral != null)
            {
                return GetPropertyOfType(typeOfObjectLiteral, location.Text());
            }
        }
        return null;
    }

    // ast/ast.go:1403 — IsArrayLiteralOrObjectLiteralDestructuringPattern (no Ts.Ast port
    // yet; the Ts.Ls copies live in SourceDefinitionDeps/LsDeps).
    private static bool IsArrayLiteralOrObjectLiteralDestructuringPattern(global::Ts.Ast.Node node)
    {
        if (!(NodeTests.IsArrayLiteralExpression(node) || NodeTests.IsObjectLiteralExpression(node)))
        {
            return false;
        }
        var parent = node.Parent;
        // [a,b,c] from:
        // [a, b, c] = someExpression;
        if (NodeTests.IsBinaryExpression(parent!) && parent!.AsBinaryExpression().Left == node && parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken)
        {
            return true;
        }
        // [a, b, c] from:
        // for([a, b, c] of expression)
        if (NodeTests.IsForOfStatement(parent!) && parent!.Initializer() == node)
        {
            return true;
        }
        // {x, a: {a, b, c} } = someExpression
        if (NodeTests.IsPropertyAssignment(parent!))
        {
            return IsArrayLiteralOrObjectLiteralDestructuringPattern(parent!.Parent!);
        }
        // [a, b, c] of
        // [x, [a, b, c] ] = someExpression
        return IsArrayLiteralOrObjectLiteralDestructuringPattern(parent!);
    }

    // services.go:1079 — getTypeOfAssignmentPattern. Private Go body with no C# port;
    // carried here verbatim beside its only external consumer.
    internal global::Ts.Checker.Type? GetTypeOfAssignmentPattern(global::Ts.Ast.Node expr)
    {
        // If this is from "for of"
        //     for ( { a } of elems) {
        //     }
        if (NodeTests.IsForOfStatement(expr.Parent!))
        {
            var iteratedType = CheckRightHandSideOfForOf(expr.Parent!);
            return CheckDestructuringAssignment(expr, iteratedType ?? errorType!, CheckMode.Normal, false);
        }
        // If this is from "for" initializer
        //     for ({a } = elems[0];.....) { }
        if (NodeTests.IsBinaryExpression(expr.Parent!))
        {
            var iteratedType = GetTypeOfExpression(expr.Parent!.AsBinaryExpression().Right!);
            return CheckDestructuringAssignment(expr, iteratedType ?? errorType!, CheckMode.Normal, false);
        }
        // If this is from nested object binding pattern
        //     for ({ skills: { primary, secondary } } = multiRobot, i = 0; i < 1; i++) {
        if (NodeTests.IsPropertyAssignment(expr.Parent!))
        {
            var node = expr.Parent!.Parent!;
            var typeOfParentObjectLiteral = GetTypeOfAssignmentPattern(node) ?? errorType!;
            var properties = node.Properties();
            var propertyIndex = -1;
            for (var i = 0; i < properties!.Length; i++)
            {
                if (properties[i] == expr.Parent)
                {
                    propertyIndex = i;
                    break;
                }
            }
            return CheckObjectLiteralDestructuringPropertyAssignment(node, typeOfParentObjectLiteral, propertyIndex, null, false);
        }
        // Array literal assignment - array destructuring pattern
        var arrayNode = expr.Parent!;
        //    [{ property1: p1, property2 }] = elems;
        var typeOfArrayLiteral = GetTypeOfAssignmentPattern(arrayNode) ?? errorType!;
        var elements = arrayNode.Elements();
        var elementIndex = -1;
        for (var i = 0; i < elements!.Length; i++)
        {
            if (elements[i] == expr)
            {
                elementIndex = i;
                break;
            }
        }
        var elementType = CheckIteratedTypeOrElementType(IterationUse.Destructuring, typeOfArrayLiteral, undefinedType!, arrayNode.Parent) ?? errorType!;
        return CheckArrayLiteralDestructuringElementAssignment(arrayNode, typeOfArrayLiteral, elementIndex, elementType, CheckMode.Normal);
    }
}

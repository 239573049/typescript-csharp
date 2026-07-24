// Ported from tsgo internal/ls/lsutil/symbol_display.go (whole file): ScriptElementKind /
// ScriptElementKindModifier and the symbol-kind/modifier classification shared by completions
// and navigation features.

using System.Collections.Generic;
using Ts.Ast;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

// symbol_display.go:10-82 — ScriptElementKind.
public enum ScriptElementKind
{
    Unknown = 0,
    Warning,
    // predefined type (void) or keyword (class)
    Keyword,
    // top level script node
    ScriptElement,
    // module foo {}
    ModuleElement,
    // class X {}
    ClassElement,
    // var x = class X {}
    LocalClassElement,
    // interface Y {}
    InterfaceElement,
    // type T = ...
    TypeElement,
    // enum E {}
    EnumElement,
    EnumMemberElement,
    // Inside module and script only.
    // const v = ...
    VariableElement,
    // Inside function.
    LocalVariableElement,
    // using foo = ...
    VariableUsingElement,
    // await using foo = ...
    VariableAwaitUsingElement,
    // Inside module and script only.
    // function f() {}
    FunctionElement,
    // Inside function.
    LocalFunctionElement,
    // class X { [public|private]* foo() {} }
    MemberFunctionElement,
    // class X { [public|private]* [get|set] foo:number; }
    MemberGetAccessorElement,
    MemberSetAccessorElement,
    // class X { [public|private]* foo:number; }
    // interface Y { foo:number; }
    MemberVariableElement,
    // class X { [public|private]* accessor foo: number; }
    MemberAccessorVariableElement,
    // class X { constructor() { } }
    // class X { static { } }
    ConstructorImplementationElement,
    // interface Y { ():number; }
    CallSignatureElement,
    // interface Y { []:number; }
    IndexSignatureElement,
    // interface Y { new():Y; }
    ConstructSignatureElement,
    // function foo(*Y*: string)
    ParameterElement,
    TypeParameterElement,
    PrimitiveType,
    Label,
    Alias,
    ConstElement,
    LetElement,
    Directory,
    ExternalModuleName,
    // String literal
    String,
    // Jsdoc @link: in `{@link C link text}`, the before and after text "{@link " and "}"
    Link,
    // Jsdoc @link: in `{@link C link text}`, the entity name "C"
    LinkName,
    // Jsdoc @link: in `{@link C link text}`, the link text "link text"
    LinkText,
}

// symbol_display.go:84-109 — ScriptElementKindModifier.
[global::System.Flags]
public enum ScriptElementKindModifier : uint
{
    None = 0,
    Public = 1 << 1,
    Private = 1 << 2,
    Protected = 1 << 3,
    Exported = 1 << 4,
    Ambient = 1 << 5,
    Static = 1 << 6,
    Abstract = 1 << 7,
    Optional = 1 << 8,
    Deprecated = 1 << 9,
    Dts = 1 << 10,
    Ts = 1 << 11,
    Tsx = 1 << 12,
    Js = 1 << 13,
    Jsx = 1 << 14,
    Json = 1 << 15,
    Dmts = 1 << 16,
    Mts = 1 << 17,
    Mjs = 1 << 18,
    Dcts = 1 << 19,
    Cts = 1 << 20,
    Cjs = 1 << 21,
}

public static class SymbolDisplay
{
    // symbol_display.go:111-136 — scriptElementKindModifierNames.
    private static readonly (ScriptElementKindModifier Flag, string Name)[] ScriptElementKindModifierNames =
    [
        (ScriptElementKindModifier.Public, "public"),
        (ScriptElementKindModifier.Private, "private"),
        (ScriptElementKindModifier.Protected, "protected"),
        (ScriptElementKindModifier.Exported, "export"),
        (ScriptElementKindModifier.Ambient, "declare"),
        (ScriptElementKindModifier.Static, "static"),
        (ScriptElementKindModifier.Abstract, "abstract"),
        (ScriptElementKindModifier.Optional, "optional"),
        (ScriptElementKindModifier.Deprecated, "deprecated"),
        (ScriptElementKindModifier.Dts, ".d.ts"),
        (ScriptElementKindModifier.Ts, ".ts"),
        (ScriptElementKindModifier.Tsx, ".tsx"),
        (ScriptElementKindModifier.Js, ".js"),
        (ScriptElementKindModifier.Jsx, ".jsx"),
        (ScriptElementKindModifier.Json, ".json"),
        (ScriptElementKindModifier.Dmts, ".d.mts"),
        (ScriptElementKindModifier.Mts, ".mts"),
        (ScriptElementKindModifier.Mjs, ".mjs"),
        (ScriptElementKindModifier.Dcts, ".d.cts"),
        (ScriptElementKindModifier.Cts, ".cts"),
        (ScriptElementKindModifier.Cjs, ".cjs"),
    ];

    // symbol_display.go:138-146 — (m ScriptElementKindModifier) Strings().
    public static HashSet<string> Strings(this ScriptElementKindModifier m)
    {
        var result = new HashSet<string>(global::System.StringComparer.Ordinal);
        foreach (var (flag, name) in ScriptElementKindModifierNames)
        {
            if ((m & flag) != 0)
            {
                result.Add(name);
            }
        }
        return result;
    }

    // symbol_display.go:148-160 — FileExtensionKindModifiers.
    public const ScriptElementKindModifier FileExtensionKindModifiers =
        ScriptElementKindModifier.Dts |
        ScriptElementKindModifier.Ts |
        ScriptElementKindModifier.Tsx |
        ScriptElementKindModifier.Js |
        ScriptElementKindModifier.Jsx |
        ScriptElementKindModifier.Json |
        ScriptElementKindModifier.Dmts |
        ScriptElementKindModifier.Mts |
        ScriptElementKindModifier.Mjs |
        ScriptElementKindModifier.Dcts |
        ScriptElementKindModifier.Cts |
        ScriptElementKindModifier.Cjs;

    // symbol_display.go:162-198 — GetSymbolKind.
    public static ScriptElementKind GetSymbolKind(TsChecker? typeChecker, Symbol symbol, Node location)
    {
        var result = GetSymbolKindOfConstructorPropertyMethodAccessorFunctionOrVar(typeChecker, symbol, location);
        if (result != ScriptElementKind.Unknown)
        {
            return result;
        }
        var flags = symbol.CombinedLocalAndExportSymbolFlags();
        if ((flags & SymbolFlags.Class) != 0)
        {
            var decl = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.ClassExpression);
            if (decl != null)
            {
                return ScriptElementKind.LocalClassElement;
            }
            return ScriptElementKind.ClassElement;
        }
        if ((flags & SymbolFlags.Enum) != 0)
        {
            return ScriptElementKind.EnumElement;
        }
        if ((flags & SymbolFlags.TypeAlias) != 0)
        {
            return ScriptElementKind.TypeElement;
        }
        if ((flags & SymbolFlags.Interface) != 0)
        {
            return ScriptElementKind.InterfaceElement;
        }
        if ((flags & SymbolFlags.TypeParameter) != 0)
        {
            return ScriptElementKind.TypeParameterElement;
        }
        if ((flags & SymbolFlags.EnumMember) != 0)
        {
            return ScriptElementKind.EnumMemberElement;
        }
        if ((flags & SymbolFlags.Alias) != 0)
        {
            return ScriptElementKind.Alias;
        }
        if ((flags & SymbolFlags.Module) != 0)
        {
            return ScriptElementKind.ModuleElement;
        }

        return ScriptElementKind.Unknown;
    }

    // symbol_display.go:200-297 — getSymbolKindOfConstructorPropertyMethodAccessorFunctionOrVar.
    private static ScriptElementKind GetSymbolKindOfConstructorPropertyMethodAccessorFunctionOrVar(TsChecker? typeChecker, Symbol symbol, Node location)
    {
        Symbol[] roots;
        if (typeChecker != null)
        {
            roots = typeChecker.GetRootSymbols(symbol);
        }
        else
        {
            roots = [symbol];
        }

        // If this is a method from a mapped type, leave as a method so long as it still has a call signature, as opposed to e.g.
        // `{ [K in keyof I]: number }`.
        if (roots.Length == 1 &&
            (roots[0].Flags & SymbolFlags.Method) != 0 &&
            (typeChecker == null || typeChecker.GetCallSignatures(typeChecker.GetNonNullableType(typeChecker.GetTypeOfSymbolAtLocation(symbol, location))).Length > 0))
        {
            return ScriptElementKind.MemberFunctionElement;
        }

        if (typeChecker != null)
        {
            if (typeChecker.IsUndefinedSymbol(symbol))
            {
                return ScriptElementKind.VariableElement;
            }
            if (typeChecker.IsArgumentsSymbol(symbol))
            {
                return ScriptElementKind.LocalVariableElement;
            }
            if (location.Kind == SyntaxKind.ThisKeyword && AstUtilities.IsExpression(location) ||
                AstUtilities.IsThisInTypeQuery(location))
            {
                return ScriptElementKind.ParameterElement;
            }
        }

        var flags = symbol.CombinedLocalAndExportSymbolFlags();
        if ((flags & SymbolFlags.Variable) != 0)
        {
            if (IsFirstDeclarationOfSymbolParameter(symbol))
            {
                return ScriptElementKind.ParameterElement;
            }
            else if (symbol.ValueDeclaration != null && AstUtilities.IsVarConst(symbol.ValueDeclaration))
            {
                return ScriptElementKind.ConstElement;
            }
            else if (symbol.ValueDeclaration != null && AstUtilities.IsVarUsing(symbol.ValueDeclaration))
            {
                return ScriptElementKind.VariableUsingElement;
            }
            else if (symbol.ValueDeclaration != null && AstUtilities.IsVarAwaitUsing(symbol.ValueDeclaration))
            {
                return ScriptElementKind.VariableAwaitUsingElement;
            }
            else if (global::Ts.Core.CoreUtil.Some(symbol.Declarations, AstUtilities.IsLet))
            {
                return ScriptElementKind.LetElement;
            }
            if (IsLocalVariableOrFunction(symbol))
            {
                return ScriptElementKind.LocalVariableElement;
            }
            return ScriptElementKind.VariableElement;
        }
        if ((flags & SymbolFlags.Function) != 0)
        {
            if (IsLocalVariableOrFunction(symbol))
            {
                return ScriptElementKind.LocalFunctionElement;
            }
            return ScriptElementKind.FunctionElement;
        }
        // FIXME: getter and setter use the same symbol. And it is rare to use only setter without getter, so in most cases the symbol always has getter flag.
        // So, even when the location is just on the declaration of setter, this function returns getter.
        if ((flags & SymbolFlags.GetAccessor) != 0)
        {
            return ScriptElementKind.MemberGetAccessorElement;
        }
        if ((flags & SymbolFlags.SetAccessor) != 0)
        {
            return ScriptElementKind.MemberSetAccessorElement;
        }
        if ((flags & SymbolFlags.Method) != 0)
        {
            return ScriptElementKind.MemberFunctionElement;
        }
        if ((flags & SymbolFlags.Constructor) != 0)
        {
            return ScriptElementKind.ConstructorImplementationElement;
        }
        if ((flags & SymbolFlags.Signature) != 0)
        {
            return ScriptElementKind.IndexSignatureElement;
        }

        if ((flags & SymbolFlags.Property) != 0)
        {
            if (typeChecker != null && (flags & SymbolFlags.Transient) != 0 && (symbol.CheckFlags & CheckFlags.Synthetic) != 0)
            {
                // If union property is result of union of non method (property/accessors/variables), it is labeled as property
                var unionPropertyKind = ScriptElementKind.Unknown;
                foreach (var rootSymbol in roots)
                {
                    if ((rootSymbol.Flags & (SymbolFlags.PropertyOrAccessor | SymbolFlags.Variable)) != 0)
                    {
                        unionPropertyKind = ScriptElementKind.MemberVariableElement;
                        break;
                    }
                }
                if (unionPropertyKind == ScriptElementKind.Unknown)
                {
                    // If this was union of all methods,
                    // make sure it has call signatures before we can label it as method.
                    var typeOfUnionProperty = typeChecker.GetTypeOfSymbolAtLocation(symbol, location);
                    if (typeChecker.GetCallSignatures(typeOfUnionProperty).Length > 0)
                    {
                        return ScriptElementKind.MemberFunctionElement;
                    }
                    return ScriptElementKind.MemberVariableElement;
                }
                return unionPropertyKind;
            }

            return ScriptElementKind.MemberVariableElement;
        }

        return ScriptElementKind.Unknown;
    }

    // symbol_display.go:299-315 — isFirstDeclarationOfSymbolParameter.
    private static bool IsFirstDeclarationOfSymbolParameter(Symbol symbol)
    {
        Node? declaration = null;
        if ((symbol.Declarations ?? []).Length > 0)
        {
            declaration = symbol.Declarations![0];
        }
        var result = AstUtilities.FindAncestorOrQuit(declaration, n =>
        {
            if (NodeTests.IsParameterDeclaration(n))
            {
                return FindAncestorResult.True;
            }
            if (NodeTests.IsBindingElement(n) || NodeTests.IsObjectBindingPattern(n) || NodeTests.IsArrayBindingPattern(n))
            {
                return FindAncestorResult.False;
            }
            return FindAncestorResult.Quit;
        });

        return result != null;
    }

    // symbol_display.go:317-347 — isLocalVariableOrFunction.
    private static bool IsLocalVariableOrFunction(Symbol symbol)
    {
        if (symbol.Parent != null)
        {
            return false; // This is exported symbol
        }

        foreach (var decl in symbol.Declarations ?? [])
        {
            // Function expressions are local
            if (decl.Kind == SyntaxKind.FunctionExpression)
            {
                return true;
            }

            if (decl.Kind != SyntaxKind.VariableDeclaration && decl.Kind != SyntaxKind.FunctionDeclaration)
            {
                continue;
            }

            // If the parent is not source file or module block, it is a local variable.
            var parent = decl.Parent!;
            for (; !AstUtilities.IsFunctionBlock(parent); parent = parent.Parent!)
            {
                // Reached source file or module block
                if (parent.Kind == SyntaxKind.SourceFile || parent.Kind == SyntaxKind.ModuleBlock)
                {
                    break;
                }
            }

            if (AstUtilities.IsFunctionBlock(parent))
            {
                // Parent is in function block.
                return true;
            }
        }
        return false;
    }

    // symbol_display.go:349-367 — GetSymbolModifiers.
    public static ScriptElementKindModifier GetSymbolModifiers(TsChecker? typeChecker, Symbol? symbol)
    {
        if (symbol == null)
        {
            return ScriptElementKindModifier.None;
        }

        var modifiers = GetNormalizedSymbolModifiers(typeChecker, symbol);
        if ((symbol.Flags & SymbolFlags.Alias) != 0 && typeChecker != null)
        {
            var resolvedSymbol = typeChecker.GetAliasedSymbol(symbol);
            if (resolvedSymbol != symbol)
            {
                modifiers |= GetNormalizedSymbolModifiers(typeChecker, resolvedSymbol!);
            }
        }
        if ((symbol.Flags & SymbolFlags.Optional) != 0)
        {
            modifiers |= ScriptElementKindModifier.Optional;
        }

        return modifiers;
    }

    // symbol_display.go:369-388 — getNormalizedSymbolModifiers.
    private static ScriptElementKindModifier GetNormalizedSymbolModifiers(TsChecker? typeChecker, Symbol symbol)
    {
        var modifierSet = ScriptElementKindModifier.None;
        if ((symbol.Declarations ?? []).Length > 0)
        {
            var declaration = symbol.Declarations![0];
            var declarations = symbol.Declarations[1..];
            // omit deprecated flag if some declarations are not deprecated
            ModifierFlags excludeFlags;
            if (declarations.Length > 0 &&
                IsDeprecatedDeclaration(typeChecker, declaration) && // !!! include jsdoc node flags
                global::Ts.Core.CoreUtil.Some(declarations, d => !IsDeprecatedDeclaration(typeChecker, d)))
            {
                excludeFlags = ModifierFlags.Deprecated;
            }
            else
            {
                excludeFlags = ModifierFlags.None;
            }
            modifierSet = GetNodeModifiers(typeChecker, declaration, excludeFlags);
        }

        return modifierSet;
    }

    // symbol_display.go:390-395 — isDeprecatedDeclaration.
    private static bool IsDeprecatedDeclaration(TsChecker? typeChecker, Node declaration)
    {
        if (typeChecker != null)
        {
            return typeChecker.IsDeprecatedDeclaration(declaration);
        }
        return AstUtilities.IsDeprecatedDeclaration(declaration);
    }

    // symbol_display.go:397-438 — getNodeModifiers.
    private static ScriptElementKindModifier GetNodeModifiers(TsChecker? typeChecker, Node node, ModifierFlags excludeFlags)
    {
        var result = ScriptElementKindModifier.None;
        var flags = ModifierFlags.None;
        if (AstUtilities.IsDeclaration(node))
        {
            flags = AstUtilities.GetCombinedModifierFlags(node);
            if (IsDeprecatedDeclaration(typeChecker, node))
            {
                flags |= ModifierFlags.Deprecated;
            }
            flags &= ~excludeFlags;
        }

        if ((flags & ModifierFlags.Private) != 0)
        {
            result |= ScriptElementKindModifier.Private;
        }
        if ((flags & ModifierFlags.Protected) != 0)
        {
            result |= ScriptElementKindModifier.Protected;
        }
        if ((flags & ModifierFlags.Public) != 0)
        {
            result |= ScriptElementKindModifier.Public;
        }
        if ((flags & ModifierFlags.Static) != 0)
        {
            result |= ScriptElementKindModifier.Static;
        }
        if ((flags & ModifierFlags.Abstract) != 0)
        {
            result |= ScriptElementKindModifier.Abstract;
        }
        if ((flags & ModifierFlags.Export) != 0)
        {
            result |= ScriptElementKindModifier.Exported;
        }
        if ((flags & ModifierFlags.Deprecated) != 0)
        {
            result |= ScriptElementKindModifier.Deprecated;
        }
        if ((flags & ModifierFlags.Ambient) != 0)
        {
            result |= ScriptElementKindModifier.Ambient;
        }
        if ((node.Flags & NodeFlags.Ambient) != 0)
        {
            result |= ScriptElementKindModifier.Ambient;
        }
        if (node.Kind == SyntaxKind.ExportAssignment)
        {
            result |= ScriptElementKindModifier.Exported;
        }

        return result;
    }
}

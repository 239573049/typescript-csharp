using System;

namespace Ts.Ast;

// Ported from tsgo internal/ast/symbol.go.
// `SymbolTable` (Go `type SymbolTable map[string]*Symbol`) is a project-level global
// using alias to Dictionary<string, Symbol> (plan/05 §2.3), declared in GlobalUsings.cs.

// Go: internal/ast/symbol.go:10-21 — full port (phase 4, binder). Field shape follows
// plan/03-checker-design.md §11.1. Declarations is Node[] per plan/05 §6.1 ([]*Node → T[])
// and the dispatcher's port note (Go `[]*ast.Node`).
public sealed class Symbol
{
    public SymbolFlags Flags;
    public CheckFlags CheckFlags; // Non-zero only in transient symbols created by Checker
    public string Name = "";
    public Node[]? Declarations; // Go []*Node
    public Node? ValueDeclaration;
    public SymbolTable? Members;
    public SymbolTable? Exports;
    internal ulong id; // Go atomic.Uint64; accessed via Interlocked in AstUtilities.GetSymbolId
    public Symbol? Parent;
    public Symbol? ExportSymbol;

    // Go: symbol.go:23-25
    public bool IsExternalModule()
    {
        return (Flags & SymbolFlags.Module) != 0 && Name.Length > 0 && Name[0] == '"';
    }

    // Go: symbol.go:27-33
    public bool IsStatic()
    {
        if (ValueDeclaration == null)
        {
            return false;
        }
        var modifierFlags = ValueDeclaration.ModifierFlags();
        return (modifierFlags & ModifierFlags.Static) != 0;
    }

    // See comment on `declareModuleMember` in `binder.go`.
    // Go: symbol.go:35-41
    public SymbolFlags CombinedLocalAndExportSymbolFlags()
    {
        if (ExportSymbol != null)
        {
            return Flags | ExportSymbol.Flags;
        }
        return Flags;
    }
}

// Go: internal/ast/symbol.go — internal symbol name sentinels.
public static class InternalSymbolName
{
    // Go: symbol.go:47 — `const InternalSymbolNamePrefix = "\xFE"` (invalid UTF-8 byte in
    // Go; never occurs as an IdentifierName). PORT-NOTE: in UTF-16 this is the single code
    // unit U+00FE; escaping to "__" (below) is what reaches baselines, so the raw sentinel
    // is internal-only. See binder report for the UTF-16 identifier-collision note.
    public const string Prefix = "\xFE";

    public const string Call = Prefix + "call"; // Call signatures
    public const string Constructor = Prefix + "constructor"; // Constructor implementations
    public const string New = Prefix + "new"; // Constructor signatures
    public const string Index = Prefix + "index"; // Index signatures
    public const string ExportStar = Prefix + "export"; // Module export * declarations
    public const string Global = Prefix + "global"; // Global self-reference
    public const string Missing = Prefix + "missing"; // Indicates missing symbol
    public const string Type = Prefix + "type"; // Anonymous type literal symbol
    public const string Object = Prefix + "object"; // Anonymous object literal declaration
    public const string JSXAttributes = Prefix + "jsxAttributes"; // Anonymous JSX attributes object literal declaration
    public const string Class = Prefix + "class"; // Unnamed class expression
    public const string Function = Prefix + "function"; // Unnamed function expression
    public const string Computed = Prefix + "computed"; // Computed property name declaration with dynamic name
    public const string AssignmentDeclaration = Prefix + "assignment"; // Assignment declarations
    public const string InstantiationExpression = Prefix + "instantiationExpression"; // Instantiation expressions
    public const string ImportAttributes = Prefix + "importAttributes";
    public const string ExportEquals = "export="; // Export assignment symbol
    public const string Default = "default"; // Default export symbol (technically not wholly internal, but included here for usability)
    public const string This = "this";
    public const string ModuleExports = "module.exports";
}

public static partial class AstUtilities
{
    // Go: symbol.go:72-77
    public static string SymbolName(Symbol symbol)
    {
        if (symbol.ValueDeclaration != null && IsPrivateIdentifierClassElementDeclaration(symbol.ValueDeclaration))
        {
            return symbol.ValueDeclaration.Name()!.Text();
        }
        return symbol.Name;
    }

    // EscapeAllInternalSymbolNames replaces internal symbol name markers ("\xFE") with "__".
    // Go: symbol.go:79-82
    public static string EscapeAllInternalSymbolNames(string name)
    {
        return name.Replace(InternalSymbolName.Prefix, "__", StringComparison.Ordinal);
    }

    // Go: symbol.go:84-89
    public static string EscapeInternalSymbolName(string name)
    {
        if (name.StartsWith(InternalSymbolName.Prefix, StringComparison.Ordinal))
        {
            return "__" + name[InternalSymbolName.Prefix.Length..];
        }
        return name;
    }

    // EscapeSymbolName converts a binder symbol name into its escaped "__String"
    // form. Internal names (prefixed with the "\xFE" sentinel) become "__"-prefixed,
    // and user names that already begin with "__" gain an extra leading underscore
    // so they can be distinguished from internal names.
    // Go: symbol.go:95-103
    public static string EscapeSymbolName(string name)
    {
        if (name.StartsWith(InternalSymbolName.Prefix, StringComparison.Ordinal))
        {
            return "__" + name[InternalSymbolName.Prefix.Length..];
        }
        if (name.Length >= 2 && name[0] == '_' && name[1] == '_')
        {
            return "_" + name;
        }
        return name;
    }

    // Go: internal/ast/utilities.go:46-51 — lazily initialize a SymbolTable field.
    public static SymbolTable GetSymbolTable(ref SymbolTable? data)
    {
        return data ??= new SymbolTable();
    }

    // Go: utilities.go:53-55
    public static SymbolTable GetMembers(Symbol symbol)
    {
        return symbol.Members ??= new SymbolTable();
    }

    // Go: utilities.go:57-59
    public static SymbolTable GetExports(Symbol symbol)
    {
        return symbol.Exports ??= new SymbolTable();
    }

    // Go: utilities.go:61-63
    public static SymbolTable GetLocals(Node container)
    {
        var data = container.LocalsContainerData()!;
        return data.Locals ??= new SymbolTable();
    }
}

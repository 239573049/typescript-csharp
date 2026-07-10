namespace Ts.Ast;

// Go: internal/ast/utilities.go — partial port (phase 0): only ModifierToFlag /
// ModifiersToFlags, which NodeFactory.NewModifierList needs. The full utilities.go
// port (phase 3) extends this class, keeping Go source order within that port.
public static partial class AstUtilities
{
    // utilities.go:971-1007
    public static ModifierFlags ModifierToFlag(SyntaxKind token)
    {
        switch (token)
        {
            case SyntaxKind.StaticKeyword:
                return ModifierFlags.Static;
            case SyntaxKind.PublicKeyword:
                return ModifierFlags.Public;
            case SyntaxKind.ProtectedKeyword:
                return ModifierFlags.Protected;
            case SyntaxKind.PrivateKeyword:
                return ModifierFlags.Private;
            case SyntaxKind.AbstractKeyword:
                return ModifierFlags.Abstract;
            case SyntaxKind.AccessorKeyword:
                return ModifierFlags.Accessor;
            case SyntaxKind.ExportKeyword:
                return ModifierFlags.Export;
            case SyntaxKind.DeclareKeyword:
                return ModifierFlags.Ambient;
            case SyntaxKind.ConstKeyword:
                return ModifierFlags.Const;
            case SyntaxKind.DefaultKeyword:
                return ModifierFlags.Default;
            case SyntaxKind.AsyncKeyword:
                return ModifierFlags.Async;
            case SyntaxKind.ReadonlyKeyword:
                return ModifierFlags.Readonly;
            case SyntaxKind.OverrideKeyword:
                return ModifierFlags.Override;
            case SyntaxKind.InKeyword:
                return ModifierFlags.In;
            case SyntaxKind.OutKeyword:
                return ModifierFlags.Out;
            case SyntaxKind.Decorator:
                return ModifierFlags.Decorator;
        }
        return ModifierFlags.None;
    }

    // utilities.go:1009-1015
    public static ModifierFlags ModifiersToFlags(Node[]? modifiers)
    {
        var flags = ModifierFlags.None;
        if (modifiers != null)
        {
            foreach (var modifier in modifiers)
            {
                flags |= ModifierToFlag(modifier.Kind);
            }
        }
        return flags;
    }
}

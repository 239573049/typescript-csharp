// Ported from tsgo internal/transformers/modifiervisitor.go.

using Ts.Ast;

namespace Ts.Transformers;

public static partial class Transformers
{
    public static ModifierList? ExtractModifiers(
        global::Ts.Printer.EmitContext emitContext,
        ModifierList? modifiers,
        ModifierFlags allowed)
    {
        if (modifiers is null)
        {
            return null;
        }

        var modifierVisitor = new ModifierVisitor(allowed);
        modifierVisitor.NewTransformer(modifierVisitor.Visit, emitContext);
        return modifierVisitor.Visitor().VisitModifiers(modifiers);
    }

    private sealed class ModifierVisitor : Transformer
    {
        private readonly ModifierFlags allowedModifiers;

        public ModifierVisitor(ModifierFlags allowedModifiers)
        {
            this.allowedModifiers = allowedModifiers;
        }

        public Node? Visit(Node node)
        {
            var flags = global::Ts.Ast.AstUtilities.ModifierToFlag(node.Kind);
            if (flags != ModifierFlags.None && (flags & allowedModifiers) == 0)
            {
                return null;
            }
            return node;
        }
    }
}

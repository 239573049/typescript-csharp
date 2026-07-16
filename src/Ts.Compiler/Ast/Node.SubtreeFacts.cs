// Ported from tsgo internal/ast/ast.go — the hand-written per-node computeSubtreeFacts /
// propagateSubtreeFacts overrides (the schema-generated bodies live in Visitors.g.cs; the caching
// SubtreeFactsWorker overrides live in Accessors.g.cs). Nodes absent here and from the generated
// files intentionally fall back to Node's None default (ast.go:1239-1241).

using Ts.Core;

namespace Ts.Ast;

public sealed partial class Token
{
    // ast.go:1620-1665
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        switch (Kind)
        {
            case SyntaxKind.UsingKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsUsing;
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
            case SyntaxKind.ReadonlyKeyword:
            case SyntaxKind.AbstractKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.InKeyword:
            case SyntaxKind.OutKeyword:
            case SyntaxKind.OverrideKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.ExportKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
            case SyntaxKind.AccessorKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsClassFields;
            case SyntaxKind.AsyncKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsAnyAwait;
            case SyntaxKind.SuperKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsLexicalSuper;
            case SyntaxKind.ThisKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsLexicalThis;
            case SyntaxKind.AsteriskAsteriskToken:
            case SyntaxKind.AsteriskAsteriskEqualsToken:
                return Ts.Ast.SubtreeFacts.SubtreeContainsExponentiationOperator;
            case SyntaxKind.QuestionQuestionToken:
                return Ts.Ast.SubtreeFacts.SubtreeContainsNullishCoalescing;
            case SyntaxKind.QuestionDotToken:
                return Ts.Ast.SubtreeFacts.SubtreeContainsOptionalChaining;
            case SyntaxKind.QuestionQuestionEqualsToken:
            case SyntaxKind.BarBarEqualsToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
                return Ts.Ast.SubtreeFacts.SubtreeContainsLogicalAssignments;
        }
        return Ts.Ast.SubtreeFacts.None;
    }
}

public sealed partial class PrivateIdentifier
{
    // ast.go:1667-1669
    internal override SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.SubtreeContainsClassFields;
}

public sealed partial class Decorator
{
    // ast.go:1674-1678
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) |
        Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript |
        Ts.Ast.SubtreeFacts.SubtreeContainsDecorators;
}

public sealed partial class ForInOrOfStatement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Initializer) |
        PropagateSubtreeFacts(Expression) |
        PropagateSubtreeFacts(Statement) |
        (AwaitModifier != null ? Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator : Ts.Ast.SubtreeFacts.None);
}

public sealed partial class ReturnStatement
{
    // return in an ES2018 async generator must be awaited
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator;
}

public sealed partial class CatchClause
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        var res = PropagateSubtreeFacts(VariableDeclaration) |
            PropagateSubtreeFacts(Block);
        if (VariableDeclaration == null)
        {
            res |= Ts.Ast.SubtreeFacts.SubtreeContainsMissingCatchClauseVariable;
        }
        return res;
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsCatchClause;
}

public sealed partial class VariableStatement
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (modifiers != null && (modifiers.ModifierFlags & Ts.Ast.ModifierFlags.Ambient) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(DeclarationList);
        }
    }
}

public sealed partial class VariableDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(name) |
        PropagateEraseableSyntaxSubtreeFacts(ExclamationToken) |
        PropagateEraseableSyntaxSubtreeFacts(Type) |
        PropagateSubtreeFacts(Initializer);
}

public sealed partial class VariableDeclarationList
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateNodeListSubtreeFacts(Declarations, PropagateSubtreeFacts) |
        ((Flags & NodeFlags.Using) != 0 ? Ts.Ast.SubtreeFacts.SubtreeContainsUsing : Ts.Ast.SubtreeFacts.None);

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsVariableDeclarationList;
}

public sealed partial class BindingPattern
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        switch (Kind)
        {
            case SyntaxKind.ObjectBindingPattern:
                return PropagateNodeListSubtreeFacts(Elements, PropagateObjectBindingElementSubtreeFacts);
            case SyntaxKind.ArrayBindingPattern:
                return PropagateNodeListSubtreeFacts(Elements, PropagateBindingElementSubtreeFacts);
            default:
                return Ts.Ast.SubtreeFacts.None;
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsBindingPattern;
}

public sealed partial class ParameterDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (name != null && JSDocReparseAstGaps.IsThisIdentifier(name))
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateEraseableSyntaxSubtreeFacts(QuestionToken) |
                PropagateEraseableSyntaxSubtreeFacts(Type) |
                PropagateSubtreeFacts(Initializer);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsParameter;
}

public sealed partial class BindingElement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(PropertyName) |
        PropagateSubtreeFacts(name) |
        PropagateSubtreeFacts(Initializer) |
        (DotDotDotToken != null ? Ts.Ast.SubtreeFacts.SubtreeContainsRestOrSpread : Ts.Ast.SubtreeFacts.None);
}

public sealed partial class FunctionDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (Body == null || (this.ModifierFlags() & Ts.Ast.ModifierFlags.Ambient) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            var isAsync = (this.ModifierFlags() & Ts.Ast.ModifierFlags.Async) != 0;
            var isGenerator = AsteriskToken != null;
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(AsteriskToken) |
                PropagateSubtreeFacts(name) |
                PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
                PropagateNodeListSubtreeFacts(Parameters, PropagateSubtreeFacts) |
                PropagateEraseableSyntaxSubtreeFacts(Type) |
                PropagateEraseableSyntaxSubtreeFacts(FullSignature) |
                PropagateSubtreeFacts(Body) |
                (isAsync && isGenerator ? Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator : Ts.Ast.SubtreeFacts.None) |
                (isAsync && !isGenerator ? Ts.Ast.SubtreeFacts.SubtreeContainsAnyAwait : Ts.Ast.SubtreeFacts.None);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsFunction;
}

public sealed partial class ClassDeclaration
{
    // ast.go ClassLikeBase.computeSubtreeFacts (the ClassLikeBase mixin's fields are flattened into
    // each introduction class in C#, so the shared Go method body is repeated per class; plan/02 §3).
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (modifiers != null && (modifiers.ModifierFlags & Ts.Ast.ModifierFlags.Ambient) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
                PropagateNodeListSubtreeFacts(HeritageClauses, PropagateSubtreeFacts) |
                PropagateNodeListSubtreeFacts(Members, PropagateSubtreeFacts);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsClass;
}

public sealed partial class ClassExpression
{
    // ast.go ClassLikeBase.computeSubtreeFacts (see note on ClassDeclaration).
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (modifiers != null && (modifiers.ModifierFlags & Ts.Ast.ModifierFlags.Ambient) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
                PropagateNodeListSubtreeFacts(HeritageClauses, PropagateSubtreeFacts) |
                PropagateNodeListSubtreeFacts(Members, PropagateSubtreeFacts);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsClass;
}

public sealed partial class HeritageClause
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        switch (Token)
        {
            case SyntaxKind.ExtendsKeyword:
                return PropagateNodeListSubtreeFacts(Types, PropagateSubtreeFacts);
            case SyntaxKind.ImplementsKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
            default:
                return Ts.Ast.SubtreeFacts.None;
        }
    }
}

public sealed partial class EnumMember
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(name) |
        PropagateSubtreeFacts(Initializer) |
        Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
}

public sealed partial class EnumDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (modifiers != null && (modifiers.ModifierFlags & Ts.Ast.ModifierFlags.Ambient) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateNodeListSubtreeFacts(Members, PropagateSubtreeFacts) |
                Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
    }
}

public sealed partial class ModuleDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if ((this.ModifierFlags() & Ts.Ast.ModifierFlags.Ambient) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateSubtreeFacts(Body) |
                Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsModule;
}

public sealed partial class ImportEqualsDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (IsTypeOnly || !NodeTests.IsExternalModuleReference(ModuleReference!))
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateSubtreeFacts(ModuleReference);
        }
    }
}

public sealed partial class ImportSpecifier
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (IsTypeOnly)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateSubtreeFacts(PropertyName) |
                PropagateSubtreeFacts(name);
        }
    }
}

public sealed partial class ImportClause
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (PhaseModifier == SyntaxKind.TypeKeyword)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateSubtreeFacts(name) |
                PropagateSubtreeFacts(NamedBindings);
        }
    }
}

public sealed partial class ExportAssignment
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateModifierListSubtreeFacts(modifiers) | PropagateSubtreeFacts(Type) | PropagateSubtreeFacts(Expression) | (IsExportEquals ? Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript : Ts.Ast.SubtreeFacts.None);
}

public sealed partial class ExportDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateModifierListSubtreeFacts(modifiers) |
        PropagateSubtreeFacts(ExportClause) |
        PropagateSubtreeFacts(ModuleSpecifier) |
        PropagateSubtreeFacts(Attributes) |
        (IsTypeOnly ? Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript : Ts.Ast.SubtreeFacts.None);
}

public sealed partial class ExportSpecifier
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (IsTypeOnly)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateSubtreeFacts(PropertyName) |
                PropagateSubtreeFacts(name);
        }
    }
}

public sealed partial class ConstructorDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (Body == null)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
                PropagateNodeListSubtreeFacts(Parameters, PropagateSubtreeFacts) |
                PropagateEraseableSyntaxSubtreeFacts(Type) |
                PropagateEraseableSyntaxSubtreeFacts(FullSignature) |
                PropagateSubtreeFacts(Body);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsConstructor;
}

public abstract partial class AccessorDeclarationBase
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (Body == null)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(name) |
                PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
                PropagateNodeListSubtreeFacts(Parameters, PropagateSubtreeFacts) |
                PropagateEraseableSyntaxSubtreeFacts(Type) |
                PropagateEraseableSyntaxSubtreeFacts(FullSignature) |
                PropagateSubtreeFacts(Body);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsAccessor |
        PropagateSubtreeFacts(name);
}

public sealed partial class MethodDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if (Body == null)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
        }
        else
        {
            var isAsync = modifiers != null && (modifiers.ModifierFlags & Ts.Ast.ModifierFlags.Async) != 0;
            var isGenerator = AsteriskToken != null;
            return PropagateModifierListSubtreeFacts(modifiers) |
                PropagateSubtreeFacts(AsteriskToken) |
                PropagateSubtreeFacts(name) |
                PropagateEraseableSyntaxSubtreeFacts(PostfixToken) |
                PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
                PropagateNodeListSubtreeFacts(Parameters, PropagateSubtreeFacts) |
                PropagateSubtreeFacts(Body) |
                PropagateEraseableSyntaxSubtreeFacts(Type) |
                PropagateEraseableSyntaxSubtreeFacts(FullSignature) |
                (isAsync && isGenerator ? Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator : Ts.Ast.SubtreeFacts.None) |
                (isAsync && !isGenerator ? Ts.Ast.SubtreeFacts.SubtreeContainsAnyAwait : Ts.Ast.SubtreeFacts.None);
        }
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsMethod |
        PropagateSubtreeFacts(name);
}

public sealed partial class PropertyDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateModifierListSubtreeFacts(modifiers) |
        PropagateSubtreeFacts(name) |
        PropagateEraseableSyntaxSubtreeFacts(PostfixToken) |
        PropagateEraseableSyntaxSubtreeFacts(Type) |
        PropagateSubtreeFacts(Initializer) |
        Ts.Ast.SubtreeFacts.SubtreeContainsClassFields;

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsProperty |
        PropagateSubtreeFacts(name);
}

public sealed partial class ClassStaticBlockDeclaration
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateModifierListSubtreeFacts(modifiers) |
        PropagateSubtreeFacts(Body) |
        Ts.Ast.SubtreeFacts.SubtreeContainsClassFields;
}

public sealed partial class KeywordExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        switch (Kind)
        {
            case SyntaxKind.ThisKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsLexicalThis;
            case SyntaxKind.SuperKeyword:
                return Ts.Ast.SubtreeFacts.SubtreeContainsLexicalSuper;
        }
        return Ts.Ast.SubtreeFacts.None;
    }
}

public sealed partial class BigIntLiteral
{
    internal override SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.None; // `bigint` is not downleveled in any way
}

public sealed partial class Identifier
{
    internal override SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.SubtreeContainsIdentifier;
}

public sealed partial class NoSubstitutionTemplateLiteral
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if ((TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsInvalidTemplateEscape;
        }
        return Ts.Ast.SubtreeFacts.None;
    }
}

public sealed partial class BinaryExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        var facts = PropagateModifierListSubtreeFacts(modifiers) |
            PropagateSubtreeFacts(Left) |
            PropagateSubtreeFacts(Type) |
            PropagateSubtreeFacts(OperatorToken) |
            PropagateSubtreeFacts(Right) |
            (OperatorToken!.Kind == SyntaxKind.InKeyword && NodeTests.IsPrivateIdentifier(Left!) ? Ts.Ast.SubtreeFacts.SubtreeContainsClassFields | Ts.Ast.SubtreeFacts.SubtreeContainsPrivateIdentifierInExpression : Ts.Ast.SubtreeFacts.None);
        if (OperatorToken.Kind == SyntaxKind.EqualsToken)
        {
            if ((NodeTests.IsObjectLiteralExpression(Left!) || NodeTests.IsArrayLiteralExpression(Left!)) && AstUtilities.ContainsObjectRestOrSpread(Left!))
            {
                facts |= Ts.Ast.SubtreeFacts.SubtreeContainsObjectRestOrSpread;
            }
        }
        return facts;
    }
}

public sealed partial class YieldExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator;
}

public sealed partial class ArrowFunction
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateModifierListSubtreeFacts(modifiers) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
        PropagateNodeListSubtreeFacts(Parameters, PropagateSubtreeFacts) |
        PropagateEraseableSyntaxSubtreeFacts(Type) |
        PropagateEraseableSyntaxSubtreeFacts(FullSignature) |
        PropagateSubtreeFacts(Body) |
        ((this.ModifierFlags() & Ts.Ast.ModifierFlags.Async) != 0 ? Ts.Ast.SubtreeFacts.SubtreeContainsAnyAwait : Ts.Ast.SubtreeFacts.None);

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsArrowFunction;
}

public sealed partial class FunctionExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        var isAsync = modifiers != null && (modifiers.ModifierFlags & Ts.Ast.ModifierFlags.Async) != 0;
        var isGenerator = AsteriskToken != null;
        return PropagateModifierListSubtreeFacts(modifiers) |
            PropagateSubtreeFacts(AsteriskToken) |
            PropagateSubtreeFacts(name) |
            PropagateEraseableSyntaxListSubtreeFacts(TypeParameters) |
            PropagateNodeListSubtreeFacts(Parameters, PropagateSubtreeFacts) |
            PropagateEraseableSyntaxSubtreeFacts(Type) |
            PropagateEraseableSyntaxSubtreeFacts(FullSignature) |
            PropagateSubtreeFacts(Body) |
            (isAsync && isGenerator ? Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator : Ts.Ast.SubtreeFacts.None) |
            (isAsync && !isGenerator ? Ts.Ast.SubtreeFacts.SubtreeContainsAnyAwait : Ts.Ast.SubtreeFacts.None);
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsFunction;
}

public sealed partial class AsExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsOuterExpression;
}

public sealed partial class SatisfiesExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsOuterExpression;
}

public sealed partial class PropertyAccessExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        var privateName = Ts.Ast.SubtreeFacts.None;
        if (!NodeTests.IsIdentifier(name!))
        {
            privateName = Ts.Ast.SubtreeFacts.SubtreeContainsPrivateIdentifierInExpression;
        }
        return PropagateSubtreeFacts(Expression) |
            PropagateSubtreeFacts(QuestionDotToken) |
            PropagateSubtreeFacts(name) | privateName;
    }

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsPropertyAccess;
}

public sealed partial class ElementAccessExpression
{
    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsElementAccess;
}

public sealed partial class CallExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) |
        PropagateSubtreeFacts(QuestionDotToken) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeArguments) |
        PropagateNodeListSubtreeFacts(Arguments, PropagateSubtreeFacts) |
        (Expression!.Kind == SyntaxKind.ImportKeyword ? Ts.Ast.SubtreeFacts.SubtreeContainsDynamicImport : Ts.Ast.SubtreeFacts.None);

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsCall;
}

public sealed partial class NewExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeArguments) |
        PropagateNodeListSubtreeFacts(Arguments, PropagateSubtreeFacts);

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsNew;
}

public sealed partial class MetaProperty
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(name) & ~Ts.Ast.SubtreeFacts.SubtreeContainsIdentifier;
}

public sealed partial class NonNullExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
}

public sealed partial class SpreadElement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsRestOrSpread;
}

public sealed partial class TaggedTemplateExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Tag) |
        PropagateSubtreeFacts(QuestionDotToken) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeArguments) |
        PropagateSubtreeFacts(Template);
}

public sealed partial class ArrayLiteralExpression
{
    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsArrayLiteral;
}

public sealed partial class ObjectLiteralExpression
{
    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsObjectLiteral;
}

public sealed partial class SpreadAssignment
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsESObjectRestOrSpread | Ts.Ast.SubtreeFacts.SubtreeContainsObjectRestOrSpread;
}

public sealed partial class PropertyAssignment
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(name) |
        PropagateSubtreeFacts(Type) |
        PropagateSubtreeFacts(Initializer);
}

public sealed partial class ShorthandPropertyAssignment
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(name) |
        PropagateSubtreeFacts(Type) |
        PropagateSubtreeFacts(ObjectAssignmentInitializer) |
        Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;
}

public sealed partial class AwaitExpression
{
    // await in an ES2018 async generator must use `yield __await(expr)`
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsAwait | Ts.Ast.SubtreeFacts.SubtreeContainsAnyAwait | Ts.Ast.SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator;
}

public sealed partial class TypeAssertion
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsTypeScript;

    internal override SubtreeFacts PropagateSubtreeFacts() =>
        SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsOuterExpression;
}

public sealed partial class ExpressionWithTypeArguments
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeArguments);
}

public sealed partial class TemplateHead
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if ((TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsInvalidTemplateEscape;
        }
        return Ts.Ast.SubtreeFacts.None;
    }
}

public sealed partial class TemplateMiddle
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if ((TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsInvalidTemplateEscape;
        }
        return Ts.Ast.SubtreeFacts.None;
    }
}

public sealed partial class TemplateTail
{
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        if ((TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0)
        {
            return Ts.Ast.SubtreeFacts.SubtreeContainsInvalidTemplateEscape;
        }
        return Ts.Ast.SubtreeFacts.None;
    }
}

public sealed partial class JsxElement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(OpeningElement) |
        PropagateNodeListSubtreeFacts(Children, PropagateSubtreeFacts) |
        PropagateSubtreeFacts(ClosingElement) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxAttributes
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateNodeListSubtreeFacts(Properties, PropagateSubtreeFacts) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxNamespacedName
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Namespace) |
        PropagateSubtreeFacts(name) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxOpeningElement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(TagName) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeArguments) |
        PropagateSubtreeFacts(Attributes) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxSelfClosingElement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(TagName) |
        PropagateEraseableSyntaxListSubtreeFacts(TypeArguments) |
        PropagateSubtreeFacts(Attributes) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxFragment
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateNodeListSubtreeFacts(Children, PropagateSubtreeFacts) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxOpeningFragment
{
    internal override SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxClosingFragment
{
    internal override SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxAttribute
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(name) |
        PropagateSubtreeFacts(Initializer) |
        Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxSpreadAttribute
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxClosingElement
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(TagName) | Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxExpression
{
    internal override SubtreeFacts ComputeSubtreeFacts() =>
        PropagateSubtreeFacts(Expression) | Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

public sealed partial class JsxText
{
    internal override SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.SubtreeContainsJsx;
}

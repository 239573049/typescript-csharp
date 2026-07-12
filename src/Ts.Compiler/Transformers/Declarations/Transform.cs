// Ported from tsgo internal/transformers/declarations/transform.go.

using System;
using System.Collections.Generic;
using global::Ts.Ast;
using global::Ts.Core;
using global::Ts.Printer;

namespace Ts.Transformers.Declarations;

public sealed class ReferencedFilePair
{
    public SourceFile? File;
    public FileReference? Reference;
}

public interface IOutputPaths
{
    string DeclarationFilePath();
    string JsFilePath();
}

// Used to be passed in the TransformationContext, which is now just an EmitContext.
public interface IDeclarationEmitHost
{
    string GetCurrentDirectory();
    bool UseCaseSensitiveFileNames();
    SourceFile? GetSourceFileFromReference(SourceFile origin, FileReference reference);
    IOutputPaths GetOutputPathsFor(SourceFile file, bool forceDtsPaths);
    ResolutionMode GetResolutionModeOverride(Node node);
    ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags);
    EmitResolver GetEmitResolver();
}

public sealed class DeclarationTransformer : global::Ts.Transformers.Transformer
{
    // PORT-NOTE: Breadth-first declaration emit slice. The core syntax transforms are live:
    // declaration-file conversion, body/initializer stripping, signature/type preservation and
    // primitive inference, class/interface/member filtering, and top-level declare injection.
    // The following checker-coupled transform.go branches remain explicit deferred work:
    //   * late visibility painting and alias reprocessing;
    //   * @internal comment-range stripping;
    //   * CommonJS/export-expression promotion and JS expando/this-property synthesis;
    //   * declaration/module-specifier/reference-directive path rewriting;
    //   * late-bound index signatures, computed-name checks, and complex extends expressions;
    //   * literal-const initializer preservation and the full isolatedDeclarations diagnostic matrix.
    // They are not replaced with invented checker behavior; resolver-backed calls are made only where
    // the landed EmitResolver contract can answer them faithfully.
    private static readonly global::Ts.NodeBuilder.Flags DeclarationEmitNodeBuilderFlags =
        global::Ts.NodeBuilder.Flags.MultilineObjectLiterals |
        global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral |
        global::Ts.NodeBuilder.Flags.UseTypeOfFunction |
        global::Ts.NodeBuilder.Flags.UseStructuralFallback |
        global::Ts.NodeBuilder.Flags.AllowEmptyTuple |
        global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams |
        global::Ts.NodeBuilder.Flags.NoTruncation;

    private const global::Ts.NodeBuilder.InternalFlags DeclarationEmitInternalNodeBuilderFlags =
        global::Ts.NodeBuilder.InternalFlags.AllowUnresolvedNames;

    private readonly IDeclarationEmitHost? host;
    private readonly CompilerOptions compilerOptions;
    private readonly EmitResolver? resolver;
    private readonly global::Ts.PseudoChecker.PseudoChecker pseudoChecker;
    private readonly SymbolTrackerSharedState state;
    private readonly SymbolTrackerImpl tracker;
    private Node? enclosingDeclaration;
    private bool needsDeclare;

    private DeclarationTransformer(
        IDeclarationEmitHost? host,
        EmitContext? context,
        CompilerOptions? compilerOptions,
        EmitResolver? resolver)
    {
        this.host = host;
        this.compilerOptions = compilerOptions ?? new CompilerOptions();
        this.resolver = resolver ?? host?.GetEmitResolver();
        pseudoChecker = global::Ts.PseudoChecker.PseudoChecker.NewPseudoChecker(
            this.compilerOptions.StrictNullChecks == Tristate.True,
            this.compilerOptions.ExactOptionalPropertyTypes == Tristate.True);
        state = new SymbolTrackerSharedState
        {
            IsolatedDeclarations = this.compilerOptions.IsolatedDeclarations == Tristate.True,
            StripInternal = this.compilerOptions.StripInternal == Tristate.True,
            Resolver = this.resolver,
        };
        tracker = SymbolTracker.NewSymbolTracker(host, this.resolver, state);
        NewTransformer(Visit, context);
    }

    // TODO: Convert to TransformerFactory signature to allow more automatic composition with other transforms.
    public static DeclarationTransformer NewDeclarationTransformer(
        IDeclarationEmitHost host,
        EmitContext? context,
        CompilerOptions? compilerOptions,
        string declarationFilePath,
        string declarationMapPath)
    {
        _ = declarationFilePath;
        _ = declarationMapPath;
        return new DeclarationTransformer(host, context, compilerOptions, host.GetEmitResolver());
    }

    // Local composition adapter used while the compiler's declaration host wiring is still landing.
    public static DeclarationTransformer NewDeclarationTransformer(global::Ts.Transformers.TransformOptions options) =>
        new(null, options.Context, options.CompilerOptions, options.EmitResolver);

    public Diagnostic[] GetDiagnostics() => state.Diagnostics.ToArray();

    private Node? Visit(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                return VisitSourceFile(node.AsSourceFile());

            case SyntaxKind.FunctionDeclaration:
                return TransformFunctionDeclaration(node.AsFunctionDeclaration());
            case SyntaxKind.ClassDeclaration:
                return TransformClassDeclaration(node.AsClassDeclaration());
            case SyntaxKind.InterfaceDeclaration:
                return TransformInterfaceDeclaration(node.AsInterfaceDeclaration());
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return TransformTypeAliasDeclaration(node.AsTypeAliasDeclaration());
            case SyntaxKind.VariableStatement:
                return TransformVariableStatement(node.AsVariableStatement());
            case SyntaxKind.EnumDeclaration:
                return TransformEnumDeclaration(node.AsEnumDeclaration());
            case SyntaxKind.ModuleDeclaration:
                return TransformModuleDeclaration(node.AsModuleDeclaration());

            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportDeclaration:
                return TransformModuleIndicator(node);
            case SyntaxKind.ExportAssignment:
                return TransformExportAssignment(node.AsExportAssignment());

            case SyntaxKind.VariableDeclaration:
                return TransformVariableDeclaration(node.AsVariableDeclaration());
            case SyntaxKind.Parameter:
                return EnsureParameter(node.AsParameterDeclaration());
            case SyntaxKind.PropertyDeclaration:
                return TransformPropertyDeclaration(node.AsPropertyDeclaration());
            case SyntaxKind.PropertySignature:
                return TransformPropertySignatureDeclaration(node.AsPropertySignatureDeclaration());
            case SyntaxKind.MethodDeclaration:
                return TransformMethodDeclaration(node.AsMethodDeclaration());
            case SyntaxKind.MethodSignature:
                return TransformMethodSignatureDeclaration(node.AsMethodSignatureDeclaration());
            case SyntaxKind.Constructor:
                return TransformConstructorDeclaration(node.AsConstructorDeclaration());
            case SyntaxKind.GetAccessor:
                return TransformGetAccessorDeclaration(node.AsGetAccessorDeclaration());
            case SyntaxKind.SetAccessor:
                return TransformSetAccessorDeclaration(node.AsSetAccessorDeclaration());
            case SyntaxKind.CallSignature:
                return TransformCallSignatureDeclaration(node.AsCallSignatureDeclaration());
            case SyntaxKind.ConstructSignature:
                return TransformConstructSignatureDeclaration(node.AsConstructSignatureDeclaration());
            case SyntaxKind.IndexSignature:
                return TransformIndexSignatureDeclaration(node.AsIndexSignatureDeclaration());
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.SemicolonClassElement:
                return null;

            case SyntaxKind.BreakStatement:
            case SyntaxKind.ContinueStatement:
            case SyntaxKind.DebuggerStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.EmptyStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.IfStatement:
            case SyntaxKind.LabeledStatement:
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.TryStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.NotEmittedStatement:
            case SyntaxKind.Block:
            case SyntaxKind.MissingDeclaration:
            case SyntaxKind.ExpressionStatement:
                return null;

            default:
                return Visitor().VisitEachChild(node);
        }
    }

    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }

        needsDeclare = true;
        enclosingDeclaration = node.AsNode();
        state.CurrentSourceFile = node;
        resolver?.PrecalculateDeclarationEmitVisibility(EmitContext().MostOriginal(node.AsNode())!.AsSourceFile());

        var statements = Visitor().VisitNodes(node.Statements) ?? Factory().NewNodeList([]);
        var result = Factory().AsNodeFactory().UpdateSourceFile(node, statements, node.EndOfFileToken);
        result.AsSourceFile().IsDeclarationFile = true;

        state.CurrentSourceFile = null;
        enclosingDeclaration = null;
        return result;
    }

    private Node? TransformModuleIndicator(Node node)
    {
        if (resolver is not null &&
            node.Kind is SyntaxKind.ImportEqualsDeclaration or SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration &&
            !resolver.IsDeclarationVisible(node))
        {
            return null;
        }
        return Visitor().VisitEachChild(node);
    }

    private Node? TransformExportAssignment(ExportAssignment input)
    {
        var expression = input.Expression;
        if (expression?.Kind == SyntaxKind.Identifier)
        {
            return Factory().AsNodeFactory().UpdateExportAssignment(
                input,
                input.Modifiers(),
                input.IsExportEquals,
                null,
                expression);
        }

        // PORT-NOTE: transform.go promotes non-identifier export assignments to a synthetic typed
        // declaration. That path requires checker-backed CreateTypeOfExpression and generated-name
        // visibility. Keep it explicitly deferred instead of emitting an invalid .d.ts expression.
        if (state.IsolatedDeclarations && expression is not null)
        {
            tracker.ReportInferenceFallback(expression);
        }
        return null;
    }

    private Node TransformTypeAliasDeclaration(TypeAliasDeclaration input)
    {
        var oldNeedsDeclare = needsDeclare;
        needsDeclare = false;
        var result = Factory().AsNodeFactory().UpdateTypeAliasDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNode(input.Type));
        needsDeclare = oldNeedsDeclare;
        return result;
    }

    private Node TransformInterfaceDeclaration(InterfaceDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        var result = Factory().AsNodeFactory().UpdateInterfaceDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNodes(input.HeritageClauses),
            Visitor().VisitNodes(input.Members));
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformFunctionDeclaration(FunctionDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        var result = Factory().AsNodeFactory().UpdateFunctionDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null,
            null);
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformClassDeclaration(ClassDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        enclosingDeclaration = input.AsNode();
        tracker.PushErrorFallbackNode(input.AsNode());

        var members = Visitor().VisitNodes(input.Members) ?? Factory().NewNodeList([]);
        var result = Factory().AsNodeFactory().UpdateClassDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.TypeParameters),
            Visitor().VisitNodes(input.HeritageClauses),
            members);

        tracker.PopErrorFallbackNode();
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformModuleDeclaration(ModuleDeclaration input)
    {
        var oldEnclosing = enclosingDeclaration;
        var oldNeedsDeclare = needsDeclare;
        enclosingDeclaration = input.AsNode();
        needsDeclare = false;
        var result = Factory().AsNodeFactory().UpdateModuleDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Keyword,
            input.Name(),
            Visitor().VisitNode(input.Body));
        needsDeclare = oldNeedsDeclare;
        enclosingDeclaration = oldEnclosing;
        return result;
    }

    private Node TransformVariableStatement(VariableStatement input)
    {
        var declarations = input.DeclarationList?.AsVariableDeclarationList().Declarations;
        var visitedDeclarations = Visitor().VisitNodes(declarations) ?? Factory().NewNodeList([]);
        var declarationList = Factory().AsNodeFactory().UpdateVariableDeclarationList(
            input.DeclarationList!.AsVariableDeclarationList(),
            visitedDeclarations,
            input.DeclarationList.Flags);
        return Factory().AsNodeFactory().UpdateVariableStatement(
            input,
            EnsureModifiers(input.AsNode()),
            declarationList);
    }

    private Node TransformVariableDeclaration(VariableDeclaration input) =>
        Factory().AsNodeFactory().UpdateVariableDeclaration(
            input,
            input.Name(),
            null,
            EnsureType(input.AsNode(), false),
            null);

    private Node TransformEnumDeclaration(EnumDeclaration input) =>
        Factory().AsNodeFactory().UpdateEnumDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            Visitor().VisitNodes(input.Members));

    private Node? TransformPropertyDeclaration(PropertyDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        var postfixToken = input.PostfixToken?.Kind == SyntaxKind.ExclamationToken
            ? null
            : input.PostfixToken;
        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            postfixToken,
            EnsureType(input.AsNode(), false),
            null);
    }

    private Node? TransformPropertySignatureDeclaration(PropertySignatureDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdatePropertySignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            input.PostfixToken,
            EnsureType(input.AsNode(), false),
            null);
    }

    private Node? TransformMethodDeclaration(MethodDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        if (GetEffectiveDeclarationFlags(input.AsNode(), ModifierFlags.Private) != 0)
        {
            return Factory().AsNodeFactory().NewPropertyDeclaration(
                EnsureModifiers(input.AsNode()), input.Name(), null, null, null);
        }
        return Factory().AsNodeFactory().UpdateMethodDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            input.Name(),
            input.PostfixToken,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null,
            null);
    }

    private Node? TransformMethodSignatureDeclaration(MethodSignatureDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        if (GetEffectiveDeclarationFlags(input.AsNode(), ModifierFlags.Private) != 0)
        {
            return Factory().AsNodeFactory().NewPropertyDeclaration(
                EnsureModifiers(input.AsNode()), input.Name(), null, null, null);
        }
        return Factory().AsNodeFactory().UpdateMethodSignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            input.PostfixToken,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));
    }

    private Node TransformConstructorDeclaration(ConstructorDeclaration input) =>
        Factory().AsNodeFactory().UpdateConstructorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            null,
            UpdateParameterList(input.AsNode(), input.Parameters),
            null,
            null,
            null);

    private Node? TransformGetAccessorDeclaration(GetAccessorDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            null,
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false),
            null,
            null);
    }

    private Node? TransformSetAccessorDeclaration(SetAccessorDeclaration input)
    {
        if (input.Name() is { } name && global::Ts.Ast.NodeTests.IsPrivateIdentifier(name))
        {
            return null;
        }
        return Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            input.Name(),
            null,
            UpdateParameterList(input.AsNode(), input.Parameters),
            null,
            null,
            null);
    }

    private Node TransformCallSignatureDeclaration(CallSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateCallSignatureDeclaration(
            input,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    private Node TransformConstructSignatureDeclaration(ConstructSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateConstructSignatureDeclaration(
            input,
            Visitor().VisitNodes(input.TypeParameters),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    private Node TransformIndexSignatureDeclaration(IndexSignatureDeclaration input) =>
        Factory().AsNodeFactory().UpdateIndexSignatureDeclaration(
            input,
            EnsureModifiers(input.AsNode()),
            UpdateParameterList(input.AsNode(), input.Parameters),
            EnsureType(input.AsNode(), false));

    private ParameterList UpdateParameterList(Node owner, ParameterList? parameters)
    {
        if (GetEffectiveDeclarationFlags(owner, ModifierFlags.Private) != 0 || parameters is null)
        {
            return Factory().NewNodeList([]);
        }
        var results = new List<Node>(parameters.Nodes.Length);
        foreach (var parameter in parameters.Nodes)
        {
            results.Add(EnsureParameter(parameter.AsParameterDeclaration()));
        }
        return Factory().NewNodeList(results.ToArray());
    }

    private Node EnsureParameter(ParameterDeclaration input)
    {
        var questionToken = input.QuestionToken;
        if (resolver?.IsOptionalParameter(input.AsNode()) == true && questionToken is null)
        {
            questionToken = Factory().NewToken(SyntaxKind.QuestionToken);
        }
        return Factory().AsNodeFactory().UpdateParameterDeclaration(
            input,
            null,
            input.DotDotDotToken,
            input.Name(),
            questionToken,
            EnsureType(input.AsNode(), true),
            null);
    }

    private Node EnsureType(Node node, bool ignorePrivate)
    {
        if (!ignorePrivate && GetEffectiveDeclarationFlags(node, ModifierFlags.Private) != 0)
        {
            return null!;
        }
        if (node.Type() is { } annotation)
        {
            return Visitor().VisitNode(annotation)!;
        }

        global::Ts.PseudoChecker.PseudoType pseudoType;
        if (global::Ts.Ast.AstUtilities.IsFunctionLike(node))
        {
            pseudoType = pseudoChecker.GetReturnTypeOfSignature(node);
        }
        else
        {
            pseudoType = pseudoChecker.GetTypeOfDeclaration(node);
        }
        return PseudoTypeToTypeNode(pseudoType);
    }

    private Node PseudoTypeToTypeNode(global::Ts.PseudoChecker.PseudoType type)
    {
        switch (type.Kind)
        {
            case global::Ts.PseudoChecker.PseudoTypeKind.Direct:
                return Visitor().VisitNode(type.AsPseudoTypeDirect().TypeNode)!;
            case global::Ts.PseudoChecker.PseudoTypeKind.MaybeConstLocation:
                return PseudoTypeToTypeNode(type.AsPseudoTypeMaybeConstLocation().RegularType);
            case global::Ts.PseudoChecker.PseudoTypeKind.Union:
            {
                var types = type.AsPseudoTypeUnion().Types;
                var nodes = new Node[types.Length];
                for (var i = 0; i < types.Length; i++)
                {
                    nodes[i] = PseudoTypeToTypeNode(types[i]);
                }
                return Factory().AsNodeFactory().NewUnionTypeNode(Factory().NewNodeList(nodes));
            }
            case global::Ts.PseudoChecker.PseudoTypeKind.String:
            case global::Ts.PseudoChecker.PseudoTypeKind.StringLiteral:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.StringKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Number:
            case global::Ts.PseudoChecker.PseudoTypeKind.NumericLiteral:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.NumberKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.BigInt:
            case global::Ts.PseudoChecker.PseudoTypeKind.BigIntLiteral:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.BigIntKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Boolean:
            case global::Ts.PseudoChecker.PseudoTypeKind.False:
            case global::Ts.PseudoChecker.PseudoTypeKind.True:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.BooleanKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Undefined:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.UndefinedKeyword);
            case global::Ts.PseudoChecker.PseudoTypeKind.Null:
                return Factory().AsNodeFactory().NewLiteralTypeNode(
                    Factory().NewKeywordExpression(SyntaxKind.NullKeyword));
            case global::Ts.PseudoChecker.PseudoTypeKind.Any:
            case global::Ts.PseudoChecker.PseudoTypeKind.Inferred:
            case global::Ts.PseudoChecker.PseudoTypeKind.NoResult:
            case global::Ts.PseudoChecker.PseudoTypeKind.SingleCallSignature:
            case global::Ts.PseudoChecker.PseudoTypeKind.Tuple:
            case global::Ts.PseudoChecker.PseudoTypeKind.ObjectLiteral:
            default:
                return Factory().AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
    }

    private ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags) =>
        host?.GetEffectiveDeclarationFlags(EmitContext().ParseNode(node) ?? node, flags) ??
        (global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(node) & flags);

    private ModifierList? EnsureModifiers(Node node)
    {
        var flags = global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(node) & ModifierFlags.All;
        flags &= ~(ModifierFlags.Public | ModifierFlags.Async | ModifierFlags.Override | ModifierFlags.Decorator);
        if (node.Parent?.Kind == SyntaxKind.SourceFile && needsDeclare &&
            !DeclarationUtilities.IsAlwaysType(node) &&
            node.Kind != SyntaxKind.TypeAliasDeclaration &&
            node.Kind != SyntaxKind.JSTypeAliasDeclaration &&
            (flags & ModifierFlags.Default) == 0)
        {
            flags |= ModifierFlags.Ambient;
        }
        else if (node.Parent?.Kind != SyntaxKind.SourceFile)
        {
            flags &= ~ModifierFlags.Ambient;
        }

        var modifiers = new List<Node>();
        AddModifierIf(flags, ModifierFlags.Export, SyntaxKind.ExportKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Default, SyntaxKind.DefaultKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Ambient, SyntaxKind.DeclareKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Private, SyntaxKind.PrivateKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Protected, SyntaxKind.ProtectedKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Static, SyntaxKind.StaticKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Readonly, SyntaxKind.ReadonlyKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Abstract, SyntaxKind.AbstractKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Accessor, SyntaxKind.AccessorKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.In, SyntaxKind.InKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Out, SyntaxKind.OutKeyword, modifiers);
        AddModifierIf(flags, ModifierFlags.Const, SyntaxKind.ConstKeyword, modifiers);
        return modifiers.Count == 0 ? null : Factory().AsNodeFactory().NewModifierList(modifiers.ToArray());
    }

    private void AddModifierIf(
        ModifierFlags flags,
        ModifierFlags flag,
        SyntaxKind kind,
        List<Node> modifiers)
    {
        if ((flags & flag) != 0)
        {
            modifiers.Add(Factory().AsNodeFactory().NewModifier(kind));
        }
    }

    // These constants document the exact node-builder contract consumed by the deferred resolver path.
    internal static global::Ts.NodeBuilder.Flags NodeBuilderFlags => DeclarationEmitNodeBuilderFlags;
    internal static global::Ts.NodeBuilder.InternalFlags InternalNodeBuilderFlags =>
        DeclarationEmitInternalNodeBuilderFlags;
}

// Ported from tsgo internal/printer/emitresolver.go
//
// NOTE: This wave ports only the EmitResolver interface contract (and its supporting enums/result
// struct). The concrete implementation lives in checker/emitresolver.go and is ported in phase 5.

using Ts.Ast;
using Ts.Binder;

namespace Ts.Printer;

public enum SymbolAccessibility : int
{
    Accessible = 0,
    NotAccessible,
    CannotBeNamed,
    NotResolved,
}

public struct SymbolAccessibilityResult
{
    public SymbolAccessibility Accessibility;
    public Node[]? AliasesToMakeVisible; // aliases that need to have this symbol visible
    public string? ErrorSymbolName; // Optional - symbol name that results in error
    public Node? ErrorNode; // Optional - node that results in error
    public string? ErrorModuleName; // Optional - If the symbol is not visible from module, module's name
}

/// <summary>
/// Indicates how to serialize the name for a TypeReferenceNode when emitting decorator metadata.
/// </summary>
public enum TypeReferenceSerializationKind : int
{
    // The TypeReferenceNode could not be resolved.
    // The type name should be emitted using a safe fallback.
    Unknown = 0,

    // The TypeReferenceNode resolves to a type with a constructor
    // function that can be reached at runtime (e.g. a `class`
    // declaration or a `var` declaration for the static side
    // of a type, such as the global `Promise` type in lib.d.ts).
    TypeWithConstructSignatureAndValue,

    // The TypeReferenceNode resolves to a Void-like, Nullable, or Never type.
    VoidNullableOrNeverType,

    // The TypeReferenceNode resolves to a Number-like type.
    NumberLikeType,

    // The TypeReferenceNode resolves to a BigInt-like type.
    BigIntLikeType,

    // The TypeReferenceNode resolves to a String-like type.
    StringLikeType,

    // The TypeReferenceNode resolves to a Boolean-like type.
    BooleanType,

    // The TypeReferenceNode resolves to an Array-like type.
    ArrayLikeType,

    // The TypeReferenceNode resolves to the ESSymbol type.
    ESSymbolType,

    // The TypeReferenceNode resolved to the global Promise constructor symbol.
    Promise,

    // The TypeReferenceNode resolves to a Function type or a type with call signatures.
    TypeWithCallSignature,

    // The TypeReferenceNode resolves to any other type.
    ObjectType,
}

public interface EmitResolver : IReferenceResolver
{
    bool IsReferencedAliasDeclaration(Node node);
    bool IsValueAliasDeclaration(Node node);
    bool IsTopLevelValueImportEqualsWithEntityName(Node node);
    void MarkLinkedReferencesRecursively(SourceFile file);
    SourceFile? GetExternalModuleFileFromDeclaration(Node node);
    ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags);
    ResolutionMode GetResolutionModeOverride(Node node);

    // decorator metadata
    TypeReferenceSerializationKind GetTypeReferenceSerializationKind(EntityName name, Node serialScope);

    // const enum inlining
    object? GetConstantValue(Node node);

    // JSX Emit
    Node? GetJsxFactoryEntity(Node location);
    Node? GetJsxFragmentFactoryEntity(Node location);
    void SetReferencedImportDeclaration(IdentifierNode node, Declaration @ref); // for overriding the reference resolver behavior for generated identifiers

    // declaration emit checker functionality projections
    void PrecalculateDeclarationEmitVisibility(SourceFile file);
    SymbolAccessibilityResult IsSymbolAccessible(Symbol symbol, Node enclosingDeclaration, SymbolFlags meaning, bool shouldComputeAliasToMarkVisible);
    SymbolAccessibilityResult IsEntityNameVisible(Node entityName, Node enclosingDeclaration); // previously SymbolVisibilityResult in strada - ErrorModuleName never set
    bool IsExpandoFunctionDeclaration(Node node);
    bool IsExpandoFunctionDeclarationUnsafe(Node node);
    bool IsLiteralConstDeclaration(Node node);
    bool RequiresAddingImplicitUndefined(Node node, Symbol symbol, Node enclosingDeclaration);
    bool IsDeclarationVisible(Node node);
    bool IsNameResolvable(Node location, string name);
    bool IsImportRequiredByAugmentation(ImportDeclaration decl);
    bool IsDefinitelyReferenceToGlobalSymbolObject(Node node);
    bool IsImplementationOfOverload(SignatureDeclaration node);
    global::Ts.Evaluator.Result GetEnumMemberValue(Node node);
    bool IsLateBound(Node node);
    bool IsOptionalParameter(Node node);
    bool IsThisPropertyAssignmentDeclarationRedundant(Node node);

    // isolatedDeclarations-specific declaration emit
    Symbol[] GetPropertiesOfContainerFunction(Node node);
    bool RequiresAddingImplicitUndefinedUnsafe(Node node, Symbol symbol, Node enclosingDeclaration);
    Declaration? GetReferencedValueDeclarationUnsafe(IdentifierNode node);

    // Node construction for declaration emit
    Node? CreateTypeOfDeclaration(EmitContext emitContext, Node declaration, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker);
    Node? CreateReturnTypeOfSignatureDeclaration(EmitContext emitContext, Node signatureDeclaration, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker);
    Node[] CreateTypeParametersOfSignatureDeclaration(EmitContext emitContext, Node signatureDeclaration, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker);
    Node? CreateLiteralConstValue(EmitContext emitContext, Node node, global::Ts.NodeBuilder.ISymbolTracker tracker);
    Node? CreateTypeOfExpression(EmitContext emitContext, Node expression, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker);
    Node[] CreateLateBoundIndexSignatures(EmitContext emitContext, Node container, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker);
    Node? TryJSTypeNodeToTypeNode(EmitContext emitContext, Node typeNode, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker tracker);
}

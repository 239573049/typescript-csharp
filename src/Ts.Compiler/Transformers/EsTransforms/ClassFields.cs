// Ported from tsgo internal/transformers/estransforms/classfields.go — the class-fields / private-name /
// auto-accessor / static-block downleveling transformer.
//
// BATCH 1 of the construction plan (see memory classfields-port-plan): the class lexical/private
// environment data structures, the computed configuration flags, and the constructor with its nine
// sub-visitors are ported here; the visit dispatch currently forwards every node unchanged
// (byte-identical to the previous shell), so behavior is unchanged until the visitor bodies land in
// batches 2-5 and the dispatch is switched over in batch 6.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

// classfields.go:18-27 — classFacts tracks various facts about a class being transformed.
[Flags]
internal enum ClassFacts
{
    None = 0,
    ClassWasDecorated = 1 << 0,
    NeedsClassConstructorReference = 1 << 1,
    NeedsClassSuperReference = 1 << 2,
    NeedsSubstitutionForThisInClassStaticField = 1 << 3,
    WillHoistInitializersToConstructor = 1 << 4,
}

// classfields.go:31-51 — privateIdentifierInfo stores information about a private identifier during
// transformation.
internal sealed class PrivateIdentifierInfo
{
    internal global::Ts.Printer.PrivateIdentifierKind kind;
    // brandCheckIdentifier can contain:
    //  - For instance field: The WeakMap that will be the storage for the field.
    //  - For instance methods or accessors: The WeakSet that will be used for brand checking.
    //  - For static members: The constructor that will be used for brand checking.
    internal IdentifierNode? brandCheckIdentifier;
    // isStatic stores if the identifier is static or not.
    internal bool isStatic;
    // isValid stores if the identifier declaration is valid or not. Reserved names (e.g. #constructor)
    // or duplicate identifiers are considered invalid.
    internal bool isValid;
    // variableName contains the variable that will serve as the storage for a static field.
    internal IdentifierNode? variableName;
    // methodName is the identifier for a variable that will contain the private method implementation.
    internal IdentifierNode? methodName;
    // getterName is the identifier for a variable that will contain the private get accessor implementation, if any.
    internal IdentifierNode? getterName;
    // setterName is the identifier for a variable that will contain the private set accessor implementation, if any.
    internal IdentifierNode? setterName;
}

// classfields.go:54-59 — privateEnvironmentData stores class-scoped environment data for private identifiers.
internal struct PrivateEnvironmentData
{
    // className is used for prefixing generated variable names.
    internal IdentifierNode? className;
    // weakSetName is used for brand check on private methods.
    internal IdentifierNode? weakSetName;
}

// classfields.go:61-70 — privateEnvironment stores a map of private identifier names to their transform
// info. Like Strada, it uses two separate maps: one for non-generated identifiers (keyed by text) and
// one for generated identifiers (keyed by original AST node). This prevents collisions when different
// auto-accessors produce generated backing field names with the same text.
internal sealed class PrivateEnvironment
{
    internal PrivateEnvironmentData data;
    internal Dictionary<string, PrivateIdentifierInfo>? members;
    internal Dictionary<Node, PrivateIdentifierInfo>? generatedIdentifiers;
}

// classfields.go:72-81 — classLexicalEnvironment stores information about the lexical environment of a class.
internal sealed class ClassLexicalEnvironment
{
    internal ClassFacts facts;
    // classConstructor is used for brand checks on static members, and `this` references in static initializers.
    internal IdentifierNode? classConstructor;
    internal IdentifierNode? classThis;
    // superClassReference is used for `super` references in static initializers.
    internal IdentifierNode? superClassReference;
}

// classfields.go:83-88 — classLexicalEnv is a linked list of class lexical environments.
internal sealed class ClassLexicalEnv
{
    internal ClassLexicalEnv? previous;
    internal ClassLexicalEnvironment? data;
    internal PrivateEnvironment? privateEnv;
}

// classfields.go:90-137 — the transformer.
internal sealed class ClassFieldsTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;
    private readonly global::Ts.Binder.IReferenceResolver? resolver;

    // Computed configuration flags (classfields.go:96-104).
    private readonly bool shouldTransformInitializersUsingSet;
    private readonly bool shouldTransformInitializersUsingDefine;
    private readonly bool shouldTransformInitializers;
    private readonly bool shouldTransformPrivateElementsOrClassStaticBlocks;
    private readonly bool shouldTransformAutoAccessors;
    private readonly bool shouldTransformThisInStaticInitializers;
    private readonly bool shouldTransformSuperInStaticInitializers;
    private bool shouldTransformPrivateStaticElementsInFile;
    private readonly bool legacyDecorators;

    // pendingExpressions tracks what computed name expressions originating from elided names
    // must be inlined at the next execution site, in document order.
    private List<Node>? pendingExpressions;
    // pendingStatements tracks what computed name expression statements and static property
    // initializers must be emitted at the next execution site, in document order (for decorated classes).
    private List<Node>? pendingStatements;
    private ClassLexicalEnv? lexicalEnvironment;
    private Node? currentClassContainer;
    private Node? currentClassElement;
    // classAliases maps class declarations to alias identifiers for substituting class name
    // references in static initializers. Replaces Strada's onSubstituteNode/trySubstituteClassAlias.
    private Dictionary<Node, IdentifierNode>? classAliases;
    private readonly global::Ts.Collections.Set<Node> enclosingClassDeclarations = new();
    private bool inIterationStatement;
    // insideComputedPropertyName replaces Strada's onEmitNode for ComputedPropertyName, which
    // switches to the outer lexical environment. Used by visitThisExpression() to apply
    // the outer environment's substitution without requiring currentClassElement to be static.
    private bool insideComputedPropertyName;
    private Node? parentNode;
    private Node? currentNode;

    // Visitors (classfields.go:126-135); constructed in the factory below. Batches 2-5 give them their
    // real callbacks; the batch-1 placeholder dispatch never invokes them.
    private NodeVisitor modifierVisitor = null!;
    private NodeVisitor discardedValueVisitor = null!;
    private NodeVisitor heritageClauseVisitor = null!;
    private NodeVisitor assignmentTargetVisitor = null!;
    private NodeVisitor classElementVisitor = null!;
    private NodeVisitor accessorFieldResultVisitor = null!;
    private NodeVisitor arrayAssignmentElementVisitor = null!;
    private NodeVisitor objectAssignmentElementVisitor = null!;
    private NodeVisitor substitutionVisitor = null!;

    // Pre-bound callback to avoid repeated closure allocation (classfields.go:137).
    private Func<Node, bool> isAnonymousClassNeedingAssignedName = null!;

    private ClassFieldsTransformer(TransformOptions opts, ScriptTarget languageVersion, bool useDefineForClassFields)
    {
        compilerOptions = opts.CompilerOptions!;
        resolver = opts.Resolver;
        legacyDecorators = compilerOptions.ExperimentalDecorators.IsTrue();

        // Always transform field initializers using Set semantics when `useDefineForClassFields: false`.
        shouldTransformInitializersUsingSet = !useDefineForClassFields;

        // Transform field initializers using Define semantics when `useDefineForClassFields: true` and target < ES2022.
        shouldTransformInitializersUsingDefine = useDefineForClassFields && languageVersion < ScriptTarget.ES2022;

        shouldTransformInitializers = shouldTransformInitializersUsingSet || shouldTransformInitializersUsingDefine;

        // We need to transform private members and class static blocks when target < ES2022.
        shouldTransformPrivateElementsOrClassStaticBlocks = languageVersion < ScriptTarget.ES2022;

        // We need to transform `accessor` fields when target < ESNext.
        // We may need to transform `accessor` fields when `useDefineForClassFields: false`
        shouldTransformAutoAccessors = languageVersion < ScriptTarget.ESNext;

        // We need to transform `this` in a static initializer into a reference to the class
        // when target < ES2022 since the assignment will be moved outside of the class body.
        shouldTransformThisInStaticInitializers = languageVersion < ScriptTarget.ES2022;

        // Since target is always >= ES2015, this is always the same as
        // shouldTransformThisInStaticInitializers.
        shouldTransformSuperInStaticInitializers = shouldTransformThisInStaticInitializers;

        // BATCH-1 explicit defaults: the visitor bodies (batches 2-5) are the writers of this state;
        // assign the Go zero values explicitly so the batch-1 slice builds warning-clean.
        shouldTransformPrivateStaticElementsInFile = false;
        pendingExpressions = null;
        pendingStatements = null;
        lexicalEnvironment = null;
        currentClassContainer = null;
        currentClassElement = null;
        classAliases = null;
        inIterationStatement = false;
        insideComputedPropertyName = false;
    }

    // classfields.go:140-195 — newClassFieldsTransformer.
    internal static Transformer? NewClassFieldsTransformer(TransformOptions opts)
    {
        var languageVersion = opts.CompilerOptions!.GetEmitScriptTarget();
        var useDefineForClassFields = opts.CompilerOptions.GetUseDefineForClassFields();

        // When targeting ESNext+ with useDefineForClassFields (the default), there are no class
        // field transformations to perform and no prior transform sets EFTransformPrivateStaticElements,
        // so every node would be returned unchanged. Skip entirely.
        if (languageVersion >= ScriptTarget.ESNext && useDefineForClassFields)
        {
            return null;
        }

        var tx = new ClassFieldsTransformer(opts, languageVersion, useDefineForClassFields);
        var result = tx.NewTransformer(tx.Visit, opts.Context);
        tx.modifierVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitModifier);
        tx.discardedValueVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitDiscardedValue);
        tx.heritageClauseVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitHeritageClause);
        tx.assignmentTargetVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitAssignmentTarget);
        tx.classElementVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitClassElement);
        tx.accessorFieldResultVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitAccessorFieldResult);
        tx.arrayAssignmentElementVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitArrayAssignmentElement);
        tx.objectAssignmentElementVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitObjectAssignmentElement);
        tx.substitutionVisitor = tx.EmitContext().NewNodeVisitor(tx.VisitForSubstitution);
        tx.isAnonymousClassNeedingAssignedName = tx.IsAnonymousClassNeedingAssignedNameWorker;
        return result;
    }

    // classfields.go:199-205 — requiresBlockScopedVar: private field temp variables should be
    // block-scoped (let) rather than function-scoped (var) when a class expression is directly inside
    // a loop body. Replaces Strada's resolver.hasNodeCheckFlag(node, BlockScopedBindingInLoop).
    private bool RequiresBlockScopedVar() =>
        inIterationStatement && currentClassContainer != null && NodeTests.IsClassExpression(currentClassContainer);

    // classfields.go:207-218 — classExpressionNeedsBlockScopedTemp: the class temp only needs to be
    // block-scoped when the class expression has a non-static property with a computed property name
    // inside a loop.
    private bool ClassExpressionNeedsBlockScopedTemp()
    {
        if (!RequiresBlockScopedVar())
        {
            return false;
        }
        foreach (var member in currentClassContainer!.Members() ?? [])
        {
            if (NodeTests.IsPropertyDeclaration(member) && !AstUtilities.HasStaticModifier(member) &&
                member.Name() != null && NodeTests.IsComputedPropertyName(member.Name()!))
            {
                return true;
            }
        }
        return false;
    }

    // classfields.go:235-246 — visitModifier.
    private Node? VisitModifier(Node node)
    {
        if (node.Kind == SyntaxKind.AccessorKeyword)
        {
            if (ShouldTransformAutoAccessorsInCurrentClass())
            {
                return null;
            }
            return node;
        }
        if (AstUtilities.IsModifier(node))
        {
            return node;
        }
        return null;
    }

    // classfields.go:1001-1009 — shouldTransformAutoAccessorsInCurrentClass. BATCH-1 form: carries the
    // configuration flag; the EmitFlags.TransformPrivateStaticElements refinement lands in batch 3.
    private bool ShouldTransformAutoAccessorsInCurrentClass() => shouldTransformAutoAccessors;

    // classfields.go:248-253 — pushNode.
    private Node? PushNode(Node node)
    {
        var grandparentNode = parentNode;
        parentNode = currentNode;
        currentNode = node;
        return grandparentNode;
    }

    // classfields.go:255-258 — popNode.
    private void PopNode(Node? grandparentNode)
    {
        currentNode = parentNode;
        parentNode = grandparentNode;
    }

    // BATCH-1 PLACEHOLDER DISPATCH — forwards every node unchanged (byte-identical to the previous
    // shell). Batch 2 lands the real dispatch (classfields.go:276) and the sub-visitor bodies.
    private Node? Visit(Node node)
    {
        var grandparentNode = PushNode(node);
        try
        {
            return node;
        }
        finally
        {
            PopNode(grandparentNode);
        }
    }

    // BATCH-1 FIELD ANCHOR — references the state fields the batch-2..5 visitor bodies will use so the
    // batch-1 slice builds warning-clean (CS0169/CS0649 under TreatWarningsAsErrors). Removed in batch 6.
    private bool BatchOneFieldAnchor() =>
        pendingExpressions == null && pendingStatements == null && lexicalEnvironment == null &&
        currentClassContainer == null && currentClassElement == null && classAliases == null &&
        !inIterationStatement && !insideComputedPropertyName && !shouldTransformPrivateStaticElementsInFile &&
        !shouldTransformInitializers && !shouldTransformInitializersUsingDefine &&
        !shouldTransformPrivateElementsOrClassStaticBlocks && !shouldTransformThisInStaticInitializers &&
        !shouldTransformSuperInStaticInitializers && !legacyDecorators && resolver == null &&
        compilerOptions != null && global::Ts.Collections.SetExtensions.Len(enclosingClassDeclarations) == 0 &&
        modifierVisitor != null && discardedValueVisitor != null && heritageClauseVisitor != null &&
        assignmentTargetVisitor != null && classElementVisitor != null && accessorFieldResultVisitor != null &&
        arrayAssignmentElementVisitor != null && objectAssignmentElementVisitor != null &&
        substitutionVisitor != null && isAnonymousClassNeedingAssignedName != null &&
        !ClassExpressionNeedsBlockScopedTemp() && VisitModifier(currentNode ?? parentNode ?? null!) == null;

    // Placeholder sub-visitor bodies (batches 2-5 replace these; the batch-1 dispatch never invokes them).
    private Node? VisitDiscardedValue(Node node) => node;
    private Node? VisitHeritageClause(Node node) => node;
    private Node? VisitAssignmentTarget(Node node) => node;
    private Node? VisitClassElement(Node node) => node;
    private Node? VisitAccessorFieldResult(Node node) => node;
    private Node? VisitArrayAssignmentElement(Node node) => node;
    private Node? VisitObjectAssignmentElement(Node node) => node;
    private Node? VisitForSubstitution(Node node) => node;
    private bool IsAnonymousClassNeedingAssignedNameWorker(Node node) => false;
}

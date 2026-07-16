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

    // classfields.go:1001-1009 — shouldTransformAutoAccessorsInCurrentClass. When targeting ESNext with
    // useDefineForClassFields: false, auto-accessors are only transformed if the current class will hoist
    // initializers to the constructor.
    private bool ShouldTransformAutoAccessorsInCurrentClass()
    {
        if (shouldTransformAutoAccessors)
        {
            return true;
        }
        return lexicalEnvironment is { data: { } data } && (data.facts & ClassFacts.WillHoistInitializersToConstructor) != 0;
    }

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

    // ── Batch 5a: class lexical / private-identifier environment management (classfields.go:2910-3194,
    // 3391-3398) ────────────────────────────────────────────────────────────────────────────────────

    // classfields.go:2910 — startClassLexicalEnvironment.
    private void StartClassLexicalEnvironment() =>
        lexicalEnvironment = new ClassLexicalEnv { previous = lexicalEnvironment };

    // classfields.go:2914 — endClassLexicalEnvironment.
    private void EndClassLexicalEnvironment() =>
        lexicalEnvironment = lexicalEnvironment!.previous;

    // classfields.go:2918 — getClassLexicalEnvironment.
    private ClassLexicalEnvironment GetClassLexicalEnvironment()
    {
        Debug.Assert(lexicalEnvironment != null);
        lexicalEnvironment!.data ??= new ClassLexicalEnvironment();
        return lexicalEnvironment.data;
    }

    // classfields.go:2926 — getPrivateIdentifierEnvironment.
    private PrivateEnvironment GetPrivateIdentifierEnvironment()
    {
        Debug.Assert(lexicalEnvironment != null);
        lexicalEnvironment!.privateEnv ??= new PrivateEnvironment
        {
            members = new Dictionary<string, PrivateIdentifierInfo>(StringComparer.Ordinal),
        };
        return lexicalEnvironment.privateEnv;
    }

    // classfields.go:2936 — addPendingExpressions.
    private void AddPendingExpressions(params Node[] exprs) =>
        (pendingExpressions ??= new List<Node>()).AddRange(exprs);

    // classfields.go:2940 — addPrivateIdentifierPropertyDeclarationToEnvironment.
    private void AddPrivateIdentifierPropertyDeclarationToEnvironment(Node node, Node name)
    {
        var lex = GetClassLexicalEnvironment();
        var env = GetPrivateIdentifierEnvironment();
        bool isStatic = AstUtilities.HasStaticModifier(node);
        var (previousInfo, _) = GetPrivateIdentifier(env, name);
        bool isValid = !IsReservedPrivateName(name) && previousInfo == null;

        if (isStatic)
        {
            var brandCheckIdentifier = lex.classThis ?? lex.classConstructor;
            var variableName = CreateHoistedVariableForPrivateName(name, "");
            SetPrivateIdentifier(env, name, new PrivateIdentifierInfo
            {
                kind = global::Ts.Printer.PrivateIdentifierKind.Field,
                isStatic = true,
                brandCheckIdentifier = brandCheckIdentifier,
                variableName = variableName,
                isValid = isValid,
            });
        }
        else
        {
            var weakMapName = CreateHoistedVariableForPrivateName(name, "");
            SetPrivateIdentifier(env, name, new PrivateIdentifierInfo
            {
                kind = global::Ts.Printer.PrivateIdentifierKind.Field,
                isStatic = false,
                brandCheckIdentifier = weakMapName,
                isValid = isValid,
            });
            AddPendingExpressions(
                Factory().NewAssignmentExpression(
                    weakMapName,
                    Factory().AsNodeFactory().NewNewExpression(
                        Factory().NewIdentifier("WeakMap"),
                        null /*typeArguments*/,
                        Factory().NewNodeList([]))));
        }
    }

    // classfields.go:2981 — addPrivateIdentifierMethodToEnvironment.
    private void AddPrivateIdentifierMethodToEnvironment(Node name, ClassLexicalEnvironment lex, PrivateEnvironment env, bool isStatic, bool isValid)
    {
        var methodName = CreateHoistedVariableForPrivateName(name, "");
        Node? brandCheckIdentifier;
        if (isStatic)
        {
            brandCheckIdentifier = lex.classThis ?? lex.classConstructor;
            Debug.Assert(brandCheckIdentifier != null, "classConstructor should be set in private identifier environment");
        }
        else
        {
            brandCheckIdentifier = env.data.weakSetName;
        }
        SetPrivateIdentifier(env, name, new PrivateIdentifierInfo
        {
            kind = global::Ts.Printer.PrivateIdentifierKind.Method,
            methodName = methodName,
            brandCheckIdentifier = brandCheckIdentifier,
            isStatic = isStatic,
            isValid = isValid,
        });
    }

    // classfields.go:3002 — addPrivateIdentifierGetAccessorToEnvironment.
    private void AddPrivateIdentifierGetAccessorToEnvironment(Node name, ClassLexicalEnvironment lex, PrivateEnvironment env, bool isStatic, bool isValid, PrivateIdentifierInfo? previousInfo)
    {
        var getterName = CreateHoistedVariableForPrivateName(name, "_get");
        Node? brandCheckIdentifier;
        if (isStatic)
        {
            brandCheckIdentifier = lex.classThis ?? lex.classConstructor;
            Debug.Assert(brandCheckIdentifier != null, "classConstructor should be set in private identifier environment");
        }
        else
        {
            brandCheckIdentifier = env.data.weakSetName;
            Debug.Assert(brandCheckIdentifier != null, "weakSetName should be set in private identifier environment");
        }

        if (previousInfo != null && previousInfo.kind == global::Ts.Printer.PrivateIdentifierKind.Accessor && previousInfo.isStatic == isStatic && previousInfo.getterName == null)
        {
            previousInfo.getterName = getterName;
        }
        else
        {
            SetPrivateIdentifier(env, name, new PrivateIdentifierInfo
            {
                kind = global::Ts.Printer.PrivateIdentifierKind.Accessor,
                getterName = getterName,
                brandCheckIdentifier = brandCheckIdentifier,
                isStatic = isStatic,
                isValid = isValid,
            });
        }
    }

    // classfields.go:3029 — addPrivateIdentifierSetAccessorToEnvironment.
    private void AddPrivateIdentifierSetAccessorToEnvironment(Node name, ClassLexicalEnvironment lex, PrivateEnvironment env, bool isStatic, bool isValid, PrivateIdentifierInfo? previousInfo)
    {
        var setterName = CreateHoistedVariableForPrivateName(name, "_set");
        Node? brandCheckIdentifier;
        if (isStatic)
        {
            brandCheckIdentifier = lex.classThis ?? lex.classConstructor;
            Debug.Assert(brandCheckIdentifier != null, "classConstructor should be set in private identifier environment");
        }
        else
        {
            brandCheckIdentifier = env.data.weakSetName;
            Debug.Assert(brandCheckIdentifier != null, "weakSetName should be set in private identifier environment");
        }

        if (previousInfo != null && previousInfo.kind == global::Ts.Printer.PrivateIdentifierKind.Accessor && previousInfo.isStatic == isStatic && previousInfo.setterName == null)
        {
            previousInfo.setterName = setterName;
        }
        else
        {
            SetPrivateIdentifier(env, name, new PrivateIdentifierInfo
            {
                kind = global::Ts.Printer.PrivateIdentifierKind.Accessor,
                setterName = setterName,
                brandCheckIdentifier = brandCheckIdentifier,
                isStatic = isStatic,
                isValid = isValid,
            });
        }
    }

    // classfields.go:3056 — addPrivateIdentifierAutoAccessorToEnvironment.
    private void AddPrivateIdentifierAutoAccessorToEnvironment(Node node, Node name, ClassLexicalEnvironment lex, PrivateEnvironment env, bool isStatic, bool isValid)
    {
        var getterName = CreateHoistedVariableForPrivateName(name, "_get");
        var setterName = CreateHoistedVariableForPrivateName(name, "_set");
        Node? brandCheckIdentifier;
        if (isStatic)
        {
            brandCheckIdentifier = lex.classThis ?? lex.classConstructor;
            Debug.Assert(brandCheckIdentifier != null, "classConstructor should be set in private identifier environment");
        }
        else
        {
            brandCheckIdentifier = env.data.weakSetName;
            Debug.Assert(brandCheckIdentifier != null, "weakSetName should be set in private identifier environment");
        }

        SetPrivateIdentifier(env, name, new PrivateIdentifierInfo
        {
            kind = global::Ts.Printer.PrivateIdentifierKind.Accessor,
            getterName = getterName,
            setterName = setterName,
            brandCheckIdentifier = brandCheckIdentifier,
            isStatic = isStatic,
            isValid = isValid,
        });
    }

    // classfields.go:3081 — addPrivateIdentifierToEnvironment.
    private void AddPrivateIdentifierToEnvironment(Node node)
    {
        var lex = GetClassLexicalEnvironment();
        var env = GetPrivateIdentifierEnvironment();
        var name = node.Name()!;
        bool isStatic = AstUtilities.HasStaticModifier(node);
        var (previousInfo, _) = GetPrivateIdentifier(env, name);
        bool isValid = !IsReservedPrivateName(name) && previousInfo == null;

        if (AstUtilities.IsAutoAccessorPropertyDeclaration(node))
        {
            AddPrivateIdentifierAutoAccessorToEnvironment(node, name, lex, env, isStatic, isValid);
        }
        else if (NodeTests.IsPropertyDeclaration(node))
        {
            AddPrivateIdentifierPropertyDeclarationToEnvironment(node, name);
        }
        else if (NodeTests.IsMethodDeclaration(node))
        {
            AddPrivateIdentifierMethodToEnvironment(name, lex, env, isStatic, isValid);
        }
        else if (NodeTests.IsGetAccessorDeclaration(node))
        {
            AddPrivateIdentifierGetAccessorToEnvironment(name, lex, env, isStatic, isValid, previousInfo);
        }
        else if (NodeTests.IsSetAccessorDeclaration(node))
        {
            AddPrivateIdentifierSetAccessorToEnvironment(name, lex, env, isStatic, isValid, previousInfo);
        }
    }

    // classfields.go:3102 — setPrivateIdentifier.
    private void SetPrivateIdentifier(PrivateEnvironment env, Node name, PrivateIdentifierInfo info)
    {
        if (EmitContext().HasAutoGenerateInfo(name))
        {
            env.generatedIdentifiers ??= new Dictionary<Node, PrivateIdentifierInfo>();
            env.generatedIdentifiers[EmitContext().GetNodeForGeneratedName(name)] = info;
        }
        else
        {
            env.members![name.Text()] = info;
        }
    }

    // classfields.go:3113 — getPrivateIdentifier.
    private (PrivateIdentifierInfo? Info, bool Ok) GetPrivateIdentifier(PrivateEnvironment env, Node name)
    {
        if (EmitContext().HasAutoGenerateInfo(name))
        {
            if (env.generatedIdentifiers != null && env.generatedIdentifiers.TryGetValue(EmitContext().GetNodeForGeneratedName(name), out var ginfo))
            {
                return (ginfo, true);
            }
            return (null, false);
        }
        if (env.members != null && env.members.TryGetValue(name.Text(), out var info))
        {
            return (info, true);
        }
        return (null, false);
    }

    // classfields.go:3122 — createHoistedVariableForClass.
    private Node CreateHoistedVariableForClass(string nameText, Node node, string suffix)
    {
        var env = GetPrivateIdentifierEnvironment();
        Node identifier;
        if (env.data.className != null)
        {
            var prefix = "_" + env.data.className.Text() + "_";
            identifier = Factory().NewUniqueNameEx(prefix + nameText, new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
                Suffix = suffix,
            });
        }
        else
        {
            identifier = Factory().NewUniqueNameEx("_" + nameText, new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
                Suffix = suffix,
            });
        }
        if (RequiresBlockScopedVar())
        {
            EmitContext().AddLexicalDeclaration(identifier);
        }
        else
        {
            EmitContext().AddVariableDeclaration(identifier);
        }
        return identifier;
    }

    // classfields.go:3145 — createHoistedVariableForClassFromNode.
    private Node CreateHoistedVariableForClassFromNode(Node name, string suffix)
    {
        var env = GetPrivateIdentifierEnvironment();
        string prefix = env.data.className != null ? "_" + env.data.className.Text() + "_" : "_";
        var identifier = Factory().NewGeneratedNameForNodeEx(name, new global::Ts.Printer.AutoGenerateOptions
        {
            Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic | global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
            Prefix = prefix,
            Suffix = suffix,
        });
        if (RequiresBlockScopedVar())
        {
            EmitContext().AddLexicalDeclaration(identifier);
        }
        else
        {
            EmitContext().AddVariableDeclaration(identifier);
        }
        return identifier;
    }

    // classfields.go:3166 — createHoistedVariableForPrivateName. If the name is a generated identifier
    // (e.g., auto-accessor backing field), use node-based name generation so the emitter can resolve the
    // name properly.
    private Node CreateHoistedVariableForPrivateName(Node name, string suffix)
    {
        if (EmitContext().HasAutoGenerateInfo(name))
        {
            return CreateHoistedVariableForClassFromNode(name, suffix);
        }
        var text = name.Text();
        if (text.Length >= 1 && text[0] == '#')
        {
            text = text[1..]; // strip leading '#'
        }
        return CreateHoistedVariableForClass(text, name, suffix);
    }

    // classfields.go:3181 — accessPrivateIdentifier: accesses an already defined PrivateIdentifier in
    // the current PrivateIdentifierEnvironment.
    private PrivateIdentifierInfo? AccessPrivateIdentifier(Node name)
    {
        for (var env = lexicalEnvironment; env != null; env = env.previous)
        {
            if (env.privateEnv != null)
            {
                var (info, ok) = GetPrivateIdentifier(env.privateEnv, name);
                if (ok)
                {
                    if (info!.kind == global::Ts.Printer.PrivateIdentifierKind.Untransformed)
                    {
                        return null;
                    }
                    return info;
                }
            }
        }
        return null;
    }

    // classfields.go:3391 — isReservedPrivateName.
    private bool IsReservedPrivateName(Node node) =>
        !(NodeTests.IsPrivateIdentifier(node) && EmitContext().HasAutoGenerateInfo(node)) && node.Text() == "#constructor";

    // classfields.go:3395 — isStaticPropertyDeclarationOrClassStaticBlock.
    private static bool IsStaticPropertyDeclarationOrClassStaticBlock(Node node) =>
        NodeTests.IsClassStaticBlockDeclaration(node) ||
        (NodeTests.IsPropertyDeclaration(node) && AstUtilities.HasStaticModifier(node));

    // ── Batch 2: skeleton visitors (classfields.go:220-833) ────────────────────────────────────────

    // classfields.go:220 — visitSourceFile.
    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }
        lexicalEnvironment = null;
        shouldTransformPrivateStaticElementsInFile = (EmitContext().EmitFlags(node.AsNode()) & global::Ts.Printer.EmitFlags.TransformPrivateStaticElements) != 0;
        classAliases = new Dictionary<Node, IdentifierNode>();
        global::Ts.Collections.SetExtensions.Clear(enclosingClassDeclarations);
        var visited = Visitor().VisitEachChild(node.AsNode())!;
        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        classAliases = null;
        global::Ts.Collections.SetExtensions.Clear(enclosingClassDeclarations);
        return visited;
    }

    // classfields.go:265 — visitForSubstitution: class-alias substitution in subtrees without class-field
    // or lexical this/super transforms; skips PropertyAccessExpression names (Strada's onSubstituteNode
    // only fires for EmitHint.Expression).
    private Node? VisitForSubstitution(Node node)
    {
        if (node.Kind == SyntaxKind.Identifier)
        {
            return VisitIdentifier(node);
        }
        if (node.Kind == SyntaxKind.PropertyAccessExpression && NodeTests.IsIdentifier(node.AsPropertyAccessExpression().Name()!))
        {
            return VisitPropertyAccessExpressionForSubstitution(node);
        }
        return substitutionVisitor.VisitEachChild(node);
    }

    // classfields.go:343 — visitDiscardedValue.
    private Node? VisitDiscardedValue(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
                return VisitPreOrPostfixUnaryExpression(node, true /*discarded*/);
            case SyntaxKind.BinaryExpression:
                return VisitBinaryExpression(node, true /*discarded*/);
            case SyntaxKind.ParenthesizedExpression:
                return VisitParenthesizedExpression(node, true /*discarded*/);
            default:
                return Visit(node);
        }
    }

    // classfields.go:357 — visitHeritageClause.
    private Node? VisitHeritageClause(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.HeritageClause:
                return heritageClauseVisitor.VisitEachChild(node);
            case SyntaxKind.ExpressionWithTypeArguments:
                return VisitExpressionWithTypeArgumentsInHeritageClause(node);
            default:
                return Visit(node);
        }
    }

    // classfields.go:369 — visitAssignmentTarget.
    private Node? VisitAssignmentTarget(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.ArrayLiteralExpression:
                return VisitAssignmentPattern(node);
            default:
                return Visit(node);
        }
    }

    // classfields.go:378 — visitDestructuringAssignmentTarget.
    private Node? VisitDestructuringAssignmentTarget(Node node)
    {
        if (NodeTests.IsObjectLiteralExpression(node) || NodeTests.IsArrayLiteralExpression(node))
        {
            return VisitAssignmentPattern(node);
        }
        if (NodeTests.IsPropertyAccessExpression(node) && NodeTests.IsPrivateIdentifier(node.AsPropertyAccessExpression().Name()!))
        {
            return WrapPrivateIdentifierForDestructuringTarget(node);
        }
        if (shouldTransformSuperInStaticInitializers && currentClassElement != null &&
            AstUtilities.IsSuperProperty(node) &&
            IsStaticPropertyDeclarationOrClassStaticBlock(currentClassElement) &&
            lexicalEnvironment is { data: { } data })
        {
            if ((data.facts & ClassFacts.ClassWasDecorated) != 0)
            {
                return VisitInvalidSuperProperty(node);
            }
            if (data.classConstructor != null && data.superClassReference != null)
            {
                Node? name = null;
                if (NodeTests.IsElementAccessExpression(node))
                {
                    name = Visitor().VisitNode(node.AsElementAccessExpression().ArgumentExpression);
                }
                else if (NodeTests.IsPropertyAccessExpression(node) && NodeTests.IsIdentifier(node.AsPropertyAccessExpression().Name()!))
                {
                    name = Factory().NewStringLiteralFromNode(node.AsPropertyAccessExpression().Name()!);
                }
                if (name != null)
                {
                    var temp = Factory().NewTempVariable();
                    var setExpr = Factory().NewReflectSetCall(
                        data.superClassReference,
                        name,
                        temp,
                        data.classConstructor);
                    return Factory().NewAssignmentTargetWrapper(temp, setExpr);
                }
            }
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:416 — visitClassElement.
    private Node? VisitClassElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
                return SetCurrentClassElementAnd(node, static (tx, n) => tx.VisitConstructorDeclaration(n), node);
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.MethodDeclaration:
                return SetCurrentClassElementAnd(node, static (tx, n) => tx.VisitMethodOrAccessorDeclaration(n), node);
            case SyntaxKind.PropertyDeclaration:
                return SetCurrentClassElementAnd(node, static (tx, n) => tx.VisitPropertyDeclaration(n), node);
            case SyntaxKind.ClassStaticBlockDeclaration:
                return SetCurrentClassElementAnd(node, static (tx, n) => tx.VisitClassStaticBlockDeclaration(n), node);
            case SyntaxKind.ComputedPropertyName:
                return VisitComputedPropertyName(node);
            case SyntaxKind.SemicolonClassElement:
                return node;
            default:
                if (AstUtilities.IsModifierLike(node))
                {
                    return VisitModifier(node);
                }
                return Visit(node);
        }
    }

    // classfields.go:439 — visitPropertyName.
    private Node? VisitPropertyName(Node name)
    {
        if (NodeTests.IsComputedPropertyName(name))
        {
            return VisitComputedPropertyName(name);
        }
        return Visitor().VisitNode(name);
    }

    // classfields.go:447 — visitAccessorFieldResult: visits the results of an auto-accessor field
    // transformation in a second pass.
    private Node? VisitAccessorFieldResult(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
                return TransformFieldInitializer(node);
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return VisitClassElement(node);
            default:
                Debug.Fail("Expected node to either be a PropertyDeclaration, GetAccessorDeclaration, or SetAccessorDeclaration");
                return null;
        }
    }

    // classfields.go:462 — visitIdentifier. Replaces Strada's onSubstituteNode/trySubstituteClassAlias:
    // resolve the identifier to its declaration and check for a registered alias.
    private Node VisitIdentifier(Node node)
    {
        var declaration = resolver?.GetReferencedValueDeclaration(EmitContext().MostOriginal(node)!);
        if (declaration != null && classAliases != null &&
            classAliases.TryGetValue(declaration, out var alias) && global::Ts.Collections.SetExtensions.Has(enclosingClassDeclarations, declaration))
        {
            var clone = alias.Clone(Factory())!;
            EmitContext().SetSourceMapRange(clone, node.Loc);
            EmitContext().SetCommentRange(clone, node.Loc);
            return clone;
        }
        return node;
    }

    // classfields.go:479 — visitPrivateIdentifier: an undeclared private name is replaced with an empty
    // identifier to indicate a problem with the code. Private identifiers in statement position (e.g.
    // `#;`) are intercepted earlier by visitExpressionStatement.
    private Node VisitPrivateIdentifier(Node node)
    {
        if (!shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            return node;
        }
        if (parentNode != null && AstUtilities.IsStatement(parentNode))
        {
            return node;
        }
        var result = Factory().NewIdentifier("");
        EmitContext().SetOriginal(result, node);
        return result;
    }

    // classfields.go:492 — transformPrivateIdentifierInInExpression: visits `#id in expr`.
    private Node TransformPrivateIdentifierInInExpression(Node node)
    {
        var bin = node.AsBinaryExpression();
        var info = AccessPrivateIdentifier(bin.Left!);
        if (info != null)
        {
            var receiver = Visitor().VisitNode(bin.Right!);
            var result = Factory().NewClassPrivateFieldInHelper(info.brandCheckIdentifier!, receiver!);
            EmitContext().SetOriginal(result, node);
            return result;
        }
        // Private name has not been declared. Subsequent transformers will handle this error.
        return Visitor().VisitEachChild(node)!;
    }

    // classfields.go:504 — visitPropertyAssignment (13.2.5.5 RS: PropertyDefinitionEvaluation).
    private Node? VisitPropertyAssignment(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, false /*ignoreEmptyStringLiteral*/, "" /*assignedName*/);
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:518 — visitVariableStatement.
    private Node? VisitVariableStatement(Node node)
    {
        var savedPendingStatements = pendingStatements;
        pendingStatements = null;

        var visitedNode = Visitor().VisitEachChild(node)!;

        if (pendingStatements is { Count: > 0 })
        {
            var result = new List<Node>(1 + pendingStatements.Count) { visitedNode };
            result.AddRange(pendingStatements);
            pendingStatements = savedPendingStatements;
            return Factory().AsNodeFactory().NewSyntaxList(result.ToArray());
        }

        pendingStatements = savedPendingStatements;
        return visitedNode;
    }

    // classfields.go:536 — visitVariableDeclaration (14.3.1.2 / 14.3.2.1 RS).
    private Node? VisitVariableDeclaration(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, false, "");
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:557 — visitParameterDeclaration (8.6.3 / 14.3.3.3 RS).
    private Node? VisitParameterDeclaration(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, false, "");
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:580 — visitBindingElement (8.6.3 / 14.3.3.3 RS).
    private Node? VisitBindingElement(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, false, "");
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:603 — visitExportAssignment (16.2.3.7 RS). NOTE: since emit for `export =`
    // translates to `module.exports = ...`, the assigned name of the class is "".
    private Node? VisitExportAssignment(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
        {
            var assignedName = node.AsExportAssignment().IsExportEquals ? "" : "default";
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, true /*ignoreEmptyStringLiteral*/, assignedName);
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:623 — injectPendingExpressions.
    private Node InjectPendingExpressions(Node expression)
    {
        if (pendingExpressions is { Count: > 0 })
        {
            if (NodeTests.IsParenthesizedExpression(expression))
            {
                pendingExpressions.Add(expression.Expression()!);
                expression = Factory().UpdateParenthesizedExpression(
                    expression.AsParenthesizedExpression(),
                    Factory().InlineExpressions(pendingExpressions.ToArray())!);
            }
            else
            {
                pendingExpressions.Add(expression);
                expression = Factory().InlineExpressions(pendingExpressions.ToArray())!;
            }
            pendingExpressions = null;
        }
        return expression;
    }

    // classfields.go:640 — visitComputedPropertyName: computed property names are evaluated in the
    // enclosing scope, not the current class (replaces Strada's onEmitNode for ComputedPropertyName).
    private Node VisitComputedPropertyName(Node node)
    {
        var savedLexicalEnvironment = lexicalEnvironment;
        var savedInsideComputedPropertyName = insideComputedPropertyName;
        insideComputedPropertyName = true;
        if (lexicalEnvironment is { previous: not null })
        {
            lexicalEnvironment = lexicalEnvironment.previous;
        }
        var expression = Visitor().VisitNode(node.AsComputedPropertyName().Expression!);
        lexicalEnvironment = savedLexicalEnvironment;
        insideComputedPropertyName = savedInsideComputedPropertyName;
        return Factory().AsNodeFactory().UpdateComputedPropertyName(node.AsComputedPropertyName(), InjectPendingExpressions(expression!));
    }

    // classfields.go:656 — visitConstructorDeclaration.
    private Node? VisitConstructorDeclaration(Node node)
    {
        if (currentClassContainer != null)
        {
            return TransformConstructor(node, currentClassContainer);
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:663 — shouldTransformClassElementToWeakMap.
    private bool ShouldTransformClassElementToWeakMap(Node node)
    {
        if (shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            return true;
        }
        return ShouldAlwaysTransformPrivateStaticElements(node);
    }

    // classfields.go:670 — shouldAlwaysTransformPrivateStaticElements.
    private bool ShouldAlwaysTransformPrivateStaticElements(Node node) =>
        AstUtilities.HasStaticModifier(node) && (EmitContext().EmitFlags(node) & global::Ts.Printer.EmitFlags.TransformPrivateStaticElements) != 0;

    // classfields.go:677 — nodeHasTransformPrivateStaticElementsFlag: checks the emit flag on a class
    // node (not a member) — class nodes themselves don't have a static modifier.
    private bool NodeHasTransformPrivateStaticElementsFlag(Node node) =>
        (EmitContext().EmitFlags(node) & global::Ts.Printer.EmitFlags.TransformPrivateStaticElements) != 0;

    // classfields.go:681 — visitMethodOrAccessorDeclaration.
    private Node? VisitMethodOrAccessorDeclaration(Node node)
    {
        Debug.Assert(!AstUtilities.HasDecorators(node));

        if (!AstUtilities.IsPrivateIdentifierClassElementDeclaration(node) || !ShouldTransformClassElementToWeakMap(node))
        {
            return classElementVisitor.VisitEachChild(node);
        }

        // leave invalid code untransformed
        var info = AccessPrivateIdentifier(node.Name()!);
        Debug.Assert(info != null, "Undeclared private name for property declaration.");
        if (!info!.isValid)
        {
            return node;
        }

        var functionName = GetHoistedFunctionName(node);
        if (functionName != null)
        {
            var modifiers = ExtractNonStaticNonAccessorModifiers(node);
            EmitContext().StartVariableEnvironment();
            var saved = inIterationStatement;
            inIterationStatement = false;
            var body = EmitContext().VisitFunctionBody(node.Body(), Visitor());
            var params_ = Visitor().VisitNodes(node.ParameterList());
            inIterationStatement = saved;

            var funcExpr = Factory().NewFunctionExpression(modifiers, node.BodyData()!.AsteriskToken, functionName, null, params_, null, null, body);
            var assignment = Factory().NewAssignmentExpression(functionName, funcExpr);
            AddPendingExpressions(assignment);
        }

        // remove method declaration from class
        return null;
    }

    // classfields.go:714 — extractNonStaticNonAccessorModifiers.
    private ModifierList? ExtractNonStaticNonAccessorModifiers(Node node) =>
        global::Ts.Transformers.Transformers.ExtractModifiers(EmitContext(), node.Modifiers(), ~(ModifierFlags.Static | ModifierFlags.Accessor));

    // classfields.go:718 — setCurrentClassElementAnd.
    private Node? SetCurrentClassElementAnd(Node? classElement, Func<ClassFieldsTransformer, Node, Node?> visitor, Node node)
    {
        if (classElement != currentClassElement)
        {
            var saved = currentClassElement;
            currentClassElement = classElement;
            var result = visitor(this, node);
            currentClassElement = saved;
            return result;
        }
        return visitor(this, node);
    }

    // classfields.go:730 — visitEachChildOfNode.
    private Node? VisitEachChildOfNode(Node node) => Visitor().VisitEachChild(node);

    // classfields.go:734 — setInIterationStatementAnd.
    private Node? SetInIterationStatementAnd(bool inIteration, Func<ClassFieldsTransformer, Node, Node?> visitor, Node node)
    {
        if (inIterationStatement != inIteration)
        {
            var saved = inIterationStatement;
            inIterationStatement = inIteration;
            var result = visitor(this, node);
            inIterationStatement = saved;
            return result;
        }
        return visitor(this, node);
    }

    // classfields.go:745 — clearClassElementAndVisitEachChild.
    private Node? ClearClassElementAndVisitEachChild(Node node) =>
        SetCurrentClassElementAnd(null, static (tx, n) => tx.VisitEachChildOfNode(n), node);

    // classfields.go:761 — visitFunctionExpressionOrDeclaration: lexical-environment scoping for
    // function expressions/declarations (mirrors Strada's onEmitNode; preserves currentClassElement for
    // functions originating from static class members so visitThisExpression can substitute `this` ->
    // `_classThis` inside synthesized descriptor functions).
    private Node? VisitFunctionExpressionOrDeclaration(Node node)
    {
        if (currentClassElement != null)
        {
            var original = EmitContext().MostOriginal(node);
            if (original != node && currentClassContainer != null)
            {
                foreach (var member in currentClassContainer.Members() ?? [])
                {
                    if (EmitContext().MostOriginal(member) == original && AstUtilities.IsStatic(member))
                    {
                        return VisitEachChildOfNode(node);
                    }
                }
            }
        }
        return SetCurrentClassElementAnd(null, static (tx, n) => tx.VisitEachChildOfNode(n), node);
    }

    // classfields.go:781 — setClassElementAndVisitEachChild.
    private Node? SetClassElementAndVisitEachChild(Node node) =>
        SetCurrentClassElementAnd(node, static (tx, n) => tx.VisitEachChildOfNode(n), node);

    // classfields.go:785 — getHoistedFunctionName.
    private Node? GetHoistedFunctionName(Node node)
    {
        Debug.Assert(node.Name() != null && NodeTests.IsPrivateIdentifier(node.Name()!));
        var info = AccessPrivateIdentifier(node.Name()!);
        Debug.Assert(info != null, "Undeclared private name for property declaration.");
        if (info!.kind == global::Ts.Printer.PrivateIdentifierKind.Method)
        {
            return info.methodName;
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Accessor)
        {
            if (NodeTests.IsGetAccessorDeclaration(node))
            {
                return info.getterName;
            }
            if (NodeTests.IsSetAccessorDeclaration(node))
            {
                return info.setterName;
            }
        }
        return null;
    }

    // classfields.go:803 — tryGetClassThis.
    private Node? TryGetClassThis()
    {
        var classThis = TryGetClassThisNoContainer();
        if (classThis != null)
        {
            return classThis;
        }
        return currentClassContainer?.Name();
    }

    // classfields.go:813 — tryGetClassThisNoContainer.
    private Node? TryGetClassThisNoContainer()
    {
        var lex = GetClassLexicalEnvironment();
        if (lex.classThis != null)
        {
            return lex.classThis;
        }
        if (lex.classConstructor != null)
        {
            return lex.classConstructor;
        }
        return null;
    }

    // ── Forward stubs (batches 3/4/5b replace these; unreachable until the batch-6 dispatch switch) ──
    private Node? VisitPreOrPostfixUnaryExpression(Node node, bool discarded) => throw new NotImplementedException("classfields batch 3");
    private Node? VisitBinaryExpression(Node node, bool discarded) => throw new NotImplementedException("classfields batch 3");
    private Node? VisitParenthesizedExpression(Node node, bool discarded) => throw new NotImplementedException("classfields batch 3");
    private Node? VisitExpressionWithTypeArgumentsInHeritageClause(Node node) => throw new NotImplementedException("classfields batch 4");
    private Node? VisitAssignmentPattern(Node node) => throw new NotImplementedException("classfields batch 5b");
    private Node? WrapPrivateIdentifierForDestructuringTarget(Node node) => throw new NotImplementedException("classfields batch 5b");
    private Node? VisitInvalidSuperProperty(Node node) => throw new NotImplementedException("classfields batch 4");
    private Node? TransformConstructor(Node node, Node container) => throw new NotImplementedException("classfields batch 4");
    private Node? VisitPropertyDeclaration(Node node) => throw new NotImplementedException("classfields batch 3");
    private Node? VisitClassStaticBlockDeclaration(Node node) => throw new NotImplementedException("classfields batch 4");
    private Node? TransformFieldInitializer(Node node) => throw new NotImplementedException("classfields batch 3");
    private Node? VisitPropertyAccessExpressionForSubstitution(Node node) => throw new NotImplementedException("classfields batch 3");
    private Node? VisitArrayAssignmentElement(Node node) => node;
    private Node? VisitObjectAssignmentElement(Node node) => node;
    private bool IsAnonymousClassNeedingAssignedNameWorker(Node node) => false;
}

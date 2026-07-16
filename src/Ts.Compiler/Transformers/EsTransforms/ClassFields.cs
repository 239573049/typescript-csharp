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

    // ── Batch 3: private-name access core (classfields.go:833-1815) plus the tail helpers it consumes
    // (classfields.go:3412-3618) ────────────────────────────────────────────────────────────────────

    // classfields.go:833 — transformAutoAccessor.
    private Node? TransformAutoAccessor(Node node)
    {
        var commentRange = EmitContext().CommentRange(node);
        var sourceMapRange = EmitContext().SourceMapRange(node);

        // Since we're creating two declarations where there was previously one, cache
        // the expression for any computed property names.
        var name = node.Name()!;
        var getterName = name;
        var setterName = name;
        if (NodeTests.IsComputedPropertyName(name) && !global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(name.Expression()!))
        {
            var cacheAssignment = FindComputedPropertyNameCacheAssignment(EmitContext(), name);
            if (cacheAssignment != null)
            {
                getterName = Factory().AsNodeFactory().UpdateComputedPropertyName(name.AsComputedPropertyName(), Visitor().VisitNode(name.Expression()!));
                setterName = Factory().AsNodeFactory().UpdateComputedPropertyName(name.AsComputedPropertyName(), cacheAssignment.Left);
            }
            else
            {
                var temp = Factory().NewTempVariable();
                EmitContext().SetSourceMapRange(temp, name.Expression()!.Loc);
                EmitContext().AddVariableDeclaration(temp);
                var expression = Visitor().VisitNode(name.Expression()!);
                var assignment = Factory().NewAssignmentExpression(temp, expression!);
                EmitContext().SetSourceMapRange(assignment, name.Expression()!.Loc);
                getterName = Factory().AsNodeFactory().UpdateComputedPropertyName(name.AsComputedPropertyName(), assignment);
                setterName = Factory().AsNodeFactory().UpdateComputedPropertyName(name.AsComputedPropertyName(), temp);
            }
        }

        var modifiers = modifierVisitor.VisitModifiers(node.Modifiers());
        var backingField = EsTransformUtilities.CreateAccessorPropertyBackingField(Factory(), node.AsPropertyDeclaration(), modifiers, node.Initializer());
        EmitContext().SetOriginal(backingField, node);
        EmitContext().AddEmitFlags(backingField, global::Ts.Printer.EmitFlags.NoComments);
        EmitContext().SetSourceMapRange(backingField, sourceMapRange);

        Node? receiver;
        if (AstUtilities.IsStatic(node))
        {
            receiver = TryGetClassThis();
            if (receiver == null)
            {
                receiver = Factory().NewThisExpression();
            }
        }
        else
        {
            receiver = Factory().NewThisExpression();
        }

        var getter = CreateAccessorPropertyGetRedirector(node.AsPropertyDeclaration(), modifiers, getterName, receiver);
        EmitContext().SetOriginal(getter, node);
        EmitContext().SetCommentRange(getter, commentRange);
        EmitContext().SetSourceMapRange(getter, sourceMapRange);

        // create a fresh copy of the modifiers so that we don't duplicate comments
        ModifierList? setterModifiers = null;
        if (modifiers != null)
        {
            setterModifiers = Factory().AsNodeFactory().NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modifiers.ModifierFlags, Factory().AsNodeFactory().NewModifier));
        }
        var setter = CreateAccessorPropertySetRedirector(node.AsPropertyDeclaration(), setterModifiers, setterName, receiver);
        EmitContext().SetOriginal(setter, node);
        EmitContext().AddEmitFlags(setter, global::Ts.Printer.EmitFlags.NoComments);
        EmitContext().SetSourceMapRange(setter, sourceMapRange);

        // Visit the results in a second pass
        var (visited, _) = accessorFieldResultVisitor.VisitSlice([backingField, getter, setter]);
        return Factory().AsNodeFactory().NewSyntaxList(visited!);
    }

    // classfields.go:895 — transformPrivateFieldInitializer.
    private Node? TransformPrivateFieldInitializer(Node node)
    {
        var propDecl = node.AsPropertyDeclaration();
        if (ShouldTransformClassElementToWeakMap(node))
        {
            // If we are transforming private elements into WeakMap/WeakSet, we should elide the node.
            var info = AccessPrivateIdentifier(node.Name()!);
            Debug.Assert(info != null, "Undeclared private name for property declaration.");

            // Leave invalid code untransformed
            if (!info!.isValid)
            {
                return node;
            }

            // If we encounter a valid private static field and we're not transforming
            // class static blocks, convert to a static block initializer.
            if (info.isStatic && !shouldTransformPrivateElementsOrClassStaticBlocks)
            {
                // TODO: fix
                var statement = TransformPropertyOrClassStaticBlock(node, Factory().NewThisExpression());
                if (statement != null)
                {
                    return Factory().AsNodeFactory().NewClassStaticBlockDeclaration(
                        null, /*modifiers*/
                        Factory().NewBlock(Factory().AsNodeFactory().NewNodeList([statement]), true /*multiLine*/));
                }
            }

            return null;
        }

        if (shouldTransformInitializersUsingSet && !AstUtilities.HasStaticModifier(node) &&
            lexicalEnvironment is { data: { } data } &&
            (data.facts & ClassFacts.WillHoistInitializersToConstructor) != 0)
        {
            return Factory().AsNodeFactory().UpdatePropertyDeclaration(
                propDecl,
                Visitor().VisitModifiers(node.Modifiers()),
                node.Name(),
                null, /*postfixToken*/
                null, /*typeNode*/
                null /*initializer*/);
        }

        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, false, "");
            propDecl = node.AsPropertyDeclaration();
        }

        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
            propDecl,
            modifierVisitor.VisitModifiers(propDecl.Modifiers()),
            VisitPropertyName(propDecl.Name()!),
            null, /*postfixToken*/
            null, /*typeNode*/
            Visitor().VisitNode(propDecl.Initializer));
    }

    // classfields.go:949 — transformPublicFieldInitializer.
    private Node? TransformPublicFieldInitializer(Node node)
    {
        var propDecl = node.AsPropertyDeclaration();
        if (shouldTransformInitializers && !AstUtilities.IsAutoAccessorPropertyDeclaration(node))
        {
            // Elide the property declaration; the initializer will be moved to the constructor.
            // For computed property names, we still need to emit the expression.
            var expr = GetPropertyNameExpressionIfNeeded(node.Name()!, propDecl.Initializer != null || compilerOptions.GetUseDefineForClassFields());
            if (expr != null)
            {
                foreach (var e in FlattenCommaList(expr))
                {
                    AddPendingExpressions(e);
                }
            }

            // When target >= ES2022 (i.e., !shouldTransformPrivateElementsOrClassStaticBlocks) and we
            // still need to transform initializers (useDefineForClassFields: false), static property
            // initializers must be converted into `static { this.x = ...; }` blocks so that `this`
            // refers to the class constructor inside the static block.
            if (AstUtilities.IsStatic(node) && !shouldTransformPrivateElementsOrClassStaticBlocks)
            {
                var initializerStatement = TransformPropertyOrClassStaticBlock(node, Factory().NewThisExpression());
                if (initializerStatement != null)
                {
                    var staticBlock = Factory().AsNodeFactory().NewClassStaticBlockDeclaration(
                        null, /*modifiers*/
                        Factory().NewBlock(Factory().AsNodeFactory().NewNodeList([initializerStatement]), false));

                    EmitContext().SetOriginal(staticBlock, node);
                    EmitContext().SetCommentRange(staticBlock, node.Loc);

                    EmitContext().AddEmitFlags(initializerStatement, global::Ts.Printer.EmitFlags.NoComments);
                    return staticBlock;
                }
            }

            return null;
        }

        return Factory().AsNodeFactory().UpdatePropertyDeclaration(
            propDecl,
            modifierVisitor.VisitModifiers(node.Modifiers()),
            VisitPropertyName(node.Name()!),
            null, /*postfixToken*/
            null, /*typeNode*/
            Visitor().VisitNode(propDecl.Initializer));
    }

    // classfields.go:993 — transformFieldInitializer.
    private Node? TransformFieldInitializer(Node node)
    {
        Debug.Assert(!AstUtilities.HasDecorators(node), "Decorators should already have been transformed and elided.");
        if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(node))
        {
            return TransformPrivateFieldInitializer(node);
        }
        return TransformPublicFieldInitializer(node);
    }

    // classfields.go:1011 — visitPropertyDeclaration.
    private Node? VisitPropertyDeclaration(Node node)
    {
        // If this is an auto-accessor, we defer to `transformAutoAccessor`. That function
        // will in turn call `transformFieldInitializer` as needed.
        if (AstUtilities.IsAutoAccessorPropertyDeclaration(node) && (ShouldTransformAutoAccessorsInCurrentClass() ||
            AstUtilities.HasStaticModifier(node) && ShouldAlwaysTransformPrivateStaticElements(node)))
        {
            return TransformAutoAccessor(node);
        }
        return TransformFieldInitializer(node);
    }

    // classfields.go:1022 — createPrivateIdentifierAccess.
    private Node CreatePrivateIdentifierAccess(PrivateIdentifierInfo info, Node receiver)
    {
        receiver = Visitor().VisitNode(receiver)!;
        return CreatePrivateIdentifierAccessHelper(info, receiver);
    }

    // classfields.go:1027 — createPrivateIdentifierAccessHelper.
    private Node CreatePrivateIdentifierAccessHelper(PrivateIdentifierInfo info, Node receiver)
    {
        EmitContext().SetCommentRange(receiver, new TextRange(-1, receiver.End));

        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Accessor)
        {
            return Factory().NewClassPrivateFieldGetHelper(
                receiver,
                info.brandCheckIdentifier!,
                info.kind,
                info.getterName);
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Method)
        {
            return Factory().NewClassPrivateFieldGetHelper(
                receiver,
                info.brandCheckIdentifier!,
                info.kind,
                info.methodName);
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Field)
        {
            Node? f = null;
            if (info.isStatic)
            {
                f = info.variableName;
            }
            return Factory().NewClassPrivateFieldGetHelper(
                receiver,
                info.brandCheckIdentifier!,
                info.kind,
                f);
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Untransformed)
        {
            Debug.Fail("Access helpers should not be created for untransformed private elements");
            return null!;
        }
        Debug.Fail("Unknown private element type");
        return null!;
    }

    // classfields.go:1064 — visitPropertyAccessExpression.
    private Node? VisitPropertyAccessExpression(Node node)
    {
        var propAccess = node.AsPropertyAccessExpression();
        if (NodeTests.IsPrivateIdentifier(propAccess.Name()!))
        {
            var info = AccessPrivateIdentifier(propAccess.Name()!);
            if (info != null)
            {
                var result = CreatePrivateIdentifierAccess(info, propAccess.Expression!);
                EmitContext().SetOriginal(result, node);
                result.Loc = node.Loc;
                return result;
            }
        }
        if (shouldTransformSuperInStaticInitializers && currentClassElement != null &&
            AstUtilities.IsSuperProperty(node) && NodeTests.IsIdentifier(propAccess.Name()!) &&
            IsStaticPropertyDeclarationOrClassStaticBlock(currentClassElement) &&
            lexicalEnvironment is { data: { } data })
        {
            if ((data.facts & ClassFacts.ClassWasDecorated) != 0)
            {
                return VisitInvalidSuperProperty(node);
            }
            if (data.classConstructor != null && data.superClassReference != null)
            {
                // converts `super.x` into `Reflect.get(_baseTemp, "x", _classTemp)`
                var superProperty = Factory().NewReflectGetCall(
                    data.superClassReference,
                    Factory().NewStringLiteralFromNode(propAccess.Name()!),
                    data.classConstructor);
                EmitContext().SetOriginal(superProperty, propAccess.Expression!);
                superProperty.Loc = propAccess.Expression!.Loc;
                return superProperty;
            }
        }
        // Visit only the expression, not the name (when it's a regular identifier), to prevent
        // substitution of property names. Strada's onSubstituteNode only fires for
        // EmitHint.Expression, which excludes the .name of PropertyAccessExpression.
        // Private identifier names are still visited through VisitEachChild so they can be
        // transformed by visitPrivateIdentifier.
        if (NodeTests.IsIdentifier(propAccess.Name()!))
        {
            return VisitPropertyAccessExpressionForSubstitution(node);
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:1108 — visitPropertyAccessExpressionForSubstitution visits only the expression of
    // a PropertyAccessExpression, leaving the name unchanged. This prevents the name from being treated
    // as a standalone identifier reference and incorrectly substituted with a class alias.
    private Node? VisitPropertyAccessExpressionForSubstitution(Node node)
    {
        var propAccess = node.AsPropertyAccessExpression();
        var expression = Visitor().VisitNode(propAccess.Expression!);
        if (expression != propAccess.Expression)
        {
            return Factory().AsNodeFactory().UpdatePropertyAccessExpression(propAccess, expression, propAccess.QuestionDotToken, propAccess.Name(), node.Flags);
        }
        return node;
    }

    // classfields.go:1116 — visitElementAccessExpression.
    private Node? VisitElementAccessExpression(Node node)
    {
        var elemAccess = node.AsElementAccessExpression();
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
                // converts `super[x]` into `Reflect.get(_baseTemp, x, _classTemp)`
                var superProperty = Factory().NewReflectGetCall(
                    data.superClassReference,
                    Visitor().VisitNode(elemAccess.ArgumentExpression)!,
                    data.classConstructor);
                EmitContext().SetOriginal(superProperty, elemAccess.Expression!);
                superProperty.Loc = elemAccess.Expression!.Loc;
                return superProperty;
            }
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:1140 — visitPreOrPostfixUnaryExpression.
    private Node? VisitPreOrPostfixUnaryExpression(Node node, bool discarded)
    {
        SyntaxKind @operator;
        Node operand;
        if (NodeTests.IsPrefixUnaryExpression(node))
        {
            @operator = node.AsPrefixUnaryExpression().Operator;
            operand = node.AsPrefixUnaryExpression().Operand!;
        }
        else
        {
            @operator = node.AsPostfixUnaryExpression().Operator;
            operand = node.AsPostfixUnaryExpression().Operand!;
        }

        if (@operator == SyntaxKind.PlusPlusToken || @operator == SyntaxKind.MinusMinusToken)
        {
            var operandSkipped = AstUtilities.SkipParentheses(operand);

            // Private identifier property access
            if (NodeTests.IsPropertyAccessExpression(operandSkipped) && NodeTests.IsPrivateIdentifier(operandSkipped.Name()!))
            {
                var info = AccessPrivateIdentifier(operandSkipped.Name()!);
                if (info != null)
                {
                    var receiver = Visitor().VisitNode(operandSkipped.Expression()!)!;
                    var (readExpression, initializeExpression) = CreateCopiableReceiverExpr(receiver);

                    var expression = CreatePrivateIdentifierAccessHelper(info, readExpression);
                    Node? temp = null;
                    if (!NodeTests.IsPrefixUnaryExpression(node) && !discarded)
                    {
                        temp = Factory().NewTempVariable();
                        EmitContext().AddVariableDeclaration(temp);
                    }
                    expression = ExpandPreOrPostfixIncrementOrDecrementExpression(Factory(), EmitContext(), node, expression, temp);
                    var assignReceiver = readExpression;
                    if (initializeExpression != null)
                    {
                        assignReceiver = initializeExpression;
                    }
                    expression = CreatePrivateIdentifierAssignment(info, assignReceiver, expression, SyntaxKind.EqualsToken);
                    EmitContext().SetOriginal(expression, node);
                    expression.Loc = node.Loc;
                    if (temp != null)
                    {
                        expression = Factory().NewCommaExpression(expression, temp);
                        expression.Loc = node.Loc;
                    }
                    return expression;
                }
            }
            else if (shouldTransformSuperInStaticInitializers && currentClassElement != null &&
                AstUtilities.IsSuperProperty(operandSkipped) &&
                IsStaticPropertyDeclarationOrClassStaticBlock(currentClassElement) &&
                lexicalEnvironment is { data: { } data })
            {
                // converts `++super.a` into `(Reflect.set(_baseTemp, "a", (_a = Reflect.get(_baseTemp, "a", _classTemp), _b = ++_a), _classTemp), _b)`
                // converts `++super[f()]` into `(Reflect.set(_baseTemp, _a = f(), (_b = Reflect.get(_baseTemp, _a, _classTemp), _c = ++_b), _classTemp), _c)`
                // converts `--super.a` into `(Reflect.set(_baseTemp, "a", (_a = Reflect.get(_baseTemp, "a", _classTemp), _b = --_a), _classTemp), _b)`
                // converts `--super[f()]` into `(Reflect.set(_baseTemp, _a = f(), (_b = Reflect.get(_baseTemp, _a, _classTemp), _c = --_b), _classTemp), _c)`
                // converts `super.a++` into `(Reflect.set(_baseTemp, "a", (_a = Reflect.get(_baseTemp, "a", _classTemp), _b = _a++), _classTemp), _b)`
                // converts `super[f()]++` into `(Reflect.set(_baseTemp, _a = f(), (_b = Reflect.get(_baseTemp, _a, _classTemp), _c = _b++), _classTemp), _c)`
                // converts `super.a--` into `(Reflect.set(_baseTemp, "a", (_a = Reflect.get(_baseTemp, "a", _classTemp), _b = _a--), _classTemp), _b)`
                // converts `super[f()]--` into `(Reflect.set(_baseTemp, _a = f(), (_b = Reflect.get(_baseTemp, _a, _classTemp), _c = _b--), _classTemp), _c)`
                if ((data.facts & ClassFacts.ClassWasDecorated) != 0)
                {
                    var visitedExpr = VisitInvalidSuperProperty(operandSkipped);
                    if (NodeTests.IsPrefixUnaryExpression(node))
                    {
                        return Factory().UpdatePrefixUnaryExpression(node.AsPrefixUnaryExpression(), node.AsPrefixUnaryExpression().Operator, visitedExpr);
                    }
                    return Factory().UpdatePostfixUnaryExpression(node.AsPostfixUnaryExpression(), visitedExpr, node.AsPostfixUnaryExpression().Operator);
                }
                if (data.classConstructor != null && data.superClassReference != null)
                {
                    Node? setterName = null;
                    Node? getterName = null;
                    if (NodeTests.IsPropertyAccessExpression(operandSkipped))
                    {
                        if (NodeTests.IsIdentifier(operandSkipped.Name()!))
                        {
                            getterName = Factory().NewStringLiteralFromNode(operandSkipped.Name()!);
                            setterName = getterName;
                        }
                    }
                    else if (NodeTests.IsElementAccessExpression(operandSkipped))
                    {
                        if (global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(operandSkipped.AsElementAccessExpression().ArgumentExpression!))
                        {
                            getterName = operandSkipped.AsElementAccessExpression().ArgumentExpression;
                            setterName = getterName;
                        }
                        else
                        {
                            getterName = Factory().NewTempVariable();
                            EmitContext().AddVariableDeclaration(getterName);
                            setterName = Factory().NewAssignmentExpression(getterName, Visitor().VisitNode(operandSkipped.AsElementAccessExpression().ArgumentExpression!)!);
                        }
                    }
                    if (setterName != null && getterName != null)
                    {
                        var expression = Factory().NewReflectGetCall(data.superClassReference, getterName, data.classConstructor);
                        expression.Loc = operandSkipped.Loc;

                        Node? temp = null;
                        if (!discarded)
                        {
                            temp = Factory().NewTempVariable();
                            EmitContext().AddVariableDeclaration(temp);
                        }
                        expression = ExpandPreOrPostfixIncrementOrDecrementExpression(Factory(), EmitContext(), node, expression, temp);
                        expression = Factory().NewReflectSetCall(data.superClassReference, setterName, expression, data.classConstructor);
                        EmitContext().SetOriginal(expression, node);
                        expression.Loc = node.Loc;
                        if (temp != null)
                        {
                            expression = Factory().NewCommaExpression(expression, temp);
                            expression.Loc = node.Loc;
                        }
                        return expression;
                    }
                }
            }
        }
        return Visitor().VisitEachChild(node);
    }

    // classfields.go:1244 — visitForStatement.
    private Node? VisitForStatement(Node node)
    {
        var forStmt = node.AsForStatement();
        var initializer = discardedValueVisitor.VisitNode(forStmt.Initializer);
        var condition = Visitor().VisitNode(forStmt.Condition);
        var incrementor = discardedValueVisitor.VisitNode(forStmt.Incrementor);
        var saved = inIterationStatement;
        inIterationStatement = true;
        var body = EmitContext().VisitIterationBody(forStmt.Statement, Visitor());
        inIterationStatement = saved;
        return Factory().UpdateForStatement(forStmt, initializer, condition, incrementor, body);
    }

    // classfields.go:1255 — visitExpressionStatement: preserve private identifiers that appear directly
    // as the expression of an ExpressionStatement (e.g., `#;`). This is error-recovery output from the
    // parser for invalid syntax. Keeping it ensures the runtime throws a SyntaxError rather than
    // silently succeeding with an empty statement.
    private Node? VisitExpressionStatement(Node node)
    {
        var exprStmt = node.AsExpressionStatement();
        if (NodeTests.IsPrivateIdentifier(exprStmt.Expression!) && shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            return node;
        }
        return Factory().AsNodeFactory().UpdateExpressionStatement(
            exprStmt,
            discardedValueVisitor.VisitNode(exprStmt.Expression));
    }

    // classfields.go:1269 — createCopiableReceiverExpr.
    private (Node readExpression, Node? initializeExpression) CreateCopiableReceiverExpr(Node receiver)
    {
        var clone = receiver;
        if (!AstUtilities.NodeIsSynthesized(receiver))
        {
            clone = receiver.Clone(Factory())!;
        }
        if (global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(receiver))
        {
            return (clone, null);
        }
        var readExpression = Factory().NewTempVariable();
        EmitContext().AddVariableDeclaration(readExpression);
        var initializeExpression = Factory().NewAssignmentExpression(readExpression, clone);
        return (readExpression, initializeExpression);
    }

    // classfields.go:1283 — visitCallExpression.
    private Node? VisitCallExpression(Node node)
    {
        var call = node.AsCallExpression();
        if (NodeTests.IsPropertyAccessExpression(call.Expression!) && NodeTests.IsPrivateIdentifier(call.Expression!.AsPropertyAccessExpression().Name()!) &&
            AccessPrivateIdentifier(call.Expression!.AsPropertyAccessExpression().Name()!) != null)
        {
            // obj.#x()

            // Transform call expressions of private names to properly bind the `this` parameter.
            var (thisArg, target) = CreateCallBinding(call.Expression!);
            var visitedTarget = Visitor().VisitNode(target)!;
            var visitedThisArg = Visitor().VisitNode(thisArg)!;
            var visitedArgs = Visitor().VisitNodes(call.Arguments)!;
            var allArgs = new List<Node>(1 + visitedArgs.Nodes.Length) { visitedThisArg };
            allArgs.AddRange(visitedArgs.Nodes);
            if ((node.Flags & NodeFlags.OptionalChain) != 0)
            {
                return Factory().UpdateCallExpression(
                    call,
                    Factory().NewPropertyAccessExpression(visitedTarget, call.QuestionDotToken, Factory().NewIdentifier("call"), NodeFlags.OptionalChain),
                    null, /*questionDotToken*/
                    null, /*typeArguments*/
                    Factory().AsNodeFactory().NewNodeList(allArgs.ToArray()),
                    node.Flags);
            }
            return Factory().UpdateCallExpression(
                call,
                Factory().NewPropertyAccessExpression(visitedTarget, null, Factory().NewIdentifier("call"), NodeFlags.None),
                null, /*questionDotToken*/
                null, /*typeArguments*/
                Factory().AsNodeFactory().NewNodeList(allArgs.ToArray()),
                node.Flags);
        }

        if (shouldTransformSuperInStaticInitializers && currentClassElement != null &&
            AstUtilities.IsSuperProperty(call.Expression!) &&
            IsStaticPropertyDeclarationOrClassStaticBlock(currentClassElement) &&
            lexicalEnvironment is { data: { } data2 } &&
            data2.classConstructor != null)
        {
            // super.x()
            // super[x]()

            // converts `super.f(...)` into `Reflect.get(_baseTemp, "f", _classTemp).call(_classTemp, ...)`
            var invocation = Factory().NewFunctionCallCall(
                Visitor().VisitNode(call.Expression!)!,
                data2.classConstructor,
                Visitor().VisitNodes(call.Arguments)!.Nodes);
            EmitContext().SetOriginal(invocation, node);
            invocation.Loc = node.Loc;
            return invocation;
        }

        return Visitor().VisitEachChild(node);
    }

    // classfields.go:1338 — visitTaggedTemplateExpression.
    private Node? VisitTaggedTemplateExpression(Node node)
    {
        var tagged = node.AsTaggedTemplateExpression();
        if (NodeTests.IsPropertyAccessExpression(tagged.Tag!) && NodeTests.IsPrivateIdentifier(tagged.Tag!.AsPropertyAccessExpression().Name()!) &&
            AccessPrivateIdentifier(tagged.Tag!.AsPropertyAccessExpression().Name()!) != null)
        {
            // Bind the `this` correctly for tagged template literals when the tag is a private identifier property access.
            var (thisArg, target) = CreateCallBinding(tagged.Tag!);
            var bindExpr = Factory().NewCallExpression(
                Factory().NewPropertyAccessExpression(Visitor().VisitNode(target)!, null, Factory().NewIdentifier("bind"), NodeFlags.None),
                null, /*questionDotToken*/
                null, /*typeArguments*/
                Factory().AsNodeFactory().NewNodeList([Visitor().VisitNode(thisArg)!]),
                NodeFlags.None);
            return Factory().UpdateTaggedTemplateExpression(
                tagged,
                bindExpr,
                null, /*questionDotToken*/
                null, /*typeArguments*/
                Visitor().VisitNode(tagged.Template!),
                node.Flags);
        }

        if (shouldTransformSuperInStaticInitializers && currentClassElement != null &&
            AstUtilities.IsSuperProperty(tagged.Tag!) &&
            IsStaticPropertyDeclarationOrClassStaticBlock(currentClassElement) &&
            lexicalEnvironment is { data: { } data } &&
            data.classConstructor != null)
        {
            // converts `` super.f`x` `` into `` Reflect.get(_baseTemp, "f", _classTemp).bind(_classTemp)`x` ``
            var invocation = Factory().NewFunctionBindCall(
                Visitor().VisitNode(tagged.Tag!)!,
                data.classConstructor,
                null!);
            EmitContext().SetOriginal(invocation, node);
            invocation.Loc = node.Loc;
            return Factory().UpdateTaggedTemplateExpression(
                tagged,
                invocation,
                null, /*questionDotToken*/
                null, /*typeArguments*/
                Visitor().VisitNode(tagged.Template!),
                node.Flags);
        }

        return Visitor().VisitEachChild(node);
    }

    // classfields.go:1386 — transformClassStaticBlockDeclaration.
    private Node? TransformClassStaticBlockDeclaration(Node node)
    {
        if (shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            if (ClassThisUtilities.IsClassThisAssignmentBlock(EmitContext(), node))
            {
                var result = Visitor().VisitNode(node.AsClassStaticBlockDeclaration().Body!.AsBlock().Statements!.Nodes[0].Expression()!)!;
                // If the generated `_classThis` assignment is a noop (i.e., `_classThis = _classThis`), we can
                // eliminate the expression
                if (AstUtilities.IsAssignmentExpression(result, true /*excludeCompoundAssignment*/))
                {
                    var binary = result.AsBinaryExpression();
                    if (binary.Left == binary.Right)
                    {
                        return null;
                    }
                }
                return result;
            }

            if (NamedEvaluationUtilities.IsClassNamedEvaluationHelperBlock(EmitContext(), node))
            {
                return Visitor().VisitNode(node.AsClassStaticBlockDeclaration().Body!.AsBlock().Statements!.Nodes[0].Expression()!);
            }

            EmitContext().StartVariableEnvironment();
            var statements = SetCurrentClassElementAndVisitStatements(node, node.AsClassStaticBlockDeclaration().Body!.AsBlock().Statements!.Nodes);
            statements = EmitContext().EndAndMergeVariableEnvironment(statements);

            var iife = Factory().NewImmediatelyInvokedArrowFunction(statements!);
            var arrowFunction = AstUtilities.SkipParentheses(iife.Expression()!);
            EmitContext().SetOriginal(arrowFunction, node);
            EmitContext().AddEmitFlags(arrowFunction, global::Ts.Printer.EmitFlags.NoLexicalArguments);
            // Preserve the statement list source range so the printer can emit detached comments
            // (e.g., `// do` inside an otherwise empty static block)
            arrowFunction.AsArrowFunction().Body!.AsBlock().Statements!.Loc = node.AsClassStaticBlockDeclaration().Body!.AsBlock().Statements!.Loc;
            EmitContext().SetOriginal(iife, node);
            EmitContext().AssignSourceMapRange(iife, node);
            EmitContext().AddEmitFlags(arrowFunction, global::Ts.Printer.EmitFlags.NoLexicalThis);
            return iife;
        }
        return null;
    }

    // classfields.go:1424 — setCurrentClassElementAndVisitStatements.
    private Node[]? SetCurrentClassElementAndVisitStatements(Node classElement, Node[]? statements)
    {
        var savedCurrentClassElement = currentClassElement;
        currentClassElement = classElement;
        var (result, _) = Visitor().VisitSlice(statements);
        currentClassElement = savedCurrentClassElement;
        return result;
    }

    // classfields.go:1432 — isAnonymousClassNeedingAssignedNameWorker.
    private bool IsAnonymousClassNeedingAssignedNameWorker(Node node)
    {
        if (NodeTests.IsClassExpression(node) && node.Name() == null)
        {
            var staticPropertiesOrClassStaticBlocks = GetStaticPropertiesAndClassStaticBlock(node);
            if (CoreUtil.Some(staticPropertiesOrClassStaticBlocks, n =>
                NamedEvaluationUtilities.IsClassNamedEvaluationHelperBlock(EmitContext(), n)))
            {
                return false;
            }
            var hasTransformableStatics = (shouldTransformPrivateElementsOrClassStaticBlocks ||
                NodeHasTransformPrivateStaticElementsFlag(node)) &&
                CoreUtil.Some(staticPropertiesOrClassStaticBlocks, n =>
                    NodeTests.IsClassStaticBlockDeclaration(n) ||
                    AstUtilities.IsPrivateIdentifierClassElementDeclaration(n) ||
                    shouldTransformInitializers && AstUtilities.IsInitializedProperty(n));
            return hasTransformableStatics;
        }
        return false;
    }

    // classfields.go:1452 — visitBinaryExpression.
    private Node? VisitBinaryExpression(Node node, bool discarded)
    {
        var bin = node.AsBinaryExpression();
        if (AstUtilities.IsDestructuringAssignment(node))
        {
            // ({ x: obj.#x } = ...)
            // ({ x: super.x } = ...)
            // ({ x: super[x] } = ...)
            var savedPendingExpressions = pendingExpressions;
            pendingExpressions = null;
            var updated = Factory().UpdateBinaryExpression(
                bin,
                null,
                assignmentTargetVisitor.VisitNode(bin.Left),
                null,
                bin.OperatorToken,
                Visitor().VisitNode(bin.Right));
            Node? result;
            if (pendingExpressions is { Count: > 0 })
            {
                var exprs = new List<Node>(pendingExpressions) { updated };
                result = Factory().InlineExpressions(exprs.ToArray());
            }
            else
            {
                result = updated;
            }
            pendingExpressions = savedPendingExpressions;
            return result;
        }

        if (AstUtilities.IsAssignmentExpression(node, false /*excludeCompound*/))
        {
            // 13.15.2 RS: Evaluation
            //   AssignmentExpression : LeftHandSideExpression `=` AssignmentExpression
            //     1. If |LeftHandSideExpression| is neither an |ObjectLiteral| nor an |ArrayLiteral|, then
            //        a. Let _lref_ be ? Evaluation of |LeftHandSideExpression|.
            //        b. If IsAnonymousFunctionDefinition(|AssignmentExpression|) and IsIdentifierRef of |LeftHandSideExpression| are both *true*, then
            //           i. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...
            //
            //   AssignmentExpression : LeftHandSideExpression `&&=` AssignmentExpression
            //     ...
            //     5. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and IsIdentifierRef of |LeftHandSideExpression| is *true*, then
            //        a. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...
            //
            //   AssignmentExpression : LeftHandSideExpression `||=` AssignmentExpression
            //     ...
            //     5. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and IsIdentifierRef of |LeftHandSideExpression| is *true*, then
            //        a. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...
            //
            //   AssignmentExpression : LeftHandSideExpression `??=` AssignmentExpression
            //     ...
            //     4. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and IsIdentifierRef of |LeftHandSideExpression| is *true*, then
            //        a. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...

            if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, isAnonymousClassNeedingAssignedName))
            {
                node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, false, "")!;
                bin = node.AsBinaryExpression();
                // Go asserts node != nil as well; `node` is non-nullable here by construction.
                Debug.Assert(AstUtilities.IsAssignmentExpression(node, false));
            }

            var left = AstUtilities.SkipOuterExpressions(bin.Left!, OuterExpressionKinds.PartiallyEmittedExpressions | OuterExpressionKinds.Parentheses);
            if (NodeTests.IsPropertyAccessExpression(left) && NodeTests.IsPrivateIdentifier(left.Name()!))
            {
                // obj.#x = ...
                var info = AccessPrivateIdentifier(left.Name()!);
                if (info != null)
                {
                    var result = CreatePrivateIdentifierAssignment(info, left.Expression()!, bin.Right!, bin.OperatorToken!.Kind);
                    EmitContext().SetOriginal(result, node);
                    result.Loc = node.Loc;
                    return result;
                }
            }
            else if (shouldTransformSuperInStaticInitializers && currentClassElement != null &&
                AstUtilities.IsSuperProperty(bin.Left!) &&
                IsStaticPropertyDeclarationOrClassStaticBlock(currentClassElement) &&
                lexicalEnvironment is { data: { } data })
            {
                // super.x = ...
                // super[x] = ...
                // super.x += ...
                // super.x -= ...
                if ((data.facts & ClassFacts.ClassWasDecorated) != 0)
                {
                    return Factory().UpdateBinaryExpression(
                        bin,
                        null,
                        VisitInvalidSuperProperty(bin.Left!),
                        null,
                        bin.OperatorToken,
                        Visitor().VisitNode(bin.Right));
                }
                if (data.classConstructor != null && data.superClassReference != null)
                {
                    Node? setterName = null;
                    if (NodeTests.IsElementAccessExpression(bin.Left!))
                    {
                        setterName = Visitor().VisitNode(bin.Left!.AsElementAccessExpression().ArgumentExpression!);
                    }
                    else if (NodeTests.IsPropertyAccessExpression(bin.Left!) && NodeTests.IsIdentifier(bin.Left!.AsPropertyAccessExpression().Name()!))
                    {
                        setterName = Factory().NewStringLiteralFromNode(bin.Left!.AsPropertyAccessExpression().Name()!);
                    }
                    if (setterName != null)
                    {
                        // converts `super.x = 1` into `(Reflect.set(_baseTemp, "x", _a = 1, _classTemp), _a)`
                        // converts `super[f()] = 1` into `(Reflect.set(_baseTemp, f(), _a = 1, _classTemp), _a)`
                        // converts `super.x += 1` into `(Reflect.set(_baseTemp, "x", _a = Reflect.get(_baseTemp, "x", _classtemp) + 1, _classTemp), _a)`
                        // converts `super[f()] += 1` into `(Reflect.set(_baseTemp, _a = f(), _b = Reflect.get(_baseTemp, _a, _classtemp) + 1, _classTemp), _b)`

                        var expression = Visitor().VisitNode(bin.Right!)!;
                        if (AstUtilities.IsCompoundAssignment(bin.OperatorToken!.Kind))
                        {
                            var getterName = setterName;
                            if (!global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(setterName))
                            {
                                getterName = Factory().NewTempVariable();
                                EmitContext().AddVariableDeclaration(getterName);
                                setterName = Factory().NewAssignmentExpression(getterName, setterName);
                            }
                            var superPropertyGet = Factory().NewReflectGetCall(
                                data.superClassReference,
                                getterName,
                                data.classConstructor);
                            EmitContext().SetOriginal(superPropertyGet, bin.Left!);
                            superPropertyGet.Loc = bin.Left!.Loc;
                            expression = Factory().NewBinaryExpression(
                                null,
                                superPropertyGet,
                                null,
                                Factory().NewToken(global::Ts.Transformers.Transformers.GetNonAssignmentOperatorForCompoundAssignment(bin.OperatorToken.Kind)),
                                expression);
                            expression.Loc = node.Loc;
                        }

                        Node? temp = null;
                        if (!discarded)
                        {
                            temp = Factory().NewTempVariable();
                            EmitContext().AddVariableDeclaration(temp);
                        }
                        if (temp != null)
                        {
                            expression = Factory().NewAssignmentExpression(temp, expression);
                            expression.Loc = node.Loc;
                        }

                        expression = Factory().NewReflectSetCall(
                            data.superClassReference,
                            setterName,
                            expression,
                            data.classConstructor);
                        EmitContext().SetOriginal(expression, node);
                        expression.Loc = node.Loc;

                        if (temp != null)
                        {
                            expression = Factory().NewCommaExpression(expression, temp);
                            expression.Loc = node.Loc;
                        }
                        return expression;
                    }
                }
            }
        }

        if (bin.OperatorToken!.Kind == SyntaxKind.InKeyword && NodeTests.IsPrivateIdentifier(bin.Left!))
        {
            // #x in obj
            return TransformPrivateIdentifierInInExpression(node);
        }

        return Visitor().VisitEachChild(node);
    }

    // classfields.go:1614 — visitParenthesizedExpression (8.4.5 RS: NamedEvaluation).
    private Node? VisitParenthesizedExpression(Node node, bool discarded)
    {
        var paren = node.AsParenthesizedExpression();
        if (discarded)
        {
            var discardedExpression = discardedValueVisitor.VisitNode(paren.Expression);
            return Factory().UpdateParenthesizedExpression(paren, discardedExpression);
        }
        var expression = Visitor().VisitNode(paren.Expression);
        return Factory().UpdateParenthesizedExpression(paren, expression);
    }

    // classfields.go:1627 — createPrivateIdentifierAssignment.
    private Node CreatePrivateIdentifierAssignment(PrivateIdentifierInfo info, Node receiver, Node right, SyntaxKind @operator)
    {
        receiver = Visitor().VisitNode(receiver)!;
        right = Visitor().VisitNode(right)!;

        if (AstUtilities.IsCompoundAssignment(@operator))
        {
            var (readExpression, initializeExpression) = CreateCopiableReceiverExpr(receiver);
            if (initializeExpression != null)
            {
                receiver = initializeExpression;
            }
            else
            {
                receiver = readExpression;
            }
            right = Factory().NewBinaryExpression(
                null,
                CreatePrivateIdentifierAccessHelper(info, readExpression),
                null,
                Factory().NewToken(global::Ts.Transformers.Transformers.GetNonAssignmentOperatorForCompoundAssignment(@operator)),
                right);
        }

        EmitContext().SetCommentRange(receiver, new TextRange(-1, receiver.End));

        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Accessor)
        {
            return Factory().NewClassPrivateFieldSetHelper(
                receiver,
                info.brandCheckIdentifier!,
                right,
                info.kind,
                info.setterName);
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Method)
        {
            return Factory().NewClassPrivateFieldSetHelper(
                receiver,
                info.brandCheckIdentifier!,
                right,
                info.kind,
                null);
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Field)
        {
            Node? f = null;
            if (info.isStatic)
            {
                f = info.variableName;
            }
            return Factory().NewClassPrivateFieldSetHelper(
                receiver,
                info.brandCheckIdentifier!,
                right,
                info.kind,
                f);
        }
        if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Untransformed)
        {
            Debug.Fail("Access helpers should not be created for untransformed private elements");
            return null!;
        }
        Debug.Fail("Unknown private element type");
        return null!;
    }

    // classfields.go:1686 — getPrivateInstanceMethodsAndAccessors.
    private Node[] GetPrivateInstanceMethodsAndAccessors(Node node) =>
        CoreUtil.Filter(node.Members(), IsNonStaticMethodOrAccessorWithPrivateName) ?? [];

    // classfields.go:1695 — memberContainsConstructorReference checks if a class member's body contains
    // an identifier that resolves to the class declaration. Replaces Strada's
    // resolver.hasNodeCheckFlag(member, NodeCheckFlags.ContainsConstructorReference) by walking the AST
    // with the EmitResolver. Only checks member bodies (not computed property names), since computed
    // property names are evaluated during class definition when the binding is still correct.
    private bool MemberContainsConstructorReference(Node member, Node classDecl)
    {
        var classOriginal = EmitContext().MostOriginal(classDecl);
        var className = AstUtilities.GetNameOfDeclaration(classDecl);
        bool Check(Node n)
        {
            if (NodeTests.IsIdentifier(n) && n != className)
            {
                var decl = resolver!.GetReferencedValueDeclaration(n);
                if (decl == classOriginal)
                {
                    return true;
                }
            }
            // For PropertyAccessExpression, only check the expression, not the name.
            // The .Name() is a property access name, not a value reference to the class.
            if (NodeTests.IsPropertyAccessExpression(n))
            {
                return Check(n.Expression()!);
            }
            return n.ForEachChild(Check);
        }
        // Check only the body/initializer of the member, not the name (which may be
        // a computed property name that shouldn't trigger alias substitution).
        if (NodeTests.IsClassStaticBlockDeclaration(member))
        {
            var body = member.AsClassStaticBlockDeclaration().Body;
            if (body != null && Check(body))
            {
                return true;
            }
        }
        else
        {
            var body = member.Body();
            if (body != null && Check(body))
            {
                return true;
            }
        }
        if (NodeTests.IsPropertyDeclaration(member))
        {
            var init = member.Initializer();
            if (init != null && Check(init))
            {
                return true;
            }
        }
        return false;
    }

    // classfields.go:1738 — classContainsConstructorReference checks if any member of a class contains
    // references to the class's own constructor. Replaces Strada's
    // resolver.hasNodeCheckFlag(node, NodeCheckFlags.ContainsConstructorReference).
    private bool ClassContainsConstructorReference(Node node)
    {
        foreach (var member in node.Members() ?? [])
        {
            if (MemberContainsConstructorReference(member, node))
            {
                return true;
            }
        }
        return false;
    }

    // classfields.go:1747 — getClassFacts.
    private ClassFacts GetClassFacts(Node node)
    {
        var facts = ClassFacts.None;

        var original = EmitContext().MostOriginal(node);
        if (AstUtilities.IsClassLike(original!) && AstUtilities.ClassOrConstructorParameterIsDecorated(legacyDecorators /*useLegacyDecorators*/, original!))
        {
            facts |= ClassFacts.ClassWasDecorated;
        }

        if (shouldTransformPrivateElementsOrClassStaticBlocks &&
            (ClassHasClassThisAssignment(EmitContext(), node) || NamedEvaluationUtilities.ClassHasExplicitlyAssignedName(EmitContext(), node)))
        {
            facts |= ClassFacts.NeedsClassConstructorReference;
        }

        var containsPublicInstanceFields = false;
        var containsInitializedPublicInstanceFields = false;
        var containsInstancePrivateElements = false;
        var containsInstanceAutoAccessors = false;

        foreach (var member in node.Members() ?? [])
        {
            if (AstUtilities.IsStatic(member))
            {
                if (member.Name() != null && (NodeTests.IsPrivateIdentifier(member.Name()!) || AstUtilities.IsAutoAccessorPropertyDeclaration(member)) &&
                    shouldTransformPrivateElementsOrClassStaticBlocks)
                {
                    facts |= ClassFacts.NeedsClassConstructorReference;
                }
                else if (AstUtilities.IsAutoAccessorPropertyDeclaration(member) && shouldTransformAutoAccessors &&
                    node.Name() == null && EmitContext().ClassThis(node) == null)
                {
                    facts |= ClassFacts.NeedsClassConstructorReference;
                }
                if (NodeTests.IsPropertyDeclaration(member) || NodeTests.IsClassStaticBlockDeclaration(member))
                {
                    if (shouldTransformThisInStaticInitializers && (member.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalThis) != 0)
                    {
                        facts |= ClassFacts.NeedsSubstitutionForThisInClassStaticField;
                        if ((facts & ClassFacts.ClassWasDecorated) == 0)
                        {
                            facts |= ClassFacts.NeedsClassConstructorReference;
                        }
                    }
                    if (shouldTransformSuperInStaticInitializers && (member.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalSuper) != 0)
                    {
                        if ((facts & ClassFacts.ClassWasDecorated) == 0)
                        {
                            facts |= ClassFacts.NeedsClassConstructorReference | ClassFacts.NeedsClassSuperReference;
                        }
                    }
                }
            }
            else if (!AstUtilities.HasAbstractModifier(EmitContext().MostOriginal(member)!))
            {
                if (AstUtilities.IsAutoAccessorPropertyDeclaration(member))
                {
                    containsInstanceAutoAccessors = true;
                    containsInstancePrivateElements = containsInstancePrivateElements || AstUtilities.IsPrivateIdentifierClassElementDeclaration(member);
                }
                else if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(member))
                {
                    containsInstancePrivateElements = true;
                    if (MemberContainsConstructorReference(member, node))
                    {
                        facts |= ClassFacts.NeedsClassConstructorReference;
                    }
                }
                else if (NodeTests.IsPropertyDeclaration(member))
                {
                    containsPublicInstanceFields = true;
                    containsInitializedPublicInstanceFields = containsInitializedPublicInstanceFields || member.Initializer() != null;
                }
            }
        }

        var willHoistInitializersToConstructor = (shouldTransformInitializersUsingDefine && containsPublicInstanceFields) ||
            (shouldTransformInitializersUsingSet && containsInitializedPublicInstanceFields) ||
            (shouldTransformPrivateElementsOrClassStaticBlocks && containsInstancePrivateElements) ||
            (shouldTransformPrivateElementsOrClassStaticBlocks && containsInstanceAutoAccessors && shouldTransformAutoAccessors);

        if (willHoistInitializersToConstructor)
        {
            facts |= ClassFacts.WillHoistInitializersToConstructor;
        }

        return facts;
    }

    // classfields.go:2876 — getPropertyNameExpressionIfNeeded.
    private Node? GetPropertyNameExpressionIfNeeded(Node name, bool shouldHoist)
    {
        if (!NodeTests.IsComputedPropertyName(name))
        {
            return null;
        }
        var cacheAssignment = FindComputedPropertyNameCacheAssignment(EmitContext(), name);
        // Switch to outer lex env for computed property name expressions, matching
        // Strada reference's onEmitNode behavior for ComputedPropertyName.
        var savedLexicalEnvironment = lexicalEnvironment;
        var savedInsideComputedPropertyName = insideComputedPropertyName;
        insideComputedPropertyName = true;
        if (lexicalEnvironment is { previous: not null })
        {
            lexicalEnvironment = lexicalEnvironment.previous;
        }
        var expression = Visitor().VisitNode(name.Expression()!);
        lexicalEnvironment = savedLexicalEnvironment;
        insideComputedPropertyName = savedInsideComputedPropertyName;
        var innerExpression = AstUtilities.SkipPartiallyEmittedExpressions(expression!);
        var inlinable = global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(innerExpression);
        var alreadyTransformed = cacheAssignment != null || (AstUtilities.IsAssignmentExpression(innerExpression, true /*excludeCompoundAssignment*/) && NodeTests.IsIdentifier(innerExpression.AsBinaryExpression().Left!) && global::Ts.Transformers.Transformers.IsGeneratedIdentifier(EmitContext(), innerExpression.AsBinaryExpression().Left));
        if (!alreadyTransformed && !inlinable && shouldHoist)
        {
            var generatedName = Factory().NewGeneratedNameForNode(name);
            if (RequiresBlockScopedVar())
            {
                EmitContext().AddLexicalDeclaration(generatedName);
            }
            else
            {
                EmitContext().AddVariableDeclaration(generatedName);
            }
            return Factory().NewAssignmentExpression(generatedName, expression!);
        }
        if (inlinable || NodeTests.IsIdentifier(innerExpression))
        {
            return null;
        }
        return expression;
    }

    // classfields.go:3412 — getStaticPropertiesAndClassStaticBlock.
    private Node[] GetStaticPropertiesAndClassStaticBlock(Node node)
    {
        var result = new List<Node>();
        foreach (var member in node.Members() ?? [])
        {
            if (NodeTests.IsClassStaticBlockDeclaration(member) || (NodeTests.IsPropertyDeclaration(member) && AstUtilities.HasStaticModifier(member)))
            {
                result.Add(member);
            }
        }
        return result.ToArray();
    }

    // classfields.go:3422 — classHasClassThisAssignment checks if a class has a static block that is a
    // class-this assignment.
    private static bool ClassHasClassThisAssignment(global::Ts.Printer.EmitContext emitContext, Node node)
    {
        foreach (var member in node.Members() ?? [])
        {
            if (ClassThisUtilities.IsClassThisAssignmentBlock(emitContext, member))
            {
                return true;
            }
        }
        return false;
    }

    // classfields.go:3432 — isNonStaticMethodOrAccessorWithPrivateName.
    private static bool IsNonStaticMethodOrAccessorWithPrivateName(Node member) =>
        !AstUtilities.IsStatic(member) &&
        (AstUtilities.IsMethodOrAccessor(member) || AstUtilities.IsAutoAccessorPropertyDeclaration(member)) &&
        NodeTests.IsPrivateIdentifier(member.Name()!);

    // classfields.go:3438 — createMemberAccessForPropertyName.
    private static Node CreateMemberAccessForPropertyName(global::Ts.Printer.NodeFactory factory, global::Ts.Printer.EmitContext emitContext, Node receiver, Node name, Node location)
    {
        if (NodeTests.IsComputedPropertyName(name))
        {
            var elementAccess = factory.NewElementAccessExpression(receiver, null, name.Expression(), NodeFlags.None);
            elementAccess.Loc = location.Loc;
            return elementAccess;
        }
        Node expression;
        if (NodeTests.IsIdentifier(name) || NodeTests.IsPrivateIdentifier(name))
        {
            expression = factory.NewPropertyAccessExpression(receiver, null, name, NodeFlags.None);
        }
        else
        {
            // string or numeric literal
            expression = factory.NewElementAccessExpression(receiver, null, name, NodeFlags.None);
        }
        emitContext.SetCommentRange(expression, name.Loc);
        emitContext.SetSourceMapRange(expression, name.Loc);
        emitContext.AddEmitFlags(expression, global::Ts.Printer.EmitFlags.NoNestedSourceMaps);
        return expression;
    }

    // classfields.go:3457 — createCallBinding.
    private (Node thisArg, Node target) CreateCallBinding(Node node)
    {
        if (AstUtilities.IsSuperProperty(node))
        {
            return (Factory().NewThisExpression(), node);
        }
        if (NodeTests.IsPropertyAccessExpression(node))
        {
            var expr = node.AsPropertyAccessExpression();
            if (ShouldBeCapturedInTempVariable(expr.Expression!))
            {
                var thisArg = Factory().NewTempVariable();
                EmitContext().AddVariableDeclaration(thisArg);
                var target = Factory().NewPropertyAccessExpression(
                    Factory().NewParenthesizedExpression( // TODO: do we even need these?
                        Factory().NewAssignmentExpression(thisArg, expr.Expression!)),
                    null,
                    expr.Name(),
                    NodeFlags.None);
                return (thisArg, target);
            }
            return (expr.Expression!, node);
        }
        return (Factory().NewVoidZeroExpression(), node);
    }

    // classfields.go:3484 — shouldBeCapturedInTempVariable.
    private static bool ShouldBeCapturedInTempVariable(Node node)
    {
        var target = AstUtilities.SkipParentheses(node);
        switch (target.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
                return false;
            default:
                return true;
        }
    }

    // classfields.go:3493 — createAccessorPropertyGetRedirector.
    private Node CreateAccessorPropertyGetRedirector(PropertyDeclaration node, ModifierList? modifiers, Node name, Node receiver)
    {
        var backingFieldName = Factory().NewGeneratedPrivateNameForNodeEx(node.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" });
        var returnExpr = Factory().NewPropertyAccessExpression(
            receiver,
            null,
            backingFieldName,
            NodeFlags.None);
        var returnStmt = Factory().NewReturnStatement(returnExpr);
        var body = Factory().NewBlock(Factory().AsNodeFactory().NewNodeList([returnStmt]), false);
        return Factory().AsNodeFactory().NewGetAccessorDeclaration(
            modifiers,
            name,
            null, /*typeParameters*/
            Factory().AsNodeFactory().NewNodeList([]),
            null, /*returnType*/
            null, /*fullSignature*/
            body);
    }

    // classfields.go:3514 — createAccessorPropertySetRedirector.
    private Node CreateAccessorPropertySetRedirector(PropertyDeclaration node, ModifierList? modifiers, Node name, Node receiver)
    {
        var backingFieldName = Factory().NewGeneratedPrivateNameForNodeEx(node.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" });
        var valueParam = Factory().NewParameterDeclaration(
            null, /*modifiers*/
            null, /*dotDotDotToken*/
            Factory().NewIdentifier("value"),
            null, /*questionToken*/
            null, /*typeNode*/
            null /*initializer*/);
        var assignExpr = Factory().NewAssignmentExpression(
            Factory().NewPropertyAccessExpression(
                receiver,
                null,
                backingFieldName,
                NodeFlags.None),
            Factory().NewIdentifier("value"));
        var exprStmt = Factory().NewExpressionStatement(assignExpr);
        var body = Factory().NewBlock(Factory().AsNodeFactory().NewNodeList([exprStmt]), false);
        return Factory().NewSetAccessorDeclaration(
            modifiers,
            name,
            null, /*typeParameters*/
            Factory().AsNodeFactory().NewNodeList([valueParam]),
            null, /*returnType*/
            null, /*fullSignature*/
            body);
    }

    // classfields.go:3547 — flattenCommaList decomposes a comma expression tree into a sequence of
    // expressions (Go iter.Seq → IEnumerable).
    private static IEnumerable<Node> FlattenCommaList(Node node)
    {
        if (NodeTests.IsParenthesizedExpression(node) && AstUtilities.NodeIsSynthesized(node))
        {
            foreach (var e in FlattenCommaList(node.Expression()!))
            {
                yield return e;
            }
        }
        else if (AstUtilities.IsCommaExpression(node))
        {
            foreach (var e in FlattenCommaList(node.AsBinaryExpression().Left!))
            {
                yield return e;
            }
            foreach (var e in FlattenCommaList(node.AsBinaryExpression().Right!))
            {
                yield return e;
            }
        }
        else
        {
            yield return node;
        }
    }

    // classfields.go:3564 — findComputedPropertyNameCacheAssignment.
    private static BinaryExpression? FindComputedPropertyNameCacheAssignment(global::Ts.Printer.EmitContext emitContext, Node name)
    {
        var node = name.Expression()!;
        while (true)
        {
            node = AstUtilities.SkipOuterExpressions(node, 0);
            if (NodeTests.IsBinaryExpression(node) && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken)
            {
                node = node.AsBinaryExpression().Right!;
                continue;
            }
            if (AstUtilities.IsAssignmentExpression(node, true /*excludeCompoundAssignment*/) && NodeTests.IsIdentifier(node.AsBinaryExpression().Left!))
            {
                return node.AsBinaryExpression();
            }
            break;
        }
        return null;
    }

    // classfields.go:3580 — expandPreOrPostfixIncrementOrDecrementExpression.
    private static Node ExpandPreOrPostfixIncrementOrDecrementExpression(global::Ts.Printer.NodeFactory factory, global::Ts.Printer.EmitContext emitContext, Node node, Node expression, Node? resultVariable)
    {
        SyntaxKind @operator;
        Node operand;
        if (NodeTests.IsPrefixUnaryExpression(node))
        {
            @operator = node.AsPrefixUnaryExpression().Operator;
            operand = node.AsPrefixUnaryExpression().Operand!;
        }
        else
        {
            @operator = node.AsPostfixUnaryExpression().Operator;
            operand = node.AsPostfixUnaryExpression().Operand!;
        }

        var temp = factory.NewTempVariable();
        emitContext.AddVariableDeclaration(temp);
        expression = factory.NewAssignmentExpression(temp, expression);
        expression.Loc = operand.Loc;

        Node operation;
        if (NodeTests.IsPrefixUnaryExpression(node))
        {
            operation = factory.NewPrefixUnaryExpression(@operator, temp);
        }
        else
        {
            operation = factory.AsNodeFactory().NewPostfixUnaryExpression(temp, @operator);
        }
        operation.Loc = node.Loc;

        if (resultVariable != null)
        {
            operation = factory.NewAssignmentExpression(resultVariable, operation);
            operation.Loc = node.Loc;
        }

        expression = factory.NewCommaExpression(expression, operation);
        expression.Loc = node.Loc;

        if (NodeTests.IsPostfixUnaryExpression(node))
        {
            expression = factory.NewCommaExpression(expression, temp);
            expression.Loc = node.Loc;
        }

        return expression;
    }

    // ── Batch 4: class environment (classfields.go:1815-2876) + the private-initializer factories it
    // consumes (classfields.go:3365-3410) ───────────────────────────────────────────────────────────

    // classfields.go:1815 — visitExpressionWithTypeArgumentsInHeritageClause.
    private Node? VisitExpressionWithTypeArgumentsInHeritageClause(Node node)
    {
        var facts = ClassFacts.None;
        if (lexicalEnvironment is { data: { } data })
        {
            facts = data.facts;
        }
        if ((facts & ClassFacts.NeedsClassSuperReference) != 0)
        {
            var temp = Factory().NewTempVariableEx(new global::Ts.Printer.AutoGenerateOptions
            {
                Flags = global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
            });
            EmitContext().AddVariableDeclaration(temp);
            GetClassLexicalEnvironment().superClassReference = temp;
            return Factory().UpdateExpressionWithTypeArguments(
                node.AsExpressionWithTypeArguments(),
                Factory().NewAssignmentExpression(temp, Visitor().VisitNode(node.AsExpressionWithTypeArguments().Expression!)!),
                null /*typeArguments*/);
        }
        return heritageClauseVisitor.VisitEachChild(node);
    }

    // classfields.go:1835 — visitInNewClassLexicalEnvironment.
    private Node? VisitInNewClassLexicalEnvironment(Node node, Func<ClassFieldsTransformer, Node, ClassFacts, Node?> visitor)
    {
        var savedCurrentClassContainer = currentClassContainer;
        var savedPendingExpressions = pendingExpressions;
        var savedLexicalEnvironment = lexicalEnvironment;
        currentClassContainer = node;
        pendingExpressions = null;
        StartClassLexicalEnvironment();
        var original = EmitContext().MostOriginal(node)!;
        enclosingClassDeclarations.Add(original);

        if (shouldTransformPrivateElementsOrClassStaticBlocks || NodeHasTransformPrivateStaticElementsFlag(node))
        {
            var name = AstUtilities.GetNameOfDeclaration(node);
            if (name != null && NodeTests.IsIdentifier(name))
            {
                GetPrivateIdentifierEnvironment().data.className = name;
            }
            else if (EmitContext().AssignedName(node) is { } assignedName)
            {
                if (NodeTests.IsStringLiteral(assignedName))
                {
                    // If the assigned name has a textSourceNode that is an identifier, use it directly.
                    if (EmitContext().TextSource(assignedName) is { } textSourceNode && NodeTests.IsIdentifier(textSourceNode))
                    {
                        GetPrivateIdentifierEnvironment().data.className = textSourceNode;
                    }
                    else if (global::Ts.Scanner.Scanner.IsIdentifierText(assignedName.Text(), LanguageVariant.Standard))
                    {
                        // If the text is a valid identifier, create an identifier from it.
                        var prefixName = Factory().NewIdentifier(assignedName.Text());
                        GetPrivateIdentifierEnvironment().data.className = prefixName;
                    }
                }
            }
        }

        if (shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            var privateInstanceMethodsAndAccessors = GetPrivateInstanceMethodsAndAccessors(node);
            if (privateInstanceMethodsAndAccessors.Length > 0)
            {
                GetPrivateIdentifierEnvironment().data.weakSetName = CreateHoistedVariableForClass(
                    "instances",
                    privateInstanceMethodsAndAccessors[0].Name()!,
                    "");
            }
        }

        var facts = GetClassFacts(node);
        if (facts != ClassFacts.None)
        {
            GetClassLexicalEnvironment().facts = facts;
        }

        var result = visitor(this, node, facts);
        enclosingClassDeclarations.Delete(original);
        EndClassLexicalEnvironment();
        Debug.Assert(lexicalEnvironment == savedLexicalEnvironment);
        currentClassContainer = savedCurrentClassContainer;
        pendingExpressions = savedPendingExpressions;
        lexicalEnvironment = savedLexicalEnvironment;
        return result;
    }

    // classfields.go:1889 — visitClassDeclaration.
    private Node? VisitClassDeclaration(Node node) =>
        VisitInNewClassLexicalEnvironment(node, static (tx, n, facts) => tx.VisitClassDeclarationInNewClassLexicalEnvironment(n, facts));

    // classfields.go:1893 — visitClassDeclarationInNewClassLexicalEnvironment.
    private Node? VisitClassDeclarationInNewClassLexicalEnvironment(Node node, ClassFacts facts)
    {
        var classDecl = node.AsClassDeclaration();
        // If a class has private static fields, or a static field has a `this` or `super` reference,
        // then we need to allocate a temp variable to hold on to that reference.
        Node? pendingClassReferenceAssignment = null;
        if ((facts & ClassFacts.NeedsClassConstructorReference) != 0)
        {
            // If we aren't transforming class static blocks, then we can't reuse `_classThis` since in
            // `class C { ... static { _classThis = ... } }; _classThis = C` the outer assignment would occur *after*
            // class static blocks evaluate and would overwrite the replacement constructor produced by class
            // decorators.

            // If we are transforming class static blocks, then we can reuse `_classThis` since the assignment
            // will be evaluated *before* the transformed static blocks are evaluated and thus won't overwrite
            // the replacement constructor.

            if (shouldTransformPrivateElementsOrClassStaticBlocks && EmitContext().ClassThis(node) != null)
            {
                var classThis = EmitContext().ClassThis(node)!;
                GetClassLexicalEnvironment().classConstructor = classThis;
                pendingClassReferenceAssignment = Factory().NewAssignmentExpression(
                    classThis,
                    Factory().GetLocalName(node));
            }
            else
            {
                var temp = Factory().NewTempVariableEx(new global::Ts.Printer.AutoGenerateOptions
                {
                    Flags = global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
                });
                EmitContext().AddVariableDeclaration(temp);
                GetClassLexicalEnvironment().classConstructor = temp.Clone(Factory());
                pendingClassReferenceAssignment = Factory().NewAssignmentExpression(
                    temp,
                    Factory().GetLocalName(node));
            }
        }

        if (EmitContext().ClassThis(node) != null)
        {
            GetClassLexicalEnvironment().classThis = EmitContext().ClassThis(node);
        }

        var isClassWithConstructorReference = ClassContainsConstructorReference(node);

        // Register class alias BEFORE visiting members (Strada registers after, since its
        // onSubstituteNode runs at emit time; we substitute eagerly during transformation).
        var alias = GetClassLexicalEnvironment().classConstructor;
        if (isClassWithConstructorReference && alias != null)
        {
            classAliases![EmitContext().MostOriginal(node)!] = alias;
        }

        var modifiers = modifierVisitor.VisitModifiers(classDecl.Modifiers());
        var heritageClauses = heritageClauseVisitor.VisitNodes(classDecl.HeritageClauses);
        var (members, membersPrologue) = TransformClassMembers(node);

        var statements = new List<Node>();

        if (pendingClassReferenceAssignment != null)
        {
            var newPending = new List<Node> { pendingClassReferenceAssignment };
            if (pendingExpressions != null)
            {
                newPending.AddRange(pendingExpressions);
            }
            pendingExpressions = newPending;
        }

        // Write any pending expressions from elided or moved computed property names
        if (pendingExpressions is { Count: > 0 })
        {
            statements.Add(Factory().NewExpressionStatement(Factory().InlineExpressions(pendingExpressions.ToArray())));
        }

        // A class declaration without a name needs a generated name if it has static
        // initialized properties, since those will be moved outside the class body and
        // need to reference the class by name.
        var name = classDecl.Name();

        if (shouldTransformInitializersUsingSet || shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            // Emit static property assignment. Because classDeclaration is lexically evaluated,
            // it is safe to emit static property assignment after classDeclaration
            // From ES6 specification:
            //   HasLexicalDeclaration (N) : Determines if the argument identifier has a binding in this environment record that was created using
            //                               a lexical declaration such as a LexicalDeclaration or a ClassDeclaration.
            var staticProperties = GetStaticPropertiesAndClassStaticBlock(node);
            if (staticProperties.Length > 0)
            {
                if (name == null)
                {
                    name = Factory().NewGeneratedNameForNode(node);
                }
                AddPropertyOrClassStaticBlockStatements(statements, staticProperties, Factory().GetLocalName(node));
            }
        }

        var isExport = AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export);
        var isDefault = AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default);

        if (statements.Count > 0 && isExport && isDefault)
        {
            modifiers = global::Ts.Transformers.Transformers.ExtractModifiers(EmitContext(), modifiers, ~ModifierFlags.ExportDefault);
            var exportAssignment = Factory().NewExportAssignment(null, false /*isExportEquals*/, null /*typeNode*/, Factory().GetLocalName(node));
            statements.Add(exportAssignment);
        }

        var updatedClass = Factory().UpdateClassDeclaration(
            classDecl,
            modifiers,
            name,
            null, /*typeParameters*/
            heritageClauses,
            members);

        var result = new List<Node>(1 + statements.Count + 1);
        if (membersPrologue != null)
        {
            result.Add(Factory().NewExpressionStatement(membersPrologue));
        }
        result.Add(updatedClass);
        result.AddRange(statements);
        return Factory().AsNodeFactory().NewSyntaxList(result.ToArray());
    }

    // classfields.go:2003 — visitClassExpression.
    private Node? VisitClassExpression(Node node) =>
        VisitInNewClassLexicalEnvironment(node, static (tx, n, facts) => tx.VisitClassExpressionInNewClassLexicalEnvironment(n, facts));

    // classfields.go:2007 — visitClassExpressionInNewClassLexicalEnvironment.
    private Node? VisitClassExpressionInNewClassLexicalEnvironment(Node node, ClassFacts facts)
    {
        var classExpr = node.AsClassExpression();

        // If this class expression is a transformation of a decorated class declaration,
        // then we want to output the pendingExpressions as statements, not as inlined
        // expressions with the class statement.
        //
        // In this case, we use pendingStatements to produce the same output as the
        // class declaration transformation. The VariableStatement visitor will insert
        // these statements after the class expression variable statement.
        var isDecoratedClassDeclaration = (facts & ClassFacts.ClassWasDecorated) != 0;

        if (EmitContext().ClassThis(node) != null)
        {
            GetClassLexicalEnvironment().classThis = EmitContext().ClassThis(node);
        }

        Node? temp = null;
        if ((facts & ClassFacts.NeedsClassConstructorReference) != 0)
        {
            if ((shouldTransformPrivateElementsOrClassStaticBlocks || NodeHasTransformPrivateStaticElementsFlag(node)) && EmitContext().ClassThis(node) != null)
            {
                var classThis = EmitContext().ClassThis(node);
                GetClassLexicalEnvironment().classConstructor = classThis;
                temp = classThis;
            }
            else
            {
                temp = Factory().NewTempVariableEx(new global::Ts.Printer.AutoGenerateOptions
                {
                    Flags = global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
                });
                if (ClassExpressionNeedsBlockScopedTemp())
                {
                    EmitContext().AddLexicalDeclaration(temp);
                }
                else
                {
                    EmitContext().AddVariableDeclaration(temp);
                }
                GetClassLexicalEnvironment().classConstructor = temp.Clone(Factory());
            }
        }

        var staticPropertiesOrClassStaticBlocks = GetStaticPropertiesAndClassStaticBlock(node);

        // Pre-compute whether the class expression will need a temp variable wrapper.
        // Strada registers class aliases AFTER transformClassMembers (since onSubstituteNode runs
        // at emit time), but we must predict this before visiting members since we substitute
        // eagerly. This requires pre-detecting willHavePrivatePendingExpressions.
        var isClassWithConstructorReference = false;
        var hasTransformableStatics = false;
        var deferTempDeclaration = false;
        if (!isDecoratedClassDeclaration)
        {
            isClassWithConstructorReference = ClassContainsConstructorReference(node);
            hasTransformableStatics = (shouldTransformPrivateElementsOrClassStaticBlocks ||
                NodeHasTransformPrivateStaticElementsFlag(node)) &&
                CoreUtil.Some(staticPropertiesOrClassStaticBlocks, n =>
                    NodeTests.IsClassStaticBlockDeclaration(n) ||
                    AstUtilities.IsPrivateIdentifierClassElementDeclaration(n) ||
                    (shouldTransformInitializers && AstUtilities.IsInitializedProperty(n)));

            // Private instance elements (fields, methods, accessors) transformed to
            // WeakMap/WeakSet will add initialization expressions to pendingExpressions
            // during transformClassMembers. Pre-detect this so we know whether the class
            // will be wrapped with a temp variable.
            var willHavePrivatePendingExpressions = shouldTransformPrivateElementsOrClassStaticBlocks &&
                CoreUtil.Some(node.Members(), n =>
                    AstUtilities.IsPrivateIdentifierClassElementDeclaration(n) && !AstUtilities.HasStaticModifier(n) && ShouldTransformClassElementToWeakMap(n));
            var willNeedTempWrapper = hasTransformableStatics || willHavePrivatePendingExpressions;

            // Register class alias BEFORE visiting members (Strada registers after, since its
            // onSubstituteNode runs at emit time). Only register when the class will be wrapped
            // with a temp, matching Strada's conditional registration.
            if (isClassWithConstructorReference && willNeedTempWrapper && GetClassLexicalEnvironment().classConstructor == null)
            {
                // Create temp early so the alias is available during member visiting, even though in the Strada
                // reference the temp would be created later in the pendingExpressions branch.
                temp = Factory().NewTempVariableEx(new global::Ts.Printer.AutoGenerateOptions
                {
                    Flags = global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
                });
                // Defer AddVariableDeclaration to preserve Strada's variable declaration ordering.
                deferTempDeclaration = true;
                GetClassLexicalEnvironment().classConstructor = temp.Clone(Factory());
            }
            if (GetClassLexicalEnvironment().classConstructor is { } alias && isClassWithConstructorReference && willNeedTempWrapper)
            {
                classAliases![EmitContext().MostOriginal(node)!] = alias;
            }
        }

        var modifiers = modifierVisitor.VisitModifiers(classExpr.Modifiers());
        var heritageClauses = heritageClauseVisitor.VisitNodes(classExpr.HeritageClauses);
        var (members, membersPrologue) = TransformClassMembers(node);

        if (deferTempDeclaration)
        {
            if (ClassExpressionNeedsBlockScopedTemp())
            {
                EmitContext().AddLexicalDeclaration(temp!);
            }
            else
            {
                EmitContext().AddVariableDeclaration(temp!);
            }
        }

        var classExpression = Factory().AsNodeFactory().UpdateClassExpression(
            classExpr,
            modifiers,
            classExpr.Name(),
            null, /*typeParameters*/
            heritageClauses,
            members);

        var expressions = new List<Node>();
        if (membersPrologue != null)
        {
            expressions.Add(membersPrologue);
        }

        if (!isDecoratedClassDeclaration)
        {
            if (hasTransformableStatics || pendingExpressions is { Count: > 0 })
            {
                if (temp == null)
                {
                    temp = Factory().NewTempVariableEx(new global::Ts.Printer.AutoGenerateOptions
                    {
                        Flags = global::Ts.Printer.GeneratedIdentifierFlags.ReservedInNestedScopes,
                    });
                    if (ClassExpressionNeedsBlockScopedTemp())
                    {
                        EmitContext().AddLexicalDeclaration(temp);
                    }
                    else
                    {
                        EmitContext().AddVariableDeclaration(temp);
                    }
                    GetClassLexicalEnvironment().classConstructor = temp.Clone(Factory());
                    if (isClassWithConstructorReference)
                    {
                        classAliases![EmitContext().MostOriginal(node)!] = GetClassLexicalEnvironment().classConstructor!;
                    }
                }

                expressions.Add(Factory().NewAssignmentExpression(temp, classExpression));

                // Add any pending expressions leftover from elided or relocated computed property names
                if (pendingExpressions != null)
                {
                    expressions.AddRange(pendingExpressions);
                }

                expressions.AddRange(GenerateInitializedPropertyExpressionsOrClassStaticBlock(staticPropertiesOrClassStaticBlocks, temp));
                expressions.Add(temp.Clone(Factory())!);
            }
            else
            {
                expressions.Add(classExpression);
            }
        }
        else
        {
            // Decorated class declaration path: emit static properties as separate statements
            // via pendingStatements, matching the class declaration output structure.

            // Write any pending expressions from elided or moved computed property names
            if (pendingExpressions is { Count: > 0 })
            {
                pendingStatements ??= new List<Node>();
                foreach (var expr in pendingExpressions)
                {
                    pendingStatements.Add(Factory().NewExpressionStatement(expr));
                }
            }

            // Emit static properties as statements (via pendingStatements) using the class's
            // internal name as the receiver, matching the class declaration output structure.
            if (staticPropertiesOrClassStaticBlocks.Length > 0)
            {
                var classThisOrName = EmitContext().ClassThis(node);
                if (classThisOrName == null)
                {
                    classThisOrName = Factory().GetLocalName(node);
                }
                pendingStatements ??= new List<Node>();
                AddPropertyOrClassStaticBlockStatements(pendingStatements, staticPropertiesOrClassStaticBlocks, classThisOrName);
            }

            if (temp != null)
            {
                expressions.Add(Factory().NewAssignmentExpression(temp, classExpression));
            }
            else if (shouldTransformPrivateElementsOrClassStaticBlocks && EmitContext().ClassThis(node) != null)
            {
                expressions.Add(Factory().NewAssignmentExpression(EmitContext().ClassThis(node)!, classExpression));
            }
            else
            {
                expressions.Add(classExpression);
            }
        }

        if (expressions.Count > 1)
        {
            EmitContext().AddEmitFlags(classExpression, global::Ts.Printer.EmitFlags.Indented);
            foreach (var expr in expressions)
            {
                EmitContext().AddEmitFlags(expr, global::Ts.Printer.EmitFlags.StartOnNewLine);
            }
        }
        return Factory().InlineExpressions(expressions.ToArray());
    }

    // classfields.go:2181 — visitClassStaticBlockDeclaration.
    private Node? VisitClassStaticBlockDeclaration(Node node)
    {
        if (!shouldTransformPrivateElementsOrClassStaticBlocks)
        {
            return Visitor().VisitEachChild(node);
        }
        // ClassStaticBlockDeclaration for classes are transformed in visitClassDeclaration/visitClassExpression.
        return null;
    }

    // classfields.go:2194 — visitThisExpression replaces Strada's substituteThisExpression /
    // onSubstituteNode. Strada substitutes `this` at emit time; we do it eagerly during transformation.
    //
    // The Strada noSubstitution set (ensureDynamicThisIfNeeded) is not needed because
    // transformAutoAccessor() passes the receiver directly rather than emitting `this`.
    private Node VisitThisExpression(Node node)
    {
        if (insideComputedPropertyName && shouldTransformThisInStaticInitializers &&
            lexicalEnvironment is { data: { } data })
        {
            // Don't replace `this` in computed property names for ES-decorated classes.
            // The esDecorator transformer wraps them in an arrow IIFE where `this` already
            // refers to the correct outer scope.
            if ((data.facts & ClassFacts.ClassWasDecorated) == 0 || legacyDecorators)
            {
                if (TryGetClassThisNoContainer() is { } classThis)
                {
                    return classThis;
                }
            }
        }
        if (shouldTransformThisInStaticInitializers && currentClassElement != null &&
            (NodeTests.IsClassStaticBlockDeclaration(currentClassElement) ||
                (NodeTests.IsPropertyDeclaration(currentClassElement) && AstUtilities.HasStaticModifier(currentClassElement))) &&
            lexicalEnvironment is { data: { } data2 })
        {
            if (TryGetClassThisNoContainer() is { } classThis)
            {
                return classThis;
            }
            // When the class was decorated with legacy decorators and no class constructor
            // reference is available, the decorator may replace the constructor, so `this`
            // cannot reliably point to the class. Use `(void 0)` instead.
            if ((data2.facts & ClassFacts.ClassWasDecorated) != 0 && legacyDecorators)
            {
                return Factory().NewParenthesizedExpression(Factory().NewVoidZeroExpression());
            }
        }
        return node;
    }

    // classfields.go:2223 — transformClassMembers.
    private (NodeList? members, Node? prologue) TransformClassMembers(Node node)
    {
        var shouldTransformPrivateStaticElementsInClass = (EmitContext().EmitFlags(node) & global::Ts.Printer.EmitFlags.TransformPrivateStaticElements) != 0;
        Node? prologue = null;

        // Declare private names
        if (shouldTransformPrivateElementsOrClassStaticBlocks || shouldTransformPrivateStaticElementsInFile)
        {
            foreach (var member in node.Members() ?? [])
            {
                if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(member))
                {
                    if (ShouldTransformClassElementToWeakMap(member))
                    {
                        AddPrivateIdentifierToEnvironment(member);
                    }
                    else
                    {
                        var env = GetPrivateIdentifierEnvironment();
                        SetPrivateIdentifier(env, member.Name()!, new PrivateIdentifierInfo
                        {
                            kind = global::Ts.Printer.PrivateIdentifierKind.Untransformed,
                        });
                    }
                }
            }

            if (shouldTransformPrivateElementsOrClassStaticBlocks)
            {
                if (GetPrivateInstanceMethodsAndAccessors(node).Length > 0)
                {
                    CreateBrandCheckWeakSetForPrivateMethods();
                }
            }

            if (ShouldTransformAutoAccessorsInCurrentClass())
            {
                foreach (var member in node.Members() ?? [])
                {
                    if (AstUtilities.IsAutoAccessorPropertyDeclaration(member))
                    {
                        var storageName = Factory().NewGeneratedPrivateNameForNodeEx(member.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" });
                        if (shouldTransformPrivateElementsOrClassStaticBlocks ||
                            shouldTransformPrivateStaticElementsInClass && AstUtilities.HasStaticModifier(member))
                        {
                            AddPrivateIdentifierPropertyDeclarationToEnvironment(member, storageName);
                        }
                        else
                        {
                            var env = GetPrivateIdentifierEnvironment();
                            // Only register as untransformed if it hasn't already been registered
                            // by the first loop (e.g., if esDecorators expanded a private auto-accessor
                            // into a backing field with the same generated name).
                            var (_, ok) = GetPrivateIdentifier(env, storageName);
                            if (!ok)
                            {
                                SetPrivateIdentifier(env, storageName, new PrivateIdentifierInfo
                                {
                                    kind = global::Ts.Printer.PrivateIdentifierKind.Untransformed,
                                });
                            }
                        }
                    }
                }
            }
        }

        var members = classElementVisitor.VisitNodes(node.MemberList())!;

        // Create a synthetic constructor if necessary
        Node? syntheticConstructor = null;
        if (!CoreUtil.Some(members.Nodes, NodeTests.IsConstructorDeclaration))
        {
            syntheticConstructor = TransformConstructor(null, node);
        }

        // If there are pending expressions create a class static block in which to evaluate them, but only if
        // class static blocks are not also being transformed. This block will be injected at the top of the class
        // to ensure that expressions from computed property names are evaluated before any other static
        // initializers.
        Node? syntheticStaticBlock = null;
        if (!shouldTransformPrivateElementsOrClassStaticBlocks && pendingExpressions is { Count: > 0 })
        {
            var statement = Factory().NewExpressionStatement(Factory().InlineExpressions(pendingExpressions.ToArray()));
            if ((statement.SubtreeFacts() & SubtreeFacts.SubtreeContainsLexicalThisOrSuper) != 0)
            {
                // If there are `this` or `super` references from computed property names, shift the expression
                // into an arrow function to be evaluated in the outer scope so that `this` and `super` are
                // properly captured.
                var temp = Factory().NewTempVariable();
                EmitContext().AddVariableDeclaration(temp);
                var arrow = Factory().AsNodeFactory().NewArrowFunction(
                    null, /*modifiers*/
                    null, /*typeParameters*/
                    Factory().AsNodeFactory().NewNodeList([]), /*parameters*/
                    null, /*returnType*/
                    null, /*fullSignature*/
                    Factory().NewToken(SyntaxKind.EqualsGreaterThanToken), /*equalsGreaterThanToken*/
                    Factory().NewBlock(Factory().AsNodeFactory().NewNodeList([statement]), false /*multiline*/));
                prologue = Factory().NewAssignmentExpression(temp, arrow);
                statement = Factory().NewExpressionStatement(
                    Factory().NewCallExpression(temp, null /*questionDotToken*/, null /*typeArguments*/, Factory().AsNodeFactory().NewNodeList([]), NodeFlags.None));
            }

            var block = Factory().NewBlock(Factory().AsNodeFactory().NewNodeList([statement]), false /*multiline*/);
            syntheticStaticBlock = Factory().AsNodeFactory().NewClassStaticBlockDeclaration(null /*modifiers*/, block);
            pendingExpressions = null;
        }

        // If we created a synthetic constructor or class static block, add them to the visited members
        if (syntheticConstructor != null || syntheticStaticBlock != null)
        {
            var membersArray = new List<Node>(members.Nodes.Length + 2);

            // Find and preserve classThis assignment block and named evaluation helper block at the top
            var classThisIdx = -1;
            var namedEvalIdx = -1;
            for (var i = 0; i < members.Nodes.Length; i++)
            {
                if (classThisIdx < 0 && ClassThisUtilities.IsClassThisAssignmentBlock(EmitContext(), members.Nodes[i]))
                {
                    classThisIdx = i;
                }
                if (namedEvalIdx < 0 && NamedEvaluationUtilities.IsClassNamedEvaluationHelperBlock(EmitContext(), members.Nodes[i]))
                {
                    namedEvalIdx = i;
                }
            }

            if (classThisIdx >= 0)
            {
                membersArray.Add(members.Nodes[classThisIdx]);
            }
            if (namedEvalIdx >= 0)
            {
                membersArray.Add(members.Nodes[namedEvalIdx]);
            }
            if (syntheticConstructor != null)
            {
                membersArray.Add(syntheticConstructor);
            }
            if (syntheticStaticBlock != null)
            {
                membersArray.Add(syntheticStaticBlock);
            }

            for (var i = 0; i < members.Nodes.Length; i++)
            {
                if (i != classThisIdx && i != namedEvalIdx)
                {
                    membersArray.Add(members.Nodes[i]);
                }
            }
            members = Factory().AsNodeFactory().NewNodeList(membersArray.ToArray());
            members.Loc = node.MemberList()!.Loc;
        }

        return (members, prologue);
    }

    // classfields.go:2348 — createBrandCheckWeakSetForPrivateMethods.
    private void CreateBrandCheckWeakSetForPrivateMethods()
    {
        var env = GetPrivateIdentifierEnvironment();
        var weakSetName = env.data.weakSetName;
        Debug.Assert(weakSetName != null, "weakSetName should be set in private identifier environment");

        AddPendingExpressions(
            Factory().NewAssignmentExpression(
                weakSetName!,
                Factory().AsNodeFactory().NewNewExpression(
                    Factory().NewIdentifier("WeakSet"),
                    null, /*typeArguments*/
                    Factory().AsNodeFactory().NewNodeList([]))));
    }

    // classfields.go:2365 — transformConstructor.
    // NOTE: The Strada reference pre-visits the constructor via `visitNode(constructor, visitor)` before
    // checking WillHoistInitializersToConstructor. This is not done here because Go's variable environment
    // (StartVariableEnvironment/EndAndMergeVariableEnvironment) is scoped inside transformConstructorBody.
    // Pre-visiting would hoist variables outside that scope, causing them to appear after field initializers
    // instead of before. Instead, we visit parameters and body separately within the correct scopes.
    private Node? TransformConstructor(Node? constructor, Node container)
    {
        if (lexicalEnvironment is not { data: { } data } ||
            (data.facts & ClassFacts.WillHoistInitializersToConstructor) == 0)
        {
            if (constructor != null)
            {
                return Visitor().VisitEachChild(constructor);
            }
            return null;
        }

        var extendsClauseElement = AstUtilities.GetClassExtendsHeritageElement(container);
        var isDerivedClass = extendsClauseElement != null && AstUtilities.SkipOuterExpressions(extendsClauseElement.Expression()!, OuterExpressionKinds.All).Kind != SyntaxKind.NullKeyword;

        NodeList? parameters = null;
        if (constructor != null)
        {
            parameters = Visitor().VisitNodes(constructor.AsConstructorDeclaration().Parameters);
        }

        var body = TransformConstructorBody(container, constructor, isDerivedClass);
        if (body == null)
        {
            if (constructor != null)
            {
                return Visitor().VisitEachChild(constructor);
            }
            return null;
        }

        if (constructor != null)
        {
            Debug.Assert(parameters != null);
            return Factory().AsNodeFactory().UpdateConstructorDeclaration(
                constructor.AsConstructorDeclaration(),
                null, /*modifiers*/
                null, /*typeParameters*/
                parameters,
                null, /*returnType*/
                null, /*fullSignature*/
                body);
        }

        if (parameters == null)
        {
            parameters = Factory().AsNodeFactory().NewNodeList([]);
        }

        var result = Factory().AsNodeFactory().NewConstructorDeclaration(
            null, /*modifiers*/
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        result.Loc = container.Loc;
        return result;
    }

    // classfields.go:2424 — transformConstructorBodyWorker.
    private List<Node> TransformConstructorBodyWorker(
        List<Node> statementsOut,
        Node[] statementsIn,
        int statementOffset,
        int[] superPath,
        int superPathDepth,
        List<Node> initializerStatements,
        Node constructor)
    {
        var superStatementIndex = superPath[superPathDepth];
        var superStatement = statementsIn[superStatementIndex];

        // Visit statements before super
        var (visited, _) = Visitor().VisitSlice(statementsIn[statementOffset..superStatementIndex]);
        statementsOut.AddRange(visited ?? []);
        statementOffset = superStatementIndex + 1;

        if (NodeTests.IsTryStatement(superStatement))
        {
            var tryBlock = superStatement.AsTryStatement().TryBlock!.AsBlock();
            var tryBlockStatements = TransformConstructorBodyWorker(
                new List<Node>(),
                tryBlock.Statements!.Nodes,
                0, /*statementOffset*/
                superPath,
                superPathDepth + 1,
                initializerStatements,
                constructor);
            var tryStatementList = Factory().AsNodeFactory().NewNodeList(tryBlockStatements.ToArray());
            tryStatementList.Loc = tryBlock.Statements.Loc;

            var catchClause = Visitor().VisitNode(superStatement.AsTryStatement().CatchClause);
            var finallyBlock = Visitor().VisitNode(superStatement.AsTryStatement().FinallyBlock);

            var updated = Factory().AsNodeFactory().UpdateTryStatement(
                superStatement.AsTryStatement(),
                Factory().UpdateBlock(tryBlock, tryStatementList, tryBlock.MultiLine),
                catchClause,
                finallyBlock);
            statementsOut.Add(updated);
        }
        else
        {
            var (visitedSuper, _) = Visitor().VisitSlice(statementsIn[superStatementIndex..(superStatementIndex + 1)]);
            statementsOut.AddRange(visitedSuper ?? []);

            // Add the property initializers. Transforms this:
            //
            //  public x = 1;
            //
            // Into this:
            //
            //  constructor() {
            //      this.x = 1;
            //  }
            //
            // If we do useDefineForClassFields, they'll be converted elsewhere.
            // We instead *remove* them from the transformed output at this stage.

            // parameter-property assignments should occur immediately after the prologue and `super()`,
            // so only count the statements that immediately follow.
            while (statementOffset < statementsIn.Length)
            {
                var stmt = statementsIn[statementOffset];
                var orig = EmitContext().MostOriginal(stmt)!;
                if (AstUtilities.IsParameterPropertyDeclaration(orig, constructor))
                {
                    statementOffset++;
                }
                else
                {
                    break;
                }
            }

            statementsOut.AddRange(initializerStatements);
        }

        // Visit remaining statements
        var (visited2, _) = Visitor().VisitSlice(statementsIn[statementOffset..]);
        statementsOut.AddRange(visited2 ?? []);
        return statementsOut;
    }

    // classfields.go:2503 — transformConstructorBody.
    private Node? TransformConstructorBody(Node container, Node? constructor, bool isDerivedClass)
    {
        var instanceProperties = GetProperties(container, false /*requireInitializer*/, false /*isStatic*/);
        var properties = instanceProperties;
        if (!compilerOptions.GetUseDefineForClassFields())
        {
            properties = CoreUtil.Filter(properties, prop =>
                prop.Initializer() != null || NodeTests.IsPrivateIdentifier(prop.Name()!) || AstUtilities.HasAccessorModifier(prop)) ?? [];
        }

        var privateMethodsAndAccessors = GetPrivateInstanceMethodsAndAccessors(container);
        var needsConstructorBody = properties.Length > 0 || privateMethodsAndAccessors.Length > 0;

        // Only generate synthetic constructor when there are property initializers to move.
        if (constructor == null && !needsConstructorBody)
        {
            return EmitContext().VisitFunctionBody(null, Visitor());
        }

        EmitContext().StartVariableEnvironment();

        var needsSyntheticConstructor = constructor == null && isDerivedClass;
        var statements = new List<Node>();

        // Add the property initializers. Transforms this:
        //
        //  public x = 1;
        //
        // Into this:
        //
        //  constructor() {
        //      this.x = 1;
        //  }
        //
        var initializerStatements = new List<Node>();
        var receiver = Factory().NewThisExpression();

        // private methods can be called in property initializers, they should execute first
        AddInstanceMethodStatements(initializerStatements, privateMethodsAndAccessors, receiver);

        if (constructor != null)
        {
            var parameterProperties = CoreUtil.Filter(instanceProperties, prop =>
                AstUtilities.IsParameterPropertyDeclaration(EmitContext().MostOriginal(prop)!, constructor)) ?? [];
            var nonParameterProperties = CoreUtil.Filter(properties, prop =>
                !AstUtilities.IsParameterPropertyDeclaration(EmitContext().MostOriginal(prop)!, constructor)) ?? [];
            AddPropertyOrClassStaticBlockStatements(initializerStatements, parameterProperties, receiver);
            AddPropertyOrClassStaticBlockStatements(initializerStatements, nonParameterProperties, receiver);
        }
        else
        {
            AddPropertyOrClassStaticBlockStatements(initializerStatements, properties, receiver);
        }

        if (constructor != null && constructor.AsConstructorDeclaration().Body != null)
        {
            var body = constructor.AsConstructorDeclaration().Body!.AsBlock();

            // Copy prologue
            foreach (var stmt in body.Statements!.Nodes)
            {
                if (AstUtilities.IsPrologueDirective(stmt))
                {
                    statements.Add(stmt);
                }
                else
                {
                    break;
                }
            }
            var statementOffset = statements.Count;

            var superPath = global::Ts.Transformers.Transformers.FindSuperStatementIndexPath(body.Statements.Nodes, statementOffset);
            if (superPath is { Length: > 0 })
            {
                statements = TransformConstructorBodyWorker(statements, body.Statements.Nodes, statementOffset, superPath, 0, initializerStatements, constructor);
            }
            else
            {
                // parameter-property assignments should occur immediately after the prologue and `super()`,
                // so only count the statements that immediately follow.
                while (statementOffset < body.Statements.Nodes.Length)
                {
                    var stmt = body.Statements.Nodes[statementOffset];
                    var orig = EmitContext().MostOriginal(stmt)!;
                    if (AstUtilities.IsParameterPropertyDeclaration(orig, constructor))
                    {
                        statementOffset++;
                    }
                    else
                    {
                        break;
                    }
                }
                statements.AddRange(initializerStatements);
                var (visited, _) = Visitor().VisitSlice(body.Statements.Nodes[statementOffset..]);
                statements.AddRange(visited ?? []);
            }
        }
        else
        {
            if (needsSyntheticConstructor)
            {
                // Add a synthetic `super` call:
                //
                //  super(...arguments);
                //
                var superCall = Factory().NewExpressionStatement(
                    Factory().NewCallExpression(
                        Factory().NewKeywordExpression(SyntaxKind.SuperKeyword),
                        null, /*questionDotToken*/
                        null, /*typeArguments*/
                        Factory().AsNodeFactory().NewNodeList([
                            Factory().NewSpreadElement(Factory().NewIdentifier("arguments")),
                        ]),
                        NodeFlags.None));
                statements.Add(superCall);
            }
            statements.AddRange(initializerStatements);
        }

        var statementsArray = EmitContext().EndAndMergeVariableEnvironment(statements.ToArray());

        if ((statementsArray == null || statementsArray.Length == 0) && constructor == null)
        {
            return null;
        }

        bool multiLine;
        if (constructor != null && constructor.AsConstructorDeclaration().Body != null &&
            constructor.AsConstructorDeclaration().Body!.AsBlock().Statements!.Nodes.Length >= (statementsArray?.Length ?? 0))
        {
            multiLine = constructor.AsConstructorDeclaration().Body!.AsBlock().MultiLine;
        }
        else
        {
            multiLine = statementsArray is { Length: > 0 };
        }

        var statementList = Factory().AsNodeFactory().NewNodeList(statementsArray ?? []);
        if (constructor != null && constructor.AsConstructorDeclaration().Body != null)
        {
            statementList.Loc = constructor.AsConstructorDeclaration().Body!.AsBlock().Statements!.Loc;
        }
        else
        {
            statementList.Loc = new TextRange(container.MemberList()!.Loc.Pos, container.MemberList()!.Loc.End);
        }

        var block = Factory().NewBlock(statementList, multiLine);
        if (constructor != null && constructor.AsConstructorDeclaration().Body != null)
        {
            block.Loc = constructor.AsConstructorDeclaration().Body!.Loc;
        }
        return block;
    }

    // classfields.go:2637 — addPropertyOrClassStaticBlockStatements generates assignment statements for
    // property initializers. (Go append-slice → the C# List is mutated in place.)
    private void AddPropertyOrClassStaticBlockStatements(List<Node> statements, Node[] properties, Node receiver)
    {
        foreach (var property in properties)
        {
            if (AstUtilities.IsStatic(property) && !shouldTransformPrivateElementsOrClassStaticBlocks)
            {
                continue;
            }
            var statement = TransformPropertyOrClassStaticBlock(property, receiver);
            if (statement != null)
            {
                statements.Add(statement);
            }
        }
    }

    // classfields.go:2650 — transformPropertyOrClassStaticBlock.
    private Node? TransformPropertyOrClassStaticBlock(Node property, Node receiver)
    {
        Node? expression;
        if (NodeTests.IsClassStaticBlockDeclaration(property))
        {
            expression = SetCurrentClassElementAnd(property, static (tx, n) => tx.TransformClassStaticBlockDeclaration(n), property);
        }
        else
        {
            expression = TransformProperty(property, receiver);
        }
        if (expression == null)
        {
            return null;
        }
        var statement = Factory().NewExpressionStatement(expression);
        EmitContext().SetOriginal(statement, property);
        EmitContext().AddEmitFlags(statement, EmitContext().EmitFlags(property) & global::Ts.Printer.EmitFlags.NoComments);
        EmitContext().SetCommentRange(statement, property.Loc);

        var propertyOriginalNode = EmitContext().MostOriginal(property)!;
        if (NodeTests.IsParameterDeclaration(propertyOriginalNode))
        {
            EmitContext().SetSourceMapRange(statement, propertyOriginalNode.Loc);
            EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoComments);
        }
        else
        {
            EmitContext().SetSourceMapRange(statement, global::Ts.Transformers.Transformers.MoveRangePastModifiers(property));
        }

        // `setOriginalNode` *copies* the `emitNode` from `property`, so now both
        // `statement` and `expression` have a copy of the synthesized comments.
        // Drop the comments from expression to avoid printing them twice.
        EmitContext().SetSyntheticLeadingComments(expression, null);
        EmitContext().SetSyntheticTrailingComments(expression, null);

        // If the property was originally an auto-accessor, don't emit comments here since they will be attached to
        // the synthesized getter.
        if (AstUtilities.HasAccessorModifier(propertyOriginalNode))
        {
            EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoComments);
        }

        return statement;
    }

    // classfields.go:2690 — generateInitializedPropertyExpressionsOrClassStaticBlock generates
    // assignment expressions for property initializers.
    private List<Node> GenerateInitializedPropertyExpressionsOrClassStaticBlock(
        Node[] propertiesOrClassStaticBlocks,
        Node receiver)
    {
        var expressions = new List<Node>();
        foreach (var property in propertiesOrClassStaticBlocks)
        {
            Node? expression;
            if (NodeTests.IsClassStaticBlockDeclaration(property))
            {
                expression = SetCurrentClassElementAnd(property, static (tx, n) => tx.TransformClassStaticBlockDeclaration(n), property);
            }
            else
            {
                expression = TransformProperty(property, receiver);
            }
            if (expression == null)
            {
                continue;
            }
            EmitContext().SetOriginalEx(expression, property, true /*allowOverwrite*/);
            EmitContext().AssignCommentAndSourceMapRanges(expression, property);
            expressions.Add(expression);
        }
        return expressions;
    }

    // classfields.go:2713 — transformProperty transforms a property initializer into an assignment
    // expression.
    private Node? TransformProperty(Node property, Node receiver)
    {
        var savedCurrentClassElement = currentClassElement;
        var transformed = TransformPropertyWorker(property, receiver);
        if (transformed != null && AstUtilities.HasStaticModifier(property))
        {
            EmitContext().AddEmitFlags(transformed, global::Ts.Printer.EmitFlags.NoLexicalThis);
        }
        if (transformed != null && AstUtilities.HasStaticModifier(property) &&
            lexicalEnvironment is { data: { } data } && data.facts != 0)
        {
            // capture the lexical environment for the member
            EmitContext().SetOriginal(transformed, property);
            EmitContext().SetSourceMapRange(transformed, EmitContext().SourceMapRange(property.Name()!));
        }
        currentClassElement = savedCurrentClassElement;
        return transformed;
    }

    // classfields.go:2729 — transformPropertyWorker.
    private Node? TransformPropertyWorker(Node property, Node receiver)
    {
        // We generate a name here in order to reuse the value cached by the relocated computed name expression (which uses the same generated name)
        var emitAssignment = !compilerOptions.GetUseDefineForClassFields();

        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), property, isAnonymousClassNeedingAssignedName))
        {
            property = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), property, false, "")!;
        }

        var propDecl = property.AsPropertyDeclaration();
        var propertyName = property.Name()!;
        if (AstUtilities.HasAccessorModifier(property))
        {
            propertyName = Factory().NewGeneratedPrivateNameForNodeEx(property.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" });
        }
        else if (NodeTests.IsComputedPropertyName(propertyName) && !global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(propertyName.Expression()!))
        {
            propertyName = Factory().AsNodeFactory().UpdateComputedPropertyName(
                propertyName.AsComputedPropertyName(),
                Factory().NewGeneratedNameForNode(propertyName));
        }

        if (AstUtilities.HasStaticModifier(property))
        {
            currentClassElement = property;
        }

        if (NodeTests.IsPrivateIdentifier(propertyName) && ShouldTransformClassElementToWeakMap(property))
        {
            var info = AccessPrivateIdentifier(propertyName);
            if (info != null)
            {
                if (info.kind == global::Ts.Printer.PrivateIdentifierKind.Field)
                {
                    if (!info.isStatic)
                    {
                        return CreatePrivateInstanceFieldInitializer(
                            Factory(),
                            receiver,
                            Visitor().VisitNode(propDecl.Initializer),
                            info.brandCheckIdentifier!);
                    }
                    return CreatePrivateStaticFieldInitializer(
                        Factory(),
                        info.variableName!,
                        Visitor().VisitNode(propDecl.Initializer));
                }
                return null;
            }
            else
            {
                Debug.Fail("Undeclared private name for property declaration.");
            }
        }

        if ((NodeTests.IsPrivateIdentifier(propertyName) || AstUtilities.HasStaticModifier(property)) && propDecl.Initializer == null)
        {
            return null;
        }

        // TODO: can we get rid of this original checking and better coordinate with runtimesyntax?
        if (AstUtilities.HasAbstractModifier(EmitContext().MostOriginal(property)!))
        {
            return null;
        }

        var initializer = Visitor().VisitNode(propDecl.Initializer);
        var propertyOriginalNode = EmitContext().MostOriginal(property)!;
        if (AstUtilities.IsParameterPropertyDeclaration(propertyOriginalNode, propertyOriginalNode.Parent!) && NodeTests.IsIdentifier(propertyName))
        {
            // A parameter-property declaration always overrides the initializer. The only time a parameter-property
            // declaration *should* have an initializer is when decorators have added initializers that need to run before
            // any other initializer
            var localName = propertyName.Clone(Factory())!;
            if (initializer != null)
            {
                // unwrap `(__runInitializers(this, _instanceExtraInitializers), void 0)`
                if (NodeTests.IsParenthesizedExpression(initializer) &&
                    AstUtilities.IsCommaExpression(initializer.Expression()!) &&
                    EmitContext().IsCallToHelper(initializer.Expression()!.AsBinaryExpression().Left!, "__runInitializers") &&
                    NodeTests.IsVoidExpression(initializer.Expression()!.AsBinaryExpression().Right!) &&
                    NodeTests.IsNumericLiteral(initializer.Expression()!.AsBinaryExpression().Right!.Expression()!))
                {
                    initializer = initializer.Expression()!.AsBinaryExpression().Left;
                }
                initializer = Factory().InlineExpressions([initializer!, localName]);
            }
            else
            {
                initializer = localName;
            }
            EmitContext().AddEmitFlags(propertyName, global::Ts.Printer.EmitFlags.NoComments | global::Ts.Printer.EmitFlags.NoSourceMap);
            EmitContext().SetSourceMapRange(localName, propertyOriginalNode.Name()!.Loc);
            EmitContext().AddEmitFlags(localName, global::Ts.Printer.EmitFlags.NoComments);
        }
        else if (initializer == null)
        {
            initializer = Factory().NewVoidZeroExpression();
        }

        if (emitAssignment || NodeTests.IsPrivateIdentifier(propertyName))
        {
            var memberAccess = CreateMemberAccessForPropertyName(Factory(), EmitContext(), receiver, propertyName, propertyName);
            EmitContext().AddEmitFlags(memberAccess, global::Ts.Printer.EmitFlags.NoLeadingComments);
            return Factory().NewAssignmentExpression(memberAccess, initializer!);
        }

        // useDefineForClassFields: Object.defineProperty
        Node? name;
        if (NodeTests.IsComputedPropertyName(propertyName))
        {
            name = propertyName.Expression();
        }
        else if (NodeTests.IsIdentifier(propertyName))
        {
            name = Factory().NewStringLiteral(propertyName.Text(), TokenFlags.None);
        }
        else
        {
            name = propertyName;
        }
        var descriptor = Factory().NewObjectLiteralExpression(Factory().AsNodeFactory().NewNodeList([
            Factory().NewPropertyAssignment(null, Factory().NewIdentifier("enumerable"), null, null, Factory().NewTrueExpression()),
            Factory().NewPropertyAssignment(null, Factory().NewIdentifier("configurable"), null, null, Factory().NewTrueExpression()),
            Factory().NewPropertyAssignment(null, Factory().NewIdentifier("writable"), null, null, Factory().NewTrueExpression()),
            Factory().NewPropertyAssignment(null, Factory().NewIdentifier("value"), null, null, initializer),
        ]), true);
        return Factory().NewObjectDefinePropertyCall(receiver, name!, descriptor);
    }

    // classfields.go:2836 — addInstanceMethodStatements generates brand-check initializer for private
    // methods. (Go append-slice → the C# List is mutated in place.)
    private void AddInstanceMethodStatements(List<Node> statements, Node[] methods, Node receiver)
    {
        if (!shouldTransformPrivateElementsOrClassStaticBlocks || methods.Length == 0)
        {
            return;
        }

        var env = GetPrivateIdentifierEnvironment();
        var weakSetName = env.data.weakSetName;
        Debug.Assert(weakSetName != null, "weakSetName should be set in private identifier environment");

        statements.Add(
            Factory().NewExpressionStatement(
                CreatePrivateInstanceMethodInitializer(Factory(), receiver, weakSetName!)));
    }

    // classfields.go:2853 — visitInvalidSuperProperty.
    private Node VisitInvalidSuperProperty(Node node)
    {
        if (NodeTests.IsPropertyAccessExpression(node))
        {
            return Factory().AsNodeFactory().UpdatePropertyAccessExpression(
                node.AsPropertyAccessExpression(),
                Factory().NewVoidZeroExpression(),
                null,
                node.Name(),
                node.Flags);
        }
        return Factory().AsNodeFactory().UpdateElementAccessExpression(
            node.AsElementAccessExpression(),
            Factory().NewVoidZeroExpression(),
            null,
            Visitor().VisitNode(node.AsElementAccessExpression().ArgumentExpression),
            node.Flags);
    }

    // classfields.go:3365 — createPrivateStaticFieldInitializer.
    private static Node CreatePrivateStaticFieldInitializer(global::Ts.Printer.NodeFactory factory, Node variableName, Node? initializer)
    {
        if (initializer == null)
        {
            initializer = factory.NewVoidZeroExpression();
        }
        return factory.NewAssignmentExpression(
            variableName,
            factory.NewObjectLiteralExpression(
                factory.AsNodeFactory().NewNodeList([
                    factory.NewPropertyAssignment(null, factory.NewIdentifier("value"), null, null, initializer),
                ]),
                false));
    }

    // classfields.go:3380 — createPrivateInstanceFieldInitializer.
    private static Node CreatePrivateInstanceFieldInitializer(global::Ts.Printer.NodeFactory factory, Node receiver, Node? initializer, Node weakMapName)
    {
        if (initializer == null)
        {
            initializer = factory.NewVoidZeroExpression();
        }
        return factory.NewMethodCall(weakMapName, factory.NewIdentifier("set"), [receiver, initializer]);
    }

    // classfields.go:3387 — createPrivateInstanceMethodInitializer.
    private static Node CreatePrivateInstanceMethodInitializer(global::Ts.Printer.NodeFactory factory, Node receiver, Node weakSetName) =>
        factory.NewMethodCall(weakSetName, factory.NewIdentifier("add"), [receiver]);

    // classfields.go:3400 — getProperties.
    private Node[] GetProperties(Node node, bool requireInitializer, bool isStatic)
    {
        var result = new List<Node>();
        foreach (var member in node.Members() ?? [])
        {
            if (NodeTests.IsPropertyDeclaration(member) &&
                (!requireInitializer || member.Initializer() != null) &&
                AstUtilities.HasStaticModifier(member) == isStatic)
            {
                result.Add(member);
            }
        }
        return result.ToArray();
    }

    // ── Forward stubs (batch 5b replaces these; unreachable until the batch-6 dispatch switch) ──
    private Node? VisitAssignmentPattern(Node node) => throw new NotImplementedException("classfields batch 5b");
    private Node? WrapPrivateIdentifierForDestructuringTarget(Node node) => throw new NotImplementedException("classfields batch 5b");
    private Node? VisitArrayAssignmentElement(Node node) => node;
    private Node? VisitObjectAssignmentElement(Node node) => node;
}

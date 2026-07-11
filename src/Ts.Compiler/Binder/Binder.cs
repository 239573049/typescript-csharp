// Ported from tsgo internal/binder/binder.go.
using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;
using Diag = Ts.Diagnostics.Diagnostics;

namespace Ts.Binder;

public enum ContainerFlags
{
    // The current node is not a container, and no container manipulation should happen before
    // recursing into it.
    None = 0,
    // The current node is a container.  It should be set as the current container (and block-
    // container) before recursing into it.  The current node does not have locals.  Examples:
    //
    //      Classes, ObjectLiterals, TypeLiterals, Interfaces...
    IsContainer = 1 << 0,
    // The current node is a block-scoped-container.  It should be set as the current block-
    // container before recursing into it.  Examples:
    //
    //      Blocks (when not parented by functions), Catch clauses, For/For-in/For-of statements...
    IsBlockScopedContainer = 1 << 1,
    // The current node is the container of a control flow path. The current control flow should
    // be saved and restored, and a new control flow initialized within the container.
    IsControlFlowContainer = 1 << 2,
    IsFunctionLike = 1 << 3,
    IsFunctionExpression = 1 << 4,
    HasLocals = 1 << 5,
    IsInterface = 1 << 6,
    IsObjectLiteralOrClassExpressionMethodOrAccessor = 1 << 7,
    IsThisContainer = 1 << 8,
    PropagatesThisKeyword = 1 << 9,
}

internal readonly record struct ExpandoAssignmentInfo(Node Node, Node Container, Node BlockScopeContainer);

internal sealed class ActiveLabel
{
    internal ActiveLabel? next;
    internal FlowLabel? breakTarget;
    internal FlowLabel? continueTarget;
    internal string name = "";
    internal bool referenced;

    public FlowNode? BreakTarget() => breakTarget;
    public FlowNode? ContinueTarget() => continueTarget;
}

public sealed partial class Binder
{
    private SourceFile file = null!;
    private readonly Visitor bindFunc;
    private FlowNode unreachableFlow = null!;

    private Node? container;
    private Node? thisContainer;
    private Node? blockScopeContainer;
    private Node? lastContainer;
    private FlowNode? currentFlow;
    private FlowLabel? currentBreakTarget;
    private FlowLabel? currentContinueTarget;
    private FlowLabel? currentReturnTarget;
    private FlowLabel? currentTrueTarget;
    private FlowLabel? currentFalseTarget;
    private FlowLabel? currentExceptionTarget;
    private FlowNode? preSwitchCaseFlow;
    private ActiveLabel? activeLabelList;
    private NodeFlags emitFlags;
    private bool seenThisKeyword;
    private bool hasExplicitReturn;
    private bool hasFlowEffects;
    private bool inAssignmentPattern;
    private bool seenParseError;
    private int symbolCount;
    private Set<string> classifiableNames = new();
    private Set<Symbol> notConstEnumOnlyModules = new();
    private List<ExpandoAssignmentInfo> expandoAssignments = new();

    public Binder()
    {
        bindFunc = Bind; // Allocate closure once
    }

    // Go: binder.go:117 — `*b = Binder{bindFunc: b.bindFunc}`; zero all mutable fields for pool reuse.
    private void Reset()
    {
        file = null!;
        unreachableFlow = null!;
        container = null;
        thisContainer = null;
        blockScopeContainer = null;
        lastContainer = null;
        currentFlow = null;
        currentBreakTarget = null;
        currentContinueTarget = null;
        currentReturnTarget = null;
        currentTrueTarget = null;
        currentFalseTarget = null;
        currentExceptionTarget = null;
        preSwitchCaseFlow = null;
        activeLabelList = null;
        emitFlags = default;
        seenThisKeyword = false;
        hasExplicitReturn = false;
        hasFlowEffects = false;
        inAssignmentPattern = false;
        seenParseError = false;
        symbolCount = 0;
        classifiableNames = new();
        notConstEnumOnlyModules = new();
        expandoAssignments = new();
    }

    public static void BindSourceFile(SourceFile file)
    {
        // This is constructed this way to make the compiler "out-line" the function,
        // avoiding most work in the common case where the file has already been bound.
        if (!file.IsBound())
        {
            BindSourceFileCore(file);
        }
    }

    // sync.Pool → Ts.Core.ObjectPool (plan/05 §11). The pool resets each binder on Return.
    private static readonly ObjectPool<Binder> binderPool = new(static () => new Binder(), static b => b.Reset());

    // Go: binder.go:121 `bindSourceFile`. The bind recursion is deep (0号风险), so the entry
    // runs on the 64MB LargeStackExecutor thread (plan §4.6, R01), mirroring ParseSourceFile.
    private static void BindSourceFileCore(SourceFile file)
    {
        LargeStackExecutor.Run(() =>
            file.BindOnce(() =>
            {
                var b = binderPool.Get();
                try
                {
                    b.file = file;
                    b.unreachableFlow = b.NewFlowNode(FlowFlags.Unreachable);
                    b.Bind(file.AsNode());
                    b.BindDeferredExpandoAssignments();
                    file.SymbolCount = b.symbolCount;
                    file.ClassifiableNames = b.classifiableNames;
                }
                finally
                {
                    binderPool.Return(b);
                }
            }));
    }

    private Symbol NewSymbol(SymbolFlags flags, string name)
    {
        symbolCount++;
        var result = new Symbol();
        result.Flags = flags;
        result.Name = name;
        return result;
    }

    /**
     * Declares a Symbol for the node and adds it to symbols. Reports errors for conflicting identifier names.
     * @param symbolTable - The symbol table which node will be added to.
     * @param parent - node's parent declaration.
     * @param node - The declaration to be added to the symbol table
     * @param includes - The SymbolFlags that node has in addition to its declaration type (eg: export, ambient, etc.)
     * @param excludes - The flags which node cannot be declared alongside in a symbol table. Used to report forbidden declarations.
     */
    private Symbol DeclareSymbol(SymbolTable symbolTable, Symbol? parent, Node node, SymbolFlags includes, SymbolFlags excludes)
    {
        return DeclareSymbolEx(symbolTable, parent, node, includes, excludes, false /*isReplaceableByMethod*/, false /*isComputedName*/);
    }

    private Symbol DeclareSymbolEx(SymbolTable symbolTable, Symbol? parent, Node node, SymbolFlags includes, SymbolFlags excludes, bool isReplaceableByMethod, bool isComputedName)
    {
        Debug.Assert(isComputedName || !AstUtilities.HasDynamicName(node));
        var isDefaultExport = AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default) || NodeTests.IsExportSpecifier(node) && AstUtilities.ModuleExportNameIsDefault(node.AsExportSpecifier().Name()!);
        // The exported symbol for an export default function/class node is always named "default"
        string name;
        if (isComputedName)
        {
            name = InternalSymbolName.Computed;
        }
        else if (isDefaultExport && parent != null)
        {
            name = InternalSymbolName.Default;
        }
        else
        {
            name = GetDeclarationName(node);
        }
        Symbol? symbol;
        if (name == InternalSymbolName.Missing)
        {
            symbol = NewSymbol(SymbolFlags.None, InternalSymbolName.Missing);
        }
        else
        {
            // Check and see if the symbol table already has a symbol with this name.  If not,
            // create a new symbol with this name and add it to the table.  Note that we don't
            // give the new symbol any flags *yet*.  This ensures that it will not conflict
            // with the 'excludes' flags we pass in.
            //
            // If we do get an existing symbol, see if it conflicts with the new symbol we're
            // creating.  For example, a 'var' symbol and a 'class' symbol will conflict within
            // the same symbol table.  If we have a conflict, report the issue on each
            // declaration we have for this symbol, and then create a new symbol for this
            // declaration.
            //
            // Note that when properties declared in Javascript constructors
            // (marked by isReplaceableByMethod) conflict with another symbol, the property loses.
            // Always. This allows the common Javascript pattern of overwriting a prototype method
            // with an bound instance method of the same type: `this.method = this.method.bind(this)`
            //
            // If we created a new symbol, either because we didn't have a symbol with this name
            // in the symbol table, or we conflicted with an existing symbol, then just add this
            // node as the sole declaration of the new symbol.
            //
            // Otherwise, we'll be merging into a compatible existing symbol (for example when
            // you have multiple 'vars' with the same name in the same container).  In this case
            // just add this node into the declarations list of the symbol.
            symbol = symbolTable.GetValueOrDefault(name);
            if ((includes & SymbolFlags.Classifiable) != 0)
            {
                classifiableNames.Add(name);
            }
            if (symbol == null)
            {
                symbol = NewSymbol(SymbolFlags.None, name);
                symbolTable[name] = symbol;
                if (isReplaceableByMethod)
                {
                    symbol.Flags |= SymbolFlags.ReplaceableByMethod;
                }
            }
            else if (isReplaceableByMethod && (symbol.Flags & SymbolFlags.ReplaceableByMethod) == 0)
            {
                // A symbol already exists, so don't add this as a declaration.
                return symbol;
            }
            else if ((symbol.Flags & excludes) != 0)
            {
                if ((symbol.Flags & SymbolFlags.ReplaceableByMethod) != 0)
                {
                    // Javascript constructor-declared symbols can be discarded in favor of
                    // prototype symbols like methods.
                    symbol = NewSymbol(SymbolFlags.None, name);
                    symbolTable[name] = symbol;
                }
                else if (!((includes & SymbolFlags.Variable) != 0 && (symbol.Flags & SymbolFlags.Assignment) != 0 ||
                    (includes & SymbolFlags.Assignment) != 0 && (symbol.Flags & SymbolFlags.Variable) != 0))
                {
                    // Assignment declarations are allowed to merge with variables, no matter what other flags they have.
                    // Report errors every position with duplicate declaration
                    // Report errors on previous encountered declarations
                    Message message;
                    if ((symbol.Flags & SymbolFlags.BlockScopedVariable) != 0)
                    {
                        message = Diag.Cannot_redeclare_block_scoped_variable_0;
                    }
                    else
                    {
                        message = Diag.Duplicate_identifier_0;
                    }
                    var messageNeedsName = true;
                    if ((symbol.Flags & SymbolFlags.Enum) != 0 || (includes & SymbolFlags.Enum) != 0)
                    {
                        message = Diag.Enum_declarations_can_only_merge_with_namespace_or_other_enum_declarations;
                        messageNeedsName = false;
                    }
                    var multipleDefaultExports = false;
                    if ((symbol.Declarations?.Length ?? 0) != 0)
                    {
                        // If the current node is a default export of some sort, then check if
                        // there are any other default exports that we need to error on.
                        // We'll know whether we have other default exports depending on if `symbol` already has a declaration list set.
                        if (isDefaultExport)
                        {
                            message = Diag.A_module_cannot_have_multiple_default_exports;
                            messageNeedsName = false;
                            multipleDefaultExports = true;
                        }
                        else
                        {
                            // This is to properly report an error in the case "export default { }" is after export default of class declaration or function declaration.
                            // Error on multiple export default in the following case:
                            // 1. multiple export default of class declaration or function declaration by checking NodeFlags.Default
                            // 2. multiple export default of export assignment. This one doesn't have NodeFlags.Default on (as export default doesn't considered as modifiers)
                            if ((symbol.Declarations?.Length ?? 0) != 0 && NodeTests.IsExportAssignment(node) && !node.AsExportAssignment().IsExportEquals)
                            {
                                message = Diag.A_module_cannot_have_multiple_default_exports;
                                messageNeedsName = false;
                                multipleDefaultExports = true;
                            }
                        }
                    }
                    Node? declarationName = AstUtilities.GetNameOfDeclaration(node);
                    if (declarationName == null)
                    {
                        declarationName = node;
                    }
                    Diagnostic diag;
                    if (messageNeedsName)
                    {
                        diag = CreateDiagnosticForNode(declarationName, message, GetDisplayName(node));
                    }
                    else
                    {
                        diag = CreateDiagnosticForNode(declarationName, message);
                    }
                    if (NodeTests.IsTypeAliasDeclaration(node) && AstUtilities.NodeIsMissing(node.Type()) && AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export) && (symbol.Flags & (SymbolFlags.Alias | SymbolFlags.Type | SymbolFlags.Namespace)) != 0)
                    {
                        // export type T; - may have meant export type { T }?
                        diag.AddRelatedInfo(CreateDiagnosticForNode(node, Diag.Did_you_mean_0, "export type { " + node.AsTypeAliasDeclaration().Name()!.Text() + " }"));
                    }
                    // Go `range symbol.Declarations` is nil-safe (0 iterations); a merged
                    // synthetic symbol (e.g. the class 'prototype') can have null Declarations.
                    var conflictingDeclarations = symbol.Declarations ?? [];
                    for (int index = 0; index < conflictingDeclarations.Length; index++)
                    {
                        var declaration = conflictingDeclarations[index];
                        Node? decl = AstUtilities.GetNameOfDeclaration(declaration);
                        if (decl == null)
                        {
                            decl = declaration;
                        }
                        Diagnostic d;
                        if (messageNeedsName)
                        {
                            d = CreateDiagnosticForNode(decl, message, GetDisplayName(declaration));
                        }
                        else
                        {
                            d = CreateDiagnosticForNode(decl, message);
                        }
                        if (multipleDefaultExports)
                        {
                            d.AddRelatedInfo(CreateDiagnosticForNode(declarationName, CoreUtil.IfElse(index == 0, Diag.Another_export_default_is_here, Diag.X_and_here)));
                        }
                        AddDiagnostic(d);
                        if (multipleDefaultExports)
                        {
                            diag.AddRelatedInfo(CreateDiagnosticForNode(decl, Diag.The_first_export_default_is_here));
                        }
                    }
                    AddDiagnostic(diag);
                    // When get or set accessor conflicts with a non-accessor or an accessor of a different kind, we mark
                    // the symbol as a full accessor such that all subsequent declarations are considered conflicting. This
                    // for example ensures that a get accessor followed by a non-accessor followed by a set accessor with the
                    // same name are all marked as duplicates.
                    if ((symbol.Flags & SymbolFlags.Accessor) != 0 && (symbol.Flags & SymbolFlags.Accessor) != (includes & SymbolFlags.Accessor))
                    {
                        symbol.Flags |= SymbolFlags.Accessor;
                    }
                    symbol = NewSymbol(SymbolFlags.None, name);
                }
            }
        }
        AddDeclarationToSymbol(symbol, node, includes);
        if (symbol.Parent == null)
        {
            symbol.Parent = parent;
        }
        else if (symbol.Parent != parent)
        {
            throw new InvalidOperationException("Existing symbol parent should match new one");
        }
        return symbol;
    }

    // Should not be called on a declaration with a computed property name,
    // unless it is a well known Symbol.
    private string GetDeclarationName(Node node)
    {
        if (NodeTests.IsExportAssignment(node))
        {
            return CoreUtil.IfElse(node.AsExportAssignment().IsExportEquals, InternalSymbolName.ExportEquals, InternalSymbolName.Default);
        }
        var name = AstUtilities.GetNameOfDeclaration(node);
        if (name != null)
        {
            if (AstUtilities.IsAmbientModule(node))
            {
                var moduleName = name.Text();
                if (AstUtilities.IsGlobalScopeAugmentation(node))
                {
                    return InternalSymbolName.Global;
                }
                return "\"" + moduleName + "\"";
            }
            if (NodeTests.IsPrivateIdentifier(name))
            {
                // containingClass exists because private names only allowed inside classes
                var containingClass = AstUtilities.GetContainingClass(node);
                if (containingClass == null)
                {
                    // we can get here in cases where there is already a parse error.
                    return InternalSymbolName.Missing;
                }
                return GetSymbolNameForPrivateIdentifier(containingClass.Symbol()!, name.Text());
            }
            if (AstUtilities.IsPropertyNameLiteral(name) || NodeTests.IsJsxNamespacedName(name))
            {
                return name.Text();
            }
            if (NodeTests.IsComputedPropertyName(name))
            {
                var nameExpression = name.Expression()!;
                // treat computed property names where expression is string/numeric literal as just string/numeric literal
                if (AstUtilities.IsStringOrNumericLiteralLike(nameExpression))
                {
                    return nameExpression.Text();
                }
                if (AstUtilities.IsSignedNumericLiteral(nameExpression))
                {
                    var unaryExpression = nameExpression.AsPrefixUnaryExpression();
                    return global::Ts.Scanner.Scanner.TokenToString(unaryExpression.Operator) + unaryExpression.Operand!.Text();
                }
                throw new InvalidOperationException("Only computed properties with literal names have declaration names");
            }
            return InternalSymbolName.Missing;
        }
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
                return InternalSymbolName.Constructor;
            case SyntaxKind.FunctionType:
            case SyntaxKind.CallSignature:
                return InternalSymbolName.Call;
            case SyntaxKind.ConstructorType:
            case SyntaxKind.ConstructSignature:
                return InternalSymbolName.New;
            case SyntaxKind.IndexSignature:
                return InternalSymbolName.Index;
            case SyntaxKind.ExportDeclaration:
                return InternalSymbolName.ExportStar;
            case SyntaxKind.SourceFile:
            case SyntaxKind.BinaryExpression:
                return InternalSymbolName.ExportEquals;
        }
        return InternalSymbolName.Missing;
    }

    private string GetDisplayName(Node node)
    {
        var nameNode = node.Name();
        if (nameNode != null)
        {
            return global::Ts.Scanner.Scanner.DeclarationNameToString(nameNode);
        }
        var name = GetDeclarationName(node);
        if (name != InternalSymbolName.Missing)
        {
            return name;
        }
        return "(Missing)";
    }

    public static string GetSymbolNameForPrivateIdentifier(Symbol containingClassSymbol, string description)
    {
        return InternalSymbolName.Prefix + "#" + ((int)AstUtilities.GetSymbolId(containingClassSymbol).Value).ToString() + "@" + description;
    }

    private Symbol DeclareModuleMember(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        var container = this.container!;
        var hasExportModifier = (AstUtilities.GetCombinedModifierFlags(node) & ModifierFlags.Export) != 0 || AstUtilities.IsImplicitlyExportedJSDocDeclaration(node);
        if ((symbolFlags & SymbolFlags.Alias) != 0)
        {
            if (node.Kind == SyntaxKind.ExportSpecifier || (node.Kind == SyntaxKind.ImportEqualsDeclaration && hasExportModifier))
            {
                return DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, symbolFlags, symbolExcludes);
            }
            return DeclareSymbol(AstUtilities.GetLocals(container), null /*parent*/, node, symbolFlags, symbolExcludes);
        }
        // Exported module members are given 2 symbols: A local symbol that is classified with an ExportValue flag,
        // and an associated export symbol with all the correct flags set on it. There are 2 main reasons:
        //
        //   1. We treat locals and exports of the same name as mutually exclusive within a container.
        //      That means the binder will issue a Duplicate Identifier error if you mix locals and exports
        //      with the same name in the same container.
        //      TODO: Make this a more specific error and decouple it from the exclusion logic.
        //   2. When we checkIdentifier in the checker, we set its resolved symbol to the local symbol,
        //      but return the export symbol (by calling getExportSymbolOfValueSymbolIfExported). That way
        //      when the emitter comes back to it, it knows not to qualify the name if it was found in a containing scope.
        //
        // NOTE: Nested ambient modules always should go to to 'locals' table to prevent their automatic merge
        //       during global merging in the checker. Why? The only case when ambient module is permitted inside another module is module augmentation
        //       and this case is specially handled. Module augmentations should only be merged with original module definition
        //       and should never be merged directly with other augmentation, and the latter case would be possible if automatic merge is allowed.
        if (!AstUtilities.IsAmbientModule(node) && (hasExportModifier || (container.Flags & NodeFlags.ExportContext) != 0))
        {
            if (!AstUtilities.IsLocalsContainer(container) || (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default) && GetDeclarationName(node) == InternalSymbolName.Missing))
            {
                return DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, symbolFlags, symbolExcludes);
                // No local symbol for an unnamed default!
            }
            var exportKind = SymbolFlags.None;
            if ((symbolFlags & SymbolFlags.Value) != 0)
            {
                exportKind = SymbolFlags.ExportValue;
            }
            var local = DeclareSymbol(AstUtilities.GetLocals(container), null /*parent*/, node, exportKind, symbolExcludes);
            local.ExportSymbol = DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, symbolFlags, symbolExcludes);
            node.ExportableData()!.LocalSymbol = local;
            return local;
        }
        return DeclareSymbol(AstUtilities.GetLocals(container), null /*parent*/, node, symbolFlags, symbolExcludes);
    }

    private Symbol DeclareClassMember(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        if (AstUtilities.IsStatic(node))
        {
            return DeclareSymbol(AstUtilities.GetExports(container!.Symbol()!), container!.Symbol(), node, symbolFlags, symbolExcludes);
        }
        return DeclareSymbol(AstUtilities.GetMembers(container!.Symbol()!), container!.Symbol(), node, symbolFlags, symbolExcludes);
    }

    private Symbol DeclareSourceFileMember(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        if (AstUtilities.IsExternalModule(file))
        {
            return DeclareModuleMember(node, symbolFlags, symbolExcludes);
        }
        return DeclareSymbol(AstUtilities.GetLocals(file.AsNode()), null /*parent*/, node, symbolFlags, symbolExcludes);
    }

    private Symbol DeclareSymbolAndAddToSymbolTable(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        switch (container!.Kind)
        {
            case SyntaxKind.ModuleDeclaration:
                return DeclareModuleMember(node, symbolFlags, symbolExcludes);
            case SyntaxKind.SourceFile:
                return DeclareSourceFileMember(node, symbolFlags, symbolExcludes);
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassDeclaration:
                return DeclareClassMember(node, symbolFlags, symbolExcludes);
            case SyntaxKind.EnumDeclaration:
                return DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, symbolFlags, symbolExcludes);
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.JsxAttributes:
                return DeclareSymbol(AstUtilities.GetMembers(container.Symbol()!), container.Symbol(), node, symbolFlags, symbolExcludes);
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.MappedType:
                return DeclareSymbol(AstUtilities.GetLocals(container), null /*parent*/, node, symbolFlags, symbolExcludes);
        }
        throw new InvalidOperationException("Unhandled case in declareSymbolAndAddToSymbolTable");
    }

    private FlowNode NewFlowNode(FlowFlags flags)
    {
        var result = new FlowNode();
        result.Flags = flags;
        return result;
    }

    private FlowNode NewFlowNodeEx(FlowFlags flags, Node? node, FlowNode? antecedent)
    {
        var result = NewFlowNode(flags);
        result.Node = node;
        result.Antecedent = antecedent;
        return result;
    }

    private FlowLabel CreateLoopLabel()
    {
        return NewFlowNode(FlowFlags.LoopLabel);
    }

    private FlowLabel CreateBranchLabel()
    {
        return NewFlowNode(FlowFlags.BranchLabel);
    }

    private FlowNode CreateReduceLabel(FlowLabel target, FlowList antecedents, FlowNode antecedent)
    {
        return NewFlowNodeEx(FlowFlags.ReduceLabel, AstUtilities.NewFlowReduceLabelData(target, antecedents), antecedent);
    }

    private FlowNode CreateFlowCondition(FlowFlags flags, FlowNode antecedent, Node? expression)
    {
        if ((antecedent.Flags & FlowFlags.Unreachable) != 0)
        {
            return antecedent;
        }
        if (expression == null)
        {
            if ((flags & FlowFlags.TrueCondition) != 0)
            {
                return antecedent;
            }
            return unreachableFlow;
        }
        if ((expression.Kind == SyntaxKind.TrueKeyword && (flags & FlowFlags.FalseCondition) != 0 || expression.Kind == SyntaxKind.FalseKeyword && (flags & FlowFlags.TrueCondition) != 0) && !AstUtilities.IsExpressionOfOptionalChainRoot(expression) && !AstUtilities.IsNullishCoalesce(expression.Parent!))
        {
            return unreachableFlow;
        }
        if (!IsNarrowingExpression(expression))
        {
            return antecedent;
        }
        SetFlowNodeReferenced(antecedent);
        return NewFlowNodeEx(flags, expression, antecedent);
    }

    private FlowNode CreateFlowMutation(FlowFlags flags, FlowNode antecedent, Node node)
    {
        SetFlowNodeReferenced(antecedent);
        hasFlowEffects = true;
        var result = NewFlowNodeEx(flags, node, antecedent);
        if (currentExceptionTarget != null)
        {
            AddAntecedent(currentExceptionTarget, result);
        }
        return result;
    }

    private FlowNode CreateFlowSwitchClause(FlowNode antecedent, Node switchStatement, int clauseStart, int clauseEnd)
    {
        SetFlowNodeReferenced(antecedent);
        return NewFlowNodeEx(FlowFlags.SwitchClause, AstUtilities.NewFlowSwitchClauseData(switchStatement, clauseStart, clauseEnd), antecedent);
    }

    private FlowNode CreateFlowCall(FlowNode antecedent, Node node)
    {
        SetFlowNodeReferenced(antecedent);
        hasFlowEffects = true;
        return NewFlowNodeEx(FlowFlags.Call, node, antecedent);
    }

    private FlowList NewFlowList(FlowNode? head, FlowList? tail)
    {
        var result = new FlowList();
        result.Flow = head;
        result.Next = tail;
        return result;
    }

    private FlowList? CombineFlowLists(FlowList? head, FlowList? tail)
    {
        if (head == null)
        {
            return tail;
        }
        return NewFlowList(head.Flow, CombineFlowLists(head.Next, tail));
    }

    private Node[] NewSingleDeclaration(Node declaration)
    {
        return new Node[] { declaration };
    }

    private static void SetFlowNodeReferenced(FlowNode flow)
    {
        // On first reference we set the Referenced flag, thereafter we set the Shared flag
        if ((flow.Flags & FlowFlags.Referenced) == 0)
        {
            flow.Flags |= FlowFlags.Referenced;
        }
        else
        {
            flow.Flags |= FlowFlags.Shared;
        }
    }

    private void AddAntecedent(FlowLabel label, FlowNode antecedent)
    {
        if ((antecedent.Flags & FlowFlags.Unreachable) != 0)
        {
            return;
        }
        // If antecedent isn't already on the Antecedents list, add it to the end of the list
        FlowList? last = null;
        for (var list = label.Antecedents; list != null; list = list.Next)
        {
            if (list.Flow == antecedent)
            {
                return;
            }
            last = list;
        }
        if (last == null)
        {
            label.Antecedents = NewFlowList(antecedent, null);
        }
        else
        {
            last.Next = NewFlowList(antecedent, null);
        }
        SetFlowNodeReferenced(antecedent);
    }

    private FlowNode FinishFlowLabel(FlowLabel label)
    {
        if (label.Antecedents == null)
        {
            return unreachableFlow;
        }
        if (label.Antecedents.Next == null)
        {
            return label.Antecedents.Flow!;
        }
        return label;
    }
}

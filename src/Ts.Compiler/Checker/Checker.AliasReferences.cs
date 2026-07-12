// Ported from tsgo internal/checker/checker.go (sections: linked-reference / alias-reference
// marking, lines ~28018-28674), plan/03 §4.2 alias-reference-marking wave.
//
// markLinkedReferences is the emit-time entry point (driven by emitResolver.MarkLinkedReferencesRecursively):
// it walks every parse-tree node and decides which imports/aliases are *referenced* by emitted JS, so the
// ImportElision transformer can drop the unreferenced ones. This is the final piece that makes real
// `.ts`→`.js` emit work. The family: markLinkedReferences (the ReferenceHint dispatcher + the big
// `Unspecified` branch that re-classifies each node), the 7 mark*AliasReferenced routines, the
// shouldMarkIdentifierAliasReferenced / isExportOrExportExpression / isPartOfImportEqualsModuleReference
// predicates, the low-level markAliasReferenced / markAliasSymbolAsReferenced / markExportAsReferenced
// chain, and the decorator-metadata reference walkers (markDecoratorMedataDataTypeNodeAsReferenced /
// markEntityNameOrEntityExpressionAsReference / getEntityNameForDecoratorMetadata* / markTypeNodeAsReferenced).
//
// Faithful per-function translation: same function order as Go, same branch structure; Go comments and
// `// !!!` markers preserved.
//
// §1.5: diagnostics via `using Diag = global::Ts.Diagnostics.Diagnostics;`; the no-collision hosts
// (NodeTests / AstUtilities / CoreUtil) are used directly, as in relater/inference/flow/statements.
// `ast.IsExpressionNode`/`ast.IsPartOfTypeNode`/`ast.IsJsxTagName` etc. resolve to AstUtilities (some
// Is* live on the generated NodeTests). `ast.IsPartOfTypeNode` is qualified `AstUtilities.IsPartOfTypeNode`
// to select the faithful ast version over the Checker's narrower TypeNodes copy. `resolveAlias` returns a
// `(Symbol?, bool)` tuple in this port, so its result is destructured / `.Item1`-indexed at call sites.
//
// Deep alias-transitivity dependencies that have not yet landed (getExportSymbolOfValueSymbolIfExported,
// getTypeOnlyAliasDeclaration, resolveSymbol, checkExternalEmitHelpers, getAssignmentTargetKind, IsTypeAny,
// getPromisedTypeOfPromise, the private-identifier property lookups, the property-access apparent-type path,
// and checkExternalImportOrExportDeclaration) are single-dedup stubbed in Checker.AliasReferences.Deps.cs /
// other Deps surfaces; they are reached only on import-heavy / decorator-metadata / private-field inputs.
// markLinkedReferences itself correctly no-ops / marks (without throwing) for plain, import-free programs,
// which is what the phase-6 emit end-to-end probes exercise.

using System;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:28018-28114
    internal void MarkLinkedReferences(Node location, ReferenceHint hint, Symbol? propSymbol, Type? parentType)
    {
        if (!canCollectSymbolAliasAccessibilityData)
        {
            return;
        }
        if ((location.Flags & NodeFlags.Ambient) != 0 && !NodeTests.IsPropertySignatureDeclaration(location) && !NodeTests.IsPropertyDeclaration(location))
        {
            // References within types and declaration files are never going to contribute to retaining a JS import,
            // except for properties (which can be decorated).
            return;
        }
        switch (hint)
        {
            case ReferenceHint.Identifier:
                MarkIdentifierAliasReferenced(location);
                break;
            case ReferenceHint.Property:
                MarkPropertyAliasReferenced(location, propSymbol, parentType);
                break;
            case ReferenceHint.ExportAssignment:
                MarkExportAssignmentAliasReferenced(location);
                break;
            case ReferenceHint.Jsx:
                MarkJsxAliasReferenced(location);
                break;
            case ReferenceHint.ExportImportEquals:
                MarkImportEqualsAliasReferenced(location);
                break;
            case ReferenceHint.ExportSpecifier:
                MarkExportSpecifierAliasReferenced(location);
                break;
            case ReferenceHint.Decorator:
                MarkDecoratorAliasReferenced(location);
                break;
            case ReferenceHint.Unspecified:
                if (AstUtilities.IsJsxTagName(location) && IsJsxIntrinsicTagName(location))
                {
                    return; // builtin JSX tag names aren't real type refs by most metrics, but are expressions, so must be filtered
                }
                if (AstUtilities.FindAncestor(location, n => NodeTests.IsMetaProperty(n)) != null)
                {
                    return; // identifiers in meta properties shouldn't be resolved, but are expressions, so must be filtered
                }
                // Identifiers in expression contexts are emitted, so we need to follow their referenced aliases and mark them as used
                // Some non-expression identifiers are also treated as expression identifiers for this purpose, eg, `a` in `b = {a}` or `q` in `import r = q`
                // This is the exception, rather than the rule - most non-expression identifiers are declaration names.
                if (NodeTests.IsIdentifier(location) &&
                    (AstUtilities.IsExpressionNode(location) ||
                        NodeTests.IsShorthandPropertyAssignment(location.Parent!) ||
                        (NodeTests.IsImportEqualsDeclaration(location.Parent!) &&
                            location.Parent!.AsImportEqualsDeclaration().ModuleReference == location)) &&
                    ShouldMarkIdentifierAliasReferenced(location))
                {
                    if (AstUtilities.IsPropertyAccessOrQualifiedName(location.Parent!))
                    {
                        Node? left;
                        if (NodeTests.IsPropertyAccessExpression(location.Parent!))
                        {
                            left = location.Parent!.Expression();
                        }
                        else
                        {
                            left = location.Parent!.AsQualifiedName().Left;
                        }
                        if (left != location)
                        {
                            return; // Only mark the LHS (the RHS is a property lookup)
                        }
                    }
                    MarkIdentifierAliasReferenced(location);
                    return;
                }
                if (AstUtilities.IsPropertyAccessOrQualifiedName(location))
                {
                    var topProp = location;
                    while (AstUtilities.IsPropertyAccessOrQualifiedName(topProp))
                    {
                        if (AstUtilities.IsPartOfTypeNode(topProp))
                        {
                            return;
                        }
                        topProp = topProp.Parent!;
                    }
                    MarkPropertyAliasReferenced(location, null /*propSymbol*/, null /*parentType*/);
                    return;
                }
                if (NodeTests.IsExportAssignment(location))
                {
                    MarkExportAssignmentAliasReferenced(location);
                    return;
                }
                if (IsJsxOpeningLikeElement(location) || NodeTests.IsJsxOpeningFragment(location))
                {
                    MarkJsxAliasReferenced(location);
                    return;
                }
                if (NodeTests.IsImportEqualsDeclaration(location))
                {
                    if (IsInternalModuleImportEqualsDeclaration(location) || CheckExternalImportOrExportDeclaration(location))
                    {
                        MarkImportEqualsAliasReferenced(location);
                        return;
                    }
                    return;
                }
                if (NodeTests.IsExportSpecifier(location))
                {
                    MarkExportSpecifierAliasReferenced(location);
                    return;
                }
                if (!compilerOptions.EmitDecoratorMetadata.IsTrue())
                {
                    return;
                }
                if (!AstUtilities.CanHaveDecorators(location) || !AstUtilities.HasDecorators(location) || location.Modifiers() == null || !AstUtilities.NodeCanBeDecorated(legacyDecorators, location, location.Parent!, location.Parent!.Parent!))
                {
                    return;
                }

                MarkDecoratorAliasReferenced(location);
                return;
            default:
                throw new InvalidOperationException("Unhandled reference hint");
        }
    }

    // checker.go:28116-28129
    private bool IsExportOrExportExpression(Node location)
    {
        return AstUtilities.FindAncestor(location, n =>
        {
            var parent = n.Parent;
            if (parent != null)
            {
                if (AstUtilities.IsAnyExportAssignment(parent))
                {
                    return parent.Expression() == n && AstUtilities.IsEntityNameExpression(n);
                }
                if (NodeTests.IsExportSpecifier(parent))
                {
                    return parent.AsExportSpecifier().Name() == n || parent.PropertyName() == n;
                }
            }
            return false;
        }) != null;
    }

    // checker.go:28131-28150
    private static bool ShouldMarkIdentifierAliasReferenced(Node node)
    {
        var parent = node.Parent;
        if (parent != null)
        {
            // A property access expression LHS? checkPropertyAccessExpression will handle that.
            if (NodeTests.IsPropertyAccessExpression(parent) && parent.Expression() == node)
            {
                return false;
            }
            // Next two check for an identifier inside a type only export.
            if (NodeTests.IsExportSpecifier(parent) && parent.IsTypeOnly())
            {
                return false;
            }
            if (parent.Parent != null)
            {
                var greatGrandparent = parent.Parent.Parent;
                if (greatGrandparent != null && NodeTests.IsExportDeclaration(greatGrandparent) && greatGrandparent.IsTypeOnly())
                {
                    return false;
                }
            }
        }
        return true;
    }

    // checker.go:28157-28162
    internal void MarkIdentifierAliasReferenced(Node location /*IdentifierNode*/)
    {
        var symbol = GetResolvedSymbol(location);
        if (symbol != null && symbol != argumentsSymbol && symbol != unknownSymbol && !AstUtilities.IsThisInTypeQuery(location))
        {
            MarkAliasReferenced(symbol, location);
        }
    }

    // checker.go:28164-28234
    internal void MarkPropertyAliasReferenced(Node location /*PropertyAccessExpression | QualifiedName*/, Symbol? propSymbol, Type? parentType)
    {
        if (IsPartOfImportEqualsModuleReference(location))
        {
            return;
        }
        Node? left;
        if (NodeTests.IsPropertyAccessExpression(location))
        {
            left = location.Expression();
        }
        else
        {
            left = location.AsQualifiedName().Left;
        }
        if (JSDocReparseAstGaps.IsThisIdentifier(left) || !NodeTests.IsIdentifier(left!))
        {
            return;
        }
        var parentSymbol = GetResolvedSymbol(left!);
        if (parentSymbol == null || parentSymbol == unknownSymbol)
        {
            return;
        }
        // In `Foo.Bar.Baz`, 'Foo' is not referenced if 'Bar' is a const enum or a module containing only const enums.
        // `Foo` is also not referenced in `enum FooCopy { Bar = Foo.Bar }`, because the enum member value gets inlined
        // here even if `Foo` is not a const enum.
        //
        // The exceptions are:
        //   1. if 'isolatedModules' is enabled, because the const enum value will not be inlined, and
        //   2. if 'preserveConstEnums' is enabled and the expression is itself an export, e.g. `export = Foo.Bar.Baz`.
        //
        // The property lookup is deferred as much as possible, in as many situations as possible, to avoid alias marking
        // pulling on types/symbols it doesn't strictly need to.
        if (compilerOptions.GetIsolatedModules() || (compilerOptions.ShouldPreserveConstEnums() && IsExportOrExportExpression(location)))
        {
            MarkAliasReferenced(parentSymbol, location);
            return;
        }
        // Hereafter, this relies on type checking - but every check prior to this only used symbol information
        var leftType = parentType;
        if (leftType == null)
        {
            leftType = CheckExpressionCached(left!);
        }
        if (IsTypeAny(leftType) || leftType == silentNeverType)
        {
            MarkAliasReferenced(parentSymbol, location);
            return;
        }
        var prop = propSymbol;
        if (prop == null && parentType == null)
        {
            Node? right;
            if (NodeTests.IsPropertyAccessExpression(location))
            {
                right = location.AsPropertyAccessExpression().Name();
            }
            else
            {
                right = location.AsQualifiedName().Right;
            }
            Symbol? lexicallyScopedSymbol = null;
            if (NodeTests.IsPrivateIdentifier(right!))
            {
                lexicallyScopedSymbol = LookupSymbolForPrivateIdentifierDeclaration(right!.Text(), right!);
            }
            var assignmentKind = GetAssignmentTargetKind(location);
            Type apparentType;
            if (assignmentKind != AssignmentKind.None || IsMethodAccessForCall(location))
            {
                apparentType = GetApparentType(GetWidenedType(leftType));
            }
            else
            {
                apparentType = GetApparentType(leftType);
            }
            if (NodeTests.IsPrivateIdentifier(right!))
            {
                if (lexicallyScopedSymbol != null)
                {
                    prop = GetPrivateIdentifierPropertyOfType(apparentType, lexicallyScopedSymbol);
                }
            }
            else
            {
                prop = GetPropertyOfType(apparentType, right!.Text());
            }
        }
        if (!(prop != null && (IsConstEnumOrConstEnumOnlyModule(prop) || (prop.Flags & SymbolFlags.EnumMember) != 0 && location.Parent!.Kind == SyntaxKind.EnumMember)))
        {
            MarkAliasReferenced(parentSymbol, location);
        }
    }

    // checker.go:28236-28247
    private static bool IsPartOfImportEqualsModuleReference(Node location)
    {
        var importEquals = AstUtilities.FindAncestorKind(location, SyntaxKind.ImportEqualsDeclaration);
        if (importEquals == null)
        {
            return false;
        }
        for (Node? node = location; node != null && node != importEquals; node = node.Parent)
        {
            if (node == importEquals.AsImportEqualsDeclaration().ModuleReference)
            {
                return true;
            }
        }
        return false;
    }

    // checker.go:28249-28257
    internal void MarkExportAssignmentAliasReferenced(Node location /*ExportAssignment*/)
    {
        var id = location.Expression()!;
        if (NodeTests.IsIdentifier(id))
        {
            var sym = GetExportSymbolOfValueSymbolIfExported(ResolveEntityName(id, SymbolFlags.All, true /*ignoreErrors*/, true /*dontResolveAlias*/, location));
            if (sym != null)
            {
                MarkAliasReferenced(sym, id);
            }
        }
    }

    // checker.go:28259-28303
    internal void MarkJsxAliasReferenced(Node node /*JsxOpeningLikeElement | JsxOpeningFragment*/)
    {
        if (GetJsxNamespaceContainerForImplicitImport(node) != null)
        {
            return;
        }
        // The reactNamespace/jsxFactory's root symbol should be marked as 'used' so we don't incorrectly elide its import.
        // And if there is no reactNamespace/jsxFactory's symbol in scope when targeting React emit, we should issue an error.
        var jsxFactoryRefErr = CoreUtil.IfElse<global::Ts.Diagnostics.Message?>(compilerOptions.Jsx == JsxEmit.React, Diag.This_JSX_tag_requires_0_to_be_in_scope_but_it_could_not_be_found, null);
        var jsxFactoryNamespace = GetJsxNamespace(node);
        var jsxFactoryLocation = node;
        if (IsJsxOpeningLikeElement(node))
        {
            jsxFactoryLocation = node.TagName()!;
        }
        var shouldFactoryRefErr = compilerOptions.Jsx != JsxEmit.Preserve && compilerOptions.Jsx != JsxEmit.ReactNative;
        // #38720/60122, allow null as jsxFragmentFactory
        Symbol? jsxFactorySym = null;
        if (!(NodeTests.IsJsxOpeningFragment(node) && jsxFactoryNamespace == "null"))
        {
            var flags = SymbolFlags.Value;
            if (!shouldFactoryRefErr)
            {
                flags &= ~SymbolFlags.Enum;
            }
            jsxFactorySym = ResolveName(jsxFactoryLocation, jsxFactoryNamespace, flags, jsxFactoryRefErr, true /*isUse*/, false /*excludeGlobals*/);
        }
        if (jsxFactorySym != null)
        {
            // Mark local symbol as referenced here because it might not have been marked
            // if jsx emit was not jsxFactory as there wont be error being emitted
            SymbolReferenced(jsxFactorySym, SymbolFlags.All);
            // If react/jsxFactory symbol is alias, mark it as referenced
            if (canCollectSymbolAliasAccessibilityData && (jsxFactorySym.Flags & SymbolFlags.Alias) != 0 && GetTypeOnlyAliasDeclaration(jsxFactorySym) == null)
            {
                MarkAliasSymbolAsReferenced(jsxFactorySym);
            }
        }
        // if JsxFragment, additionally mark jsx pragma as referenced, since `getJsxNamespace` above would have resolved to only the fragment factory if they are distinct
        if (NodeTests.IsJsxOpeningFragment(node))
        {
            var file = AstUtilities.GetSourceFileOfNode(node);
            var entity = GetJsxFactoryEntity(file!.AsNode());
            if (entity != null)
            {
                var localJsxNamespace = GetFirstIdentifier(entity).Text();
                var flags = SymbolFlags.Value;
                if (!shouldFactoryRefErr)
                {
                    flags &= ~SymbolFlags.Enum;
                }
                ResolveName(jsxFactoryLocation, localJsxNamespace, flags, jsxFactoryRefErr, true /*isUse*/, false /*excludeGlobals*/);
            }
        }
    }

    // checker.go:28305-28309
    internal void MarkImportEqualsAliasReferenced(Node location /*ImportEqualsDeclaration*/)
    {
        if (AstUtilities.HasSyntacticModifier(location, ModifierFlags.Export))
        {
            MarkExportAsReferenced(location);
        }
    }

    // checker.go:28311-28331
    internal void MarkExportSpecifierAliasReferenced(Node location /*ExportSpecifierNode*/)
    {
        if (location.Parent!.Parent!.ModuleSpecifier() == null && !location.IsTypeOnly() && !location.Parent!.Parent!.IsTypeOnly())
        {
            var exportedName = location.PropertyNameOrName()!;
            if (exportedName.Kind == SyntaxKind.StringLiteral)
            {
                return; // Skip for invalid syntax like this: export { "x" }
            }
            var symbol = ResolveName(exportedName, exportedName.Text(), SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias, null /*nameNotFoundMessage*/, true /*isUse*/, false /*excludeGlobals*/);
            if (symbol != null && (symbol == undefinedSymbol || symbol == globalThisSymbol || (symbol.Declarations != null && AstUtilities.IsGlobalSourceFile(AstUtilities.GetDeclarationContainer(symbol.Declarations[0])!))))
            {
                // Do nothing, non-local symbol
            }
            else
            {
                var target = symbol;
                if (target != null && (target.Flags & SymbolFlags.Alias) != 0)
                {
                    target = ResolveAlias(target).Item1;
                }
                if (target == null || (GetSymbolFlags(target) & SymbolFlags.Value) != 0)
                {
                    MarkExportAsReferenced(location);             // marks export as used
                    MarkIdentifierAliasReferenced(exportedName);  // marks target of export as used
                }
            }
        }
    }

    // types.go:152
    private const string ExternalHelpersModuleNameText = "tslib";

    // checker.go:28333
    internal void CheckExternalEmitHelpers(Node location, ExternalEmitHelpers helpers)
    {
        if (!compilerOptions.ImportHelpers.IsTrue())
        {
            return;
        }
        var sourceFile = AstUtilities.GetSourceFileOfNode(location);
        if (!AstUtilities.IsEffectiveExternalModule(sourceFile!, compilerOptions) || (location.Flags & NodeFlags.Ambient) != 0)
        {
            return;
        }
        var helpersModule = ResolveHelpersModule(sourceFile!, location);
        if (helpersModule == unknownSymbol)
        {
            return;
        }
        var links = sourceFileLinks.Get(sourceFile!);
        if ((links.requestedExternalEmitHelpers & helpers) != helpers)
        {
            var uncheckedHelpers = helpers & ~links.requestedExternalEmitHelpers;
            for (var helper = ExternalEmitHelpers.FirstEmitHelper; helper <= ExternalEmitHelpers.LastEmitHelper; helper = (ExternalEmitHelpers)((uint)helper << 1))
            {
                if ((uncheckedHelpers & helper) == 0)
                {
                    continue;
                }
                foreach (var name in GetHelperNames(helper))
                {
                    var symbol = ResolveSymbol(GetSymbol(GetExportsOfModule(helpersModule!), name, SymbolFlags.Value));
                    if (symbol == null)
                    {
                        Error(location, Diag.This_syntax_requires_an_imported_helper_named_1_which_does_not_exist_in_0_Consider_upgrading_your_version_of_0, ExternalHelpersModuleNameText, name);
                    }
                    else if ((helper & ExternalEmitHelpers.ClassPrivateFieldGet) != 0)
                    {
                        if (!HasSignatureWithArityGreaterThan(symbol, 3))
                        {
                            Error(location, Diag.This_syntax_requires_an_imported_helper_named_1_with_2_parameters_which_is_not_compatible_with_the_one_in_0_Consider_upgrading_your_version_of_0, ExternalHelpersModuleNameText, name, 4);
                        }
                    }
                    else if ((helper & ExternalEmitHelpers.ClassPrivateFieldSet) != 0)
                    {
                        if (!HasSignatureWithArityGreaterThan(symbol, 4))
                        {
                            Error(location, Diag.This_syntax_requires_an_imported_helper_named_1_with_2_parameters_which_is_not_compatible_with_the_one_in_0_Consider_upgrading_your_version_of_0, ExternalHelpersModuleNameText, name, 5);
                        }
                    }
                }
            }
        }
        links.requestedExternalEmitHelpers |= helpers;
    }

    // checker.go:28371
    internal bool HasSignatureWithArityGreaterThan(Symbol symbol, int arity)
    {
        foreach (var signature in GetSignaturesOfSymbol(symbol))
        {
            if (GetParameterCount(signature) > arity)
            {
                return true;
            }
        }
        return false;
    }

    // checker.go:28380
    internal string[] GetHelperNames(ExternalEmitHelpers helper)
    {
        switch (helper)
        {
            case ExternalEmitHelpers.Rest:
                return ["__rest"];
            case ExternalEmitHelpers.Decorate:
                if (legacyDecorators)
                {
                    return ["__decorate"];
                }
                return ["__esDecorate", "__runInitializers"];
            case ExternalEmitHelpers.Metadata:
                return ["__metadata"];
            case ExternalEmitHelpers.Param:
                return ["__param"];
            case ExternalEmitHelpers.Awaiter:
                return ["__awaiter"];
            case ExternalEmitHelpers.Await:
                return ["__await"];
            case ExternalEmitHelpers.AsyncGenerator:
                return ["__asyncGenerator"];
            case ExternalEmitHelpers.AsyncDelegator:
                return ["__asyncDelegator"];
            case ExternalEmitHelpers.AsyncValues:
                return ["__asyncValues"];
            case ExternalEmitHelpers.ExportStar:
                return ["__exportStar"];
            case ExternalEmitHelpers.ImportStar:
                return ["__importStar"];
            case ExternalEmitHelpers.ImportDefault:
                return ["__importDefault"];
            case ExternalEmitHelpers.MakeTemplateObject:
                return ["__makeTemplateObject"];
            case ExternalEmitHelpers.ClassPrivateFieldGet:
                return ["__classPrivateFieldGet"];
            case ExternalEmitHelpers.ClassPrivateFieldSet:
                return ["__classPrivateFieldSet"];
            case ExternalEmitHelpers.ClassPrivateFieldIn:
                return ["__classPrivateFieldIn"];
            case ExternalEmitHelpers.SetFunctionName:
                return ["__setFunctionName"];
            case ExternalEmitHelpers.PropKey:
                return ["__propKey"];
            case ExternalEmitHelpers.AddDisposableResourceAndDisposeResources:
                return ["__addDisposableResource", "__disposeResources"];
            case ExternalEmitHelpers.RewriteRelativeImportExtension:
                return ["__rewriteRelativeImportExtension"];
            default:
                throw new global::System.InvalidOperationException("Unrecognized helper");
        }
    }

    // checker.go:28430
    internal Symbol? ResolveHelpersModule(SourceFile file, Node errorNode)
    {
        var links = sourceFileLinks.Get(file);
        if (links.externalHelpersModule == null)
        {
            var location = program.GetImportHelpersImportSpecifier(file.Path());
            var helpersModule = ResolveExternalModule(location, ExternalHelpersModuleNameText, Diag.This_syntax_requires_an_imported_helper_but_module_0_cannot_be_found, errorNode, false /*isForAugmentation*/);
            if (helpersModule == null)
            {
                helpersModule = unknownSymbol;
            }
            links.externalHelpersModule = helpersModule;
        }
        return links.externalHelpersModule;
    }

    // checker.go:28443-28489
    internal void MarkDecoratorAliasReferenced(Node node /*HasDecorators*/)
    {
        if (compilerOptions.EmitDecoratorMetadata.IsFalseOrUnknown())
        {
            return;
        }
        var firstDecorator = CoreUtil.FirstOrNil(node.Decorators());
        if (firstDecorator == null)
        {
            return;
        }

        CheckExternalEmitHelpers(firstDecorator, ExternalEmitHelpers.Metadata);

        // we only need to perform these checks if we are emitting serialized type metadata for the target of a decorator.
        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                {
                    var ctor = AstUtilities.GetFirstConstructorWithBody(node);
                    if (ctor != null)
                    {
                        foreach (var p in ctor.Parameters())
                        {
                            MarkDecoratorMedataDataTypeNodeAsReferenced(GetParameterTypeNodeForDecoratorCheck(p));
                        }
                    }
                    break;
                }
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                {
                    var otherKind = SyntaxKind.SetAccessor;
                    if (node.Kind == SyntaxKind.SetAccessor)
                    {
                        otherKind = SyntaxKind.GetAccessor;
                    }
                    var otherAccessor = AstUtilities.GetDeclarationOfKind(GetSymbolOfDeclaration(node)!, otherKind);
                    var annotation = GetAnnotatedAccessorTypeNode(node);
                    if (annotation == null && otherAccessor != null)
                    {
                        annotation = GetAnnotatedAccessorTypeNode(otherAccessor);
                    }
                    MarkDecoratorMedataDataTypeNodeAsReferenced(annotation);
                    break;
                }
            case SyntaxKind.MethodDeclaration:
                foreach (var p in node.Parameters())
                {
                    MarkDecoratorMedataDataTypeNodeAsReferenced(GetParameterTypeNodeForDecoratorCheck(p));
                }
                MarkDecoratorMedataDataTypeNodeAsReferenced(node.Type());
                break;
            case SyntaxKind.PropertyDeclaration:
                MarkDecoratorMedataDataTypeNodeAsReferenced(node.Type());
                break;
            case SyntaxKind.Parameter:
                {
                    MarkDecoratorMedataDataTypeNodeAsReferenced(GetParameterTypeNodeForDecoratorCheck(node));
                    var containingSignature = node.Parent!;
                    foreach (var p in containingSignature.Parameters())
                    {
                        MarkDecoratorMedataDataTypeNodeAsReferenced(GetParameterTypeNodeForDecoratorCheck(p));
                    }
                    MarkDecoratorMedataDataTypeNodeAsReferenced(containingSignature.Type());
                    break;
                }
        }
    }

    // checker.go:28491-28497
    private Node? GetParameterTypeNodeForDecoratorCheck(Node node /*ParameterDeclarationNode*/)
    {
        var typeNode = node.Type();
        if (node.AsParameterDeclaration().DotDotDotToken != null)
        {
            return AstUtilities.GetRestParameterElementType(typeNode);
        }
        return typeNode;
    }

    // checker.go:28499-28504
    private void MarkDecoratorMedataDataTypeNodeAsReferenced(Node? node /*TypeNode*/)
    {
        var entityName = GetEntityNameForDecoratorMetadata(node);
        if (entityName != null && AstUtilities.IsEntityName(entityName))
        {
            MarkEntityNameOrEntityExpressionAsReference(entityName, true);
        }
    }

    // checker.go:28506-28525
    private Node? GetEntityNameForDecoratorMetadata(Node? node)
    {
        if (node == null)
        {
            return node;
        }
        switch (node.Kind)
        {
            case SyntaxKind.IntersectionType:
                return GetEntityNameForDecoratorMetadataFromTypeList(node.AsIntersectionTypeNode().Types!.Nodes);
            case SyntaxKind.UnionType:
                return GetEntityNameForDecoratorMetadataFromTypeList(node.AsUnionTypeNode().Types!.Nodes);
            case SyntaxKind.ConditionalType:
                return GetEntityNameForDecoratorMetadataFromTypeList([node.AsConditionalTypeNode().TrueType!, node.AsConditionalTypeNode().FalseType!]);
            case SyntaxKind.ParenthesizedType:
                return GetEntityNameForDecoratorMetadata(node.AsParenthesizedTypeNode().Type);
            case SyntaxKind.NamedTupleMember:
                return GetEntityNameForDecoratorMetadata(node.AsNamedTupleMember().Type);
            case SyntaxKind.TypeReference:
                return node.AsTypeReferenceNode().TypeName;
        }
        return null;
    }

    // checker.go:28527-28557
    private Node? GetEntityNameForDecoratorMetadataFromTypeList(Node[] typeNodes)
    {
        Node? commonEntityName = null;
        foreach (var typeNode in typeNodes)
        {
            if (typeNode.Kind == SyntaxKind.NeverKeyword)
            {
                continue; // Always elide `never` from the union/intersection if possible
            }
            if (!strictNullChecks && ((typeNode.Kind == SyntaxKind.LiteralType && typeNode.AsLiteralTypeNode().Literal!.Kind == SyntaxKind.NullKeyword) || typeNode.Kind == SyntaxKind.UndefinedKeyword))
            {
                continue; // Elide null and undefined from unions for metadata, just like what we did prior to the implementation of strict null checks
            }
            var individualEntityName = GetEntityNameForDecoratorMetadata(typeNode);
            if (individualEntityName == null)
            {
                // Individual is something like string number
                // So it would be serialized to either that type or object
                // Safe to return here
                return null;
            }

            if (commonEntityName == null)
            {
                commonEntityName = individualEntityName;
            }
            else
            {
                // Note this is in sync with the transformation that happens for type node.
                // Keep this in sync with serializeUnionOrIntersectionType
                // Verify if they refer to same entity and is identifier
                // return undefined if they dont match because we would emit object
                if (!NodeTests.IsIdentifier(commonEntityName) || !NodeTests.IsIdentifier(individualEntityName) || commonEntityName.AsIdentifier().Text != individualEntityName.AsIdentifier().Text)
                {
                    return null;
                }
            }
        }
        return commonEntityName;
    }

    // checker.go:28559-28576
    internal void MarkAliasReferenced(Symbol symbol, Node location)
    {
        if (!canCollectSymbolAliasAccessibilityData)
        {
            return;
        }
        if (AstUtilities.IsNonLocalAlias(symbol, SymbolFlags.Value /*excludes*/) && !IsInTypeQuery(location))
        {
            var target = ResolveAlias(symbol).Item1;
            if ((GetSymbolFlagsEx(symbol, true /*excludeTypeOnlyMeanings*/, false /*excludeLocalMeanings*/) & (SymbolFlags.Value | SymbolFlags.ExportValue)) != 0)
            {
                // An alias resolving to a const enum cannot be elided if (1) 'isolatedModules' is enabled
                // (because the const enum value will not be inlined), or if (2) the alias is an export
                // of a const enum declaration that will be preserved.
                if (compilerOptions.GetIsolatedModules() ||
                    (compilerOptions.ShouldPreserveConstEnums() && IsExportOrExportExpression(location)) ||
                    !IsConstEnumOrConstEnumOnlyModule(GetExportSymbolOfValueSymbolIfExported(target)!))
                {
                    MarkAliasSymbolAsReferenced(symbol);
                }
            }
        }
    }

    // When an alias symbol is referenced, we need to mark the entity it references as referenced and in turn repeat that until
    // we reach a non-alias or an exported entity (which is always considered referenced). We do this by checking the target of
    // the alias as an expression (which recursively takes us back here if the target references another alias).
    // checker.go:28581-28600
    internal void MarkAliasSymbolAsReferenced(Symbol symbol)
    {
        var links = aliasSymbolLinks.Get(symbol);
        if (!links.referenced)
        {
            links.referenced = true;
            var node = GetDeclarationOfAliasSymbol(symbol);
            if (node == null)
            {
                throw new InvalidOperationException("Unexpected nil in markAliasSymbolAsReferenced");
            }
            // We defer checking of the reference of an `import =` until the import itself is referenced,
            // This way a chain of imports can be elided if ultimately the final input is only used in a type
            // position.
            if (NodeTests.IsImportEqualsDeclaration(node) && node.AsImportEqualsDeclaration().ModuleReference!.Kind != SyntaxKind.ExternalModuleReference)
            {
                if ((GetSymbolFlags(ResolveSymbol(symbol)!) & SymbolFlags.Value) != 0)
                {
                    // import foo = <symbol>
                    var left = GetFirstIdentifier(node.AsImportEqualsDeclaration().ModuleReference!);
                    MarkIdentifierAliasReferenced(left);
                }
            }
        }
    }

    // checker.go:28602-28612
    internal void MarkExportAsReferenced(Node node /*ImportEqualsDeclaration | ExportSpecifier*/)
    {
        var symbol = GetSymbolOfDeclaration(node)!;
        var target = ResolveAlias(symbol).Item1;
        if (target != null)
        {
            var markAlias = target == unknownSymbol ||
                (((GetSymbolFlagsEx(symbol, true /*excludeTypeOnlyMeanings*/, false /*excludeLocalMeanings*/) & SymbolFlags.Value) != 0) && !IsConstEnumOrConstEnumOnlyModule(target));
            if (markAlias)
            {
                MarkAliasSymbolAsReferenced(symbol);
            }
        }
    }

    // checker.go:28614-28647
    private void MarkEntityNameOrEntityExpressionAsReference(Node? typeName /*EntityNameOrEntityNameExpression | nil*/, bool forDecoratorMetadata)
    {
        if (typeName == null)
        {
            return;
        }

        var rootName = GetFirstIdentifier(typeName);
        var meaning = CoreUtil.IfElse(typeName.Kind == SyntaxKind.Identifier, SymbolFlags.Type, SymbolFlags.Namespace) | SymbolFlags.Alias;
        var rootSymbol = ResolveName(rootName, rootName.Text(), meaning, null /*nameNotFoundMessage*/, true /*isUse*/, false /*excludeGlobals*/);

        if (rootSymbol != null && (rootSymbol.Flags & SymbolFlags.Alias) != 0)
        {
            if (canCollectSymbolAliasAccessibilityData &&
                SymbolIsValue(rootSymbol) &&
                !IsConstEnumOrConstEnumOnlyModule(ResolveAlias(rootSymbol).Item1!) &&
                GetTypeOnlyAliasDeclaration(rootSymbol) == null)
            {
                MarkAliasSymbolAsReferenced(rootSymbol);
            }
            else if (forDecoratorMetadata &&
                compilerOptions.GetIsolatedModules() &&
                compilerOptions.GetEmitModuleKind() >= ModuleKind.ES2015 &&
                !SymbolIsValue(rootSymbol) &&
                !CoreUtil.Some(rootSymbol.Declarations, IsTypeOnlyImportOrExportDeclaration))
            {
                var diag = Error(typeName, Diag.A_type_referenced_in_a_decorated_signature_must_be_imported_with_import_type_or_a_namespace_import_when_isolatedModules_and_emitDecoratorMetadata_are_enabled);
                Node? aliasDeclaration = null;
                foreach (var decl in rootSymbol.Declarations!)
                {
                    if (AstUtilities.IsAliasSymbolDeclaration(decl))
                    {
                        aliasDeclaration = decl;
                        break;
                    }
                }
                if (aliasDeclaration != null)
                {
                    diag.SetRelatedInfo([CreateDiagnosticForNode(aliasDeclaration, Diag.X_0_was_imported_here, rootName.Text())]);
                }
            }
        }
    }

    // checker.go:28649-28666
    private static Node? GetEntityNameFromTypeNode(Node node /*TypeNode*/)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TypeReference:
                return node.AsTypeReferenceNode().TypeName;

            case SyntaxKind.ExpressionWithTypeArguments:
                if (AstUtilities.IsEntityNameExpression(node.Expression()!))
                {
                    return node.Expression();
                }
                return null;

            // These aren't valid TypeNodes, but we treat them as such because of `isPartOfTypeNode`, which returns `true` for things that aren't `TypeNode`s.
            case SyntaxKind.Identifier:
            case SyntaxKind.QualifiedName:
                return node;
        }

        return null;
    }

    // If a TypeNode can be resolved to a value symbol imported from an external module, it is
    // marked as referenced to prevent import elision.
    // checker.go:28670-28674
    internal void MarkTypeNodeAsReferenced(Node? node /*TypeNode*/)
    {
        if (node != null)
        {
            MarkEntityNameOrEntityExpressionAsReference(GetEntityNameFromTypeNode(node), false /*forDecoratorMetadata*/);
        }
    }

    // emitresolver.go:685-687 — a package-level predicate the alias-reference chain leans on
    // (EmitResolver keeps its own private copy of the same function; this is the checker-visible one).
    private static bool IsConstEnumOrConstEnumOnlyModule(Symbol s)
    {
        return IsConstEnumSymbol(s) || (s.Flags & SymbolFlags.ConstEnumOnlyModule) != 0;
    }
}

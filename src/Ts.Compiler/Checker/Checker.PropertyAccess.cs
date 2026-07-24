// Ported from tsgo internal/checker/checker.go (property-access cluster, plan/03 §4.2 #7):
//   checkPropertyAccessExpression 11216 / checkPropertyAccessChain 11224 /
//   checkPropertyAccessExpressionOrQualifiedName 11230 / getFlowTypeOfAccessExpression 11361 /
//   getFlowTypeOfProperty 11413 (stub'd deps) / isMethodAccessForCall 11435 /
//   lookupSymbolForPrivateIdentifierDeclaration 11443 / getPrivateIdentifierPropertyOfType 11459 /
//   checkPrivateIdentifierPropertyAccess 11463 / checkPropertyNotUsedBeforeDeclaration 11678 /
//   isOptionalPropertyDeclaration 11700 / isPropertyDeclaredInAncestorClass 11704 /
//   checkPropertyAccessibility[Ex/AtLocation] 11722/11726/11755 / symbolHasNonMethodDeclaration 11869 /
//   forEachProperty 11875 / isNodeUsedDuringClassInitialization 11918 / isNodeWithinClass 11929 /
//   forEachEnclosingClass 11933 / isClassDerivedFromDeclaringClasses 11947 /
//   getEnclosingClassFromThisParameter 11956 / getThisParameterFromNodeContext 11982 /
//   isSelfTypeAccess 27141 / isAssignmentToReadonlyEntity 27145 / isThisPropertyAccessInConstructor 27194 /
//   isAutoTypedProperty 27204 / getDeclaringConstructor 27211 / isPropertyWithoutInitializer 4939.
//
// Faithful per-function translation: same order, same branch structure. Go comments and `// !!!`
// markers preserved.
//
// §1.5: the cross-package Diagnostics class is shadowed by the sibling Ts.Diagnostics namespace, so it
// is reached through the `Diag` alias; no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used
// directly. Error-reporting branches for private/protected/abstract access route through cross-cluster
// stubs (symbolToString/getDeclaringClass/…), reached only when accessibility actually fails.

using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:11216
    internal Type CheckPropertyAccessExpression(Node node, CheckMode checkMode, bool writeOnly)
    {
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            return CheckPropertyAccessChain(node, checkMode);
        }
        var expr = node.Expression()!;
        return CheckPropertyAccessExpressionOrQualifiedName(node, expr, CheckNonNullExpression(expr), node.AsPropertyAccessExpression().Name()!, checkMode, writeOnly);
    }

    // checker.go:11224
    internal Type CheckPropertyAccessChain(Node node, CheckMode checkMode)
    {
        var leftType = CheckExpression(node.Expression()!);
        var nonOptionalType = GetOptionalExpressionType(leftType, node.Expression()!);
        return PropagateOptionalTypeMarker(CheckPropertyAccessExpressionOrQualifiedName(node, node.Expression()!, CheckNonNullType(nonOptionalType, node.Expression()!), node.Name()!, checkMode, false), node, nonOptionalType != leftType);
    }

    // checker.go:11230
    internal Type CheckPropertyAccessExpressionOrQualifiedName(Node node, Node left, Type leftType, Node right, CheckMode checkMode, bool writeOnly)
    {
        var parentSymbol = GetResolvedSymbolOrNil(left);
        var assignmentKind = GetAssignmentTargetKind(node);
        var widenedType = leftType;
        if (assignmentKind != AssignmentKind.None || IsMethodAccessForCall(node))
        {
            widenedType = GetWidenedType(leftType);
        }
        var apparentType = GetApparentType(widenedType);
        bool isAnyLike = IsTypeAny(apparentType) || apparentType == silentNeverType;
        Symbol? prop = null;
        if (NodeTests.IsPrivateIdentifier(right))
        {
            if (languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.PrivateNamesAndClassStaticBlocks ||
                languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.ClassAndClassElementDecorators ||
                !compilerOptions.GetUseDefineForClassFields())
            {
                if (assignmentKind != AssignmentKind.None)
                {
                    CheckExternalEmitHelpers(node, ExternalEmitHelpers.ClassPrivateFieldSet);
                }
                if (assignmentKind != AssignmentKind.Definite)
                {
                    CheckExternalEmitHelpers(node, ExternalEmitHelpers.ClassPrivateFieldGet);
                }
            }
            var lexicallyScopedSymbol = LookupSymbolForPrivateIdentifierDeclaration(right.Text(), right);
            if (assignmentKind != AssignmentKind.None && lexicallyScopedSymbol != null && lexicallyScopedSymbol.ValueDeclaration != null && NodeTests.IsMethodDeclaration(lexicallyScopedSymbol.ValueDeclaration))
            {
                GrammarErrorOnNode(right, Diag.Cannot_assign_to_private_method_0_Private_methods_are_not_writable, right.Text());
            }
            if (isAnyLike)
            {
                if (lexicallyScopedSymbol != null)
                {
                    if (IsErrorType(apparentType))
                    {
                        return errorType!;
                    }
                    return apparentType;
                }
                if (GetContainingClassExcludingClassDecorators(right) == null)
                {
                    GrammarErrorOnNode(right, Diag.Private_identifiers_are_not_allowed_outside_class_bodies);
                    return anyType!;
                }
            }
            if (lexicallyScopedSymbol != null)
            {
                prop = GetPrivateIdentifierPropertyOfType(leftType, lexicallyScopedSymbol);
            }
            if (prop == null)
            {
                // Check for private-identifier-specific shadowing and lexical-scoping errors.
                if (CheckPrivateIdentifierPropertyAccess(leftType, right, lexicallyScopedSymbol))
                {
                    return errorType!;
                }
                var containingClass = GetContainingClassExcludingClassDecorators(right);
                if (containingClass != null && AstUtilities.IsPlainJSFile(AstUtilities.GetSourceFileOfNode(containingClass), compilerOptions.CheckJs))
                {
                    GrammarErrorOnNode(right, Diag.Private_field_0_must_be_declared_in_an_enclosing_class, right.Text());
                }
            }
            else
            {
                bool isSetonlyAccessor = (prop.Flags & SymbolFlags.SetAccessor) != 0 && (prop.Flags & SymbolFlags.GetAccessor) == 0;
                if (isSetonlyAccessor && assignmentKind != AssignmentKind.Definite)
                {
                    Error(node, Diag.Private_accessor_was_defined_without_a_getter);
                }
            }
        }
        else
        {
            if (isAnyLike)
            {
                if (NodeTests.IsIdentifier(left) && parentSymbol != null)
                {
                    MarkLinkedReferences(node, ReferenceHint.Property, null /*propSymbol*/, leftType);
                }
                if (IsErrorType(apparentType))
                {
                    return errorType!;
                }
                return apparentType;
            }
            prop = GetPropertyOfTypeEx(apparentType, right.Text(), IsConstEnumObjectType(apparentType) /*skipObjectFunctionPropertyAugment*/, node.Kind == SyntaxKind.QualifiedName /*includeTypeOnlyMembers*/);
        }
        MarkLinkedReferences(node, ReferenceHint.Property, prop, leftType);
        Type? propType;
        if (prop == null)
        {
            IndexInfo? indexInfo = null;
            if (!NodeTests.IsPrivateIdentifier(right) && (assignmentKind == AssignmentKind.None || !IsGenericObjectType(leftType) || IsThisTypeParameter(leftType)))
            {
                indexInfo = GetApplicableIndexInfoForName(apparentType, right.Text());
            }
            if (indexInfo == null)
            {
                bool isUncheckedJS = IsUncheckedJSSuggestion(node, leftType.symbol, true /*excludeClasses*/);
                if (!isUncheckedJS && IsJSLiteralType(leftType))
                {
                    return anyType!;
                }
                if (leftType.symbol == globalThisSymbol)
                {
                    var globalSymbol = globalThisSymbol!.Exports!.GetValueOrDefault(right.Text());
                    if (globalSymbol != null && (globalSymbol.Flags & SymbolFlags.BlockScoped) != 0)
                    {
                        Error(right, Diag.Property_0_does_not_exist_on_type_1, right.Text(), TypeToString(leftType));
                    }
                    else if (noImplicitAny)
                    {
                        Error(right, Diag.Element_implicitly_has_an_any_type_because_type_0_has_no_index_signature, TypeToString(leftType));
                    }
                    return anyType!;
                }
                if (right.Text() != "" && !CheckAndReportErrorForExtendingInterface(node))
                {
                    ReportNonexistentProperty(right, CoreUtil.IfElse(IsThisTypeParameter(leftType), apparentType, leftType), isUncheckedJS);
                }
                return errorType!;
            }
            if (indexInfo.isReadonly && (AstUtilities.IsAssignmentTarget(node) || IsDeleteTarget(node)))
            {
                Error(node, Diag.Index_signature_in_type_0_only_permits_reading, TypeToString(apparentType));
            }
            propType = indexInfo.valueType;
            if (compilerOptions.NoUncheckedIndexedAccess == Tristate.True && GetAssignmentTargetKind(node) != AssignmentKind.Definite)
            {
                propType = GetUnionType([propType, missingType!]);
            }
            if (compilerOptions.NoPropertyAccessFromIndexSignature == Tristate.True && NodeTests.IsPropertyAccessExpression(node))
            {
                Error(right, Diag.Property_0_comes_from_an_index_signature_so_it_must_be_accessed_with_0, right.Text());
            }
            if (indexInfo.declaration != null && IsDeprecatedDeclaration(indexInfo.declaration))
            {
                AddDeprecatedSuggestion(right, [indexInfo.declaration], right.Text());
            }
        }
        else
        {
            var targetPropSymbol = ResolveAliasWithDeprecationCheck(prop, right);
            if (IsDeprecatedSymbol(targetPropSymbol) && IsUncalledFunctionReference(node, targetPropSymbol) && targetPropSymbol.Declarations != null)
            {
                AddDeprecatedSuggestion(right, targetPropSymbol.Declarations, right.Text());
            }
            CheckPropertyNotUsedBeforeDeclaration(prop, node, right);
            MarkPropertyAsReferenced(prop, node, IsSelfTypeAccess(left, parentSymbol));
            symbolNodeLinks.Get(node).resolvedSymbol = prop;
            CheckPropertyAccessibility(node, left.Kind == SyntaxKind.SuperKeyword, AstUtilities.IsWriteAccess(node), apparentType, prop);
            if (IsAssignmentToReadonlyEntity(node, prop, assignmentKind))
            {
                Error(right, Diag.Cannot_assign_to_0_because_it_is_a_read_only_property, right.Text());
                return errorType!;
            }
            if (IsThisPropertyAccessInConstructor(node, prop))
            {
                propType = autoType;
            }
            else if (writeOnly || AstUtilities.IsWriteOnlyAccess(node))
            {
                propType = GetWriteTypeOfSymbol(prop);
            }
            else
            {
                propType = GetTypeOfSymbol(prop);
            }
        }
        return GetFlowTypeOfAccessExpression(node, prop, propType!, right, checkMode);
    }

    // checker.go:11361
    internal Type GetFlowTypeOfAccessExpression(Node node, Symbol? prop, Type propType, Node errorNode, CheckMode checkMode)
    {
        // Only compute control flow type if this is a property access expression that isn't an
        // assignment target, and the referenced property was declared as a variable, property,
        // accessor, or optional method.
        var assignmentKind = GetAssignmentTargetKind(node);
        if (assignmentKind == AssignmentKind.Definite)
        {
            return RemoveMissingType(propType, prop != null && (prop.Flags & SymbolFlags.Optional) != 0);
        }
        if (prop != null && (prop.Flags & (SymbolFlags.Variable | SymbolFlags.Property | SymbolFlags.Accessor)) == 0 && !((prop.Flags & SymbolFlags.Method) != 0 && (propType.flags & TypeFlags.Union) != 0))
        {
            return propType;
        }
        if (propType == autoType)
        {
            return GetFlowTypeOfProperty(node, prop);
        }
        propType = GetNarrowableTypeForReference(propType, node, checkMode);
        // If strict null checks and strict property initialization checks are enabled, if we have
        // a this.xxx property access, if the property is an instance property without an initializer,
        // and if we are in a constructor of the same class as the property declaration, assume that
        // the property is uninitialized at the top of the control flow.
        bool assumeUninitialized = false;
        if (strictNullChecks && prop != null)
        {
            var declaration = prop.ValueDeclaration;
            if (declaration != null)
            {
                if (strictPropertyInitialization && AstUtilities.IsAccessExpression(node) && node.Expression()!.Kind == SyntaxKind.ThisKeyword &&
                    IsPropertyWithoutInitializer(declaration) && !AstUtilities.IsStatic(declaration))
                {
                    var flowContainer = GetControlFlowContainer(node);
                    if (flowContainer != null && NodeTests.IsConstructorDeclaration(flowContainer) && flowContainer.Parent == declaration.Parent && (declaration.Flags & NodeFlags.Ambient) == 0)
                    {
                        assumeUninitialized = true;
                    }
                }
                else if (NodeTests.IsBinaryExpression(declaration) && NodeTests.IsPropertyAccessExpression(declaration.AsBinaryExpression().Left!) &&
                    GetControlFlowContainer(node) == GetControlFlowContainer(declaration))
                {
                    assumeUninitialized = true;
                }
            }
        }
        var flowType = GetFlowTypeOfReferenceEx(node, propType, AddOptionalityEx(propType, false /*isProperty*/, assumeUninitialized), null, null);
        if (assumeUninitialized && !ContainsUndefinedType(this, propType) && ContainsUndefinedType(this, flowType))
        {
            Error(errorNode, Diag.Property_0_is_used_before_being_assigned, SymbolToString(prop));
            // Return the declared type to reduce follow-on errors
            return propType;
        }
        if (assignmentKind != AssignmentKind.None)
        {
            return GetBaseTypeOfLiteralType(flowType);
        }
        return flowType;
    }

    // checker.go:11435
    internal bool IsMethodAccessForCall(Node node)
    {
        while (NodeTests.IsParenthesizedExpression(node.Parent!))
        {
            node = node.Parent!;
        }
        return AstUtilities.IsCallOrNewExpression(node.Parent!) && node.Parent!.Expression() == node;
    }

    // checker.go:11443 — Lookup the private identifier lexically.
    internal Symbol? LookupSymbolForPrivateIdentifierDeclaration(string propName, Node location)
    {
        for (var containingClass = GetContainingClassExcludingClassDecorators(location); containingClass != null; containingClass = AstUtilities.GetContainingClass(containingClass))
        {
            var symbol = containingClass.Symbol();
            var name = global::Ts.Binder.Binder.GetSymbolNameForPrivateIdentifier(symbol!, propName);
            var prop = symbol!.Members?.GetValueOrDefault(name);
            if (prop != null)
            {
                return prop;
            }
            prop = symbol.Exports?.GetValueOrDefault(name);
            if (prop != null)
            {
                return prop;
            }
        }
        return null;
    }

    // checker.go:11459
    internal Symbol? GetPrivateIdentifierPropertyOfType(Type leftType, Symbol lexicallyScopedIdentifier)
    {
        return GetPropertyOfType(leftType, lexicallyScopedIdentifier.Name);
    }

    // checker.go:11463
    internal bool CheckPrivateIdentifierPropertyAccess(Type leftType, Node right, Symbol? lexicallyScopedIdentifier)
    {
        // Either the identifier could not be looked up in the lexical scope OR the lexically scoped identifier did not exist on the type.
        // Find a private identifier with the same description on the type.
        var properties = GetPropertiesOfType(leftType);
        Symbol? propertyOnType = null;
        foreach (var symbol in properties)
        {
            var decl = symbol.ValueDeclaration;
            if (decl != null && decl.Name() != null && NodeTests.IsPrivateIdentifier(decl.Name()!) && decl.Name()!.Text() == right.Text())
            {
                propertyOnType = symbol;
                break;
            }
        }
        var diagName = global::Ts.Scanner.Scanner.DeclarationNameToString(right);
        if (propertyOnType != null)
        {
            var typeValueDecl = propertyOnType.ValueDeclaration!;
            var typeClass = AstUtilities.GetContainingClass(typeValueDecl);
            // We found a private identifier property with the same description.
            // Either:
            // - There is a lexically scoped private identifier AND it shadows the one we found on the type.
            // - It is an attempt to access the private identifier outside of the class.
            if (lexicallyScopedIdentifier != null && lexicallyScopedIdentifier.ValueDeclaration != null)
            {
                var lexicalValueDecl = lexicallyScopedIdentifier.ValueDeclaration;
                var lexicalClass = AstUtilities.GetContainingClass(lexicalValueDecl);
                if (AstUtilities.FindAncestor(lexicalClass, n => typeClass == n) != null)
                {
                    var diagnostic = Error(right, Diag.The_property_0_cannot_be_accessed_on_type_1_within_this_class_because_it_is_shadowed_by_another_private_identifier_with_the_same_spelling, diagName, TypeToString(leftType));
                    diagnostic.AddRelatedInfo(CreateDiagnosticForNode(lexicalValueDecl, Diag.The_shadowing_declaration_of_0_is_defined_here, diagName));
                    diagnostic.AddRelatedInfo(CreateDiagnosticForNode(typeValueDecl, Diag.The_declaration_of_0_that_you_probably_intended_to_use_is_defined_here, diagName));
                    return true;
                }
            }
            Error(right, Diag.Property_0_is_not_accessible_outside_class_1_because_it_has_a_private_identifier, diagName, SymbolToString(typeClass!.Symbol()));
            return true;
        }
        return false;
    }

    // checker.go:11499
    internal void ReportNonexistentProperty(Node propNode, Type containingType, bool isUncheckedJS)
    {
        var key = new NonExistentPropertyKey(propNode, containingType, isUncheckedJS);
        if (nonExistentProperties.Has(key))
        {
            return;
        }
        nonExistentProperties.Add(key);
        var links = nodeLinks.Get(propNode);
        if ((links.flags & NodeCheckFlags.TypeChecked) != 0)
        {
            return; // error already made/in progress
        }
        links.flags |= NodeCheckFlags.TypeChecked;
        if (AstUtilities.IsJSDocNameReferenceContext(propNode))
        {
            return;
        }
        Diagnostic? diagnostic = null;
        if (!NodeTests.IsPrivateIdentifier(propNode) && (containingType.flags & TypeFlags.Union) != 0 && (containingType.flags & TypeFlags.Primitive) == 0)
        {
            foreach (var subtype in containingType.Types())
            {
                if (GetPropertyOfType(subtype, propNode.Text()) == null && GetApplicableIndexInfoForName(subtype, propNode.Text()) == null)
                {
                    diagnostic = NewDiagnosticChainForNode(diagnostic, propNode, Diag.Property_0_does_not_exist_on_type_1, global::Ts.Scanner.Scanner.DeclarationNameToString(propNode), TypeToString(subtype));
                    break;
                }
            }
        }
        if (TypeHasStaticProperty(propNode.Text(), containingType))
        {
            var propName = global::Ts.Scanner.Scanner.DeclarationNameToString(propNode);
            var typeName = TypeToString(containingType);
            diagnostic = NewDiagnosticChainForNode(diagnostic, propNode, Diag.Property_0_does_not_exist_on_type_1_Did_you_mean_to_access_the_static_member_2_instead, propName, typeName, typeName + "." + propName);
        }
        else
        {
            var promisedType = GetPromisedTypeOfPromise(containingType);
            if (promisedType != null && GetPropertyOfType(promisedType, propNode.Text()) != null)
            {
                diagnostic = NewDiagnosticChainForNode(diagnostic, propNode, Diag.Property_0_does_not_exist_on_type_1, global::Ts.Scanner.Scanner.DeclarationNameToString(propNode), TypeToString(containingType));
                diagnostic.AddRelatedInfo(NewDiagnosticForNode(propNode, Diag.Did_you_forget_to_use_await));
            }
            else
            {
                var missingProperty = global::Ts.Scanner.Scanner.DeclarationNameToString(propNode);
                var container = TypeToString(containingType);
                var libSuggestion = GetSuggestedLibForNonExistentProperty(missingProperty, containingType);
                if (libSuggestion != "")
                {
                    diagnostic = NewDiagnosticChainForNode(diagnostic, propNode, Diag.Property_0_does_not_exist_on_type_1_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_2_or_later, missingProperty, container, libSuggestion);
                }
                else
                {
                    var suggestion = GetSuggestedSymbolForNonexistentProperty(propNode, containingType);
                    if (suggestion != null)
                    {
                        var suggestedName = AstUtilities.SymbolName(suggestion);
                        var message = CoreUtil.IfElse(isUncheckedJS, Diag.Property_0_may_not_exist_on_type_1_Did_you_mean_2, Diag.Property_0_does_not_exist_on_type_1_Did_you_mean_2);
                        diagnostic = NewDiagnosticChainForNode(diagnostic, propNode, message, missingProperty, container, suggestedName);
                        if (suggestion.ValueDeclaration != null)
                        {
                            diagnostic.AddRelatedInfo(NewDiagnosticForNode(suggestion.ValueDeclaration, Diag.X_0_is_declared_here, suggestedName));
                        }
                    }
                    else
                    {
                        diagnostic = ElaborateNeverIntersection(diagnostic, propNode, containingType);
                        global::Ts.Diagnostics.Message message;
                        if (ContainerSeemsToBeEmptyDomElement(containingType))
                        {
                            message = Diag.Property_0_does_not_exist_on_type_1_Try_changing_the_lib_compiler_option_to_include_dom;
                        }
                        else
                        {
                            message = Diag.Property_0_does_not_exist_on_type_1;
                        }
                        diagnostic = NewDiagnosticChainForNode(diagnostic, propNode, message, missingProperty, container);
                    }
                }
            }
        }
        AddErrorOrSuggestion(!isUncheckedJS || diagnostic.Code() != Diag.Property_0_may_not_exist_on_type_1_Did_you_mean_2.Code, diagnostic);
    }

    // checker.go:11563-11575 — lib-feature suggestion for a non-existent property (feature table in
    // Checker.Utilities.FeatureMap.cs).
    internal string GetSuggestedLibForNonExistentProperty(string missingProperty, Type containingType)
    {
        var container = GetApparentType(containingType).symbol;
        if (container != null)
        {
            var featureMap = GetFeatureMap();
            if (featureMap.TryGetValue(container.Name, out var typeFeatures))
            {
                foreach (var entry in typeFeatures)
                {
                    if (System.Array.IndexOf(entry.Props, missingProperty) >= 0)
                    {
                        return entry.Lib;
                    }
                }
            }
        }
        return "";
    }

    // checker.go:11580
    internal Symbol? GetSuggestedSymbolForNonexistentProperty(Node name, Type containingType)
    {
        var props = GetPropertiesOfType(containingType);
        var parent = name.Parent!;
        if (NodeTests.IsPropertyAccessExpression(parent))
        {
            props = CoreUtil.Filter(props, prop => IsValidPropertyAccessForCompletions(parent, containingType, prop)).OrEmpty();
        }
        return GetSpellingSuggestionForName(name.Text(), props, SymbolFlags.Value);
    }

    /**
     * Checks if an existing property access is valid for completions purposes.
     * @param node a property access-like node where we want to check if we can access a property.
     * This node does not need to be an access of the property we are checking.
     * e.g. in completions, this node will often be an incomplete property access node, as in `foo.`.
     * Besides providing a location (i.e. scope) used to check property accessibility, we use this node for
     * computing whether this is a `super` property access.
     * @param type the type whose property we are checking.
     * @param property the accessed property's symbol.
     */
    // checker.go:11609
    public bool IsValidPropertyAccessForCompletions(Node node, Type t, Symbol property)
    {
        return IsPropertyAccessible(node, NodeTests.IsPropertyAccessExpression(node) && node.Expression()!.Kind == SyntaxKind.SuperKeyword, false /*isWrite*/, t, property);
        // Previously we validated the 'this' type of methods but this adversely affected performance. See #31377 for more context.
    }

    /**
     * Checks if a property can be accessed in a location.
     * The location is given by the `node` parameter.
     * The node does not need to be a property access.
     * @param node location where to check property accessibility
     * @param isSuper whether to consider this a `super` property access, e.g. `super.foo`.
     * @param isWrite whether this is a write access, e.g. `++foo.x`.
     * @param containingType type where the property comes from.
     * @param property property symbol.
     */
    // checker.go:11612
    public bool IsPropertyAccessible(Node node, bool isSuper, bool isWrite, Type containingType, Symbol property)
    {
        // Short-circuiting for improved performance.
        if (IsTypeAny(containingType))
        {
            return true;
        }
        // A #private property access in an optional chain is an error dealt with by the parser.
        // The checker does not check for it, so we need to do our own check here.
        if (property.ValueDeclaration != null && AstUtilities.IsPrivateIdentifierClassElementDeclaration(property.ValueDeclaration))
        {
            var declClass = AstUtilities.GetContainingClass(property.ValueDeclaration);
            return !AstUtilities.IsOptionalChain(node) && AstUtilities.IsNodeDescendantOf(node, declClass);
        }
        return CheckPropertyAccessibilityAtLocation(node, isSuper, isWrite, containingType, property, null);
    }

    // checker.go:11623
    internal bool ContainerSeemsToBeEmptyDomElement(Type containingType)
    {
        // Go: slices.Contains(c.compilerOptions.Lib, "lib.dom.d.ts") — Contains on a nil slice is false.
        return !(compilerOptions.Lib != null && System.Array.IndexOf(compilerOptions.Lib, "lib.dom.d.ts") >= 0) &&
            EveryContainedType(containingType, HasCommonDomTypeName) &&
            IsEmptyObjectType(containingType);
    }

    // checker.go:11627 (free func hasCommonDomTypeName)
    internal static bool HasCommonDomTypeName(Type t)
    {
        if (t.symbol == null)
        {
            return false;
        }
        var name = t.symbol.Name;
        return name == "EventTarget" || name == "Node" || name == "Element" ||
            name.StartsWith("HTML", System.StringComparison.Ordinal) && name.EndsWith("Element", System.StringComparison.Ordinal);
    }

    // checker.go:11678
    internal void CheckPropertyNotUsedBeforeDeclaration(Symbol prop, Node node, Node right)
    {
        var valueDeclaration = prop.ValueDeclaration;
        if (valueDeclaration == null || AstUtilities.GetSourceFileOfNode(node)!.IsDeclarationFile)
        {
            return;
        }
        Diagnostic? diagnostic = null;
        var declarationName = right.Text();
        if (IsInPropertyInitializerOrClassStaticBlock(node, false /*ignoreArrowFunctions*/) &&
            !IsOptionalPropertyDeclaration(valueDeclaration) &&
            !(AstUtilities.IsAccessExpression(node) && AstUtilities.IsAccessExpression(node.Expression()!)) &&
            !IsBlockScopedNameDeclaredBeforeUse(valueDeclaration, right) &&
            !(NodeTests.IsMethodDeclaration(valueDeclaration) && (GetCombinedModifierFlagsCached(valueDeclaration) & ModifierFlags.Static) != 0) &&
            (compilerOptions.GetUseDefineForClassFields() || !IsPropertyDeclaredInAncestorClass(prop)))
        {
            diagnostic = Error(right, Diag.Property_0_is_used_before_its_initialization, declarationName);
        }
        else if (NodeTests.IsClassDeclaration(valueDeclaration) && !NodeTests.IsTypeReferenceNode(node.Parent!) && (valueDeclaration.Flags & NodeFlags.Ambient) == 0 && !IsBlockScopedNameDeclaredBeforeUse(valueDeclaration, right))
        {
            diagnostic = Error(right, Diag.Class_0_used_before_its_declaration, declarationName);
        }
        if (diagnostic != null)
        {
            diagnostic.AddRelatedInfo(NewDiagnosticForNode(valueDeclaration, Diag.X_0_is_declared_here, declarationName));
        }
    }

    // checker.go:11700
    internal bool IsOptionalPropertyDeclaration(Node node)
    {
        return NodeTests.IsPropertyDeclaration(node) && !AstUtilities.HasAccessorModifier(node) && ParserStatementsAstGaps.IsQuestionToken(node.PostfixToken());
    }

    // checker.go:11704
    internal bool IsPropertyDeclaredInAncestorClass(Symbol prop)
    {
        if ((prop.Parent!.Flags & SymbolFlags.Class) != 0)
        {
            var baseTypes = GetBaseTypes(GetDeclaredTypeOfSymbol(prop.Parent));
            if (baseTypes.Length != 0)
            {
                var superProperty = GetPropertyOfType(baseTypes[0], prop.Name);
                return superProperty != null && superProperty.ValueDeclaration != null;
            }
        }
        return false;
    }

    /**
     * Check whether the requested property access is valid.
     * Returns true if node is a valid property access, and false otherwise.
     * @param node The node to be checked.
     * @param isSuper True if the access is from `super.`.
     * @param type The type of the object whose property is being accessed. (Not the type of the property.)
     * @param prop The symbol for the property being accessed.
     */
    // checker.go:11722
    internal bool CheckPropertyAccessibility(Node node, bool isSuper, bool writing, Type t, Symbol prop)
    {
        return CheckPropertyAccessibilityEx(node, isSuper, writing, t, prop, true /*reportError*/);
    }

    // checker.go:11726
    internal bool CheckPropertyAccessibilityEx(Node node, bool isSuper, bool writing, Type t, Symbol prop, bool reportError)
    {
        Node? errorNode = null;
        if (reportError)
        {
            switch (node.Kind)
            {
                case SyntaxKind.PropertyAccessExpression:
                    errorNode = node.AsPropertyAccessExpression().Name();
                    break;
                case SyntaxKind.QualifiedName:
                    errorNode = node.AsQualifiedName().Right;
                    break;
                case SyntaxKind.ImportType:
                    errorNode = node;
                    break;
                case SyntaxKind.BindingElement:
                    errorNode = GetBindingElementPropertyName(node);
                    break;
                default:
                    errorNode = node.Name();
                    break;
            }
        }
        return CheckPropertyAccessibilityAtLocation(node, isSuper, writing, t, prop, errorNode);
    }

    /**
     * Check whether the requested property can be accessed at the requested location.
     * Returns true if node is a valid property access, and false otherwise.
     */
    // checker.go:11755
    internal bool CheckPropertyAccessibilityAtLocation(Node location, bool isSuper, bool writing, Type containingType, Symbol prop, Node? errorNode)
    {
        var flags = GetDeclarationModifierFlagsFromSymbolEx(prop, writing);
        if (isSuper)
        {
            // TS 1.0 spec (April 2014): 4.8.2
            // - In a constructor, instance member function, instance member accessor, or
            //   instance member variable initializer where this references a derived class instance,
            //   a super property access is permitted and must specify a public instance member function of the base class.
            // - In a static member function or static member accessor
            //   where this references the constructor function object of a derived class,
            //   a super property access is permitted and must specify a public static member function of the base class.
            if ((flags & ModifierFlags.Abstract) != 0)
            {
                // A method cannot be accessed in a super property access if the method is abstract.
                // This error could mask a private property access error. But, a member
                // cannot simultaneously be private and abstract, so this will trigger an
                // additional error elsewhere.
                if (errorNode != null)
                {
                    Error(errorNode, Diag.Abstract_method_0_in_class_1_cannot_be_accessed_via_super_expression, SymbolToString(prop), TypeToString(GetDeclaringClass(prop)!));
                }
                return false;
            }
            // A class field cannot be accessed via super.* from a derived class.
            // This is true for both [[Set]] (old) and [[Define]] (ES spec) semantics.
            if ((flags & ModifierFlags.Static) == 0 && CoreUtil.Some(prop.Declarations, IsClassInstanceProperty))
            {
                if (errorNode != null)
                {
                    Error(errorNode, Diag.Class_field_0_defined_by_the_parent_class_is_not_accessible_in_the_child_class_via_super, SymbolToString(prop));
                }
                return false;
            }
        }
        // Referencing abstract properties within their own constructors is not allowed
        if ((flags & ModifierFlags.Abstract) != 0 && SymbolHasNonMethodDeclaration(prop) && (IsThisProperty(location) ||
            IsThisInitializedObjectBindingExpression(location) ||
            NodeTests.IsObjectBindingPattern(location.Parent!) && IsThisInitializedDeclaration(location.Parent!.Parent)))
        {
            var parentSymbol = GetParentOfSymbol(prop);
            if (parentSymbol != null && (parentSymbol.Flags & SymbolFlags.Class) != 0 && IsNodeUsedDuringClassInitialization(location))
            {
                if (errorNode != null)
                {
                    Error(errorNode, Diag.Abstract_property_0_in_class_1_cannot_be_accessed_in_the_constructor, SymbolToString(prop), SymbolToString(parentSymbol));
                }
                return false;
            }
        }
        // Public properties are otherwise accessible.
        if ((flags & ModifierFlags.NonPublicAccessibilityModifier) == 0)
        {
            return true;
        }
        // Property is known to be private or protected at this point
        // Private property is accessible if the property is within the declaring class
        if ((flags & ModifierFlags.Private) != 0)
        {
            var declaringClassDeclaration = AstUtilities.GetClassLikeDeclarationOfSymbol(GetParentOfSymbol(prop)!);
            if (!IsNodeWithinClass(location, declaringClassDeclaration))
            {
                if (errorNode != null)
                {
                    Error(errorNode, Diag.Property_0_is_private_and_only_accessible_within_class_1, SymbolToString(prop), TypeToString(GetDeclaringClass(prop)!));
                }
                return false;
            }
            return true;
        }
        // Property is known to be protected at this point
        // All protected properties of a supertype are accessible in a super access
        if (isSuper)
        {
            return true;
        }
        // Find the first enclosing class that has the declaring classes of the protected constituents
        // of the property as base classes
        Type? enclosingClass = null;
        var container = AstUtilities.GetContainingClass(location);
        while (container != null)
        {
            var cls = GetDeclaredTypeOfSymbol(GetSymbolOfDeclaration(container)!);
            if (IsClassDerivedFromDeclaringClasses(cls, prop, writing))
            {
                enclosingClass = cls;
                break;
            }
            container = AstUtilities.GetContainingClass(container);
        }
        // A protected property is accessible if the property is within the declaring class or classes derived from it
        if (enclosingClass == null)
        {
            // allow PropertyAccessibility if context is in function with this parameter
            // static member access is disallowed
            var cls = GetEnclosingClassFromThisParameter(location);
            if (cls != null && IsClassDerivedFromDeclaringClasses(cls, prop, writing))
            {
                enclosingClass = cls;
            }
            if ((flags & ModifierFlags.Static) != 0 || enclosingClass == null)
            {
                if (errorNode != null)
                {
                    var cls2 = GetDeclaringClass(prop);
                    if (cls2 == null)
                    {
                        cls2 = containingType;
                    }
                    Error(errorNode, Diag.Property_0_is_protected_and_only_accessible_within_class_1_and_its_subclasses, SymbolToString(prop), TypeToString(cls2));
                }
                return false;
            }
        }
        // No further restrictions for static properties
        if ((flags & ModifierFlags.Static) != 0)
        {
            return true;
        }
        if ((containingType.flags & TypeFlags.TypeParameter) != 0)
        {
            // get the original type -- represented as the type constraint of the 'this' type
            if (containingType.AsTypeParameter().isThisType)
            {
                containingType = GetConstraintOfTypeParameter(containingType)!;
            }
            else
            {
                containingType = GetBaseConstraintOfType(containingType)!;
            }
        }
        if (containingType == null || !HasBaseType(containingType, enclosingClass))
        {
            if (errorNode != null && containingType != null)
            {
                Error(errorNode, Diag.Property_0_is_protected_and_only_accessible_through_an_instance_of_class_1_This_is_an_instance_of_class_2, SymbolToString(prop), TypeToString(enclosingClass), TypeToString(containingType));
            }
            return false;
        }
        return true;
    }

    // checker.go:11869
    internal bool SymbolHasNonMethodDeclaration(Symbol symbol)
    {
        return ForEachProperty(symbol, prop => (prop.Flags & SymbolFlags.Method) == 0);
    }

    // checker.go:11875 — Invoke the callback for each underlying property symbol of the given symbol
    // and return the first value that isn't undefined.
    internal bool ForEachProperty(Symbol prop, System.Func<Symbol, bool> callback)
    {
        if ((prop.CheckFlags & CheckFlags.Synthetic) == 0)
        {
            return callback(prop);
        }
        // map-iter: 序无关 — short-circuit union-constituent property scan; result independent of order.
        foreach (var t in valueSymbolLinks.Get(prop).containingType!.Types())
        {
            var p = GetPropertyOfType(t, prop.Name);
            if (p != null && ForEachProperty(p, callback))
            {
                return true;
            }
        }
        return false;
    }

    // checker.go:11918
    internal bool IsNodeUsedDuringClassInitialization(Node node)
    {
        return AstUtilities.FindAncestorOrQuit(node, element =>
        {
            if (NodeTests.IsConstructorDeclaration(element) && AstUtilities.NodeIsPresent(element.Body()) || NodeTests.IsPropertyDeclaration(element))
            {
                return FindAncestorResult.True;
            }
            else if (AstUtilities.IsClassLike(element) || AstUtilities.IsFunctionLikeDeclaration(element))
            {
                return FindAncestorResult.Quit;
            }
            return FindAncestorResult.False;
        }) != null;
    }

    // checker.go:11929
    internal bool IsNodeWithinClass(Node node, Node? classDeclaration)
    {
        return ForEachEnclosingClass(node, n => n == classDeclaration);
    }

    // checker.go:11933
    internal bool ForEachEnclosingClass(Node node, System.Func<Node, bool> callback)
    {
        var containingClass = AstUtilities.GetContainingClass(node);
        while (containingClass != null)
        {
            var result = callback(containingClass);
            if (result)
            {
                return true;
            }
            containingClass = AstUtilities.GetContainingClass(containingClass);
        }
        return false;
    }

    // checker.go:11947 — Return true if the given class derives from each of the declaring classes of
    // the protected constituents of the given property.
    internal bool IsClassDerivedFromDeclaringClasses(Type checkClass, Symbol prop, bool writing)
    {
        return !ForEachProperty(prop, p =>
        {
            if ((GetDeclarationModifierFlagsFromSymbolEx(p, writing) & ModifierFlags.Protected) != 0)
            {
                return !HasBaseType(checkClass, GetDeclaringClass(p));
            }
            return false;
        });
    }

    // checker.go:11956
    internal Type? GetEnclosingClassFromThisParameter(Node node)
    {
        // 'this' type for a node comes from, in priority order...
        // 1. The type of a syntactic 'this' parameter in the enclosing function scope
        var thisParameter = GetThisParameterFromNodeContext(node);
        Type? thisType = null;
        if (thisParameter != null && thisParameter.Type() != null)
        {
            thisType = GetTypeFromTypeNode(thisParameter.Type()!);
        }
        if (thisType != null)
        {
            // 2. The constraint of a type parameter used for an explicit 'this' parameter
            if ((thisType.flags & TypeFlags.TypeParameter) != 0)
            {
                thisType = GetConstraintOfTypeParameter(thisType);
            }
        }
        else
        {
            // 3. The 'this' parameter of a contextual type
            var thisContainer = AstUtilities.GetThisContainer(node, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
            if (thisContainer != null && AstUtilities.IsFunctionLike(thisContainer))
            {
                thisType = GetContextualThisParameterType(thisContainer);
            }
        }
        if (thisType != null && (thisType.objectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0)
        {
            return GetTargetType(thisType);
        }
        return null;
    }

    // checker.go:11982
    internal static Node? GetThisParameterFromNodeContext(Node node)
    {
        var thisContainer = AstUtilities.GetThisContainer(node, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
        if (thisContainer != null && AstUtilities.IsFunctionLike(thisContainer))
        {
            return AstUtilities.GetThisParameter(thisContainer);
        }
        return null;
    }

    // checker.go:27141
    internal bool IsSelfTypeAccess(Node name, Symbol? parent)
    {
        return name.Kind == SyntaxKind.ThisKeyword || parent != null && AstUtilities.IsEntityNameExpression(name) && parent == GetResolvedSymbol(GetFirstIdentifier(name));
    }

    // checker.go:27145
    internal bool IsAssignmentToReadonlyEntity(Node expr, Symbol symbol, AssignmentKind assignmentKind)
    {
        if (assignmentKind == AssignmentKind.None)
        {
            // no assignment means it doesn't matter whether the entity is readonly
            return false;
        }
        if (AstUtilities.IsAccessExpression(expr))
        {
            var node = AstUtilities.SkipParentheses(expr.Expression()!);
            if (NodeTests.IsIdentifier(node))
            {
                var expressionSymbol = GetResolvedSymbol(node);
                // CommonJS module.exports is never readonly
                if ((expressionSymbol.Flags & SymbolFlags.ModuleExports) != 0)
                {
                    return false;
                }
            }
        }
        if (IsReadonlySymbol(symbol))
        {
            // Allow assignments to readonly properties within constructors of the same class declaration.
            if ((symbol.Flags & SymbolFlags.Property) != 0 && AstUtilities.IsAccessExpression(expr) && expr.Expression()!.Kind == SyntaxKind.ThisKeyword)
            {
                // Look for if this is the constructor for the class that `symbol` is a property of.
                var ctor = GetControlFlowContainer(expr);
                if (ctor == null || !NodeTests.IsConstructorDeclaration(ctor))
                {
                    return true;
                }
                if (symbol.ValueDeclaration != null)
                {
                    bool isAssignmentDeclaration = NodeTests.IsBinaryExpression(symbol.ValueDeclaration);
                    bool isLocalPropertyDeclaration = ctor.Parent == symbol.ValueDeclaration.Parent;
                    bool isLocalParameterProperty = ctor == symbol.ValueDeclaration.Parent;
                    bool isLocalThisPropertyAssignment = isAssignmentDeclaration && symbol.Parent!.ValueDeclaration == ctor.Parent;
                    bool isLocalThisPropertyAssignmentConstructorFunction = isAssignmentDeclaration && symbol.Parent!.ValueDeclaration == ctor;
                    bool isWriteableSymbol = isLocalPropertyDeclaration || isLocalParameterProperty || isLocalThisPropertyAssignment || isLocalThisPropertyAssignmentConstructorFunction;
                    return !isWriteableSymbol;
                }
            }
            return true;
        }
        if (AstUtilities.IsAccessExpression(expr))
        {
            // references through namespace import should be readonly
            var node = AstUtilities.SkipParentheses(expr.Expression()!);
            if (NodeTests.IsIdentifier(node))
            {
                var expressionSymbol = GetResolvedSymbol(node);
                if ((expressionSymbol.Flags & SymbolFlags.Alias) != 0)
                {
                    var declaration = GetDeclarationOfAliasSymbol(expressionSymbol);
                    return declaration != null && NodeTests.IsNamespaceImport(declaration);
                }
            }
        }
        return false;
    }

    // checker.go:27194
    internal bool IsThisPropertyAccessInConstructor(Node node, Symbol prop)
    {
        Node? constructor = null;
        var (kind, location) = IsConstructorDeclaredThisProperty(prop);
        if (kind == ThisAssignmentDeclarationKind.Constructor)
        {
            constructor = location;
        }
        else if (IsThisProperty(node) && IsAutoTypedProperty(prop))
        {
            constructor = GetDeclaringConstructor(prop);
        }
        return AstUtilities.GetThisContainer(node, true /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/) == constructor;
    }

    // checker.go:27204
    internal bool IsAutoTypedProperty(Symbol symbol)
    {
        // A property is auto-typed when its declaration has no type annotation or initializer and we're in
        // noImplicitAny mode or a .js file.
        var declaration = symbol.ValueDeclaration;
        return declaration != null && NodeTests.IsPropertyDeclaration(declaration) && declaration.Type() == null && declaration.Initializer() == null && noImplicitAny;
    }

    // checker.go:27211
    internal Node? GetDeclaringConstructor(Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations!)
        {
            var container = AstUtilities.GetThisContainer(declaration, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
            if (container != null && NodeTests.IsConstructorDeclaration(container))
            {
                return container;
            }
        }
        return null;
    }

    // checker.go:4939
    internal bool IsPropertyWithoutInitializer(Node node)
    {
        return NodeTests.IsPropertyDeclaration(node) && !AstUtilities.HasAbstractModifier(node) && !IsExclamationToken(node.PostfixToken()) && node.Initializer() == null;
    }

    // checker.go:27572
    internal void MarkPropertyAsReferenced(Symbol prop, Node? nodeForCheckWriteOnly, bool isSelfTypeAccess)
    {
        if ((prop.Flags & SymbolFlags.ClassMember) == 0 || prop.ValueDeclaration == null)
        {
            return;
        }
        bool hasPrivateModifier = HasModifier(prop.ValueDeclaration, ModifierFlags.Private);
        bool hasPrivateIdentifier = prop.ValueDeclaration.Name() != null && NodeTests.IsPrivateIdentifier(prop.ValueDeclaration.Name()!);
        if (!hasPrivateModifier && !hasPrivateIdentifier)
        {
            return;
        }
        if (nodeForCheckWriteOnly != null && AstUtilities.IsWriteOnlyAccess(nodeForCheckWriteOnly) && (prop.Flags & SymbolFlags.SetAccessor) == 0)
        {
            return;
        }
        if (isSelfTypeAccess)
        {
            // Find any FunctionLikeDeclaration because those create a new 'this' binding. But this should only matter for methods (or getters/setters).
            var containingMethod = AstUtilities.FindAncestor(nodeForCheckWriteOnly, AstUtilities.IsFunctionLikeDeclaration);
            if (containingMethod != null && containingMethod.Symbol() == prop)
            {
                return;
            }
        }
        var target = prop;
        if ((prop.CheckFlags & CheckFlags.Instantiated) != 0)
        {
            target = valueSymbolLinks.Get(prop).target!;
        }
        symbolReferenceLinks.Get(target).referenceKinds |= SymbolFlags.All;
    }

    // checker.go/utilities.go:971
    internal static Node? GetContainingClassExcludingClassDecorators(Node node)
    {
        var decorator = AstUtilities.FindAncestorOrQuit(node.Parent, n =>
        {
            if (AstUtilities.IsClassLike(n))
            {
                return FindAncestorResult.Quit;
            }
            if (NodeTests.IsDecorator(n))
            {
                return FindAncestorResult.True;
            }
            return FindAncestorResult.False;
        });
        if (decorator != null && AstUtilities.IsClassLike(decorator.Parent!))
        {
            return AstUtilities.GetContainingClass(decorator.Parent!);
        }
        if (decorator != null)
        {
            return AstUtilities.GetContainingClass(decorator);
        }
        return AstUtilities.GetContainingClass(node);
    }

    // utilities.go:694
    internal static ModifierFlags GetDeclarationModifierFlagsFromSymbolEx(Symbol s, bool isWrite)
    {
        if (s.ValueDeclaration != null)
        {
            Node? declaration = null;
            if (isWrite)
            {
                declaration = CoreUtil.Find(s.Declarations, NodeTests.IsSetAccessorDeclaration);
            }
            if (declaration == null && (s.Flags & SymbolFlags.GetAccessor) != 0)
            {
                declaration = CoreUtil.Find(s.Declarations, NodeTests.IsGetAccessorDeclaration);
            }
            if (declaration == null)
            {
                declaration = s.ValueDeclaration;
            }
            var flags = AstUtilities.GetCombinedModifierFlags(declaration);
            if (s.Parent != null && (s.Parent.Flags & SymbolFlags.Class) != 0)
            {
                return flags;
            }
            return flags & ~ModifierFlags.AccessibilityModifier;
        }
        if ((s.CheckFlags & CheckFlags.Synthetic) != 0)
        {
            ModifierFlags accessModifier;
            if ((s.CheckFlags & CheckFlags.ContainsPrivate) != 0)
            {
                accessModifier = ModifierFlags.Private;
            }
            else if ((s.CheckFlags & CheckFlags.ContainsPublic) != 0)
            {
                accessModifier = ModifierFlags.Public;
            }
            else
            {
                accessModifier = ModifierFlags.Protected;
            }
            ModifierFlags staticModifier = ModifierFlags.None;
            if ((s.CheckFlags & CheckFlags.ContainsStatic) != 0)
            {
                staticModifier = ModifierFlags.Static;
            }
            return accessModifier | staticModifier;
        }
        if ((s.Flags & SymbolFlags.Prototype) != 0)
        {
            return ModifierFlags.Public | ModifierFlags.Static;
        }
        return ModifierFlags.None;
    }

    // utilities.go:994
    internal static bool IsClassInstanceProperty(Node node)
    {
        if (AstUtilities.IsInJSFile(node) && IsExpandoPropertyDeclaration(node))
        {
            var left = node.AsBinaryExpression().Left!;
            return (!AstUtilities.IsBindableStaticAccessExpression(left, false /*excludeThisKeyword*/) || !AstUtilities.IsPrototypeAccess(left.Expression()!)) &&
                !AstUtilities.IsBindableStaticNameExpression(left, true /*excludeThisKeyword*/);
        }
        return node.Parent != null && AstUtilities.IsClassLike(node.Parent) && NodeTests.IsPropertyDeclaration(node) && !AstUtilities.HasAccessorModifier(node);
    }

    // utilities.go:1003
    internal static bool IsThisInitializedObjectBindingExpression(Node? node)
    {
        return node != null && (NodeTests.IsShorthandPropertyAssignment(node) || NodeTests.IsPropertyAssignment(node)) && NodeTests.IsBinaryExpression(node.Parent!.Parent!) &&
            node.Parent!.Parent!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken &&
            node.Parent!.Parent!.AsBinaryExpression().Right!.Kind == SyntaxKind.ThisKeyword;
    }

    // utilities.go:1009
    internal static bool IsThisInitializedDeclaration(Node? node)
    {
        return node != null && NodeTests.IsVariableDeclaration(node) && node.Initializer() != null && node.Initializer()!.Kind == SyntaxKind.ThisKeyword;
    }

    // utilities.go:272
    internal static bool IsExclamationToken(Node? node)
    {
        return node != null && node.Kind == SyntaxKind.ExclamationToken;
    }

    // utilities.go:1730
    internal bool IsJSLiteralType(Type t)
    {
        if (noImplicitAny)
        {
            return false;
            // Flag is meaningless under `noImplicitAny` mode
        }
        if ((t.objectFlags & ObjectFlags.JSLiteral) != 0)
        {
            return true;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return CoreUtil.Every(t.AsUnionType().types, IsJSLiteralType);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Some(t.AsIntersectionType().types, IsJSLiteralType);
        }
        if ((t.flags & TypeFlags.Instantiable) != 0)
        {
            var constraint = GetResolvedBaseConstraint(t, null);
            return constraint != t && IsJSLiteralType(constraint);
        }
        return false;
    }

    // utilities.go:900
    internal static bool IsThisProperty(Node node)
    {
        return (NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsElementAccessExpression(node)) && node.Expression()!.Kind == SyntaxKind.ThisKeyword;
    }

    // ===== non-null cluster (checker.go:7386-7459) =====

    // checker.go:7386
    internal Type CheckNonNullType(Type t, Node node)
    {
        return CheckNonNullTypeWithReporter(t, node, ReportObjectPossiblyNullOrUndefinedError);
    }

    // checker.go:7390
    internal Type CheckNonNullTypeWithReporter(Type t, Node node, global::System.Action<Node, TypeFacts> reportError)
    {
        if (strictNullChecks && (t.flags & TypeFlags.Unknown) != 0)
        {
            if (AstUtilities.IsEntityNameExpression(node))
            {
                var nodeText = EntityNameToString(node);
                if (nodeText.Length < 100)
                {
                    Error(node, Diag.X_0_is_of_type_unknown, nodeText);
                    return errorType!;
                }
            }
            Error(node, Diag.Object_is_of_type_unknown);
            return errorType!;
        }
        var facts = GetTypeFacts(t, TypeFacts.IsUndefinedOrNull);
        if ((facts & TypeFacts.IsUndefinedOrNull) != 0)
        {
            reportError(node, facts);
            var nonNullable = GetNonNullableType(t);
            if ((nonNullable.flags & (TypeFlags.Nullable | TypeFlags.Never)) != 0)
            {
                return errorType!;
            }
            return nonNullable;
        }
        return t;
    }

    // checker.go:7414
    internal Type CheckNonNullNonVoidType(Type t, Node node)
    {
        var nonNullType = CheckNonNullType(t, node);
        if ((nonNullType.flags & TypeFlags.Void) != 0)
        {
            if (AstUtilities.IsEntityNameExpression(node))
            {
                var nodeText = EntityNameToString(node);
                if (NodeTests.IsIdentifier(node) && nodeText == "undefined")
                {
                    Error(node, Diag.The_value_0_cannot_be_used_here, nodeText);
                    return nonNullType;
                }
                if (nodeText.Length < 100)
                {
                    Error(node, Diag.X_0_is_possibly_undefined, nodeText);
                    return nonNullType;
                }
            }
            Error(node, Diag.Object_is_possibly_undefined);
        }
        return nonNullType;
    }

    // checker.go:7433
    internal void ReportObjectPossiblyNullOrUndefinedError(Node node, TypeFacts facts)
    {
        string nodeText = "";
        if (AstUtilities.IsEntityNameExpression(node))
        {
            nodeText = EntityNameToString(node);
        }
        if (node.Kind == SyntaxKind.NullKeyword)
        {
            Error(node, Diag.The_value_0_cannot_be_used_here, "null");
            return;
        }
        if (nodeText != "" && nodeText.Length < 100)
        {
            if (NodeTests.IsIdentifier(node) && nodeText == "undefined")
            {
                Error(node, Diag.The_value_0_cannot_be_used_here, "undefined");
                return;
            }
            Error(node, CoreUtil.IfElse((facts & TypeFacts.IsUndefined) != 0,
                CoreUtil.IfElse((facts & TypeFacts.IsNull) != 0,
                    Diag.X_0_is_possibly_null_or_undefined,
                    Diag.X_0_is_possibly_undefined),
                Diag.X_0_is_possibly_null), nodeText);
        }
        else
        {
            Error(node, CoreUtil.IfElse((facts & TypeFacts.IsUndefined) != 0,
                CoreUtil.IfElse((facts & TypeFacts.IsNull) != 0,
                    Diag.Object_is_possibly_null_or_undefined,
                    Diag.Object_is_possibly_undefined),
                Diag.Object_is_possibly_null));
        }
    }
}

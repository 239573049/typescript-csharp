// Ported from tsgo internal/checker/checker.go — object-literal checking (§4.2 partial, checkObjectLiteral
// at checker.go:13094). Companion deep leaves (getSpreadType, checkPropertyAssignment,
// checkShorthandPropertyAssignment, checkObjectLiteralMethod, tryMergeUnionOfObjectTypeAndEmptyObject,
// isValidSpreadType, checkSpreadPropOverrides, getInferenceContext, hasDefaultValue) remain stubs and are
// only reached from the spread / inferential / destructuring paths not yet exercised by the curated slice.
//
// §1.5: cross-package via global::; host classes (AstUtilities/NodeTests) direct. Nullability follows Go.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:13094-13301
    internal Type CheckObjectLiteral(Node node, CheckMode checkMode)
    {
        // Expando object literals have empty properties but filled exports
        if (node.Properties().Len() == 0 && node.Symbol() != null && (node.Symbol()!.Exports?.Count ?? 0) != 0)
        {
            var expando = NewAnonymousType(node.Symbol(), node.Symbol()!.Exports, null, null, null);
            if (AstUtilities.IsInJSFile(node) && !AstUtilities.IsInJsonFile(node))
            {
                expando.objectFlags |= ObjectFlags.JSLiteral;
            }
            return expando;
        }
        bool inDestructuringPattern = AstUtilities.IsAssignmentTarget(node);
        // Grammar checking
        CheckGrammarObjectLiteralExpression(node.AsObjectLiteralExpression(), inDestructuringPattern);
        SymbolTable? allPropertiesTable = null;
        if (strictNullChecks)
        {
            allPropertiesTable = [];
        }
        SymbolTable propertiesTable = [];
        List<Symbol> propertiesArray = [];
        var spread = emptyObjectType!;
        PushCachedContextualType(node);
        var contextualType = GetApparentTypeOfContextualType(node, ContextFlags.None);
        bool contextualTypeHasPattern = false;
        if (contextualType != null)
        {
            var pattern = patternForType.GetValueOrDefault(contextualType);
            if (pattern != null && (NodeTests.IsObjectBindingPattern(pattern) || NodeTests.IsObjectLiteralExpression(pattern)))
            {
                contextualTypeHasPattern = true;
            }
        }
        bool inConstContext = IsConstContext(node);
        CheckFlags checkFlags = CheckFlags.None;
        if (inConstContext)
        {
            checkFlags = CheckFlags.Readonly;
        }
        var objectFlags = ObjectFlags.FreshLiteral;
        bool patternWithComputedProperties = false;
        bool hasComputedStringProperty = false;
        bool hasComputedNumberProperty = false;
        bool hasComputedSymbolProperty = false;
        // Spreads may cause an early bail; ensure computed names are always checked (this is cached)
        // As otherwise they may not be checked until exports for the type at this position are retrieved,
        // which may never occur.
        foreach (var elem in node.Properties().OrEmpty())
        {
            if (elem.Name() != null && NodeTests.IsComputedPropertyName(elem.Name()!))
            {
                CheckComputedPropertyName(elem.Name()!);
            }
        }
        int offset = 0;
        Type CreateObjectLiteralType()
        {
            List<IndexInfo> indexInfos = [];
            bool isReadonly = IsConstContext(node);
            if (hasComputedStringProperty)
            {
                indexInfos.Add(GetObjectLiteralIndexInfo(isReadonly, propertiesArray.GetRange(offset, propertiesArray.Count - offset).ToArray(), stringType!));
            }
            if (hasComputedNumberProperty)
            {
                indexInfos.Add(GetObjectLiteralIndexInfo(isReadonly, propertiesArray.GetRange(offset, propertiesArray.Count - offset).ToArray(), numberType!));
            }
            if (hasComputedSymbolProperty)
            {
                indexInfos.Add(GetObjectLiteralIndexInfo(isReadonly, propertiesArray.GetRange(offset, propertiesArray.Count - offset).ToArray(), esSymbolType!));
            }
            var literal = NewAnonymousType(node.Symbol(), propertiesTable, null, null, indexInfos.ToArray());
            literal.objectFlags |= objectFlags | ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
            if (contextualType == null && AstUtilities.IsInJSFile(node) && !AstUtilities.IsInJsonFile(node))
            {
                literal.objectFlags |= ObjectFlags.JSLiteral;
            }
            if (patternWithComputedProperties)
            {
                literal.objectFlags |= ObjectFlags.ObjectLiteralPatternWithComputedProperties;
            }
            if (inDestructuringPattern)
            {
                patternForType[literal] = node;
            }
            return literal;
        }
        foreach (var memberDecl in node.Properties().OrEmpty())
        {
            var member = GetSymbolOfDeclaration(memberDecl)!;
            Type? computedNameType = null;
            if (memberDecl.Name() != null && memberDecl.Name()!.Kind == SyntaxKind.ComputedPropertyName)
            {
                computedNameType = CheckComputedPropertyName(memberDecl.Name()!);
            }
            if (NodeTests.IsPropertyAssignment(memberDecl) || NodeTests.IsShorthandPropertyAssignment(memberDecl) || AstUtilities.IsObjectLiteralMethod(memberDecl))
            {
                Type t;
                switch (memberDecl.Kind)
                {
                    case SyntaxKind.PropertyAssignment:
                        t = CheckPropertyAssignment(memberDecl, checkMode);
                        break;
                    case SyntaxKind.ShorthandPropertyAssignment:
                        t = CheckShorthandPropertyAssignment(memberDecl, inDestructuringPattern, checkMode);
                        break;
                    default:
                        t = CheckObjectLiteralMethod(memberDecl, checkMode);
                        break;
                }
                objectFlags |= t.objectFlags & ObjectFlags.PropagatingFlags;
                Type? nameType = null;
                if (computedNameType != null && IsTypeUsableAsPropertyName(computedNameType))
                {
                    nameType = computedNameType;
                }
                Symbol prop;
                if (nameType != null)
                {
                    prop = NewSymbolEx(SymbolFlags.Property | member.Flags, GetPropertyNameFromType(nameType), checkFlags | CheckFlags.Late);
                }
                else
                {
                    prop = NewSymbolEx(SymbolFlags.Property | member.Flags, member.Name, checkFlags);
                }
                var links = valueSymbolLinks.Get(prop);
                if (nameType != null)
                {
                    links.nameType = nameType;
                }
                if (inDestructuringPattern && HasDefaultValue(memberDecl))
                {
                    // If object literal is an assignment pattern and if the assignment pattern specifies a default value
                    // for the property, make the property optional.
                    prop.Flags |= SymbolFlags.Optional;
                }
                else if (contextualTypeHasPattern && (contextualType!.objectFlags & ObjectFlags.ObjectLiteralPatternWithComputedProperties) == 0)
                {
                    // If object literal is contextually typed by the implied type of a binding pattern, and if the
                    // binding pattern specifies a default value for the property, make the property optional.
                    var impliedProp = GetPropertyOfType(contextualType, member.Name);
                    if (impliedProp != null)
                    {
                        prop.Flags |= impliedProp.Flags & SymbolFlags.Optional;
                    }
                    else if (GetIndexInfoOfType(contextualType, stringType!) == null)
                    {
                        Error(memberDecl.Name(), Diag.Object_literal_may_only_specify_known_properties_and_0_does_not_exist_in_type_1, SymbolToString(member), TypeToString(contextualType));
                    }
                }
                prop.Declarations = member.Declarations;
                prop.Parent = member.Parent;
                prop.ValueDeclaration = member.ValueDeclaration;
                links.resolvedType = t;
                links.target = member;
                member = prop;
                if (allPropertiesTable != null)
                {
                    allPropertiesTable[prop.Name] = prop;
                }
                if (contextualType != null && (checkMode & CheckMode.Inferential) != 0 && (checkMode & CheckMode.SkipContextSensitive) == 0 && (NodeTests.IsPropertyAssignment(memberDecl) || NodeTests.IsMethodDeclaration(memberDecl)) && IsContextSensitive(memberDecl))
                {
                    var inferenceContext = GetInferenceContext(node);
                    // In CheckMode.Inferential we should always have an inference context
                    var inferenceNode = memberDecl;
                    if (NodeTests.IsPropertyAssignment(memberDecl))
                    {
                        inferenceNode = memberDecl.Initializer()!;
                    }
                    AddIntraExpressionInferenceSite(inferenceContext, inferenceNode, t);
                }
            }
            else if (memberDecl.Kind == SyntaxKind.SpreadAssignment)
            {
                if (propertiesArray.Count > 0)
                {
                    spread = GetSpreadType(spread, CreateObjectLiteralType(), node.Symbol(), objectFlags, inConstContext);
                    propertiesArray = [];
                    propertiesTable = [];
                    hasComputedStringProperty = false;
                    hasComputedNumberProperty = false;
                    hasComputedSymbolProperty = false;
                }
                var t = GetReducedType(CheckExpressionEx(memberDecl.Expression()!, checkMode & CheckMode.Inferential));
                if (IsValidSpreadType(t))
                {
                    var mergedType = TryMergeUnionOfObjectTypeAndEmptyObject(t, inConstContext);
                    if (allPropertiesTable != null)
                    {
                        CheckSpreadPropOverrides(mergedType, allPropertiesTable, memberDecl);
                    }
                    offset = propertiesArray.Count;
                    if (IsErrorType(spread))
                    {
                        continue;
                    }
                    spread = GetSpreadType(spread, mergedType, node.Symbol(), objectFlags, inConstContext);
                }
                else
                {
                    Error(memberDecl, Diag.Spread_types_may_only_be_created_from_object_types);
                    spread = errorType!;
                }
                continue;
            }
            else
            {
                // TypeScript 1.0 spec (April 2014)
                // A get accessor declaration is processed in the same manner as
                // an ordinary function declaration(section 6.1) with no parameters.
                // A set accessor declaration is processed in the same manner
                // as an ordinary function declaration with a single parameter and a Void return type.
                Debug.Assert(memberDecl.Kind == SyntaxKind.GetAccessor || memberDecl.Kind == SyntaxKind.SetAccessor);
                CheckNodeDeferred(memberDecl);
            }
            if (computedNameType != null && (computedNameType.flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0)
            {
                if (IsTypeAssignableTo(computedNameType, stringNumberSymbolType!))
                {
                    if (IsTypeAssignableTo(computedNameType, numberType!))
                    {
                        hasComputedNumberProperty = true;
                    }
                    else if (IsTypeAssignableTo(computedNameType, esSymbolType!))
                    {
                        hasComputedSymbolProperty = true;
                    }
                    else
                    {
                        hasComputedStringProperty = true;
                    }
                    if (inDestructuringPattern)
                    {
                        patternWithComputedProperties = true;
                    }
                }
            }
            else
            {
                propertiesTable[member.Name] = member;
            }
            propertiesArray.Add(member);
        }
        PopContextualType();
        if (IsErrorType(spread))
        {
            return errorType!;
        }
        if (spread != emptyObjectType)
        {
            if (propertiesArray.Count > 0)
            {
                spread = GetSpreadType(spread, CreateObjectLiteralType(), node.Symbol(), objectFlags, inConstContext);
                propertiesArray = [];
                propertiesTable = [];
                hasComputedStringProperty = false;
                hasComputedNumberProperty = false;
            }
            // remap the raw emptyObjectType fed in at the top into a fresh empty object literal type, unique to this use site
            return MapType(spread, t =>
            {
                if (t == emptyObjectType)
                {
                    return CreateObjectLiteralType();
                }
                return t;
            });
        }
        return CreateObjectLiteralType();
    }
}

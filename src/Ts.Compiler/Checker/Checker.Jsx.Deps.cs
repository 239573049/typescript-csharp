// Single dedup surface for the jsx wave (plan/03 §4.2 jsx). The JSX family (Checker.Jsx.cs) calls a
// number of surrounding-subsystem helpers that are not yet landed in any wave: contextual-type
// fetchers, spread-type construction, iteration types, call resolution, external-module resolution,
// a couple of JSX-runtime AST helpers, and the JSX-alias-reference marker. Each is stubbed here with
// its faithful C# signature (Go signature preserved in the comment) so the assembly compiles and the
// intrinsic-tag / element / fragment / attributes trunk resolves for real where its dependencies
// already exist. The owning wave DELETES the corresponding stub when it lands the real body.
//
// Stubs already declared by other dedup surfaces are intentionally NOT redeclared here (CS0111):
//   GetContextualType (Stubs), GetResolvedSignature (Flow.Deps), ResolveSymbol (Stubs),
//   CheckExpressionForMutableLocation / CreateTypeReference / IsValidSpreadType (MembersSignatures.Deps),
//   GetRegularTypeOfObjectLiteral / GetTypeOfPropertyOrIndexSignatureOfType (Relater.Deps),
//   IsTypeAny (SymbolTypes), IsContextSensitive (TypeNodes.Stubs), RemoveMissingType (SymbolTypes),
//   NewDiagnosticForNode (TypeOps.Deps), GetSpellingSuggestionForName (NameLookup.Deps),
//   GetSymbolAtLocation (Statements.Deps), IsDeprecatedSymbol / AddDeprecatedSuggestion /
//   EntityNameToString / GetFirstIdentifier (SymbolResolution).
//
// §1.5: diagnostics via the fully-qualified `global::Ts.Diagnostics.Message` in signatures; no
// culture-sensitive APIs. Do NOT implement whole missing subsystems here.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;

namespace Ts.Checker;

public partial class Checker
{
    // ---- contextual typing (contextual wave: checker.go:29xxx-30xxx) ----

    // CheckExpressionWithContextualType + GetContextNode are now REAL in Checker.Expressions.cs (checker.go:7461/7482).

    // getApparentTypeOfContextualType (30443), getTypeOfPropertyOfContextualType (30308),
    // findContextualNode (30643), getContextualTypeForArgumentAtIndex (29529): real bodies landed in
    // Checker.ContextualType.cs (call-resolution + contextual-type wave).

    // isPossiblyDiscriminantValue (checker.go:30556) implemented in Checker.ContextualType.cs (#21).

    // ---- type mapping / spread (checker.go union/spread wave) ----

    // MapTypeEx is now REAL in Checker.Widening.cs (checker.go:25473).

    // checker.go:13319-13427
    internal Type GetSpreadType(Type left, Type right, Symbol? symbol, ObjectFlags objectFlags, bool @readonly)
    {
        if ((left.flags & TypeFlags.Any) != 0 || (right.flags & TypeFlags.Any) != 0)
        {
            return anyType!;
        }
        if ((left.flags & TypeFlags.Unknown) != 0 || (right.flags & TypeFlags.Unknown) != 0)
        {
            return unknownType!;
        }
        if ((left.flags & TypeFlags.Never) != 0)
        {
            return right;
        }
        if ((right.flags & TypeFlags.Never) != 0)
        {
            return left;
        }
        left = TryMergeUnionOfObjectTypeAndEmptyObject(left, @readonly);
        if ((left.flags & TypeFlags.Union) != 0)
        {
            if (CheckCrossProductUnion([left, right]))
            {
                return MapType(left, t => GetSpreadType(t, right, symbol, objectFlags, @readonly));
            }
            return errorType!;
        }
        right = TryMergeUnionOfObjectTypeAndEmptyObject(right, @readonly);
        if ((right.flags & TypeFlags.Union) != 0)
        {
            if (CheckCrossProductUnion([left, right]))
            {
                return MapType(right, t => GetSpreadType(left, t, symbol, objectFlags, @readonly));
            }
            return errorType!;
        }
        if ((right.flags & (TypeFlags.BooleanLike | TypeFlags.NumberLike | TypeFlags.BigIntLike | TypeFlags.StringLike | TypeFlags.EnumLike | TypeFlags.NonPrimitive | TypeFlags.Index)) != 0)
        {
            return left;
        }
        if (IsGenericObjectType(left) || IsGenericObjectType(right))
        {
            if (IsEmptyObjectType(left))
            {
                return right;
            }
            // When the left type is an intersection, we may need to merge the last constituent of the
            // intersection with the right type. For example when the left type is 'T & { a: string }'
            // and the right type is '{ b: string }' we produce 'T & { a: string, b: string }'.
            if ((left.flags & TypeFlags.Intersection) != 0)
            {
                var types = left.Types();
                var lastLeft = types[types.Length - 1];
                if (IsNonGenericObjectType(lastLeft) && IsNonGenericObjectType(right))
                {
                    var newTypes = (Type[])types.Clone();
                    newTypes[newTypes.Length - 1] = GetSpreadType(lastLeft, right, symbol, objectFlags, @readonly);
                    return GetIntersectionType(newTypes);
                }
            }
            return GetIntersectionType([left, right]);
        }
        var members = new SymbolTable();
        var skippedPrivateMembers = new global::Ts.Collections.Set<string>();
        IndexInfo[]? indexInfos;
        if (left == emptyObjectType)
        {
            indexInfos = GetIndexInfosOfType(right);
        }
        else
        {
            indexInfos = GetUnionIndexInfos([left, right]);
        }
        foreach (var rightProp in GetPropertiesOfType(right))
        {
            if ((GetDeclarationModifierFlagsFromSymbol(rightProp) & (ModifierFlags.Private | ModifierFlags.Protected)) != 0)
            {
                skippedPrivateMembers.Add(rightProp.Name);
            }
            else if (IsSpreadableProperty(rightProp))
            {
                members[rightProp.Name] = GetSpreadSymbol(rightProp, @readonly);
            }
        }

        foreach (var leftProp in GetPropertiesOfType(left))
        {
            if (skippedPrivateMembers.Has(leftProp.Name) || !IsSpreadableProperty(leftProp))
            {
                continue;
            }
            if (members.GetValueOrDefault(leftProp.Name) is not null)
            {
                var rightProp = members[leftProp.Name];
                var rightType = GetTypeOfSymbol(rightProp);
                if ((rightProp.Flags & SymbolFlags.Optional) != 0)
                {
                    var declarations = global::Ts.Core.CoreUtil.Concatenate(leftProp.Declarations, rightProp.Declarations);
                    var flags = SymbolFlags.Property | (leftProp.Flags & SymbolFlags.Optional);
                    var result = NewSymbol(flags, leftProp.Name);
                    var links = valueSymbolLinks.Get(result);
                    // Optimization: avoid calculating the union type if spreading into the exact same type.
                    // This is common, e.g. spreading one options bag into another where the bags have the
                    // same type, or have properties which overlap. If the unions are large, it may turn out
                    // to be expensive to perform subtype reduction.
                    var leftType = GetTypeOfSymbol(leftProp);
                    var leftTypeWithoutUndefined = RemoveMissingOrUndefinedType(leftType);
                    var rightTypeWithoutUndefined = RemoveMissingOrUndefinedType(rightType);
                    if (leftTypeWithoutUndefined == rightTypeWithoutUndefined)
                    {
                        links.resolvedType = leftType;
                    }
                    else
                    {
                        links.resolvedType = GetUnionTypeEx([leftType, rightTypeWithoutUndefined], UnionReduction.Subtype, null, null);
                    }
                    spreadLinks.Get(result).leftSpread = leftProp;
                    spreadLinks.Get(result).rightSpread = rightProp;
                    result.Declarations = declarations;
                    links.nameType = valueSymbolLinks.Get(leftProp).nameType;
                    members[leftProp.Name] = result;
                }
            }
            else
            {
                members[leftProp.Name] = GetSpreadSymbol(leftProp, @readonly);
            }
        }
        var spreadIndexInfos = global::Ts.Core.CoreUtil.SameMap(indexInfos, info => GetIndexInfoWithReadonly(info, @readonly));
        var spread = NewAnonymousType(symbol, members, null, null, spreadIndexInfos);
        spread.objectFlags |= ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral | ObjectFlags.ContainsSpread | objectFlags;
        return spread;
    }

    // checker.go:13429-13434
    internal IndexInfo GetIndexInfoWithReadonly(IndexInfo info, bool @readonly)
    {
        if (info.isReadonly != @readonly)
        {
            return NewIndexInfo(info.keyType, info.valueType, @readonly, info.declaration, info.components);
        }
        return info;
    }

    // checker.go:13442-13456
    internal IndexInfo[]? GetUnionIndexInfos(Type[] types)
    {
        var sourceInfos = GetIndexInfosOfType(types[0]);
        List<IndexInfo>? result = null;
        foreach (var info in sourceInfos)
        {
            var indexType = info.keyType;
            if (global::Ts.Core.CoreUtil.Every(types, t => GetIndexInfoOfType(t, indexType) is not null))
            {
                var valueType = GetUnionType(global::Ts.Core.CoreUtil.Map(types, t => GetIndexTypeOfType(t, indexType)!));
                var isReadonly = global::Ts.Core.CoreUtil.Some(types, t => GetIndexInfoOfType(t, indexType)!.isReadonly);
                (result ??= []).Add(NewIndexInfo(indexType, valueType, isReadonly, null, null));
            }
        }
        return result?.ToArray();
    }

    // checker.go:13462-13509
    internal Type TryMergeUnionOfObjectTypeAndEmptyObject(Type t, bool @readonly)
    {
        if ((t.flags & TypeFlags.Union) == 0)
        {
            return t;
        }
        if (global::Ts.Core.CoreUtil.Every(t.Types(), IsEmptyObjectTypeOrSpreadsIntoEmptyObject))
        {
            var empty = global::Ts.Core.CoreUtil.Find(t.Types(), IsEmptyObjectType);
            if (empty is not null)
            {
                return empty;
            }
            return emptyObjectType!;
        }
        var firstType = global::Ts.Core.CoreUtil.Find(t.Types(), tt => !IsEmptyObjectTypeOrSpreadsIntoEmptyObject(tt));
        if (firstType is null)
        {
            return t;
        }
        var secondType = global::Ts.Core.CoreUtil.Find(t.Types(), tt => tt != firstType && !IsEmptyObjectTypeOrSpreadsIntoEmptyObject(tt));
        if (secondType is not null)
        {
            return t;
        }
        // gets the type as if it had been spread, but where everything in the spread is made optional
        var members = new SymbolTable();
        foreach (var prop in GetPropertiesOfType(firstType))
        {
            if ((GetDeclarationModifierFlagsFromSymbol(prop) & (ModifierFlags.Private | ModifierFlags.Protected)) != 0)
            {
                // do nothing, skip privates
            }
            else if (IsSpreadableProperty(prop))
            {
                var isSetonlyAccessor = (prop.Flags & SymbolFlags.SetAccessor) != 0 && (prop.Flags & SymbolFlags.GetAccessor) == 0;
                var flags = SymbolFlags.Property | SymbolFlags.Optional;
                var result = NewSymbolEx(flags, prop.Name, (prop.CheckFlags & CheckFlags.Late) | global::Ts.Core.CoreUtil.IfElse(@readonly, CheckFlags.Readonly, CheckFlags.None));
                var links = valueSymbolLinks.Get(result);
                if (isSetonlyAccessor)
                {
                    links.resolvedType = undefinedType!;
                }
                else
                {
                    links.resolvedType = AddOptionalityEx(GetTypeOfSymbol(prop), true /*isProperty*/, true /*isOptional*/);
                }
                result.Declarations = prop.Declarations;
                links.nameType = valueSymbolLinks.Get(prop).nameType;
                mappedSymbolLinks.Get(result).syntheticOrigin = prop;
                members[prop.Name] = result;
            }
        }
        var spread = NewAnonymousType(firstType.symbol, members, null, null, GetIndexInfosOfType(firstType));
        spread.objectFlags |= ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
        return spread;
    }

    // checker.go:13536-13538
    internal bool IsEmptyObjectTypeOrSpreadsIntoEmptyObject(Type t) =>
        IsEmptyObjectType(t) ||
        (t.flags & (TypeFlags.Null | TypeFlags.Undefined | TypeFlags.BooleanLike | TypeFlags.NumberLike | TypeFlags.BigIntLike | TypeFlags.StringLike | TypeFlags.EnumLike | TypeFlags.NonPrimitive | TypeFlags.Index)) != 0;

    // checker.go:13520 — pushContextualType(node, getContextualType(node, None), isCache: true).
    internal void PushCachedContextualType(Node node) => PushContextualType(node, GetContextualType(node, ContextFlags.None), true /*isCache*/);

    // checker.go:13303-13312
    internal void CheckSpreadPropOverrides(Type t, SymbolTable props, Node spread)
    {
        foreach (var right in GetPropertiesOfType(t))
        {
            if ((right.Flags & SymbolFlags.Optional) == 0 && (right.CheckFlags & CheckFlags.Partial) == 0)
            {
                var left = props.GetValueOrDefault(right.Name);
                if (left is not null)
                {
                    var diagnostic = Error(left.ValueDeclaration, global::Ts.Diagnostics.Diagnostics.X_0_is_specified_more_than_once_so_this_usage_will_be_overwritten, left.Name);
                    diagnostic.AddRelatedInfo(NewDiagnosticForNode(spread, global::Ts.Diagnostics.Diagnostics.This_spread_always_overwrites_this_property));
                }
            }
        }
    }

    // ---- signatures / first-parameter fallback (checker.go signature wave) ----

    // checker.go:28762 — getTypeOfFirstParameterOfSignatureWithFallback: real body landed in
    // Checker.AwaitedTypes.cs (#22 awaited-types wave).

    // getUnionSignatures (checker.go:21017) implemented in Checker.Instantiation.cs (#14).

    // ---- index symbol (checker.go:31839) ----

    // checker.go:31839
    internal Symbol? GetApplicableIndexSymbol(Type t, Type keyType) => throw new NotImplementedException("getApplicableIndexSymbol");

    // ---- iteration types (checker.go iteration wave) ----

    // checker.go:1088 — lazy global-type resolver `getGlobalIterableType func() *Type`.
    internal Type GetGlobalIterableType() => throw new NotImplementedException("getGlobalIterableType");

    // getIterationTypeOfIterable (6217): real body landed in Checker.IterationTypes.cs (iteration wave).

    // ---- inference context (checker.go:30722) ----

    // PushInferenceContext / PopInferenceContext / GetInferenceContext are now REAL in
    // Checker.ContextualType.cs (checker.go:30712-30729).

    // ---- call resolution (checker.go call wave: 8xxx-9xxx) ----
    // resolveCall (8819), resolveErrorCall (9899), resolveUntypedCall (9878), isUntypedFunctionCall
    // (9909): real bodies landed in Checker.CallResolution.cs / Checker.CallResolution.Applicability.cs.

    // checkDeprecatedSignature 8340: real body landed in Checker.Deprecation.cs.

    // ---- external module resolution (checker.go:15067) ----
    // resolveExternalModule: real body landed in Checker.SymbolResolution.cs (trunk; deep branches
    // remain `// !!!` stubs there).

    // ---- jsx alias reference marking (markJsxAliasReferenced is now REAL in Checker.AliasReferences.cs) ----

    // ---- JSX-runtime AST helpers (ast.GetPragmaFromSourceFile / GetSemanticJsxChildren) ----
    // Live in the transformer adapters today; the AST layer owns their permanent home. Stubbed here
    // (JSX-specific) so the pragma/children paths compile until the AST helper lands cleanly.

    // ast/utilities.go:2782 — ast.GetPragmaFromSourceFile: real body landed in
    // Ast/Utilities.Binder.cs (AstUtilities.GetPragmaFromSourceFile); call sites use it directly.

    // ast — ast.GetSemanticJsxChildren (utilities.go:3773). Forwarder: the faithful body lives in
    // TransformerAstAdapters.GetSemanticJsxChildren until the AST layer owns it.
    internal static Node[] GetSemanticJsxChildren(Node[]? children) =>
        global::Ts.Transformers.TransformerAstAdapters.GetSemanticJsxChildren(children);
}

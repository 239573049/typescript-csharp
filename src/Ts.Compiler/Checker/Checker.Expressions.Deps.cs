// Single dedup surface for the expression-core wave (#7/#8). Methods here fall into two groups:
//
//   (a) Leaf expression checkers OWNED by #7 (checker.go:7494-12292) but not yet ported in this
//       breadth-first pass — the dispatch trunk (Checker.Expressions.cs) routes to them so the
//       assembly compiles and literal/binary expressions type-check for real. When a follow-up pass
//       ports a leaf, it moves the body into Checker.Expressions.cs and DELETES the stub here.
//
//   (b) Cross-cluster helpers that #7/#8 CALL but do not own — they live in not-yet-landed waves
//       (grammar checks, contextual typing, JSX links, symbol/reference marking, awaited types,
//       falsy-type extraction). Each throws NotImplementedException so the call sites compile; the
//       owning wave DELETES the corresponding stub when it lands.
//
// Stubs already declared by other dedup surfaces (Statements.Deps / MembersSignatures.Deps /
// Flow.Deps / SymbolResolution / Stubs) are intentionally NOT redeclared here to avoid duplicate
// members (e.g. CheckSuperExpression, CheckArrayLiteral, CheckDestructuringAssignment,
// PushContextualType/PopContextualType, GetTypeOfExpression, GetContextualType, IsImportCall,
// GetAwaitedTypeNoAlias, ErrorAndMaybeSuggestAwait, GrammarErrorOnNode, CheckExternalEmitHelpers,
// GetAwaitedTypeOfPromise, CheckExpressionForMutableLocation, EntityNameToString,
// CreateDiagnosticForNode). **Do NOT implement whole missing subsystems here.**

using System;
using System.Collections.Generic;
using System.Globalization;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ===== (a) #7 leaf expression checkers (owned; body pending a follow-up breadth pass) =====

    // checker.go:7814 / 7823
    internal Type CheckPrivateIdentifierExpression(Node node) => throw new NotImplementedException("checkPrivateIdentifierExpression");

    // checkIdentifier (checker.go:11014) landed in Checker.CheckIdentifier.cs.

    // checkThisExpression (checker.go:12046) + tryGetThisTypeAt/Ex + checkThisBeforeSuper family
    // landed in Checker.Expressions.cs.

    // checkTemplateExpression (checker.go:7953) landed in Checker.Expressions.cs.

    // checker.go:7989
    internal Type CheckRegularExpressionLiteral(Node node) => throw new NotImplementedException("checkRegularExpressionLiteral");

    // checker.go:13094 — checkObjectLiteral: real body landed in Checker.ObjectLiteral.cs.

    // checker.go:11216 — checkPropertyAccessExpression: real body landed in Checker.PropertyAccess.cs.

    // checker.go:8099
    internal Type CheckQualifiedName(Node node, CheckMode checkMode) => throw new NotImplementedException("checkQualifiedName");

    // checker.go:8110 — checkIndexedAccess: real body landed in Checker.Expressions.cs.

    // checker.go:8244
    internal Type CheckImportCallExpression(Node node) => throw new NotImplementedException("checkImportCallExpression");

    // checker.go:8298 — checkCallExpression: real body landed in Checker.CallResolution.cs.

    // checker.go:10010 — checkTaggedTemplateExpression: real body landed in Checker.Expressions.cs.

    // checker.go:10019 — checkParenthesizedExpression: real body landed in Checker.Expressions.cs.

    // checkClassExpression (checker.go:10023) + getFirstTransformableStaticClassElement (10031) +
    // checkClassExpressionExternalHelpers (10057) + checkClassExpressionDeferred (10085) landed in
    // Checker.Expressions.cs.

    // ---- AST-layer gaps needed by the class-expression cluster (ast/utilities.go, not yet in Ts.Ast) ----

    // checker/utilities.go:1829
    internal static Node? WalkUpOuterExpressions(Node node)
    {
        var parent = node.Parent;
        while (parent != null && AstUtilities.IsOuterExpression(parent, OuterExpressionKinds.All))
        {
            parent = parent.Parent;
        }
        return parent;
    }

    // ast/utilities.go:4534
    internal static bool IsNamedEvaluationSource(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAssignment:
                return !IsProtoSetter(node.AsPropertyAssignment().Name()!);
            case SyntaxKind.ShorthandPropertyAssignment:
                return node.AsShorthandPropertyAssignment().ObjectAssignmentInitializer != null;
            case SyntaxKind.VariableDeclaration:
                return NodeTests.IsIdentifier(node.AsVariableDeclaration().Name()!) && node.Initializer() != null;
            case SyntaxKind.Parameter:
                return NodeTests.IsIdentifier(node.AsParameterDeclaration().Name()!) && node.Initializer() != null && node.AsParameterDeclaration().DotDotDotToken == null;
            case SyntaxKind.BindingElement:
                return NodeTests.IsIdentifier(node.AsBindingElement().Name()!) && node.Initializer() != null && node.AsBindingElement().DotDotDotToken == null;
            case SyntaxKind.PropertyDeclaration:
                return node.Initializer() != null;
            case SyntaxKind.BinaryExpression:
                switch (node.AsBinaryExpression().OperatorToken!.Kind)
                {
                    case SyntaxKind.EqualsToken:
                    case SyntaxKind.AmpersandAmpersandEqualsToken:
                    case SyntaxKind.BarBarEqualsToken:
                    case SyntaxKind.QuestionQuestionEqualsToken:
                        return NodeTests.IsIdentifier(node.AsBinaryExpression().Left!);
                }
                break;
            case SyntaxKind.ExportAssignment:
                return true;
        }
        return false;
    }

    // ast/utilities.go:4560
    // Indicates whether a property name is the special `__proto__` property.
    // Per the ECMA-262 spec, this only matters for property assignments whose name is
    // the Identifier `__proto__`, or the string literal `"__proto__"`, but not for
    // computed property names.
    internal static bool IsProtoSetter(Node node)
    {
        return (NodeTests.IsIdentifier(node) || NodeTests.IsStringLiteral(node)) && node.Text() == "__proto__";
    }

    // ast/utilities.go:3740
    // Gets a value indicating whether a class element is either a static or an instance property declaration with an initializer.
    internal static bool IsInitializedProperty(Node member)
    {
        return member.Kind == SyntaxKind.PropertyDeclaration &&
            member.Initializer() != null;
    }

    // CheckFunctionExpressionOrObjectLiteralMethod (+ contextual/deferred/parameter-assignment family)
    // is now REAL in Checker.Expressions.cs (checker.go:10090-10433).

    // checkAssertion (checker.go:12256) + checkAssertionDeferred (checker.go:12277) landed in
    // Checker.Expressions.cs.

    // checker.go:10593
    // checker.go:10593
    internal Type CheckTypeOfExpression(Node node)
    {
        CheckExpression(node.Expression()!);
        return typeofType!;
    }

    // checkNonNullAssertion (checker.go:10598) + checkNonNullChain (checker.go:10605) landed in
    // Checker.Expressions.cs.

    // checkSatisfiesExpression (checker.go:10715) landed in Checker.Expressions.cs.

    // checkMetaProperty (checker.go:10727) + checkNewTargetMetaProperty (checker.go:10743) +
    // checkImportMetaProperty (checker.go:10756) landed in Checker.Expressions.cs
    // (GetNewTargetContainer helper temporarily lives there too — see PORT NOTE).

    // checkDeleteExpression (checker.go:10778) + checkDeleteExpressionMustBeOptional (checker.go:10800)
    // landed in Checker.Expressions.cs.

    // checkVoidExpression (checker.go:10814) landed in Checker.Expressions.cs.

    // checkAwaitExpression (checker.go:10819) landed in Checker.Expressions.cs.

    // checkPrefixUnaryExpression (checker.go:10829) + getUnaryResultType landed in Checker.Expressions.cs.

    // checkPostfixUnaryExpression (checker.go:10883) landed in Checker.Expressions.cs.

    // checkConditionalExpression (checker.go:10908) landed in Checker.Expressions.cs.

    // checkSpreadExpression (checker.go:10921) landed in Checker.Expressions.cs.

    // checkYieldExpression (checker.go:10926) + getYieldedTypeOfYieldExpression (checker.go:10993)
    // landed in Checker.Expressions.cs.

    // checker.go:11006 — checkSyntheticExpression: real body landed in Checker.Expressions.cs.

    // checker.go:7576
    internal Type InstantiateTypeWithSingleGenericCallSignature(Node node, Type t, CheckMode checkMode)
    {
        if ((checkMode & (CheckMode.Inferential | CheckMode.SkipGenericFunctions)) == 0)
        {
            return t;
        }
        var callSignature = GetSingleSignature(t, SignatureKind.Call, true /*allowMembers*/);
        var constructSignature = GetSingleSignature(t, SignatureKind.Construct, true /*allowMembers*/);
        var signature = callSignature ?? constructSignature;
        if (signature is null || (signature.typeParameters?.Length ?? 0) == 0)
        {
            return t;
        }
        var contextualType = GetApparentTypeOfContextualType(node, ContextFlags.NoConstraints);
        if (contextualType is null)
        {
            return t;
        }
        var contextualSignature = GetSingleSignature(
            GetNonNullableType(contextualType),
            callSignature is not null ? SignatureKind.Call : SignatureKind.Construct,
            false /*allowMembers*/);
        if (contextualSignature is null || (contextualSignature.typeParameters?.Length ?? 0) != 0)
        {
            return t;
        }
        if ((checkMode & CheckMode.SkipGenericFunctions) != 0)
        {
            SkippedGenericFunction(node, checkMode);
            return anyFunctionType!;
        }
        var context = GetInferenceContext(node);
        // We have an expression that is an argument of a generic function for which we are performing
        // type argument inference. The expression is of a function type with a single generic call
        // signature and a contextual function type with a single non-generic call signature. Now check
        // if the outer function returns a function type with a single non-generic call signature and
        // if some of the outer function type parameters have no inferences so far. If so, we can
        // potentially add inferred type parameters to the outer function return type.
        Signature? returnSignature = null;
        if (context!.signature is not null)
        {
            var returnType = GetReturnTypeOfSignature(context.signature);
            if (returnType is not null)
            {
                returnSignature = GetSingleCallOrConstructSignature(returnType);
            }
        }
        if (returnSignature is not null &&
            (returnSignature.typeParameters?.Length ?? 0) == 0 &&
            !CoreUtil.Every(context.inferences, HasInferenceCandidates))
        {
            // Instantiate the signature with its own type parameters as type arguments, possibly
            // renaming the type parameters to ensure they have unique names.
            var uniqueTypeParameters = GetUniqueTypeParameters(context, signature.typeParameters!);
            var instantiatedSignature = GetSignatureInstantiationWithoutFillingInTypeArguments(signature, uniqueTypeParameters);
            // Infer from the parameters of the instantiated signature to the parameters of the
            // contextual signature starting with an empty set of inference candidates.
            var inferences = CoreUtil.Map(context.inferences, info => NewInferenceInfo(info.typeParameter))!;
            ApplyToParameterTypes(instantiatedSignature, contextualSignature, (source, target) =>
                InferTypes(inferences, source, target, InferencePriority.None, true /*contravariant*/));
            if (CoreUtil.Some(inferences, HasInferenceCandidates))
            {
                // We have inference candidates, indicating that one or more type parameters are referenced
                // in the parameter types of the contextual signature. Now also infer from the return type.
                ApplyToReturnTypes(instantiatedSignature, contextualSignature, (source, target) =>
                    InferTypes(inferences, source, target, InferencePriority.None, false));
                // If the type parameters for which we produced candidates do not have any inferences yet,
                // we adopt the new inference candidates and add the type parameters of the expression type
                // to the set of inferred type parameters for the outer function return type.
                if (!HasOverlappingInferences(context.inferences, inferences))
                {
                    MergeInferences(context.inferences, inferences);
                    context.inferredTypeParameters = CoreUtil.Concatenate(context.inferredTypeParameters, uniqueTypeParameters);
                    return GetOrCreateTypeFromSignature(instantiatedSignature);
                }
            }
        }
        // TODO: The signature may reference any outer inference contexts, but we map pop off and then apply new inference contexts,
        // and thus get different inferred types. That this is cached on the *first* such attempt is not currently an issue, since expression
        // types *also* get cached on the first pass. If we ever properly speculate, though, the cached "isolatedSignatureType" signature
        // field absolutely needs to be included in the list of speculative caches.
        return GetOrCreateTypeFromSignature(InstantiateSignatureInContextOf(signature, contextualSignature, context, null));
    }

    // checker.go:7661
    internal Type[] GetUniqueTypeParameters(InferenceContext context, Type[] typeParameters)
    {
        List<Type>? oldTypeParameters = null;
        List<Type>? newTypeParameters = null;
        var result = new List<Type>(typeParameters.Length);
        foreach (var tp in typeParameters)
        {
            var name = tp.symbol!.Name;
            if (HasTypeParameterByName(context.inferredTypeParameters, name) || HasTypeParameterByName(result, name))
            {
                var combined = new List<Type>();
                if (context.inferredTypeParameters is not null)
                {
                    combined.AddRange(context.inferredTypeParameters);
                }
                combined.AddRange(result);
                var newName = GetUniqueTypeParameterName(combined, name);
                var symbol = NewSymbol(SymbolFlags.TypeParameter, newName);
                var newTypeParameter = NewTypeParameter(symbol);
                newTypeParameter.AsTypeParameter().target = tp;
                (oldTypeParameters ??= []).Add(tp);
                (newTypeParameters ??= []).Add(newTypeParameter);
                result.Add(newTypeParameter);
            }
            else
            {
                result.Add(tp);
            }
        }
        if (newTypeParameters is not null && newTypeParameters.Count != 0)
        {
            var mapper = TypeMapper.NewTypeMapper(oldTypeParameters!.ToArray(), newTypeParameters.ToArray());
            foreach (var tp in newTypeParameters)
            {
                tp.AsTypeParameter().mapper = mapper;
            }
        }
        return result.ToArray();
    }

    // checker.go:7688
    internal static bool HasTypeParameterByName(IReadOnlyList<Type>? typeParameters, string name)
    {
        if (typeParameters is null)
        {
            return false;
        }
        foreach (var tp in typeParameters)
        {
            if (tp.symbol!.Name == name)
            {
                return true;
            }
        }
        return false;
    }

    // checker.go:7694
    internal static string GetUniqueTypeParameterName(IReadOnlyList<Type> typeParameters, string baseName)
    {
        while (baseName.Length > 1 && baseName[^1] >= '0' && baseName[^1] <= '9')
        {
            baseName = baseName[..^1];
        }
        var index = 1;
        while (true)
        {
            var augmentedName = baseName + index.ToString(CultureInfo.InvariantCulture);
            if (!HasTypeParameterByName(typeParameters, augmentedName))
            {
                return augmentedName;
            }
            index++;
        }
    }

    // checker.go:7550 — const-enum access-site validation (needs program/verbatim-module wiring).
    internal void CheckConstEnumAccess(Node node, Type t) => throw new NotImplementedException("checkConstEnumAccess");

    // ===== (b) cross-cluster helpers called by #7/#8 (owned elsewhere) =====

    // jsx.go — JSX expression checkers: real bodies landed in Checker.Jsx.cs (jsx wave).

    // grammar.go — CheckGrammarNumericLiteral/CheckGrammarBigIntLiteral are now REAL in Checker.Grammar.cs (grammar wave).

    // checker.go — global-symbol resolution (Construction / NameLookup clusters).
    internal Symbol? GetGlobalNaNSymbolOrNil() => throw new NotImplementedException("getGlobalNaNSymbolOrNil");

    // reportNonexistentProperty (checker.go:11499) + its suggestion helpers landed in
    // Checker.PropertyAccess.cs.

    // checker.go — unchecked-JS suggestion heuristic (utilities cluster).
    // utilities.go:1700
    internal bool IsUncheckedJSSuggestion(Node? node, Symbol? suggestion, bool excludeClasses)
    {
        var file = AstUtilities.GetSourceFileOfNode(node);
        if (file != null)
        {
            if (compilerOptions.CheckJs.IsUnknown() && file.CheckJsDirective == null && (file.ScriptKind == global::Ts.Core.ScriptKind.JS || file.ScriptKind == global::Ts.Core.ScriptKind.Jsx))
            {
                SourceFile? declarationFile = null;
                if (suggestion != null)
                {
                    var firstDeclaration = CoreUtil.FirstOrNil(suggestion.Declarations);
                    if (firstDeclaration != null)
                    {
                        declarationFile = AstUtilities.GetSourceFileOfNode(firstDeclaration);
                    }
                }
                var suggestionHasNoExtendsOrDecorators = suggestion == null ||
                    suggestion.ValueDeclaration == null ||
                    !AstUtilities.IsClassLike(suggestion.ValueDeclaration) ||
                    AstUtilities.GetExtendsHeritageClauseElements(suggestion.ValueDeclaration).Length != 0 ||
                    AstUtilities.ClassOrConstructorParameterIsDecorated(false, suggestion.ValueDeclaration);
                return !(file != declarationFile && declarationFile != null && AstUtilities.IsGlobalSourceFile(declarationFile.AsNode())) &&
                    !(excludeClasses && suggestion != null && (suggestion.Flags & SymbolFlags.Class) != 0 && suggestionHasNoExtendsOrDecorators) &&
                    !(node != null && excludeClasses && NodeTests.IsPropertyAccessExpression(node) && node.Expression()!.Kind == SyntaxKind.ThisKeyword && suggestionHasNoExtendsOrDecorators);
            }
        }
        return false;
    }

    // checker.go:28867
    internal Type ExtractDefinitelyFalsyTypes(Type type) => MapType(type, GetDefinitelyFalsyPartOfType);
    // checker.go:28871
    internal Type GetDefinitelyFalsyPartOfType(Type t)
    {
        if ((t.flags & TypeFlags.String) != 0)
        {
            return emptyStringType!;
        }
        if ((t.flags & TypeFlags.Number) != 0)
        {
            return zeroType!;
        }
        if ((t.flags & TypeFlags.BigInt) != 0)
        {
            return zeroBigIntType!;
        }
        if (t == regularFalseType || t == falseType ||
            (t.flags & (TypeFlags.Void | TypeFlags.Undefined | TypeFlags.Null | TypeFlags.AnyOrUnknown)) != 0 ||
            (t.flags & TypeFlags.StringLiteral) != 0 && GetStringLiteralValue(t) == "" ||
            (t.flags & TypeFlags.NumberLiteral) != 0 && GetNumberLiteralValue(t) == 0 ||
            (t.flags & TypeFlags.BigIntLiteral) != 0 && IsZeroBigInt(t))
        {
            return t;
        }
        return neverType!;
    }
    // checker.go:28863
    internal Type RemoveDefinitelyFalsyTypes(Type t) => FilterType(t, x => HasTypeFacts(x, TypeFacts.Truthy));

    // checker.go:25362
    // This like getBaseTypeOfLiteralType, but instead treats enum literals as strings/numbers instead
    // of returning their enum base type (which depends on the types of other literals in the enum).
    internal Type GetBaseTypeOfLiteralTypeForComparison(Type t)
    {
        if ((t.flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
        {
            return stringType!;
        }
        if ((t.flags & (TypeFlags.NumberLiteral | TypeFlags.Enum)) != 0)
        {
            return numberType!;
        }
        if ((t.flags & TypeFlags.BigIntLiteral) != 0)
        {
            return bigintType!;
        }
        if ((t.flags & TypeFlags.BooleanLiteral) != 0)
        {
            return booleanType!;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return MapType(t, GetBaseTypeOfLiteralTypeForComparison);
        }
        return t;
    }

    // IsWriteOnlyAccess (ast.go:1265) landed on AstUtilities (Utilities.AccessKind.cs); call sites use
    // AstUtilities.IsWriteOnlyAccess directly.

    // utilities.go:1041 — literal-expression-of-object predicate (utilities cluster free func).
    internal static bool IsLiteralExpressionOfObject(Node node) => node.Kind switch
    {
        SyntaxKind.ObjectLiteralExpression or SyntaxKind.ArrayLiteralExpression or
        SyntaxKind.RegularExpressionLiteral or SyntaxKind.FunctionExpression or
        SyntaxKind.ClassExpression => true,
        _ => false,
    };
}

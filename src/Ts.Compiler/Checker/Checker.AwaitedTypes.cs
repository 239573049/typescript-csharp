// Ported from tsgo internal/checker/checker.go — awaited-type family (#22 awaited-types wave).
//
// Trunk (ascending Go line order): GetPromisedTypeOfPromise (28679) + getPromisedTypeOfPromiseEx (28683),
// getTypeOfFirstParameterOfSignature (28758) + ...WithFallback (28762), checkAwaitedType (30990),
// getAwaitedType (31010) + getAwaitedTypeEx (31014), getAwaitedTypeNoAlias (31023) +
// getAwaitedTypeNoAliasEx (31027, CachedTypeKindAwaitedType cache + awaitedTypeStack cycle guard),
// isAwaitedTypeInstantiation (31141), isAwaitedTypeNeeded (31149), createAwaitedTypeIfNeeded (31167),
// tryCreateAwaitedType (31184), unwrapAwaitedType (31196), isThenableType (31206),
// getAwaitedTypeOfPromise (31215) + getAwaitedTypeOfPromiseEx (31219).
//
// §1.5: cross-package via global::; host classes direct. Deep leaves still stubbed and CALLED (bodies
// owned by other waves): GetTypeWithFacts (thenable/`then`-signature paths only; the global-Promise
// reference fast path and all non-thenable types never reach it). getGlobalAwaitedSymbol /
// getGlobalAwaitedSymbolOrNil already real (Checker.Construction.cs GetGlobalTypeAliasResolver).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:28679
    internal Type? GetPromisedTypeOfPromise(Type t) => GetPromisedTypeOfPromiseEx(t, null, out _);

    // Gets the "promised type" of a promise.
    // @param type The type of the promise.
    // @remarks The "promised type" of a type is the type of the "value" parameter of the "onfulfilled" callback.
    // checker.go:28683 — PORT-NOTE: Go's `thisTypeForErrorOut **Type` (nil pointer = caller doesn't care)
    // becomes a C# `out Type?`; Go callers that passed nil discard via `out _`. The out value is null
    // unless a `then` this-context mismatch was the reason no candidate signature survived.
    internal Type? GetPromisedTypeOfPromiseEx(Type t, Node? errorNode, out Type? thisTypeForErrorOut)
    {
        //  { // type
        //      then( // thenFunction
        //          onfulfilled: ( // onfulfilledParameterType
        //              value: T // valueParameterType
        //          ) => any
        //      ): any;
        //  }
        thisTypeForErrorOut = null;
        if (IsTypeAny(t))
        {
            return null;
        }
        var key = new CachedTypeKey(CachedTypeKind.PromisedTypeOfPromise, t.id);
        var cached = cachedTypes.GetValueOrDefault(key);
        if (cached != null)
        {
            return cached;
        }
        if (IsReferenceToType(t, getGlobalPromiseType()))
        {
            var result = GetTypeArguments(t)[0];
            cachedTypes[key] = result;
            return result;
        }
        // primitives with a `{ then() }` won't be unwrapped/adopted.
        if (AllTypesAssignableToKind(GetBaseConstraintOrType(t), TypeFlags.Primitive | TypeFlags.Never))
        {
            return null;
        }
        var thenFunction = GetTypeOfPropertyOfType(t, "then");
        // TODO: GH#18217
        if (IsTypeAny(thenFunction))
        {
            return null;
        }
        Signature[] thenSignatures = [];
        if (thenFunction != null)
        {
            thenSignatures = GetSignaturesOfType(thenFunction, SignatureKind.Call);
        }
        if (thenSignatures.Length == 0)
        {
            if (errorNode != null)
            {
                Error(errorNode, Diag.A_promise_must_have_a_then_method);
            }
            return null;
        }
        Type? thisTypeForError = null;
        List<Signature> candidates = [];
        foreach (var thenSignature in thenSignatures)
        {
            var thisType = GetThisTypeOfSignature(thenSignature);
            if (thisType != null && thisType != voidType && !IsTypeRelatedTo(t, thisType, subtypeRelation!))
            {
                thisTypeForError = thisType;
            }
            else
            {
                candidates.Add(thenSignature);
            }
        }
        if (candidates.Count == 0)
        {
            global::Ts.Core.Debug.Assert(thisTypeForError != null);
            thisTypeForErrorOut = thisTypeForError;
            if (errorNode != null)
            {
                Error(errorNode, Diag.The_this_context_of_type_0_is_not_assignable_to_method_s_this_of_type_1, TypeToString(t), TypeToString(thisTypeForError!));
            }
            return null;
        }
        var onfulfilledParameterType = GetTypeWithFacts(GetUnionType(CoreUtil.Map(candidates.ToArray(), GetTypeOfFirstParameterOfSignature)), TypeFacts.NEUndefinedOrNull);
        if (IsTypeAny(onfulfilledParameterType))
        {
            return null;
        }
        var onfulfilledParameterSignatures = GetSignaturesOfType(onfulfilledParameterType, SignatureKind.Call);
        if (onfulfilledParameterSignatures.Length == 0)
        {
            if (errorNode != null)
            {
                Error(errorNode, Diag.The_first_parameter_of_the_then_method_of_a_promise_must_be_a_callback);
            }
            return null;
        }
        var unionResult = GetUnionTypeEx(CoreUtil.Map(onfulfilledParameterSignatures, GetTypeOfFirstParameterOfSignature), UnionReduction.Subtype, null, null);
        cachedTypes[key] = unionResult;
        return unionResult;
    }

    // checker.go:28758
    internal Type GetTypeOfFirstParameterOfSignature(Signature signature) =>
        GetTypeOfFirstParameterOfSignatureWithFallback(signature, neverType!);

    // checker.go:28762
    internal Type GetTypeOfFirstParameterOfSignatureWithFallback(Signature signature, Type fallbackType)
    {
        if (signature.Parameters().Length > 0)
        {
            return GetTypeAtPosition(signature, 0);
        }
        return fallbackType;
    }

    // Gets the "awaited type" of a type.
    // @param type The type to await.
    // @param withAlias When `true`, wraps the "awaited type" in `Awaited<T>` if needed.
    // @remarks The "awaited type" of an expression is its "promised type" if the expression is a
    // Promise-like type; otherwise, it is the type of the expression. This is used to reflect
    // The runtime behavior of the `await` keyword.
    // checker.go:30990
    internal Type CheckAwaitedType(Type t, bool withAlias, Node? errorNode, global::Ts.Diagnostics.Message? diagnosticMessage)
    {
        Type? awaitedType;
        if (withAlias)
        {
            awaitedType = GetAwaitedTypeEx(t, errorNode, diagnosticMessage);
        }
        else
        {
            awaitedType = GetAwaitedTypeNoAliasEx(t, errorNode, diagnosticMessage);
        }
        if (awaitedType != null)
        {
            return awaitedType;
        }
        return errorType!;
    }

    // Gets the "awaited type" of a type.
    //
    // The "awaited type" of an expression is its "promised type" if the expression is a
    // Promise-like type; otherwise, it is the type of the expression. If the "promised
    // type" is itself a Promise-like, the "promised type" is recursively unwrapped until a
    // non-promise type is found.
    //
    // This is used to reflect the runtime behavior of the `await` keyword.
    // checker.go:31010
    internal Type? GetAwaitedType(Type t) => GetAwaitedTypeEx(t, null, null);

    // checker.go:31014
    internal Type? GetAwaitedTypeEx(Type t, Node? errorNode, global::Ts.Diagnostics.Message? diagnosticMessage, params object?[] args)
    {
        var awaitedType = GetAwaitedTypeNoAliasEx(t, errorNode, diagnosticMessage, args);
        if (awaitedType != null)
        {
            return CreateAwaitedTypeIfNeeded(awaitedType);
        }
        return null;
    }

    // Gets the "awaited type" of a type without introducing an `Awaited<T>` wrapper.
    // checker.go:31023
    internal Type? GetAwaitedTypeNoAlias(Type t) => GetAwaitedTypeNoAliasEx(t, null, null);

    // checker.go:31027
    internal Type? GetAwaitedTypeNoAliasEx(Type t, Node? errorNode, global::Ts.Diagnostics.Message? diagnosticMessage, params object?[] args)
    {
        if (IsTypeAny(t))
        {
            return t;
        }
        // If this is already an `Awaited<T>`, just return it. This avoids `Awaited<Awaited<T>>` in higher-order
        if (IsAwaitedTypeInstantiation(t))
        {
            return t;
        }
        // If we've already cached an awaited type, return a possible `Awaited<T>` for it.
        var key = new CachedTypeKey(CachedTypeKind.AwaitedType, t.id);
        var cachedAwaitedType = cachedTypes.GetValueOrDefault(key);
        if (cachedAwaitedType != null)
        {
            return cachedAwaitedType;
        }
        // For a union, get a union of the awaited types of each constituent.
        if ((t.flags & TypeFlags.Union) != 0)
        {
            if (awaitedTypeStack.Contains(t))
            {
                if (errorNode != null)
                {
                    Error(errorNode, Diag.Type_is_referenced_directly_or_indirectly_in_the_fulfillment_callback_of_its_own_then_method);
                }
                return null;
            }
            awaitedTypeStack.Add(t);
            // PORT-NOTE: Go's c.mapType with a nil-returning mapper → MapTypeEx (nullable mapper/result).
            var mapped = MapTypeEx(t, tt => GetAwaitedTypeNoAliasEx(tt, errorNode, diagnosticMessage, args), false /*noReductions*/);
            awaitedTypeStack.RemoveAt(awaitedTypeStack.Count - 1);
            // PORT-NOTE: Go stores a possibly-nil `mapped` in cachedTypes; every read of this key is
            // nil-guarded, so a stored nil is indistinguishable from an absent key — store only non-null.
            if (mapped != null)
            {
                cachedTypes[key] = mapped;
            }
            return mapped;
        }
        // If `type` is generic and should be wrapped in `Awaited<T>`, return it.
        if (IsAwaitedTypeNeeded(t))
        {
            cachedTypes[key] = t;
            return t;
        }
        var promisedType = GetPromisedTypeOfPromiseEx(t, null /*errorNode*/, out var thisTypeForError);
        if (promisedType != null)
        {
            if (t == promisedType || awaitedTypeStack.Contains(promisedType))
            {
                // Verify that we don't have a bad actor in the form of a promise whose
                // promised type is the same as the promise type, or a mutually recursive
                // promise. If so, we return undefined as we cannot guess the shape. If this
                // were the actual case in the JavaScript, this Promise would never resolve.
                //
                // An example of a bad actor with a singly-recursive promise type might
                // be:
                //
                //  interface BadPromise {
                //      then(
                //          onfulfilled: (value: BadPromise) => any,
                //          onrejected: (error: any) => any): BadPromise;
                //  }
                //
                // The above interface will pass the PromiseLike check, and return a
                // promised type of `BadPromise`. Since this is a self reference, we
                // don't want to keep recursing ad infinitum.
                //
                // An example of a bad actor in the form of a mutually-recursive
                // promise type might be:
                //
                //  interface BadPromiseA {
                //      then(
                //          onfulfilled: (value: BadPromiseB) => any,
                //          onrejected: (error: any) => any): BadPromiseB;
                //  }
                //
                //  interface BadPromiseB {
                //      then(
                //          onfulfilled: (value: BadPromiseA) => any,
                //          onrejected: (error: any) => any): BadPromiseA;
                //  }
                //
                if (errorNode != null)
                {
                    Error(errorNode, Diag.Type_is_referenced_directly_or_indirectly_in_the_fulfillment_callback_of_its_own_then_method);
                }
                return null;
            }
            // Keep track of the type we're about to unwrap to avoid bad recursive promise types.
            // See the comments above for more information.
            awaitedTypeStack.Add(t);
            var awaitedType = GetAwaitedTypeNoAliasEx(promisedType, errorNode, diagnosticMessage, args);
            awaitedTypeStack.RemoveAt(awaitedTypeStack.Count - 1);
            if (awaitedType == null)
            {
                return null;
            }
            cachedTypes[key] = awaitedType;
            return awaitedType;
        }
        // The type was not a promise, so it could not be unwrapped any further.
        // As long as the type does not have a callable "then" property, it is
        // safe to return the type; otherwise, an error is reported and we return
        // undefined.
        //
        // An example of a non-promise "thenable" might be:
        //
        //  await { then(): void {} }
        //
        // The "thenable" does not match the minimal definition for a promise. When
        // a Promise/A+-compatible or ES6 promise tries to adopt this value, the promise
        // will never settle. We treat this as an error to help flag an early indicator
        // of a runtime problem. If the user wants to return this value from an async
        // function, they would need to wrap it in some other value. If they want it to
        // be treated as a promise, they can cast to <any>.
        if (IsThenableType(t))
        {
            if (errorNode != null)
            {
                Diagnostic? diagnostic = null;
                if (thisTypeForError != null)
                {
                    diagnostic = NewDiagnosticForNode(errorNode, Diag.The_this_context_of_type_0_is_not_assignable_to_method_s_this_of_type_1, TypeToString(t), TypeToString(thisTypeForError));
                }
                // PORT-NOTE: Go passes a possibly-nil diagnosticMessage straight through; upstream
                // guarantees errorNode != nil ⇒ diagnosticMessage != nil (Go would panic otherwise).
                AddDiagnostic(NewDiagnosticChainForNode(diagnostic, errorNode, diagnosticMessage!, args));
            }
            return null;
        }
        cachedTypes[key] = t;
        return t;
    }

    // checker.go:31141
    internal bool IsAwaitedTypeInstantiation(Type t)
    {
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            var awaitedSymbol = getGlobalAwaitedSymbolOrNil();
            return awaitedSymbol != null && t.alias != null && t.alias.symbol == awaitedSymbol && (t.alias.typeArguments?.Length ?? 0) == 1;
        }
        return false;
    }

    // checker.go:31149
    internal bool IsAwaitedTypeNeeded(Type t)
    {
        // If this is already an `Awaited<T>`, we shouldn't wrap it. This helps to avoid `Awaited<Awaited<T>>` in higher-order.
        if (IsTypeAny(t) || IsAwaitedTypeInstantiation(t))
        {
            return false;
        }
        // We only need `Awaited<T>` if `T` contains possibly non-primitive types.
        if (IsGenericObjectType(t))
        {
            var baseConstraint = GetBaseConstraintOfType(t);
            // We only need `Awaited<T>` if `T` is a type variable that has no base constraint, or the base constraint of `T` is `any`, `unknown`, `{}`, `object`,
            // or is promise-like.
            if (baseConstraint != null)
            {
                return (baseConstraint.flags & TypeFlags.AnyOrUnknown) != 0 || IsEmptyObjectType(baseConstraint) || SomeType(baseConstraint, IsThenableType);
            }
            return MaybeTypeOfKind(t, TypeFlags.TypeVariable);
        }
        return false;
    }

    // checker.go:31167
    internal Type CreateAwaitedTypeIfNeeded(Type t)
    {
        // We wrap type `T` in `Awaited<T>` based on the following conditions:
        // - `T` is not already an `Awaited<U>`, and
        // - `T` is generic, and
        // - One of the following applies:
        //   - `T` has no base constraint, or
        //   - The base constraint of `T` is `any`, `unknown`, `object`, or `{}`, or
        //   - The base constraint of `T` is an object type with a callable `then` method.
        if (IsAwaitedTypeNeeded(t))
        {
            var awaitedType = TryCreateAwaitedType(t);
            if (awaitedType != null)
            {
                return awaitedType;
            }
        }
        return t;
    }

    // checker.go:31184
    internal Type? TryCreateAwaitedType(Type t)
    {
        // Nothing to do if `Awaited<T>` doesn't exist
        var awaitedSymbol = getGlobalAwaitedSymbol();
        if (awaitedSymbol != null)
        {
            // Unwrap unions that may contain `Awaited<T>`, otherwise its possible to manufacture an `Awaited<Awaited<T> | U>` where
            // an `Awaited<T | U>` would suffice.
            return GetTypeAliasInstantiation(awaitedSymbol, [UnwrapAwaitedType(t)], null);
        }
        return null;
    }

    // For a generic `Awaited<T>`, gets `T`.
    // checker.go:31196
    internal Type UnwrapAwaitedType(Type t)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return MapType(t, UnwrapAwaitedType);
        }
        if (IsAwaitedTypeInstantiation(t))
        {
            return t.alias!.typeArguments![0];
        }
        return t;
    }

    // checker.go:31206
    internal bool IsThenableType(Type t)
    {
        if (AllTypesAssignableToKind(GetBaseConstraintOrType(t), TypeFlags.Primitive | TypeFlags.Never))
        {
            // primitive types cannot be considered "thenable" since they are not objects.
            return false;
        }
        var thenFunction = GetTypeOfPropertyOfType(t, "then");
        return thenFunction != null && GetSignaturesOfType(GetTypeWithFacts(thenFunction, TypeFacts.NEUndefinedOrNull), SignatureKind.Call).Length != 0;
    }

    // checker.go:31215
    internal Type? GetAwaitedTypeOfPromise(Type t) => GetAwaitedTypeOfPromiseEx(t, null, null);

    // checker.go:31219
    internal Type? GetAwaitedTypeOfPromiseEx(Type t, Node? errorNode, global::Ts.Diagnostics.Message? diagnosticMessage, params object?[] args)
    {
        var promisedType = GetPromisedTypeOfPromiseEx(t, errorNode, out _);
        if (promisedType != null)
        {
            return GetAwaitedTypeEx(promisedType, errorNode, diagnosticMessage, args);
        }
        return null;
    }
}

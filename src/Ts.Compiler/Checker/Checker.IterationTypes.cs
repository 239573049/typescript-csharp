// Ported from tsgo internal/checker/checker.go (sections: iterated/element type machinery,
// lines ~6074-6713): the checkIteratedTypeOrElementType family that powers for-of / for-await-of,
// array/spread destructuring, yield*, and async/generator element-type resolution.
//
// Covers: checkIteratedTypeOrElementType, getIteratedTypeOrElementType,
// getIterationTypeOfGeneratorFunctionReturnType, getIterationTypesOfGeneratorFunctionReturnType,
// getIterationTypeOfIterable, getIterationTypesOfIterable(+Worker/Fast/Slow),
// getIterationTypesOfIterator(+Worker/Fast/Slow), getIterationTypesOfMethod,
// getIterationTypesOfIteratorResult, combineIterationTypes, getIterationTypeUnion,
// getAsyncFromSyncIterationTypes, isReferenceToType/isReferenceToSomeType,
// getBuiltinIteratorReturnType, the IterationTypes.hasTypes/getType + IterationTypesResolver
// .getResolvedIterationTypes helpers, IteratorResult predicates, reportTypeNotIterableError,
// getIterationDiagnosticDetails, isES2015OrLaterIterable.
//
// §1.5: cross-package classes are fully qualified via `global::Ts.<Pkg>.<Class>` (Diag alias for
// diagnostics; NodeTests / AstUtilities used directly). map-iter: this family has no `for range m`.
//
// PORT-NOTE: reportTypeNotIterableError faithfully mirrors Go's `return c.errorAndMaybeSuggestAwait(...)`
// but that helper is still a `void` stub owned by the statements wave (Checker.Statements.Deps.cs);
// until it lands its real *ast.Diagnostic-returning signature, we emit the error for its side-effect
// and return null, so the lone related-info consumer (getIterationTypesOfIterableWorker) guards with `?.`.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:6074-6083
    internal Type CheckIteratedTypeOrElementType(IterationUse use, Type inputType, Type sentType, Node? errorNode)
    {
        if (IsTypeAny(inputType))
        {
            return inputType;
        }
        var t = GetIteratedTypeOrElementType(use, inputType, sentType, errorNode, true /*checkAssignability*/);
        if (t is not null)
        {
            return t;
        }
        return anyType!;
    }

    // checker.go:6085-6190
    internal Type? GetIteratedTypeOrElementType(IterationUse use, Type inputType, Type sentType, Node? errorNode, bool checkAssignability)
    {
        var allowAsyncIterables = (use & IterationUse.AllowsAsyncIterablesFlag) != 0;
        if (inputType == neverType)
        {
            if (errorNode is not null)
            {
                ReportTypeNotIterableError(errorNode, inputType, allowAsyncIterables);
            }
            return null;
        }
        var iterableExists = getGlobalIterableType() != emptyGenericType;
        var possibleOutOfBounds = compilerOptions.NoUncheckedIndexedAccess == Tristate.True && (use & IterationUse.PossiblyOutOfBounds) != 0;
        if (iterableExists || allowAsyncIterables)
        {
            var iterationTypes = GetIterationTypesOfIterable(inputType, use, CoreUtil.IfElse(iterableExists, errorNode, null));
            if (checkAssignability)
            {
                if (iterationTypes.NextType is not null)
                {
                    global::Ts.Diagnostics.Message? diagnostic = null;
                    if ((use & IterationUse.ForOfFlag) != 0)
                    {
                        diagnostic = Diag.Cannot_iterate_value_because_the_next_method_of_its_iterator_expects_type_1_but_for_of_will_always_send_0;
                    }
                    else if ((use & IterationUse.SpreadFlag) != 0)
                    {
                        diagnostic = Diag.Cannot_iterate_value_because_the_next_method_of_its_iterator_expects_type_1_but_array_spread_will_always_send_0;
                    }
                    else if ((use & IterationUse.DestructuringFlag) != 0)
                    {
                        diagnostic = Diag.Cannot_iterate_value_because_the_next_method_of_its_iterator_expects_type_1_but_array_destructuring_will_always_send_0;
                    }
                    else if ((use & IterationUse.YieldStarFlag) != 0)
                    {
                        diagnostic = Diag.Cannot_delegate_iteration_to_value_because_the_next_method_of_its_iterator_expects_type_1_but_the_containing_generator_will_always_send_0;
                    }
                    if (diagnostic is not null)
                    {
                        CheckTypeAssignableTo(sentType, iterationTypes.NextType, errorNode, diagnostic);
                    }
                }
            }
            if (iterationTypes.YieldType is not null || iterableExists)
            {
                if (iterationTypes.YieldType is null)
                {
                    return null;
                }
                if (possibleOutOfBounds)
                {
                    return IncludeUndefinedInIndexSignature(iterationTypes.YieldType);
                }
                return iterationTypes.YieldType;
            }
        }
        var arrayType = inputType;
        var hasStringConstituent = false;
        // If strings are permitted, remove any string-like constituents from the array type.
        // This allows us to find other non-string element types from an array unioned with
        // a string.
        if ((use & IterationUse.AllowsStringInputFlag) != 0)
        {
            if ((arrayType.flags & TypeFlags.Union) != 0)
            {
                // After we remove all types that are StringLike, we will know if there was a string constituent
                // based on whether the result of filter is a new array.
                var arrayTypes = inputType.Types();
                var filteredTypes = CoreUtil.Filter(arrayTypes, t => (t.flags & TypeFlags.StringLike) == 0);
                if (!CoreUtil.Same(filteredTypes, arrayTypes))
                {
                    arrayType = GetUnionTypeEx(filteredTypes, UnionReduction.Subtype, null, null);
                }
            }
            else if ((arrayType.flags & TypeFlags.StringLike) != 0)
            {
                arrayType = neverType!;
            }
            hasStringConstituent = arrayType != inputType;
            if (hasStringConstituent)
            {
                // Now that we've removed all the StringLike types, if no constituents remain, then the entire
                // arrayOrStringType was a string.
                if ((arrayType.flags & TypeFlags.Never) != 0)
                {
                    if (possibleOutOfBounds)
                    {
                        return IncludeUndefinedInIndexSignature(stringType!);
                    }
                    return stringType!;
                }
            }
        }
        if (!IsArrayLikeType(arrayType))
        {
            if (errorNode is not null)
            {
                // Which error we report depends on whether we allow strings or if there was a
                // string constituent. For example, if the input type is number | string, we
                // want to say that number is not an array type. But if the input was just
                // number and string input is allowed, we want to say that number is not an
                // array type or a string type.
                var allowsStrings = (use & IterationUse.AllowsStringInputFlag) != 0 && !hasStringConstituent;
                var (defaultDiagnostic, maybeMissingAwait) = GetIterationDiagnosticDetails(use, inputType, allowsStrings);
                ErrorAndMaybeSuggestAwait(errorNode, maybeMissingAwait && GetAwaitedTypeOfPromise(arrayType) is not null, defaultDiagnostic, TypeToString(arrayType));
            }
            if (hasStringConstituent)
            {
                if (possibleOutOfBounds)
                {
                    return IncludeUndefinedInIndexSignature(stringType!);
                }
                return stringType!;
            }
            return null;
        }
        var arrayElementType = GetIndexTypeOfType(arrayType, numberType!);
        if (hasStringConstituent && arrayElementType is not null)
        {
            // This is just an optimization for the case where arrayOrStringType is string | string[]
            if ((arrayElementType.flags & TypeFlags.StringLike) != 0 && compilerOptions.NoUncheckedIndexedAccess != Tristate.True)
            {
                return stringType!;
            }
            if (possibleOutOfBounds)
            {
                return GetUnionTypeEx([arrayElementType, stringType!, undefinedType!], UnionReduction.Subtype, null, null);
            }
            return GetUnionTypeEx([arrayElementType, stringType!], UnionReduction.Subtype, null, null);
        }
        if ((use & IterationUse.PossiblyOutOfBounds) != 0)
        {
            return IncludeUndefinedInIndexSignature(arrayElementType!);
        }
        return arrayElementType;
    }

    // Gets the requested "iteration type" from a type that is either `Iterable`-like, `Iterator`-like,
    // `IterableIterator`-like, or `Generator`-like (for a non-async generator); or `AsyncIterable`-like,
    // `AsyncIterator`-like, `AsyncIterableIterator`-like, or `AsyncGenerator`-like (for an async generator).
    // checker.go:6195-6201
    internal Type? GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind typeKind, Type returnType, bool isAsyncGenerator)
    {
        if (IsTypeAny(returnType))
        {
            return null;
        }
        var iterationTypes = GetIterationTypesOfGeneratorFunctionReturnType(returnType, isAsyncGenerator);
        return iterationTypes.GetType(typeKind);
    }

    // checker.go:6203-6214
    internal IterationTypes GetIterationTypesOfGeneratorFunctionReturnType(Type t, bool isAsyncGenerator)
    {
        if (IsTypeAny(t))
        {
            return new IterationTypes(anyType, anyType, anyType);
        }
        var use = CoreUtil.IfElse(isAsyncGenerator, IterationUse.AsyncGeneratorReturnType, IterationUse.GeneratorReturnType);
        var resolver = CoreUtil.IfElse(isAsyncGenerator, asyncIterationTypesResolver!, syncIterationTypesResolver!);
        var result = GetIterationTypesOfIterable(t, use, null /*errorNode*/);
        if (result.HasTypes())
        {
            return result;
        }
        return GetIterationTypesOfIterator(t, resolver, null /*errorNode*/, null /*diagnosticOutput*/);
    }

    // Gets the requested "iteration type" from an `Iterable`-like or `AsyncIterable`-like type.
    // checker.go:6217-6223
    internal Type? GetIterationTypeOfIterable(IterationUse use, IterationTypeKind typeKind, Type inputType, Node? errorNode)
    {
        if (IsTypeAny(inputType))
        {
            return null;
        }
        var iterationTypes = GetIterationTypesOfIterable(inputType, use, errorNode);
        return iterationTypes.GetType(typeKind);
    }

    // Gets the *yield*, *return*, and *next* types from an `Iterable`-like or `AsyncIterable`-like type.
    //
    // At every level that involves analyzing return types of signatures, we union the return types of all the signatures.
    //
    // Another thing to note is that at any step of this process, we could run into a dead end,
    // meaning either the property is missing, or we run into the anyType. If either of these things
    // happens, we return a default `IterationTypes{}` to signal that we could not find the iteration type.
    // If a property is missing, and the previous step did not result in `any`, then we also give an error
    // if the caller requested it. Then the caller can decide what to do in the case where there is no
    // iterated type.
    //
    // For a **for-of** statement, `yield*` (in a normal generator), spread, array
    // destructuring, or normal generator we will only ever look for a `[Symbol.iterator]()`
    // method.
    //
    // For an async generator we will only ever look at the `[Symbol.asyncIterator]()` method.
    //
    // For a **for-await-of** statement or a `yield*` in an async generator we will look for
    // the `[Symbol.asyncIterator]()` method first, and then the `[Symbol.iterator]()` method.
    // checker.go:6244-6264
    internal IterationTypes GetIterationTypesOfIterable(Type t, IterationUse use, Node? errorNode)
    {
        t = GetReducedType(t);
        if (IsTypeAny(t))
        {
            return new IterationTypes(anyType, anyType, anyType);
        }
        var key = new IterationTypesKey(t.id, use & IterationUse.CacheFlags);
        // If we are reporting errors and encounter a cached `noIterationTypes`, we should ignore the cached value and continue as if nothing was cached.
        // In addition, we should not cache any new results for this call.
        var noCache = false;
        if (iterationTypesCache.TryGetValue(key, out var cached))
        {
            if (errorNode is null || cached.HasTypes())
            {
                return cached;
            }
            noCache = true;
        }
        var result = GetIterationTypesOfIterableWorker(t, use, errorNode, noCache);
        if (!noCache)
        {
            iterationTypesCache[key] = result;
        }
        return result;
    }

    // checker.go:6266-6334
    internal IterationTypes GetIterationTypesOfIterableWorker(Type t, IterationUse use, Node? errorNode, bool noCache)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            var allIterationTypes = new List<IterationTypes>(t.Types().Length);
            foreach (var constituent in t.Types())
            {
                var iterationTypes = GetIterationTypesOfIterableWorker(constituent, use, null, noCache);
                if (!iterationTypes.HasTypes())
                {
                    if (errorNode is not null)
                    {
                        AddDeferredDiagnostic(() =>
                        {
                            ReportTypeNotIterableError(errorNode, t, (use & IterationUse.AllowsAsyncIterablesFlag) != 0);
                        });
                    }
                    return default;
                }
                allIterationTypes.Add(iterationTypes);
            }
            return CombineIterationTypes(allIterationTypes.ToArray());
        }
        // PORT-NOTE: Go declares `var diags []*ast.Diagnostic` (nil) and threads `&diags`; the C#
        // diagnosticOutput convention is a pre-allocated shared List that callees Add to (never
        // reassign), so we pre-allocate an empty list — Count reflects appends identically.
        List<Diagnostic> diags = [];
        if ((use & IterationUse.AllowsAsyncIterablesFlag) != 0)
        {
            var iterationTypes = GetIterationTypesOfIterableFast(t, asyncIterationTypesResolver!);
            if (iterationTypes.HasTypes())
            {
                if ((use & IterationUse.ForOfFlag) != 0)
                {
                    return GetAsyncFromSyncIterationTypes(iterationTypes, errorNode);
                }
                return iterationTypes;
            }
            iterationTypes = GetIterationTypesOfIterableSlow(t, asyncIterationTypesResolver!, errorNode, diags);
            if (iterationTypes.HasTypes())
            {
                if (diags.Count != 0)
                {
                    foreach (var d in diags)
                    {
                        AddDiagnostic(d);
                    }
                }
                return iterationTypes;
            }
        }
        if ((use & IterationUse.AllowsSyncIterablesFlag) != 0)
        {
            var iterationTypes = GetIterationTypesOfIterableFast(t, syncIterationTypesResolver!);
            if (iterationTypes.HasTypes())
            {
                if ((use & IterationUse.AllowsAsyncIterablesFlag) != 0)
                {
                    return GetAsyncFromSyncIterationTypes(iterationTypes, errorNode);
                }
                return iterationTypes;
            }
            iterationTypes = GetIterationTypesOfIterableSlow(t, syncIterationTypesResolver!, errorNode, diags);
            if (iterationTypes.HasTypes())
            {
                if (diags.Count != 0)
                {
                    foreach (var d in diags)
                    {
                        AddDiagnostic(d);
                    }
                }
                if ((use & IterationUse.AllowsAsyncIterablesFlag) != 0)
                {
                    return GetAsyncFromSyncIterationTypes(iterationTypes, errorNode);
                }
                return iterationTypes;
            }
        }
        if (errorNode is not null)
        {
            // We defer the diagnostic because TypeToString may attempt to resolve symbols that are already being
            // resolved, possibly causing circularities.
            AddDeferredDiagnostic(() =>
            {
                var diagnostic = ReportTypeNotIterableError(errorNode, t, (use & IterationUse.AllowsAsyncIterablesFlag) != 0);
                foreach (var d in diags)
                {
                    diagnostic?.AddRelatedInfo(d);
                }
            });
        }
        return default;
    }

    // checker.go:6336-6361
    internal IterationTypes GetIterationTypesOfIterableFast(Type t, IterationTypesResolver r)
    {
        // As an optimization, if the type is an instantiation of the following global type, then
        // just grab its related type arguments:
        // - `Iterable<T, TReturn, TNext>` or `AsyncIterable<T, TReturn, TNext>`
        // - `IteratorObject<T, TReturn, TNext>` or `AsyncIteratorObject<T, TReturn, TNext>`
        // - `IterableIterator<T, TReturn, TNext>` or `AsyncIterableIterator<T, TReturn, TNext>`
        // - `Generator<T, TReturn, TNext>` or `AsyncGenerator<T, TReturn, TNext>`
        if (IsReferenceToType(t, r.getGlobalIterableType()) ||
            IsReferenceToType(t, r.getGlobalIteratorObjectType()) ||
            IsReferenceToType(t, r.getGlobalIterableIteratorType()) ||
            IsReferenceToType(t, r.getGlobalGeneratorType()))
        {
            var typeArguments = GetTypeArguments(t);
            return r.GetResolvedIterationTypes(typeArguments[0], typeArguments[1], typeArguments[2]);
        }
        // As an optimization, if the type is an instantiation of one of the following global types, then
        // just grab the related type argument:
        // - `ArrayIterator<T>`
        // - `MapIterator<T>`
        // - `SetIterator<T>`
        // - `StringIterator<T>`
        // - `ReadableStreamAsyncIterator<T>`
        if (IsReferenceToSomeType(t, r.getGlobalBuiltinIteratorTypes()))
        {
            return r.GetResolvedIterationTypes(GetTypeArguments(t)[0], GetBuiltinIteratorReturnType(), unknownType!);
        }
        return default;
    }

    // checker.go:6371-6373
    internal bool IsReferenceToType(Type? t, Type target) =>
        t is not null && (t.objectFlags & ObjectFlags.Reference) != 0 && t.Target() == target;

    // checker.go:6375-6377
    internal bool IsReferenceToSomeType(Type? t, Type[] targets) =>
        t is not null && (t.objectFlags & ObjectFlags.Reference) != 0 && Array.IndexOf(targets, t.Target()) >= 0;

    // checker.go:6379-6381
    internal Type GetBuiltinIteratorReturnType() =>
        CoreUtil.IfElse(strictBuiltinIteratorReturn, undefinedType!, anyType!);

    // Gets the *yield*, *return*, and *next* types of an `Iterable`-like or `AsyncIterable`-like
    // type from its members.
    //
    // If we successfully found the *yield*, *return*, and *next* types, an `IterationTypes` with non-nil
    // members is returned. Otherwise, a default `IterationTypes{}` is returned.
    //
    // NOTE: You probably don't want to call this directly and should be calling
    // `getIterationTypesOfIterable` instead.
    // checker.go:6439-6458
    internal IterationTypes GetIterationTypesOfIterableSlow(Type t, IterationTypesResolver r, Node? errorNode, List<Diagnostic>? diagnosticOutput)
    {
        var method = GetPropertyOfType(t, GetPropertyNameForKnownSymbolName(r.iteratorSymbolName));
        if (method is not null && (method.Flags & SymbolFlags.Optional) == 0)
        {
            var methodType = GetTypeOfSymbol(method);
            if (IsTypeAny(methodType))
            {
                return new IterationTypes(anyType, anyType, anyType);
            }
            var allSignatures = GetSignaturesOfType(methodType, SignatureKind.Call);
            var validSignatures = CoreUtil.Filter(allSignatures, sig => GetMinArgumentCount(sig) == 0);
            if (validSignatures is not null && validSignatures.Length != 0)
            {
                var iteratorType = GetIntersectionType(CoreUtil.Map(validSignatures, GetReturnTypeOfSignature));
                return GetIterationTypesOfIteratorWorker(iteratorType, r, errorNode, diagnosticOutput);
            }
            if (errorNode is not null && allSignatures.Length != 0)
            {
                CheckTypeAssignableToEx(t, r.getGlobalIterableTypeChecked(), errorNode, null, diagnosticOutput);
            }
        }
        return default;
    }

    // Gets the *yield*, *return*, and *next* types from an `Iterator`-like or `AsyncIterator`-like type.
    //
    // If we successfully found the *yield*, *return*, and *next* types, an `IterationTypes` with non-nil
    // members is returned. Otherwise, a default `IterationTypes{}` is returned.
    // checker.go:6464-6466
    internal IterationTypes GetIterationTypesOfIterator(Type t, IterationTypesResolver r, Node? errorNode, List<Diagnostic>? diagnosticOutput)
    {
        return GetIterationTypesOfIteratorWorker(t, r, errorNode, diagnosticOutput);
    }

    // Gets the *yield*, *return*, and *next* types from an `Iterator`-like or `AsyncIterator`-like type.
    //
    // If we successfully found the *yield*, *return*, and *next* types, an `IterationTypes` with non-nil
    // members is returned. Otherwise, a default `IterationTypes{}` is returned.
    //
    // NOTE: You probably don't want to call this directly and should be calling `getIterationTypesOfIterator` instead.
    // checker.go:6474-6483
    internal IterationTypes GetIterationTypesOfIteratorWorker(Type t, IterationTypesResolver r, Node? errorNode, List<Diagnostic>? diagnosticOutput)
    {
        if (IsTypeAny(t))
        {
            return new IterationTypes(anyType, anyType, anyType);
        }
        var iterationTypes = GetIterationTypesOfIteratorFast(t, r);
        if (iterationTypes.HasTypes())
        {
            return iterationTypes;
        }
        return GetIterationTypesOfIteratorSlow(t, r, errorNode, diagnosticOutput);
    }

    // checker.go:6485-6510
    internal IterationTypes GetIterationTypesOfIteratorFast(Type t, IterationTypesResolver r)
    {
        // As an optimization, if the type is an instantiation of the following global type, then
        // just grab its related type arguments:
        // - `Iterable<T, TReturn, TNext>` or `AsyncIterable<T, TReturn, TNext>`
        // - `IteratorObject<T, TReturn, TNext>` or `AsyncIteratorObject<T, TReturn, TNext>`
        // - `IterableIterator<T, TReturn, TNext>` or `AsyncIterableIterator<T, TReturn, TNext>`
        // - `Generator<T, TReturn, TNext>` or `AsyncGenerator<T, TReturn, TNext>`
        if (IsReferenceToType(t, r.getGlobalIteratorType()) ||
            IsReferenceToType(t, r.getGlobalIteratorObjectType()) ||
            IsReferenceToType(t, r.getGlobalIterableIteratorType()) ||
            IsReferenceToType(t, r.getGlobalGeneratorType()))
        {
            var typeArguments = GetTypeArguments(t);
            return r.GetResolvedIterationTypes(typeArguments[0], typeArguments[1], typeArguments[2]);
        }
        // As an optimization, if the type is an instantiation of one of the following global types, then
        // just grab the related type argument:
        // - `ArrayIterator<T>`
        // - `MapIterator<T>`
        // - `SetIterator<T>`
        // - `StringIterator<T>`
        // - `ReadableStreamAsyncIterator<T>`
        if (IsReferenceToSomeType(t, r.getGlobalBuiltinIteratorTypes()))
        {
            return r.GetResolvedIterationTypes(GetTypeArguments(t)[0], GetBuiltinIteratorReturnType(), unknownType!);
        }
        return default;
    }

    // checker.go:6512-6518
    internal IterationTypes GetIterationTypesOfIteratorSlow(Type t, IterationTypesResolver r, Node? errorNode, List<Diagnostic>? diagnosticOutput)
    {
        return CombineIterationTypes(
        [
            GetIterationTypesOfMethod(t, r, "next", errorNode, diagnosticOutput),
            GetIterationTypesOfMethod(t, r, "return", errorNode, diagnosticOutput),
            GetIterationTypesOfMethod(t, r, "throw", errorNode, diagnosticOutput),
        ]);
    }

    // checker.go:6520-6621
    internal IterationTypes GetIterationTypesOfMethod(Type t, IterationTypesResolver resolver, string methodName, Node? errorNode, List<Diagnostic>? diagnosticOutput)
    {
        var method = GetPropertyOfType(t, methodName);
        // Ignore 'return' or 'throw' if they are missing.
        if (method is null && methodName != "next")
        {
            return default;
        }
        Type? methodType = null;
        if (method is not null && !(methodName == "next" && (method.Flags & SymbolFlags.Optional) != 0))
        {
            if (methodName == "next")
            {
                methodType = GetTypeOfSymbol(method);
            }
            else
            {
                methodType = GetTypeWithFacts(GetTypeOfSymbol(method), TypeFacts.NEUndefinedOrNull);
            }
        }
        if (IsTypeAny(methodType))
        {
            return new IterationTypes(anyType, anyType, anyType);
        }
        // Both async and non-async iterators *must* have a `next` method.
        Signature[]? methodSignatures = null;
        if (methodType is not null)
        {
            methodSignatures = GetSignaturesOfType(methodType, SignatureKind.Call);
        }
        if (methodSignatures is null || methodSignatures.Length == 0)
        {
            if (errorNode is not null)
            {
                var diagnostic = CoreUtil.IfElse(methodName == "next", resolver.mustHaveANextMethodDiagnostic, resolver.mustBeAMethodDiagnostic);
                ReportDiagnostic(NewDiagnosticForNode(errorNode, diagnostic, methodName), diagnosticOutput);
            }
            return default;
        }
        // If the method signature comes exclusively from the global iterator or generator type,
        // create iteration types from its type arguments like `getIterationTypesOfIteratorFast`
        // does (so as to remove `undefined` from the next and return types). We arrive here when
        // a contextual type for a generator was not a direct reference to one of those global types,
        // but looking up `methodType` referred to one of them (and nothing else). E.g., in
        // `interface SpecialIterator extends Iterator<number> {}`, `SpecialIterator` is not a
        // reference to `Iterator`, but its `next` member derives exclusively from `Iterator`.
        if (methodSignatures.Length == 1 && methodType!.symbol is not null)
        {
            var globalGeneratorType = resolver.getGlobalGeneratorType();
            var globalIteratorType = resolver.getGlobalIteratorType();
            var isGeneratorMethod = globalGeneratorType.symbol is not null && globalGeneratorType.symbol.Members is not null &&
                globalGeneratorType.symbol.Members.GetValueOrDefault(methodName) == methodType.symbol;
            var isIteratorMethod = !isGeneratorMethod && globalIteratorType.symbol is not null && globalIteratorType.symbol.Members is not null &&
                globalIteratorType.symbol.Members.GetValueOrDefault(methodName) == methodType.symbol;
            if (isGeneratorMethod || isIteratorMethod)
            {
                var typeParameters = CoreUtil.IfElse(isGeneratorMethod, globalGeneratorType, globalIteratorType).AsInterfaceType()!.TypeParameters()!;
                var mapper = methodType.Mapper();
                Type? nextType = null;
                if (methodName == "next")
                {
                    nextType = mapper!.Map(typeParameters[2]);
                }
                return new IterationTypes(mapper!.Map(typeParameters[0]), mapper.Map(typeParameters[1]), nextType);
            }
        }
        // Extract the first parameter and return type of each signature.
        List<Type>? methodParameterTypes = null;
        List<Type>? methodReturnTypes = null;
        foreach (var signature in methodSignatures)
        {
            if (methodName != "throw" && signature.parameters is not null && signature.parameters.Length != 0)
            {
                (methodParameterTypes ??= new List<Type>()).Add(GetTypeAtPosition(signature, 0));
            }
            (methodReturnTypes ??= new List<Type>()).Add(GetReturnTypeOfSignature(signature));
        }
        // Resolve the *next* or *return* type from the first parameter of a `next()` or
        // `return()` method, respectively.
        List<Type>? returnTypes = null;
        Type? nextType2 = null;
        if (methodName != "throw")
        {
            Type methodParameterType;
            if (methodParameterTypes is not null)
            {
                methodParameterType = GetUnionType(methodParameterTypes.ToArray());
            }
            else
            {
                methodParameterType = unknownType!;
            }
            if (methodName == "next")
            {
                // The value of `next(value)` is *not* awaited by async generators
                nextType2 = methodParameterType;
            }
            else if (methodName == "return")
            {
                // The value of `return(value)` *is* awaited by async generators
                var resolvedMethodParameterType = CoreUtil.OrElse(resolver.resolveIterationType(methodParameterType, errorNode), anyType!)!;
                (returnTypes ??= new List<Type>()).Add(resolvedMethodParameterType);
            }
        }
        // Resolve the *yield* and *return* types from the return type of the method (i.e. `IteratorResult`)
        Type? yieldType;
        Type methodReturnType;
        if (methodReturnTypes is not null)
        {
            methodReturnType = GetIntersectionType(methodReturnTypes.ToArray());
        }
        else
        {
            methodReturnType = neverType!;
        }
        var resolvedMethodReturnType = CoreUtil.OrElse(resolver.resolveIterationType(methodReturnType, errorNode), anyType!)!;
        var iterationTypes = GetIterationTypesOfIteratorResult(resolvedMethodReturnType);
        if (!iterationTypes.HasTypes())
        {
            if (errorNode is not null)
            {
                ReportDiagnostic(NewDiagnosticForNode(errorNode, resolver.mustHaveAValueDiagnostic, methodName), diagnosticOutput);
            }
            yieldType = anyType;
            (returnTypes ??= new List<Type>()).Add(anyType!);
        }
        else
        {
            yieldType = iterationTypes.YieldType;
            (returnTypes ??= new List<Type>()).Add(iterationTypes.ReturnType!);
        }
        return new IterationTypes(yieldType, GetUnionType(returnTypes?.ToArray()), nextType2);
    }

    // Gets the *yield* and *return* types of an `IteratorResult`-like type.
    //
    // If we are unable to determine a *yield* or a *return* type, `noIterationTypes` is
    // returned to indicate to the caller that it should handle the error. Otherwise, an
    // `IterationTypes` record is returned.
    // checker.go:6628-6659
    internal IterationTypes GetIterationTypesOfIteratorResult(Type t)
    {
        if (IsTypeAny(t))
        {
            return new IterationTypes(anyType, anyType, anyType);
        }
        // As an optimization, if the type is an instantiation of one of the global `IteratorYieldResult<T>`
        // or `IteratorReturnResult<TReturn>` types, then just grab its type argument.
        if (IsReferenceToType(t, getGlobalIteratorYieldResultType()))
        {
            return new IterationTypes(GetTypeArguments(t)[0], null, null);
        }
        if (IsReferenceToType(t, getGlobalIteratorReturnResultType()))
        {
            return new IterationTypes(null, GetTypeArguments(t)[0], null);
        }
        // Choose any constituents that can produce the requested iteration type.
        var yieldIteratorResult = FilterType(t, IsYieldIteratorResult);
        Type? yieldType = null;
        if (yieldIteratorResult != neverType)
        {
            yieldType = GetTypeOfPropertyOfType(yieldIteratorResult, "value" /* as __String */);
        }
        var returnIteratorResult = FilterType(t, IsReturnIteratorResult);
        Type? returnType = null;
        if (returnIteratorResult != neverType)
        {
            returnType = GetTypeOfPropertyOfType(returnIteratorResult, "value" /* as __String */);
        }
        if (yieldType is null && returnType is null)
        {
            return default;
        }
        // From https://tc39.github.io/ecma262/#sec-iteratorresult-interface
        // > ... If the iterator does not have a return value, `value` is `undefined`. In that case, the
        // > `value` property may be absent from the conforming object if it does not inherit an explicit
        // > `value` property.
        return new IterationTypes(yieldType, CoreUtil.OrElse(returnType, voidType!), null);
    }

    // checker.go:6399-6405
    internal IterationTypes CombineIterationTypes(IterationTypes[] iterationTypes)
    {
        return new IterationTypes(
            GetIterationTypeUnion(iterationTypes, t => t.YieldType),
            GetIterationTypeUnion(iterationTypes, t => t.ReturnType),
            GetIterationTypeUnion(iterationTypes, t => t.NextType));
    }

    // checker.go:6407-6413
    internal Type? GetIterationTypeUnion(IterationTypes[] iterationTypes, Func<IterationTypes, Type?> f)
    {
        var types = CoreUtil.MapNonNil(iterationTypes, f);
        if (types is null || types.Length == 0)
        {
            return null;
        }
        // MapNonNil strips the nulls, so every element is non-null; project to a non-nullable array.
        return GetUnionType(Array.ConvertAll(types, x => x!));
    }

    // checker.go:6415-6429
    internal IterationTypes GetAsyncFromSyncIterationTypes(IterationTypes iterationTypes, Node? errorNode)
    {
        if (!iterationTypes.HasTypes() ||
            iterationTypes.YieldType == anyType && iterationTypes.ReturnType == anyType && iterationTypes.NextType == anyType)
        {
            return iterationTypes;
        }
        // if we're requesting diagnostics, report errors for a missing `Awaited<T>`.
        if (errorNode is not null)
        {
            getGlobalAwaitedSymbol();
        }
        return new IterationTypes(
            CoreUtil.OrElse(GetAwaitedTypeEx(iterationTypes.YieldType!, errorNode, null /*diagnosticMessage*/), anyType!),
            CoreUtil.OrElse(GetAwaitedTypeEx(iterationTypes.ReturnType!, errorNode, null /*diagnosticMessage*/), anyType!),
            iterationTypes.NextType);
    }

    // checker.go:6661-6663
    internal bool IsYieldIteratorResult(Type t) => IsIteratorResult(t, IterationTypeKind.Yield);

    // checker.go:6665-6667
    internal bool IsReturnIteratorResult(Type t) => IsIteratorResult(t, IterationTypeKind.Return);

    // checker.go:6669-6676
    internal bool IsIteratorResult(Type t, IterationTypeKind kind)
    {
        // From https://tc39.github.io/ecma262/#sec-iteratorresult-interface:
        // > [done] is the result status of an iterator `next` method call. If the end of the iterator was reached `done` is `true`.
        // > If the end was not reached `done` is `false` and a value is available.
        // > If a `done` property (either own or inherited) does not exist, it is consider to have the value `false`.
        var doneType = CoreUtil.OrElse(GetTypeOfPropertyOfType(t, "done"), falseType!);
        return IsTypeAssignableTo(CoreUtil.IfElse(kind == IterationTypeKind.Yield, falseType!, trueType!), doneType!);
    }

    // checker.go:6678-6691
    // PORT-NOTE: Go returns the emitted *ast.Diagnostic (via errorAndMaybeSuggestAwait); the C#
    // errorAndMaybeSuggestAwait helper is still a void stub owned by the statements wave, so we emit
    // the error for its side-effect and return null. The single related-info consumer guards with `?.`.
    internal Diagnostic? ReportTypeNotIterableError(Node errorNode, Type t, bool allowAsyncIterables)
    {
        global::Ts.Diagnostics.Message message;
        if (allowAsyncIterables)
        {
            message = Diag.Type_0_must_have_a_Symbol_asyncIterator_method_that_returns_an_async_iterator;
        }
        else
        {
            message = Diag.Type_0_must_have_a_Symbol_iterator_method_that_returns_an_iterator;
        }
        var suggestAwait = GetAwaitedTypeOfPromise(t) is not null || (!allowAsyncIterables &&
            NodeTests.IsForOfStatement(errorNode.Parent!) &&
            errorNode.Parent!.Expression() == errorNode &&
            getGlobalAsyncIterableType() != emptyGenericType &&
            IsTypeAssignableTo(t, CreateTypeFromGenericGlobalType(getGlobalAsyncIterableType(), [anyType!, anyType!, anyType!])));
        return ErrorAndMaybeSuggestAwait(errorNode, suggestAwait, message, TypeToString(t));
    }

    // checker.go:6693-6705
    internal (global::Ts.Diagnostics.Message, bool) GetIterationDiagnosticDetails(IterationUse use, Type inputType, bool allowsStrings)
    {
        var yieldType = GetIterationTypeOfIterable(use, IterationTypeKind.Yield, inputType, null /*errorNode*/);
        if (yieldType is not null)
        {
            return (Diag.Type_0_can_only_be_iterated_through_when_using_the_downlevelIteration_flag_or_with_a_target_of_es2015_or_higher, false);
        }
        if (inputType.symbol is not null && IsES2015OrLaterIterable(inputType.symbol.Name))
        {
            return (Diag.Type_0_can_only_be_iterated_through_when_using_the_downlevelIteration_flag_or_with_a_target_of_es2015_or_higher, true);
        }
        if (allowsStrings)
        {
            return (Diag.Type_0_is_not_an_array_type_or_a_string_type, true);
        }
        return (Diag.Type_0_is_not_an_array_type, true);
    }

    // checker.go:6707-6713
    internal static bool IsES2015OrLaterIterable(string n)
    {
        switch (n)
        {
            case "Float32Array":
            case "Float64Array":
            case "Int16Array":
            case "Int32Array":
            case "Int8Array":
            case "NodeList":
            case "Uint16Array":
            case "Uint32Array":
            case "Uint8Array":
            case "Uint8ClampedArray":
                return true;
        }
        return false;
    }
}

// checker.go:6383-6397 (IterationTypes.hasTypes / getType) + 6363-6369 (IterationTypesResolver.getResolvedIterationTypes).
// PORT-NOTE: IterationTypes (Signature.cs) and IterationTypesResolver (Checker.cs) are not declared
// partial, so these Go pointer-receiver methods are ported as extension methods here.
internal static class IterationTypesExtensions
{
    // checker.go:6383-6385
    internal static bool HasTypes(this IterationTypes iterationTypes) =>
        iterationTypes.YieldType is not null || iterationTypes.ReturnType is not null || iterationTypes.NextType is not null;

    // checker.go:6387-6397
    internal static Type? GetType(this IterationTypes iterationTypes, IterationTypeKind typeKind)
    {
        switch (typeKind)
        {
            case IterationTypeKind.Yield:
                return iterationTypes.YieldType;
            case IterationTypeKind.Return:
                return iterationTypes.ReturnType;
            case IterationTypeKind.Next:
                return iterationTypes.NextType;
        }
        throw new InvalidOperationException("Unhandled case in getType(IterationTypeKind)");
    }

    // checker.go:6363-6369
    internal static IterationTypes GetResolvedIterationTypes(this IterationTypesResolver r, Type yieldType, Type returnType, Type nextType)
    {
        return new IterationTypes(
            CoreUtil.OrElse(r.resolveIterationType(yieldType, null /*errorNode*/), yieldType),
            CoreUtil.OrElse(r.resolveIterationType(returnType, null /*errorNode*/), returnType),
            nextType);
    }
}

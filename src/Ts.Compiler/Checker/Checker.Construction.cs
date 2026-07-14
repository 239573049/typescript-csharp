// Ported from tsgo internal/checker/checker.go (section: NewChecker + init helpers, lines
// 902-1513). plan/03 §4.2 #3. The constructor body is REAL (it fixes field initialization);
// the deep global-resolution methods it *would* reach (mergeGlobalSymbol, getGlobalType, …) are
// stubs, and initializeChecker is a no-op for the skeleton (global symbol merging + global type
// resolution land in later waves) — this is what lets NewChecker complete without throwing.

using System.Linq;
using System.Threading;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // flow.go:623-632 — typeof narrowing facts. Only the sorted key set is used at construction.
    private static readonly Dictionary<string, TypeFacts> typeofNEFacts = new()
    {
        ["string"] = TypeFacts.TypeofNEString,
        ["number"] = TypeFacts.TypeofNENumber,
        ["bigint"] = TypeFacts.TypeofNEBigInt,
        ["boolean"] = TypeFacts.TypeofNEBoolean,
        ["symbol"] = TypeFacts.TypeofNESymbol,
        ["undefined"] = TypeFacts.NEUndefined,
        ["object"] = TypeFacts.TypeofNEObject,
        ["function"] = TypeFacts.TypeofNEFunction,
    };

    // checker.go:902-1116
    public static (Checker Checker, Lock Lock) NewChecker(Program program, Tracer? tracer)
    {
        program.BindSourceFiles();

        var c = new Checker();
        c.id = Interlocked.Increment(ref nextCheckerId);
        c.tracer = tracer;
        c.program = program;
        c.compilerOptions = program.Options();
        c.files = program.SourceFiles();
        c.fileIndexMap = CreateFileIndexMap(c.files);
        c.compareSymbols = c.CompareSymbolsWorker; // Closure optimization
        c.compareSymbolChains = c.CompareSymbolChainsWorker; // Closure optimization
        c.languageVersion = c.compilerOptions.GetEmitScriptTarget();
        c.moduleKind = c.compilerOptions.GetEmitModuleKind();
        c.moduleResolutionKind = c.compilerOptions.GetModuleResolutionKind();
        c.legacyDecorators = c.compilerOptions.ExperimentalDecorators == Tristate.True;
        c.emitStandardClassFields = c.compilerOptions.GetEmitStandardClassFields();
        c.strictNullChecks = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.StrictNullChecks);
        c.strictFunctionTypes = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.StrictFunctionTypes);
        c.strictBindCallApply = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.StrictBindCallApply);
        c.strictPropertyInitialization = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.StrictPropertyInitialization);
        c.strictBuiltinIteratorReturn = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.StrictBuiltinIteratorReturn);
        c.noImplicitAny = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.NoImplicitAny);
        c.noImplicitThis = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.NoImplicitThis);
        c.useUnknownInCatchVariables = c.compilerOptions.GetStrictOptionValue(c.compilerOptions.UseUnknownInCatchVariables);
        c.exactOptionalPropertyTypes = c.compilerOptions.ExactOptionalPropertyTypes == Tristate.True;
        c.canCollectSymbolAliasAccessibilityData = c.compilerOptions.VerbatimModuleSyntax.IsFalseOrUnknown();
        c.arrayVariances = [VarianceFlags.Covariant];
        c.globals = new SymbolTable(CountGlobalSymbols(c.files));
        c.evaluate = global::Ts.Evaluator.EvaluatorFactory.NewEvaluator(c.EvaluateEntity, OuterExpressionKinds.Parentheses);
        c.stringLiteralTypes = new();
        c.numberLiteralTypes = new();
        c.bigintLiteralTypes = new();
        c.enumLiteralTypes = new();
        c.enumNaNLiteralTypes = new(ReferenceEqualityComparer<Symbol>.Instance);
        c.indexedAccessTypes = new();
        c.templateLiteralTypes = new();
        c.stringMappingTypes = new();
        c.uniqueESSymbolTypes = new(ReferenceEqualityComparer<Symbol>.Instance);
        c.thisExpandoKinds = new(ReferenceEqualityComparer<Symbol>.Instance);
        c.thisExpandoLocations = new(ReferenceEqualityComparer<Symbol>.Instance);
        c.subtypeReductionCache = new();
        c.cachedTypes = new();
        c.cachedSignatures = new();
        c.undefinedProperties = new();
        c.narrowedTypes = new();
        c.assignmentReducedTypes = new();
        c.discriminatedContextualTypes = new();
        c.instantiationExpressionTypes = new();
        c.substitutionTypes = new();
        c.reverseMappedCache = new();
        c.reverseHomomorphicMappedCache = new();
        c.iterationTypesCache = new();
        c.undefinedSymbol = c.NewSymbol(SymbolFlags.Property, "undefined");
        c.argumentsSymbol = c.NewSymbol(SymbolFlags.Property, "arguments");
        c.requireSymbol = c.NewSymbol(SymbolFlags.Property, "require");
        c.unknownSymbol = c.NewSymbol(SymbolFlags.Property, "unknown");
        c.unresolvedSymbols = new();
        c.errorTypes = new();
        c.moduleSymbols = new(ReferenceEqualityComparer<Node>.Instance);
        c.globalThisSymbol = c.NewSymbolEx(SymbolFlags.Module, "globalThis", CheckFlags.Readonly);
        c.globalThisSymbol.Exports = c.globals;
        c.globals[c.globalThisSymbol.Name] = c.globalThisSymbol;
        c.resolveName = c.CreateNameResolver().Resolve;
        c.resolveNameForSymbolSuggestion = c.CreateNameResolverForSuggestion().Resolve;
        c.tupleTypes = new();
        c.unionTypes = new();
        c.unionOfUnionTypes = new();
        c.intersectionTypes = new();
        c.propertiesTypes = new();
        c.mergedSymbols = new(ReferenceEqualityComparer<Symbol>.Instance);
        c.patternForType = new(ReferenceEqualityComparer<Type>.Instance);
        c.contextFreeTypes = new(ReferenceEqualityComparer<Node>.Instance);
        c.anyType = c.NewIntrinsicType(TypeFlags.Any, "any");
        c.autoType = c.NewIntrinsicTypeEx(TypeFlags.Any, "any", ObjectFlags.NonInferrableType);
        c.wildcardType = c.NewIntrinsicType(TypeFlags.Any, "any");
        c.blockedStringType = c.NewIntrinsicType(TypeFlags.Any, "any");
        c.errorType = c.NewIntrinsicType(TypeFlags.Any, "error");
        c.unresolvedType = c.NewIntrinsicType(TypeFlags.Any, "unresolved");
        c.nonInferrableAnyType = c.NewIntrinsicTypeEx(TypeFlags.Any, "any", ObjectFlags.ContainsWideningType);
        c.intrinsicMarkerType = c.NewIntrinsicType(TypeFlags.Any, "intrinsic");
        c.unknownType = c.NewIntrinsicType(TypeFlags.Unknown, "unknown");
        c.undefinedType = c.NewIntrinsicType(TypeFlags.Undefined, "undefined");
        c.undefinedWideningType = c.CreateWideningType(c.undefinedType);
        c.missingType = c.NewIntrinsicType(TypeFlags.Undefined, "undefined");
        c.undefinedOrMissingType = CoreUtil.IfElse(c.exactOptionalPropertyTypes, c.missingType, c.undefinedType);
        c.optionalType = c.NewIntrinsicType(TypeFlags.Undefined, "undefined");
        c.nullType = c.NewIntrinsicType(TypeFlags.Null, "null");
        c.nullWideningType = c.CreateWideningType(c.nullType);
        c.stringType = c.NewIntrinsicType(TypeFlags.String, "string");
        c.numberType = c.NewIntrinsicType(TypeFlags.Number, "number");
        c.bigintType = c.NewIntrinsicType(TypeFlags.BigInt, "bigint");
        c.regularFalseType = c.NewLiteralType(TypeFlags.BooleanLiteral, false, null);
        c.falseType = c.NewLiteralType(TypeFlags.BooleanLiteral, false, c.regularFalseType);
        c.regularFalseType.AsLiteralType().freshType = c.falseType;
        c.falseType.AsLiteralType().freshType = c.falseType;
        c.regularTrueType = c.NewLiteralType(TypeFlags.BooleanLiteral, true, null);
        c.trueType = c.NewLiteralType(TypeFlags.BooleanLiteral, true, c.regularTrueType);
        c.regularTrueType.AsLiteralType().freshType = c.trueType;
        c.trueType.AsLiteralType().freshType = c.trueType;
        c.booleanType = c.GetUnionType([c.regularFalseType, c.regularTrueType]);
        c.esSymbolType = c.NewIntrinsicType(TypeFlags.ESSymbol, "symbol");
        c.voidType = c.NewIntrinsicType(TypeFlags.Void, "void");
        c.neverType = c.NewIntrinsicType(TypeFlags.Never, "never");
        c.silentNeverType = c.NewIntrinsicTypeEx(TypeFlags.Never, "never", ObjectFlags.NonInferrableType);
        c.implicitNeverType = c.NewIntrinsicType(TypeFlags.Never, "never");
        c.unreachableNeverType = c.NewIntrinsicType(TypeFlags.Never, "never");
        c.nonPrimitiveType = c.NewIntrinsicType(TypeFlags.NonPrimitive, "object");
        c.stringOrNumberType = c.GetUnionType([c.stringType, c.numberType]);
        c.stringNumberSymbolType = c.GetUnionType([c.stringType, c.numberType, c.esSymbolType]);
        c.numberOrBigIntType = c.GetUnionType([c.numberType, c.bigintType]);
        c.numericStringType = c.GetTemplateLiteralType(["", ""], [c.numberType]); // The `${number}` type
        c.templateConstraintType = c.GetUnionType([c.stringType, c.numberType, c.booleanType, c.bigintType, c.nullType, c.undefinedType]);
        c.uniqueLiteralType = c.NewIntrinsicType(TypeFlags.Never, "never"); // Special `never` flagged by union reduction to behave as a literal
        c.uniqueLiteralMapper = TypeMapper.NewFunctionTypeMapper(c.GetUniqueLiteralTypeForTypeParameter);
        c.reportUnreliableMapper = TypeMapper.NewFunctionTypeMapper(c.ReportUnreliableWorker);
        c.reportUnmeasurableMapper = TypeMapper.NewFunctionTypeMapper(c.ReportUnmeasurableWorker);
        c.restrictiveMapper = TypeMapper.NewFunctionTypeMapper(c.RestrictiveMapperWorker);
        c.permissiveMapper = TypeMapper.NewFunctionTypeMapper(c.PermissiveMapperWorker);
        c.emptyObjectType = c.NewAnonymousType(null, null, null, null, null);
        c.emptyJsxObjectType = c.NewAnonymousType(null, null, null, null, null);
        c.emptyFreshJsxObjectType = c.NewAnonymousType(null, null, null, null, null);
        c.emptyTypeLiteralType = c.NewAnonymousType(c.NewSymbol(SymbolFlags.TypeLiteral, InternalSymbolName.Type), null, null, null, null);
        c.unknownEmptyObjectType = c.NewAnonymousType(null, null, null, null, null);
        c.unknownUnionType = c.CreateUnknownUnionType();
        c.emptyGenericType = c.NewAnonymousType(null, null, null, null, null);
        c.emptyGenericType.AsObjectType()!.instantiations = new();
        c.anyFunctionType = c.NewAnonymousType(null, null, null, null, null);
        c.anyFunctionType.objectFlags |= ObjectFlags.NonInferrableType;
        c.noConstraintType = c.NewAnonymousType(null, null, null, null, null);
        c.circularConstraintType = c.NewAnonymousType(null, null, null, null, null);
        c.resolvingDefaultType = c.NewAnonymousType(null, null, null, null, null);
        c.markerSuperType = c.NewTypeParameter(null);
        c.markerSubType = c.NewTypeParameter(null);
        c.markerSubType.AsTypeParameter().constraint = c.markerSuperType;
        c.markerOtherType = c.NewTypeParameter(null);
        c.markerSuperTypeForCheck = c.NewTypeParameter(null);
        c.markerSubTypeForCheck = c.NewTypeParameter(null);
        c.markerSubTypeForCheck.AsTypeParameter().constraint = c.markerSuperTypeForCheck;
        c.noTypePredicate = new TypePredicate { kind = TypePredicateKind.Identifier, parameterIndex = 0, parameterName = "<<unresolved>>", t = c.anyType };
        c.anySignature = c.NewSignature(SignatureFlags.None, null, null, null, null, c.anyType, null, 0);
        c.unknownSignature = c.NewSignature(SignatureFlags.None, null, null, null, null, c.errorType, null, 0);
        c.resolvingSignature = c.NewSignature(SignatureFlags.None, null, null, null, null, c.anyType, null, 0);
        c.silentNeverSignature = c.NewSignature(SignatureFlags.None, null, null, null, null, c.silentNeverType, null, 0);
        c.cachedArgumentsReferenced = new(ReferenceEqualityComparer<Node>.Instance);
        c.enumNumberIndexInfo = new IndexInfo { keyType = c.numberType, valueType = c.stringType, isReadonly = true };
        c.anyBaseTypeIndexInfo = new IndexInfo { keyType = c.stringType, valueType = c.anyType, isReadonly = false };
        c.emptyStringType = c.GetStringLiteralType("");
        c.zeroType = c.GetNumberLiteralType(new Ts.JsNum.Number(0));
        c.zeroBigIntType = c.GetBigIntLiteralType(default);
        // map-iter: 已排序 — Go sorts typeofNEFacts keys (checker.go:1052) before unioning.
        c.typeofType = c.GetUnionType(CoreUtil.Map(typeofNEFacts.Keys.Order(StringComparer.Ordinal).ToArray(), c.GetStringLiteralType)!);
        c.flowLoopCache = new();
        c.flowNodeReachable = new(ReferenceEqualityComparer<FlowNode>.Instance);
        c.flowNodePostSuper = new(ReferenceEqualityComparer<FlowNode>.Instance);
        c.flowTypeCache = new(ReferenceEqualityComparer<Node>.Instance);
        c.subtypeRelation = new Relation();
        c.strictSubtypeRelation = new Relation();
        c.assignableRelation = new Relation();
        c.comparableRelation = new Relation();
        c.identityRelation = new Relation();
        c.enumRelation = new();
        c.getGlobalESSymbolType = c.GetGlobalTypeResolver("Symbol", 0, false);
        c.getGlobalBigIntType = c.GetGlobalTypeResolver("BigInt", 0, false);
        c.getGlobalImportMetaType = c.GetGlobalTypeResolver("ImportMeta", 0, true);
        c.getGlobalImportAttributesType = c.GetGlobalTypeResolver("ImportAttributes", 0, false);
        c.getGlobalImportAttributesTypeChecked = c.GetGlobalTypeResolver("ImportAttributes", 0, true);
        c.getGlobalNonNullableTypeAliasOrNil = c.GetGlobalTypeAliasResolver("NonNullable", 1, false);
        c.getGlobalExtractSymbol = c.GetGlobalTypeAliasResolver("Extract", 2, true);
        c.getGlobalDisposableType = c.GetGlobalTypeResolver("Disposable", 0, true);
        c.getGlobalAsyncDisposableType = c.GetGlobalTypeResolver("AsyncDisposable", 0, true);
        c.getGlobalAwaitedSymbol = c.GetGlobalTypeAliasResolver("Awaited", 1, true);
        c.getGlobalAwaitedSymbolOrNil = c.GetGlobalTypeAliasResolver("Awaited", 1, false);
        c.getGlobalNaNSymbolOrNil = c.GetGlobalValueSymbolResolver("NaN", false);
        c.getGlobalRecordSymbol = c.GetGlobalTypeAliasResolver("Record", 2, true);
        c.getGlobalTemplateStringsArrayType = c.GetGlobalTypeResolver("TemplateStringsArray", 0, true);
        c.getGlobalESSymbolConstructorSymbolOrNil = c.GetGlobalValueSymbolResolver("Symbol", false);
        c.getGlobalESSymbolConstructorTypeSymbolOrNil = c.GetGlobalTypeSymbolResolver("SymbolConstructor", false);
        c.getGlobalImportCallOptionsType = c.GetGlobalTypeResolver("ImportCallOptions", 0, false);
        c.getGlobalImportCallOptionsTypeChecked = c.GetGlobalTypeResolver("ImportCallOptions", 0, true);
        c.getGlobalPromiseType = c.GetGlobalTypeResolver("Promise", 1, false);
        c.getGlobalPromiseTypeChecked = c.GetGlobalTypeResolver("Promise", 1, true);
        c.getGlobalPromiseLikeType = c.GetGlobalTypeResolver("PromiseLike", 1, true);
        c.getGlobalPromiseConstructorSymbol = c.GetGlobalValueSymbolResolver("Promise", true);
        c.getGlobalPromiseConstructorSymbolOrNil = c.GetGlobalValueSymbolResolver("Promise", false);
        c.getGlobalOmitSymbol = c.GetGlobalTypeAliasResolver("Omit", 2, true);
        c.getGlobalNoInferSymbolOrNil = c.GetGlobalTypeAliasResolver("NoInfer", 1, false);
        c.getGlobalIteratorType = c.GetGlobalTypeResolver("Iterator", 3, false);
        c.getGlobalIterableType = c.GetGlobalTypeResolver("Iterable", 3, false);
        c.getGlobalIterableTypeChecked = c.GetGlobalTypeResolver("Iterable", 3, true);
        c.getGlobalIterableIteratorType = c.GetGlobalTypeResolver("IterableIterator", 3, false);
        c.getGlobalIterableIteratorTypeChecked = c.GetGlobalTypeResolver("IterableIterator", 3, true);
        c.getGlobalIteratorObjectType = c.GetGlobalTypeResolver("IteratorObject", 3, false);
        c.getGlobalGeneratorType = c.GetGlobalTypeResolver("Generator", 3, false);
        c.getGlobalAsyncIteratorType = c.GetGlobalTypeResolver("AsyncIterator", 3, false);
        c.getGlobalAsyncIterableType = c.GetGlobalTypeResolver("AsyncIterable", 3, false);
        c.getGlobalAsyncIterableTypeChecked = c.GetGlobalTypeResolver("AsyncIterable", 3, true);
        c.getGlobalAsyncIterableIteratorType = c.GetGlobalTypeResolver("AsyncIterableIterator", 3, false);
        c.getGlobalAsyncIterableIteratorTypeChecked = c.GetGlobalTypeResolver("AsyncIterableIterator", 3, true);
        c.getGlobalAsyncIteratorObjectType = c.GetGlobalTypeResolver("AsyncIteratorObject", 3, false);
        c.getGlobalAsyncGeneratorType = c.GetGlobalTypeResolver("AsyncGenerator", 3, false);
        c.getGlobalIteratorYieldResultType = c.GetGlobalTypeResolver("IteratorYieldResult", 1, false);
        c.getGlobalIteratorReturnResultType = c.GetGlobalTypeResolver("IteratorReturnResult", 1, false);
        c.getGlobalTypedPropertyDescriptorType = c.GetGlobalTypeResolver("TypedPropertyDescriptor", 1, true);
        c.getGlobalClassDecoratorContextType = c.GetGlobalTypeResolver("ClassDecoratorContext", 1, true);
        c.getGlobalClassMethodDecoratorContextType = c.GetGlobalTypeResolver("ClassMethodDecoratorContext", 2, true);
        c.getGlobalClassGetterDecoratorContextType = c.GetGlobalTypeResolver("ClassGetterDecoratorContext", 2, true);
        c.getGlobalClassSetterDecoratorContextType = c.GetGlobalTypeResolver("ClassSetterDecoratorContext", 2, true);
        c.getGlobalClassAccessorDecoratorContextType = c.GetGlobalTypeResolver("ClassAccessorDecoratorContext", 2, true);
        c.getGlobalClassAccessorDecoratorTargetType = c.GetGlobalTypeResolver("ClassAccessorDecoratorTarget", 2, true);
        c.getGlobalClassAccessorDecoratorResultType = c.GetGlobalTypeResolver("ClassAccessorDecoratorResult", 2, true);
        c.getGlobalClassFieldDecoratorContextType = c.GetGlobalTypeResolver("ClassFieldDecoratorContext", 2, true);
        c.InitializeClosures();
        c.InitializeIterationResolvers();
        c.InitializeChecker();
        return (c, c.mu);
    }

    // checker.go:1118-1124
    internal static Dictionary<SourceFile, int> CreateFileIndexMap(SourceFile[] files)
    {
        var result = new Dictionary<SourceFile, int>(files.Length, ReferenceEqualityComparer<SourceFile>.Instance);
        for (int i = 0; i < files.Length; i++)
        {
            result[files[i]] = i;
        }
        return result;
    }

    // checker.go:1126-1134
    internal static int CountGlobalSymbols(SourceFile[] files)
    {
        int count = 0;
        foreach (var file in files)
        {
            if (!AstUtilities.IsExternalOrCommonJSModule(file))
            {
                count += file.Locals?.Count ?? 0;
            }
        }
        return count;
    }

    // checker.go:1136-1141
    internal Type ReportUnreliableWorker(Type t)
    {
        if (t == markerSuperType || t == markerSubType || t == markerOtherType)
        {
            reliabilityFlags |= RelationComparisonResult.ReportsUnreliable;
        }
        return t;
    }

    // checker.go:1143-1148
    internal Type ReportUnmeasurableWorker(Type t)
    {
        if (t == markerSuperType || t == markerSubType || t == markerOtherType)
        {
            reliabilityFlags |= RelationComparisonResult.ReportsUnmeasurable;
        }
        return t;
    }

    // checker.go:1151-1155
    internal Func<Type> GetGlobalTypeResolver(string name, int arity, bool reportErrors) =>
        CoreUtil.Memoize(() => GetGlobalType(name, arity, reportErrors));

    // checker.go:1158-1162
    internal Func<Symbol?> GetGlobalTypeAliasResolver(string name, int arity, bool reportErrors) =>
        CoreUtil.Memoize(() => GetGlobalTypeAliasSymbol(name, arity, reportErrors));

    // checker.go:1164-1169
    internal Func<Symbol?> GetGlobalValueSymbolResolver(string name, bool reportErrors) =>
        CoreUtil.Memoize(() => GetGlobalSymbol(name, SymbolFlags.Value, reportErrors ? global::Ts.Diagnostics.Diagnostics.Cannot_find_global_value_0 : null));

    // checker.go:1171-1175
    internal Func<Symbol?> GetGlobalTypeSymbolResolver(string name, bool reportErrors) =>
        CoreUtil.Memoize(() => GetGlobalSymbol(name, SymbolFlags.Type, reportErrors ? global::Ts.Diagnostics.Diagnostics.Cannot_find_global_type_0 : null));

    // checker.go:1177-1183
    internal Func<Type[]> GetGlobalTypesResolver(string[] names, int arity, bool reportErrors) =>
        CoreUtil.Memoize(() => CoreUtil.Map(names, name => GetGlobalType(name, arity, reportErrors))!);

    // checker.go:1241-1244
    internal Symbol? GetGlobalSymbol(string name, SymbolFlags meaning, global::Ts.Diagnostics.Message? diagnostic) =>
        // Don't track references for global symbols; the isUse value is arbitrary.
        resolveName(null, name, meaning, diagnostic, false, false);

    // checker.go:1246-1257
    internal void InitializeClosures()
    {
        isPrimitiveOrObjectOrEmptyType = t =>
            (t.flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0 || IsEmptyAnonymousObjectType(t);
        containsMissingType = t =>
            t == missingType || ((t.flags & TypeFlags.Union) != 0 && t.Types()[0] == missingType);
        couldContainTypeVariables = CouldContainTypeVariablesWorker;
        isStringIndexSignatureOnlyType = IsStringIndexSignatureOnlyTypeWorker;
        markNodeAssignments = MarkNodeAssignmentsWorker;
        compareTypesAssignable = CompareTypesAssignableWorker;
    }

    // checker.go:1259-1294
    internal void InitializeIterationResolvers()
    {
        syncIterationTypesResolver = new IterationTypesResolver
        {
            iteratorSymbolName = "iterator",
            getGlobalIteratorType = getGlobalIteratorType,
            getGlobalIterableType = getGlobalIterableType,
            getGlobalIterableTypeChecked = getGlobalIterableTypeChecked,
            getGlobalIterableIteratorType = getGlobalIterableIteratorType,
            getGlobalIterableIteratorTypeChecked = getGlobalIterableIteratorTypeChecked,
            getGlobalIteratorObjectType = getGlobalIteratorObjectType,
            getGlobalGeneratorType = getGlobalGeneratorType,
            getGlobalBuiltinIteratorTypes = GetGlobalTypesResolver(["ArrayIterator", "MapIterator", "SetIterator", "StringIterator"], 1, false),
            resolveIterationType = (t, errorNode) => t,
            mustHaveANextMethodDiagnostic = global::Ts.Diagnostics.Diagnostics.An_iterator_must_have_a_next_method,
            mustBeAMethodDiagnostic = global::Ts.Diagnostics.Diagnostics.The_0_property_of_an_iterator_must_be_a_method,
            mustHaveAValueDiagnostic = global::Ts.Diagnostics.Diagnostics.The_type_returned_by_the_0_method_of_an_iterator_must_have_a_value_property,
        };
        asyncIterationTypesResolver = new IterationTypesResolver
        {
            iteratorSymbolName = "asyncIterator",
            getGlobalIteratorType = getGlobalAsyncIteratorType,
            getGlobalIterableType = getGlobalAsyncIterableType,
            getGlobalIterableTypeChecked = getGlobalAsyncIterableTypeChecked,
            getGlobalIterableIteratorType = getGlobalAsyncIterableIteratorType,
            getGlobalIterableIteratorTypeChecked = getGlobalAsyncIterableIteratorTypeChecked,
            getGlobalIteratorObjectType = getGlobalAsyncIteratorObjectType,
            getGlobalGeneratorType = getGlobalAsyncGeneratorType,
            getGlobalBuiltinIteratorTypes = GetGlobalTypesResolver(["ReadableStreamAsyncIterator"], 1, false),
            resolveIterationType = (t, errorNode) => GetAwaitedTypeEx(t, errorNode, global::Ts.Diagnostics.Diagnostics.Type_of_await_operand_must_either_be_a_valid_promise_or_must_not_contain_a_callable_then_member),
            mustHaveANextMethodDiagnostic = global::Ts.Diagnostics.Diagnostics.An_async_iterator_must_have_a_next_method,
            mustBeAMethodDiagnostic = global::Ts.Diagnostics.Diagnostics.The_0_property_of_an_async_iterator_must_be_a_method,
            mustHaveAValueDiagnostic = global::Ts.Diagnostics.Diagnostics.The_type_returned_by_the_0_method_of_an_async_iterator_must_be_a_promise_for_a_type_with_a_value_property,
        };
    }

    // checker.go:1296-1384 — global symbol merging + global type resolution.
    // PORT-NOTE: skeleton no-op. The full body (mergeGlobalSymbol/mergeModuleAugmentation/global
    // type creation) requires symbol-resolution machinery that is stubbed here; it lands in the
    // Checker.Construction / SymbolResolution waves. Leaving global* types null is safe for the
    // skeleton (no checking is performed).
    // checker.go:1300-1384 (initializeChecker). Only the "Initialize special types" block is ported so
    // far — it resolves the global intrinsic-wrapper/array types the apparent-type machinery reads. The
    // surrounding steps (undefined-globals registration, IArguments/globalThis value types, ambient
    // module-augmentation merges) belong to the module/binder-merge subsystem and remain deferred; they
    // are not needed for intrinsic-type checking. With no lib present, each getGlobalType returns
    // emptyObjectType/emptyGenericType (and reports a location-less "Cannot find global type" global
    // diagnostic), matching tsgo's NoLib behavior.
    internal void InitializeChecker()
    {
        // Initialize global symbol table
        // PORT-NOTE: module-augmentation merges (mergeModuleAugmentation) are deferred to the
        // module-merge wave. Ambient module symbols are collected here and merged into globals after
        // global type initialization (end of this method), matching checker.go:1371-1374.
        var ambientModuleSymbols = new List<Symbol>();
        foreach (var file in files)
        {
            if (!AstUtilities.IsExternalOrCommonJSModule(file))
            {
                // It is an error for a non-external-module (i.e. script) to declare its own `globalThis`.
                var fileGlobalThisSymbol = file.Locals?.GetValueOrDefault("globalThis");
                if (fileGlobalThisSymbol != null)
                {
                    foreach (var d in fileGlobalThisSymbol.Declarations.OrEmpty())
                    {
                        AddDiagnostic(NewDiagnosticForNode(d, global::Ts.Diagnostics.Diagnostics.Declaration_name_conflicts_with_built_in_global_identifier_0, "globalThis"));
                    }
                }
                // map-iter: 序无关 — each local is merged into c.globals keyed by name; the merge is
                // order-independent for non-conflicting lib declarations (Go relies on the same).
                foreach (var symbol in (file.Locals?.Values ?? System.Linq.Enumerable.Empty<Symbol>()))
                {
                    // We defer merging of global ambient module declarations since they may require other
                    // global symbols and types to be resolved.
                    if ((symbol.Flags & SymbolFlags.Module) != 0 && global::Ts.Ast.AstUtilities.IsAmbientModuleSymbolName(symbol.Name))
                    {
                        ambientModuleSymbols.Add(symbol);
                    }
                    else
                    {
                        MergeGlobalSymbol(symbol);
                    }
                }
            }
            patternAmbientModules = CoreUtil.Concatenate(patternAmbientModules, file.PatternAmbientModules);
            if (file.Symbol != null)
            {
                // Merge in UMD exports with first-in-wins semantics (see #9771)
                if (file.GlobalExports != null)
                {
                    foreach (var (name, symbol) in file.GlobalExports)
                    {
                        if (!globals.ContainsKey(name))
                        {
                            globals[name] = symbol;
                        }
                    }
                }
            }
        }
        // PORT-NOTE: global-scope module-augmentation merges (mergeModuleAugmentation) deferred to the
        // module-merge wave.
        AddUndefinedToGlobalsOrErrorOnRedeclaration();
        valueSymbolLinks.Get(undefinedSymbol).resolvedType = undefinedWideningType;
        valueSymbolLinks.Get(argumentsSymbol).resolvedType = GetGlobalType("IArguments", 0 /*arity*/, true /*reportErrors*/);
        valueSymbolLinks.Get(unknownSymbol).resolvedType = errorType;
        valueSymbolLinks.Get(globalThisSymbol).resolvedType = NewObjectType(ObjectFlags.Anonymous, globalThisSymbol);
        // Initialize special types
        globalArrayType = GetGlobalType("Array", 1 /*arity*/, true /*reportErrors*/);
        globalObjectType = GetGlobalType("Object", 0 /*arity*/, true /*reportErrors*/);
        globalFunctionType = GetGlobalType("Function", 0 /*arity*/, true /*reportErrors*/);
        globalCallableFunctionType = GetGlobalStrictFunctionType("CallableFunction");
        globalNewableFunctionType = GetGlobalStrictFunctionType("NewableFunction");
        globalStringType = GetGlobalType("String", 0 /*arity*/, true /*reportErrors*/);
        globalNumberType = GetGlobalType("Number", 0 /*arity*/, true /*reportErrors*/);
        globalBooleanType = GetGlobalType("Boolean", 0 /*arity*/, true /*reportErrors*/);
        globalRegExpType = GetGlobalType("RegExp", 0 /*arity*/, true /*reportErrors*/);
        anyArrayType = CreateArrayType(anyType!);
        autoArrayType = CreateArrayType(autoType!);
        if (autoArrayType == emptyObjectType)
        {
            // autoArrayType is used as a marker, so even if global Array type is not defined, it needs to be a unique type
            autoArrayType = NewAnonymousType(null, null, null, null, null);
        }
        globalReadonlyArrayType = GetGlobalType("ReadonlyArray", 1 /*arity*/, false /*reportErrors*/);
        if (globalReadonlyArrayType == emptyGenericType)
        {
            globalReadonlyArrayType = globalArrayType;
        }
        anyReadonlyArrayType = CreateTypeFromGenericGlobalType(globalReadonlyArrayType!, [anyType!]);
        globalThisType = GetGlobalType("ThisType", 1 /*arity*/, false /*reportErrors*/);
        // Now merge global ambient module declarations (checker.go:1371-1374) — deferred until other
        // global symbols and types are resolved.
        foreach (var symbol in ambientModuleSymbols)
        {
            MergeGlobalSymbol(symbol);
        }
        // PORT-NOTE: non-global module-augmentation merges (mergeModuleAugmentation, checker.go:1376-1383)
        // remain deferred to the module-merge wave (augmentation lists are not yet collected).
    }

    // checker.go:1466-1482
    internal global::Ts.Binder.NameResolver CreateNameResolver() =>
        new()
        {
            CompilerOptions = compilerOptions,
            GetSymbolOfDeclaration = GetSymbolOfDeclaration,
            Error = Error,
            Globals = globals,
            ArgumentsSymbol = argumentsSymbol,
            RequireSymbol = requireSymbol,
            Lookup = GetSymbol,
            SymbolReferenced = SymbolReferenced,
            SetRequiresScopeChangeCache = SetRequiresScopeChangeCache,
            GetRequiresScopeChangeCache = GetRequiresScopeChangeCache,
            OnPropertyWithInvalidInitializer = CheckAndReportErrorForInvalidInitializer,
            OnFailedToResolveSymbol = OnFailedToResolveSymbol,
            OnSuccessfullyResolvedSymbol = OnSuccessfullyResolvedSymbol,
        };

    // checker.go:1484-1497
    internal global::Ts.Binder.NameResolver CreateNameResolverForSuggestion() =>
        new()
        {
            CompilerOptions = compilerOptions,
            GetSymbolOfDeclaration = GetSymbolOfDeclaration,
            Error = Error,
            Globals = globals,
            ArgumentsSymbol = argumentsSymbol,
            RequireSymbol = requireSymbol,
            Lookup = GetSuggestionForSymbolNameLookup,
            SymbolReferenced = SymbolReferenced,
            SetRequiresScopeChangeCache = SetRequiresScopeChangeCache,
            GetRequiresScopeChangeCache = GetRequiresScopeChangeCache,
        };

    // checker.go:1499-1501
    internal void SymbolReferenced(Symbol symbol, SymbolFlags meaning) =>
        symbolReferenceLinks.Get(symbol).referenceKinds |= meaning;

    // checker.go:1503-1505
    internal Ts.Core.Tristate GetRequiresScopeChangeCache(Node node) =>
        nodeLinks.Get(node).declarationRequiresScopeChange;

    // checker.go:1507-1509
    internal void SetRequiresScopeChangeCache(Node node, Ts.Core.Tristate value) =>
        nodeLinks.Get(node).declarationRequiresScopeChange = value;

    // checker.go:13996-14002 (arena → new)
    internal Symbol NewSymbol(SymbolFlags flags, string name)
    {
        SymbolCount++;
        var result = new Symbol
        {
            Flags = flags | SymbolFlags.Transient,
            Name = name,
        };
        return result;
    }

    // checker.go:14004-14008
    internal Symbol NewSymbolEx(SymbolFlags flags, string name, CheckFlags checkFlags)
    {
        var result = NewSymbol(flags, name);
        result.CheckFlags = checkFlags;
        return result;
    }
}

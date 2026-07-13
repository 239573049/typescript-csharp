// Ported from tsgo internal/checker/checker.go (sections: file header consts + Checker struct
// fields, lines 1-99, 262-901). plan/03 §4.2 #1.
//
// Concurrency (plan/03 §9): one Checker per worker, one lock per Checker; a Type's `checker`
// back-pointer guarantees types never cross checkers. All caches/LinkStores are per-Checker.
// Arenas (symbolArena/signatureArena/indexInfoArena) are deleted (plan §一.5).

using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;

namespace Ts.Checker;

// checker.go:51 — `type TypeSystemEntity any`; modelled inline as object? (see TypeResolution).

// checker.go:68-72
internal struct TypeResolution
{
    internal object? target; // TypeSystemEntity
    internal TypeSystemPropertyName propertyName;
    internal bool result;
}

// checker.go:76-80
internal struct ContextualInfo
{
    internal Node? node;
    internal Type? t;
    internal bool isCache;
}

// checker.go:84-87
internal struct InferenceContextInfo
{
    internal Node? node;
    internal InferenceContext? context;
}

// checker.go:275-286
public sealed class InferenceContext
{
    internal InferenceInfo[] inferences = [];
    internal Signature? signature;
    internal InferenceFlags flags;
    internal TypeComparer compareTypes = null!;
    internal TypeMapper? mapper;
    internal TypeMapper? nonFixingMapper;
    internal TypeMapper? returnMapper;
    internal TypeMapper? outerReturnMapper;
    internal Type[]? inferredTypeParameters;
    internal IntraExpressionInferenceSite[]? intraExpressionInferenceSites;
}

// checker.go:288-298
public sealed class InferenceInfo
{
    internal Type typeParameter = null!;
    internal Type[]? candidates;
    internal int[]? candidateDepths;
    internal Type[]? contraCandidates;
    internal Type? inferredType;
    internal InferencePriority priority;
    internal bool topLevel;
    internal bool isFixed;
    internal int impliedArity;
}

// checker.go:321-324
internal struct IntraExpressionInferenceSite
{
    internal Node? node;
    internal Type? t;
}

// checker.go:521-535
internal sealed class IterationTypesResolver
{
    internal string iteratorSymbolName = "";
    internal Func<Type> getGlobalIteratorType = null!;
    internal Func<Type> getGlobalIterableType = null!;
    internal Func<Type> getGlobalIterableTypeChecked = null!;
    internal Func<Type> getGlobalIterableIteratorType = null!;
    internal Func<Type> getGlobalIterableIteratorTypeChecked = null!;
    internal Func<Type> getGlobalIteratorObjectType = null!;
    internal Func<Type> getGlobalGeneratorType = null!;
    internal Func<Type[]> getGlobalBuiltinIteratorTypes = null!;
    internal Func<Type, Node?, Type?> resolveIterationType = null!; // checker.go:530 — nullable per Go (*Type)
    internal global::Ts.Diagnostics.Message mustHaveANextMethodDiagnostic = null!;
    internal global::Ts.Diagnostics.Message mustBeAMethodDiagnostic = null!;
    internal global::Ts.Diagnostics.Message mustHaveAValueDiagnostic = null!;
}

// checker.go:537-544
internal sealed class WideningContext
{
    internal WideningContext? parent;
    internal string propertyName = "";
    internal Type[]? siblings;
    internal Symbol[]? resolvedProperties;
    internal Dictionary<string, WideningContext>? childContexts;
    internal Dictionary<Type, Type>? widenedTypes;
}

// checker.go:576-900 — the Checker itself.
public sealed partial class Checker
{
    internal const int MaxSerializationLevel = 2; // checker.go:546

    // checker.go:578
    private static uint nextCheckerId;

    internal uint id;
    internal Program program = null!;
    internal CompilerOptions compilerOptions = null!;
    internal SourceFile[] files = [];
    internal Dictionary<SourceFile, int> fileIndexMap = null!;
    internal Func<Symbol?, Symbol?, int> compareSymbols = null!;
    internal Func<Symbol[], Symbol[], int> compareSymbolChains = null!;
    internal uint TypeCount;
    internal uint SymbolCount;
    internal uint SignatureCount;
    internal uint TotalInstantiationCount;
    internal uint instantiationCount;
    internal uint instantiationDepth;
    internal uint conditionalConstraintDepth;
    internal int inlineLevel;
    internal int serializationLevel;
    internal Node? currentNode;
    internal Type? varianceTypeParameter;
    internal ScriptTarget languageVersion;
    internal ModuleKind moduleKind;
    internal ModuleResolutionKind moduleResolutionKind;
    internal bool isInferencePartiallyBlocked;
    internal bool legacyDecorators;
    internal bool emitStandardClassFields;
    internal bool strictNullChecks;
    internal bool strictFunctionTypes;
    internal bool strictBindCallApply;
    internal bool strictPropertyInitialization;
    internal bool strictBuiltinIteratorReturn;
    internal bool noImplicitAny;
    internal bool noImplicitThis;
    internal bool useUnknownInCatchVariables;
    internal bool exactOptionalPropertyTypes;
    internal bool canCollectSymbolAliasAccessibilityData;
    internal bool wasCanceled;
    internal bool saveDeferredDiagnostics;
    internal VarianceFlags[] arrayVariances = [];
    internal SymbolTable globals = null!;
    internal Ts.Evaluator.Evaluator evaluate = null!;
    internal Dictionary<string, Type> stringLiteralTypes = null!;
    internal Dictionary<Ts.JsNum.Number, Type> numberLiteralTypes = null!;
    internal Type? nanType;
    internal Dictionary<Ts.JsNum.PseudoBigInt, Type> bigintLiteralTypes = null!;
    internal Dictionary<EnumLiteralKey, Type> enumLiteralTypes = null!;
    internal Dictionary<Symbol, Type> enumNaNLiteralTypes = null!;
    internal Dictionary<CacheHashKey, Type> indexedAccessTypes = null!;
    internal Dictionary<CacheHashKey, Type> templateLiteralTypes = null!;
    internal Dictionary<StringMappingKey, Type> stringMappingTypes = null!;
    internal Dictionary<Symbol, Type> uniqueESSymbolTypes = null!;
    internal Dictionary<Symbol, ThisAssignmentDeclarationKind> thisExpandoKinds = null!;
    internal Dictionary<Symbol, Node> thisExpandoLocations = null!;
    internal Dictionary<CacheHashKey, Type[]> subtypeReductionCache = null!;
    internal Dictionary<CachedTypeKey, Type> cachedTypes = null!;
    internal Dictionary<CachedSignatureKey, Signature> cachedSignatures = null!;
    internal Dictionary<string, Symbol> undefinedProperties = null!;
    internal Dictionary<NarrowedTypeKey, Type> narrowedTypes = null!;
    internal Dictionary<AssignmentReducedKey, Type> assignmentReducedTypes = null!;
    internal Dictionary<DiscriminatedContextualTypeKey, Type> discriminatedContextualTypes = null!;
    internal Dictionary<InstantiationExpressionKey, Type> instantiationExpressionTypes = null!;
    internal Dictionary<SubstitutionTypeKey, Type> substitutionTypes = null!;
    internal Dictionary<ReverseMappedTypeKey, Type> reverseMappedCache = null!;
    internal Dictionary<ReverseMappedTypeKey, Type> reverseHomomorphicMappedCache = null!;
    internal Dictionary<IterationTypesKey, IterationTypes> iterationTypesCache = null!;
    internal readonly Set<Type> markerTypes = new();
    internal Symbol undefinedSymbol = null!;
    internal Symbol argumentsSymbol = null!;
    internal Symbol requireSymbol = null!;
    internal Symbol unknownSymbol = null!;
    internal Dictionary<string, Symbol> unresolvedSymbols = null!;
    internal Dictionary<CacheHashKey, Type> errorTypes = null!;
    internal Dictionary<Node, Symbol> moduleSymbols = null!;
    internal Symbol globalThisSymbol = null!;
    internal Dictionary<SymbolTableId, Symbol[]> symbolTableAliasCache = null!;
    internal Dictionary<NodeId, SymbolTable> classExpressionNameTables = null!;
    internal Func<Node?, string, SymbolFlags, global::Ts.Diagnostics.Message?, bool, bool, Symbol?> resolveName = null!;
    internal Func<Node?, string, SymbolFlags, global::Ts.Diagnostics.Message?, bool, bool, Symbol?> resolveNameForSymbolSuggestion = null!;
    internal Dictionary<CacheHashKey, Type> tupleTypes = null!;
    internal Dictionary<CacheHashKey, Type> unionTypes = null!;
    internal Dictionary<UnionOfUnionKey, Type> unionOfUnionTypes = null!;
    internal Dictionary<CacheHashKey, Type> intersectionTypes = null!;
    internal Dictionary<PropertiesTypesKey, Type> propertiesTypes = null!;
    internal readonly DiagnosticsCollection diagnostics = new();
    internal readonly DiagnosticsCollection suggestionDiagnostics = new();
    internal Dictionary<Symbol, Symbol> mergedSymbols = null!;
    internal readonly NodeFactory factory = new();
    internal readonly LinkStore<Node, NodeLinks> nodeLinks = new();
    internal readonly LinkStore<Node, SignatureLinks> signatureLinks = new();
    internal readonly LinkStore<Node, SymbolNodeLinks> symbolNodeLinks = new();
    internal readonly LinkStore<Node, TypeNodeLinks> typeNodeLinks = new();
    internal readonly LinkStore<Node, EnumMemberLinks> enumMemberLinks = new();
    internal readonly LinkStore<Node, AssertionLinks> assertionLinks = new();
    internal readonly LinkStore<Node, ArrayLiteralLinks> arrayLiteralLinks = new();
    internal readonly LinkStore<Node, SwitchStatementLinks> switchStatementLinks = new();
    internal readonly LinkStore<Node, JsxElementLinks> jsxElementLinks = new();
    internal readonly LinkStore<Symbol, SymbolReferenceLinks> symbolReferenceLinks = new();
    internal readonly LinkStore<Symbol, ValueSymbolLinks> valueSymbolLinks = new();
    internal readonly LinkStore<Symbol, MappedSymbolLinks> mappedSymbolLinks = new();
    internal readonly LinkStore<Symbol, DeferredSymbolLinks> deferredSymbolLinks = new();
    internal readonly LinkStore<Symbol, AliasSymbolLinks> aliasSymbolLinks = new();
    internal readonly LinkStore<Symbol, ModuleSymbolLinks> moduleSymbolLinks = new();
    internal readonly LinkStore<Symbol, LateBoundLinks> lateBoundLinks = new();
    internal readonly LinkStore<Symbol, ExportTypeLinks> exportTypeLinks = new();
    internal readonly LinkStore<Symbol, MembersAndExportsLinks> membersAndExportsLinks = new();
    internal readonly LinkStore<Symbol, TypeAliasLinks> typeAliasLinks = new();
    internal readonly LinkStore<Symbol, DeclaredTypeLinks> declaredTypeLinks = new();
    internal readonly LinkStore<Symbol, SpreadLinks> spreadLinks = new();
    internal readonly LinkStore<Symbol, VarianceLinks> varianceLinks = new();
    // checker.go:691 — the only Go-exported (upper-case) link field; C# keeps camelCase field.
    internal readonly LinkStore<Symbol, ReverseMappedSymbolLinks> reverseMappedSymbolLinks = new();
    internal readonly LinkStore<Symbol, MarkedAssignmentSymbolLinks> markedAssignmentSymbolLinks = new();
    internal readonly LinkStore<Symbol, ContainingSymbolLinks> symbolContainerLinks = new();
    internal readonly LinkStore<SourceFile, SourceFileLinks> sourceFileLinks = new();
    internal global::Ts.Scanner.Scanner? regExpScanner;
    internal Dictionary<Type, Node> patternForType = null!;
    internal Dictionary<Node, Type> contextFreeTypes = null!;
    internal Type? anyType;
    internal Type? autoType;
    internal Type? wildcardType;
    internal Type? blockedStringType;
    internal Type? errorType;
    internal Type? unresolvedType;
    internal Type? nonInferrableAnyType;
    internal Type? intrinsicMarkerType;
    internal Type? unknownType;
    internal Type? undefinedType;
    internal Type? undefinedWideningType;
    internal Type? missingType;
    internal Type? undefinedOrMissingType;
    internal Type? optionalType;
    internal Type? nullType;
    internal Type? nullWideningType;
    internal Type? stringType;
    internal Type? numberType;
    internal Type? bigintType;
    internal Type? regularFalseType;
    internal Type? falseType;
    internal Type? regularTrueType;
    internal Type? trueType;
    internal Type? booleanType;
    internal Type? esSymbolType;
    internal Type? voidType;
    internal Type? neverType;
    internal Type? silentNeverType;
    internal Type? implicitNeverType;
    internal Type? unreachableNeverType;
    internal Type? nonPrimitiveType;
    internal Type? stringOrNumberType;
    internal Type? stringNumberSymbolType;
    internal Type? numberOrBigIntType;
    internal Type? templateConstraintType;
    internal Type? numericStringType;
    internal Type? uniqueLiteralType;
    internal TypeMapper? uniqueLiteralMapper;
    internal RelationComparisonResult reliabilityFlags;
    internal TypeMapper? reportUnreliableMapper;
    internal TypeMapper? reportUnmeasurableMapper;
    internal TypeMapper? restrictiveMapper;
    internal TypeMapper? permissiveMapper;
    internal Type? emptyObjectType;
    internal Type? emptyJsxObjectType;
    internal Type? emptyFreshJsxObjectType;
    internal Type? emptyTypeLiteralType;
    internal Type? unknownEmptyObjectType;
    internal Type? unknownUnionType;
    internal Type? emptyGenericType;
    internal Type? anyFunctionType;
    internal Type? noConstraintType;
    internal Type? circularConstraintType;
    internal Type? resolvingDefaultType;
    internal Type? markerSuperType;
    internal Type? markerSubType;
    internal Type? markerOtherType;
    internal Type? markerSuperTypeForCheck;
    internal Type? markerSubTypeForCheck;
    internal TypePredicate? noTypePredicate;
    internal Signature? anySignature;
    internal Signature? unknownSignature;
    internal Signature? resolvingSignature;
    internal Signature? silentNeverSignature;
    internal Dictionary<Node, bool> cachedArgumentsReferenced = null!;
    internal IndexInfo? enumNumberIndexInfo;
    internal IndexInfo? anyBaseTypeIndexInfo;
    internal PatternAmbientModule[]? patternAmbientModules;
    internal SymbolTable? patternAmbientModuleAugmentations;
    internal Type? globalObjectType;
    internal Type? globalFunctionType;
    internal Type? globalCallableFunctionType;
    internal Type? globalNewableFunctionType;
    internal Type? globalArrayType;
    internal Type? globalReadonlyArrayType;
    internal Type? globalStringType;
    internal Type? globalNumberType;
    internal Type? globalBooleanType;
    internal Type? globalRegExpType;
    internal Type? globalThisType;
    internal Type? anyArrayType;
    internal Type? autoArrayType;
    internal Type? anyReadonlyArrayType;
    internal Type? deferredGlobalImportMetaExpressionType;
    internal Node[]? contextualBindingPatterns;
    internal Type? emptyStringType;
    internal Type? zeroType;
    internal Type? zeroBigIntType;
    internal Type? typeofType;
    internal List<TypeResolution> typeResolutions = [];
    internal int resolutionStart;
    internal bool inVarianceComputation;
    internal int? apparentArgumentCount;
    internal Node? lastGetCombinedNodeFlagsNode;
    internal NodeFlags lastGetCombinedNodeFlagsResult;
    internal Node? lastGetCombinedModifierFlagsNode;
    internal ModifierFlags lastGetCombinedModifierFlagsResult;
    internal InferenceState? freeinferenceState;
    internal FlowState? freeFlowState;
    internal Dictionary<FlowLoopKey, Type> flowLoopCache = null!;
    internal List<FlowLoopInfo> flowLoopStack = [];
    internal List<SharedFlow> sharedFlows = [];
    internal Type[] antecedentTypes = []; // Go nil slice: len(nil)==0 / append OK — start empty, never null
    internal bool flowAnalysisDisabled;
    internal int flowInvocationCount;
    internal Dictionary<Node, Type> flowTypeCache = null!;
    internal FlowNode? lastFlowNode;
    internal bool lastFlowNodeReachable;
    internal Dictionary<FlowNode, bool> flowNodeReachable = null!;
    internal Dictionary<FlowNode, bool> flowNodePostSuper = null!;
    internal Node[]? renamedBindingElementsInTypes;
    internal List<ContextualInfo> contextualInfos = [];
    internal List<InferenceContextInfo> inferenceContextInfos = [];
    internal List<Type> awaitedTypeStack = [];
    internal List<Type> reverseMappedSourceStack = [];
    internal List<Type> reverseMappedTargetStack = [];
    internal ExpandingFlags reverseExpandingFlags;
    internal Relater? freeRelater;
    internal Relation? subtypeRelation;
    internal Relation? strictSubtypeRelation;
    internal Relation? assignableRelation;
    internal Relation? comparableRelation;
    internal Relation? identityRelation;
    internal Dictionary<EnumRelationKey, RelationComparisonResult> enumRelation = null!;
    internal Func<Type> getGlobalESSymbolType = null!;
    internal Func<Type> getGlobalBigIntType = null!;
    internal Func<Type> getGlobalImportMetaType = null!;
    internal Func<Type> getGlobalImportAttributesType = null!;
    internal Func<Type> getGlobalImportAttributesTypeChecked = null!;
    internal Func<Symbol?> getGlobalNonNullableTypeAliasOrNil = null!;
    internal Func<Symbol?> getGlobalExtractSymbol = null!;
    internal Func<Type> getGlobalDisposableType = null!;
    internal Func<Type> getGlobalAsyncDisposableType = null!;
    internal Func<Symbol?> getGlobalAwaitedSymbol = null!;
    internal Func<Symbol?> getGlobalAwaitedSymbolOrNil = null!;
    internal Func<Symbol?> getGlobalNaNSymbolOrNil = null!;
    internal Func<Symbol?> getGlobalRecordSymbol = null!;
    internal Func<Type> getGlobalTemplateStringsArrayType = null!;
    internal Func<Symbol?> getGlobalESSymbolConstructorSymbolOrNil = null!;
    internal Func<Symbol?> getGlobalESSymbolConstructorTypeSymbolOrNil = null!;
    internal Func<Type> getGlobalImportCallOptionsType = null!;
    internal Func<Type> getGlobalImportCallOptionsTypeChecked = null!;
    internal Func<Type> getGlobalPromiseType = null!;
    internal Func<Type> getGlobalPromiseTypeChecked = null!;
    internal Func<Type> getGlobalPromiseLikeType = null!;
    internal Func<Symbol?> getGlobalPromiseConstructorSymbol = null!;
    internal Func<Symbol?> getGlobalPromiseConstructorSymbolOrNil = null!;
    internal Func<Symbol?> getGlobalOmitSymbol = null!;
    internal Func<Symbol?> getGlobalNoInferSymbolOrNil = null!;
    internal Func<Type> getGlobalIteratorType = null!;
    internal Func<Type> getGlobalIterableType = null!;
    internal Func<Type> getGlobalIterableTypeChecked = null!;
    internal Func<Type> getGlobalIterableIteratorType = null!;
    internal Func<Type> getGlobalIterableIteratorTypeChecked = null!;
    internal Func<Type> getGlobalIteratorObjectType = null!;
    internal Func<Type> getGlobalGeneratorType = null!;
    internal Func<Type> getGlobalAsyncIteratorType = null!;
    internal Func<Type> getGlobalAsyncIterableType = null!;
    internal Func<Type> getGlobalAsyncIterableTypeChecked = null!;
    internal Func<Type> getGlobalAsyncIterableIteratorType = null!;
    internal Func<Type> getGlobalAsyncIterableIteratorTypeChecked = null!;
    internal Func<Type> getGlobalAsyncIteratorObjectType = null!;
    internal Func<Type> getGlobalAsyncGeneratorType = null!;
    internal Func<Type> getGlobalIteratorYieldResultType = null!;
    internal Func<Type> getGlobalIteratorReturnResultType = null!;
    internal Func<Type> getGlobalTypedPropertyDescriptorType = null!;
    internal Func<Type> getGlobalClassDecoratorContextType = null!;
    internal Func<Type> getGlobalClassMethodDecoratorContextType = null!;
    internal Func<Type> getGlobalClassGetterDecoratorContextType = null!;
    internal Func<Type> getGlobalClassSetterDecoratorContextType = null!;
    internal Func<Type> getGlobalClassAccessorDecoratorContxtType = null!; // checker.go:868 (Go typo, unused)
    internal Func<Type> getGlobalClassAccessorDecoratorContextType = null!;
    internal Func<Type> getGlobalClassAccessorDecoratorTargetType = null!;
    internal Func<Type> getGlobalClassAccessorDecoratorResultType = null!;
    internal Func<Type> getGlobalClassFieldDecoratorContextType = null!;
    internal IterationTypesResolver? syncIterationTypesResolver;
    internal IterationTypesResolver? asyncIterationTypesResolver;
    internal Func<Type, bool> isPrimitiveOrObjectOrEmptyType = null!;
    internal Func<Type, bool> containsMissingType = null!;
    internal Func<Type, bool> couldContainTypeVariables = null!;
    internal Func<Type, bool> isStringIndexSignatureOnlyType = null!;
    internal Func<Node, bool> markNodeAssignments = null!;
    internal TypeComparer compareTypesAssignable = null!;
    internal EmitResolver? emitResolver;
    internal readonly Once emitResolverOnce = new();
    internal string jsxNamespace = ""; // Go: _jsxNamespace
    internal Node? jsxFactoryEntity; // Go: _jsxFactoryEntity
    internal readonly Set<Node> skipDirectInferenceNodes = new();
    internal CancellationToken ctx; // Go context.Context
    internal Dictionary<string, bool>? packagesMap;
    internal List<TypeMapper> activeMappers = [];
    internal List<Dictionary<CacheHashKey, Type>> activeTypeMappersCaches = [];
    internal readonly Once ambientModulesOnce = new();
    internal Symbol[]? ambientModules;
    internal bool withinUnreachableCode;
    internal readonly Set<Node> reportedUnreachableNodes = new();
    internal readonly Set<NonExistentPropertyKey> nonExistentProperties = new();
    internal List<Action> deferredDiagnosticCallbacks = [];
    internal NodeBuilder? typeToStringNodebuilder;

    internal readonly Lock mu = new(); // checker.go:898
    internal Tracer? tracer; // checker.go:899
}

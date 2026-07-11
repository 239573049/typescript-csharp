// Ported from tsgo internal/checker/{types.go,checker.go,relater.go,mapper.go}.
// The remaining checker enums (TypeFlags/ObjectFlags/SignatureFlags live in their own files).
// Values match Go exactly; do not reorder or renumber.

namespace Ts.Checker;

// --- from types.go ---

// types.go:19-28
[System.Flags]
public enum ParseFlags : uint
{
    None = 0,
    Yield = 1 << 0,
    Await = 1 << 1,
    Type = 1 << 2,
    IgnoreMissingOpenBrace = 1 << 4,
    JSDoc = 1 << 5,
}

// types.go:30-35
public enum SignatureKind
{
    Call,
    Construct,
}

// types.go:37-45
[System.Flags]
public enum ContextFlags : uint
{
    None = 0,
    Signature = 1 << 0, // Obtaining contextual signature
    NoConstraints = 1 << 1, // Don't obtain type variable constraints
    IgnoreNodeInferences = 1 << 2, // Ignore inference to current node and parent nodes out to the containing call
    SkipBindingPatterns = 1 << 3, // Ignore contextual types applied by binding patterns
}

// types.go:47-87
[System.Flags]
public enum TypeFormatFlags : uint
{
    None = 0,
    NoTruncation = 1 << 0,
    WriteArrayAsGenericType = 1 << 1,
    GenerateNamesForShadowedTypeParams = 1 << 2,
    UseStructuralFallback = 1 << 3,
    WriteTypeArgumentsOfSignature = 1 << 5,
    UseFullyQualifiedType = 1 << 6,
    SuppressAnyReturnType = 1 << 8,
    MultilineObjectLiterals = 1 << 10,
    WriteClassExpressionAsTypeLiteral = 1 << 11,
    UseTypeOfFunction = 1 << 12,
    OmitParameterModifiers = 1 << 13,
    UseAliasDefinedOutsideCurrentScope = 1 << 14,
    UseSingleQuotesForStringLiteralType = 1 << 28,
    NoTypeReduction = 1 << 29,
    UseInstantiationExpressions = 1 << 30,
    OmitThisParameter = 1 << 25,
    WriteCallStyleSignature = 1 << 27,
    // Error Handling
    AllowUniqueESSymbolType = 1 << 20,
    // TypeFormatFlags exclusive
    AddUndefined = 1 << 17,
    WriteArrowStyleSignature = 1 << 18,
    // State
    InArrayType = 1 << 19,
    InElementType = 1 << 21,
    InFirstTypeArgument = 1 << 22,
    InTypeAlias = 1 << 23,

    NodeBuilderFlagsMask = NoTruncation | WriteArrayAsGenericType | GenerateNamesForShadowedTypeParams | UseStructuralFallback | WriteTypeArgumentsOfSignature |
        UseFullyQualifiedType | SuppressAnyReturnType | MultilineObjectLiterals | WriteClassExpressionAsTypeLiteral |
        UseTypeOfFunction | OmitParameterModifiers | UseAliasDefinedOutsideCurrentScope | AllowUniqueESSymbolType | InTypeAlias |
        UseInstantiationExpressions |
        UseSingleQuotesForStringLiteralType | NoTypeReduction | OmitThisParameter,
}

// types.go:89-112
[System.Flags]
public enum SymbolFormatFlags : uint
{
    None = 0,
    WriteTypeParametersOrArguments = 1 << 0,
    UseOnlyExternalAliasing = 1 << 1,
    AllowAnyNodeKind = 1 << 2,
    UseAliasDefinedOutsideCurrentScope = 1 << 3,
    WriteComputedProps = 1 << 4,
    DoNotIncludeSymbolChain = 1 << 5,
}

// types.go:114-150
[System.Flags]
public enum ExternalEmitHelpers : uint
{
    Rest = 1 << 0, // __rest
    Decorate = 1 << 1, // __decorate
    Metadata = 1 << 2, // __metadata
    Param = 1 << 3, // __param
    Awaiter = 1 << 4, // __awaiter
    Await = 1 << 5, // __await
    AsyncGenerator = 1 << 6, // __asyncGenerator
    AsyncDelegator = 1 << 7, // __asyncDelegator
    AsyncValues = 1 << 8, // __asyncValues
    ExportStar = 1 << 9, // __exportStar
    ImportStar = 1 << 10, // __importStar
    ImportDefault = 1 << 11, // __importDefault
    MakeTemplateObject = 1 << 12, // __makeTemplateObject
    ClassPrivateFieldGet = 1 << 13, // __classPrivateFieldGet
    ClassPrivateFieldSet = 1 << 14, // __classPrivateFieldSet
    ClassPrivateFieldIn = 1 << 15, // __classPrivateFieldIn
    SetFunctionName = 1 << 16, // __setFunctionName
    PropKey = 1 << 17, // __propKey
    AddDisposableResourceAndDisposeResources = 1 << 18, // __addDisposableResource / __disposeResources
    RewriteRelativeImportExtension = 1 << 19, // __rewriteRelativeImportExtension
    ESDecorateAndRunInitializers = Decorate, // __esDecorate and __runInitializers

    FirstEmitHelper = Rest,
    LastEmitHelper = RewriteRelativeImportExtension,
    ForAwaitOfIncludes = AsyncValues,
    AsyncGeneratorIncludes = Await | AsyncGenerator,
    AsyncDelegatorIncludes = Await | AsyncDelegator | AsyncValues,
}

// types.go:298-310
[System.Flags]
public enum VarianceFlags : uint
{
    Invariant = 0, // Neither covariant nor contravariant
    Covariant = 1 << 0, // Covariant
    Contravariant = 1 << 1, // Contravariant
    Bivariant = Covariant | Contravariant, // Both covariant and contravariant
    Independent = 1 << 2, // Unwitnessed type parameter
    VarianceMask = Invariant | Covariant | Contravariant | Independent, // Mask of all measured variances
    Unmeasurable = 1 << 3, // Variance result is unusable
    Unreliable = 1 << 4, // Variance result is unreliable
    AllowsStructuralFallback = Unmeasurable | Unreliable,
}

// types.go:329-343
[System.Flags]
public enum AccessFlags : uint
{
    None = 0,
    IncludeUndefined = 1 << 0,
    NoIndexSignatures = 1 << 1,
    Writing = 1 << 2,
    CacheSymbol = 1 << 3,
    AllowMissing = 1 << 4,
    ExpressionPosition = 1 << 5,
    ReportDeprecated = 1 << 6,
    SuppressNoImplicitAnyError = 1 << 7,
    Contextual = 1 << 8,
    Persistent = IncludeUndefined,
}

// types.go:345-358
[System.Flags]
public enum NodeCheckFlags : uint
{
    None = 0,
    TypeChecked = 1 << 0, // Node has been type checked
    ContextChecked = 1 << 6, // Contextual types have been assigned
    EnumValuesComputed = 1 << 10, // Values for enum members have been computed
    AssignmentsMarked = 1 << 17, // Parameter assignments have been marked
    ContainsClassWithPrivateIdentifiers = 1 << 20,
    ContainsSuperPropertyInStaticInitializer = 1 << 21,
    InCheckIdentifier = 1 << 22,
    InitializerIsUndefined = 1 << 24,
    InitializerIsUndefinedComputed = 1 << 25,
}

// types.go:249-258
public enum ExhaustiveState : byte
{
    Unknown, // Exhaustive state not computed
    Computing, // Exhaustive state computation in progress
    False, // Switch statement is not exhaustive
    True, // Switch statement is exhaustive
}

// types.go:276-281
public enum MembersOrExportsResolutionKind
{
    ResolvedExports = 0,
    ResolvedMembers = 1,
}

// types.go:1036-1048
[System.Flags]
public enum ElementFlags : uint
{
    None = 0,
    Required = 1 << 0, // T
    Optional = 1 << 1, // T?
    Rest = 1 << 2, // ...T[]
    Variadic = 1 << 3, // ...T
    Fixed = Required | Optional,
    Variable = Rest | Variadic,
    NonRequired = Optional | Rest | Variadic,
    NonRest = Required | Optional | Variadic,
}

// types.go:1166-1173
[System.Flags]
public enum IndexFlags : uint
{
    None = 0,
    StringsOnly = 1 << 0,
    NoIndexSignatures = 1 << 1,
    NoReducibleCheck = 1 << 2,
}

// types.go:1334-1341
public enum TypePredicateKind
{
    This,
    Identifier,
    AssertsThis,
    AssertsIdentifier,
}

// types.go:1401-1408
public enum Ternary : sbyte
{
    False = 0,
    Unknown = 1,
    Maybe = 3,
    True = -1,
}

// --- from checker.go ---

// checker.go:35-49
[System.Flags]
public enum CheckMode : uint
{
    Normal = 0,
    Contextual = 1 << 0,
    Inferential = 1 << 1,
    SkipContextSensitive = 1 << 2,
    SkipGenericFunctions = 1 << 3,
    IsForSignatureHelp = 1 << 4,
    RestBindingElement = 1 << 5,
    TypeOnly = 1 << 6,
    ForceTuple = 1 << 7,
}

// checker.go:53-66
public enum TypeSystemPropertyName
{
    Type,
    ResolvedBaseConstructorType,
    DeclaredType,
    ResolvedReturnType,
    ResolvedBaseConstraint,
    ResolvedTypeArguments,
    ResolvedBaseTypes,
    WriteType,
    InitializerIsUndefined,
    AliasTarget,
}

// checker.go:91-98
public enum WideningKind
{
    Normal,
    FunctionReturn,
    GeneratorNext,
    GeneratorYield,
}

// checker.go:116-141
public enum CachedTypeKind
{
    LiteralUnionBaseType,
    IndexType,
    StringIndexType,
    EquivalentBaseType,
    ApparentType,
    AwaitedType,
    EvolvingArrayType,
    ArrayLiteralType,
    PermissiveInstantiation,
    RestrictiveInstantiation,
    RestrictiveTypeParameter,
    IndexedAccessForReading,
    IndexedAccessForWriting,
    Widened,
    RegularObjectLiteral,
    PromisedTypeOfPromise,
    DefaultOnlyType,
    SyntheticType,
    DecoratorContext,
    DecoratorContextStatic,
    DecoratorContextPrivate,
    DecoratorContextPrivateStatic,
}

// checker.go:264-271
[System.Flags]
public enum InferenceFlags : uint
{
    None = 0,
    NoDefault = 1 << 0,
    AnyDefault = 1 << 1,
    SkippedGenericFunction = 1 << 2,
}

// checker.go:300-319
public enum InferencePriority
{
    None = 0,
    NakedTypeVariable = 1 << 0,
    SpeculativeTuple = 1 << 1,
    SubstituteSource = 1 << 2,
    HomomorphicMappedType = 1 << 3,
    PartialHomomorphicMappedType = 1 << 4,
    MappedTypeConstraint = 1 << 5,
    ContravariantConditional = 1 << 6,
    ReturnType = 1 << 7,
    LiteralKeyof = 1 << 8,
    NoConstraints = 1 << 9,
    AlwaysStrict = 1 << 10,
    MaxValue = 1 << 11,
    Circularity = -1, // Inference circularity (value less than all other priorities)

    PriorityImpliesCombination = ReturnType | MappedTypeConstraint | LiteralKeyof,
}

// checker.go:326-336
[System.Flags]
public enum DeclarationMeaning : uint
{
    GetAccessor = 1 << 0,
    SetAccessor = 1 << 1,
    PropertyAssignment = 1 << 2,
    Method = 1 << 3,
    PrivateStatic = 1 << 4,
    GetOrSetAccessor = GetAccessor | SetAccessor,
    PropertyAssignmentOrMethod = PropertyAssignment | Method,
}

// checker.go:338-345
[System.Flags]
public enum DeclarationSpaces
{
    None = 0,
    ExportValue = 1 << 0,
    ExportType = 1 << 1,
    ExportNamespace = 1 << 2,
}

// checker.go:349-358
public enum IntrinsicTypeKind
{
    Unknown,
    Uppercase,
    Lowercase,
    Capitalize,
    Uncapitalize,
    NoInfer,
}

// checker.go:368-375
[System.Flags]
public enum MappedTypeModifiers : uint
{
    IncludeReadonly = 1 << 0,
    ExcludeReadonly = 1 << 1,
    IncludeOptional = 1 << 2,
    ExcludeOptional = 1 << 3,
}

// checker.go:377-383
public enum MappedTypeNameTypeKind
{
    None,
    Filtering,
    Remapping,
}

// checker.go:385-396
public enum ReferenceHint
{
    Unspecified,
    Identifier,
    Property,
    ExportAssignment,
    Jsx,
    ExportImportEquals,
    ExportSpecifier,
    Decorator,
}

// checker.go:398-481
[System.Flags]
public enum TypeFacts : uint
{
    None = 0,
    TypeofEQString = 1 << 0,
    TypeofEQNumber = 1 << 1,
    TypeofEQBigInt = 1 << 2,
    TypeofEQBoolean = 1 << 3,
    TypeofEQSymbol = 1 << 4,
    TypeofEQObject = 1 << 5,
    TypeofEQFunction = 1 << 6,
    TypeofEQHostObject = 1 << 7,
    TypeofNEString = 1 << 8,
    TypeofNENumber = 1 << 9,
    TypeofNEBigInt = 1 << 10,
    TypeofNEBoolean = 1 << 11,
    TypeofNESymbol = 1 << 12,
    TypeofNEObject = 1 << 13,
    TypeofNEFunction = 1 << 14,
    TypeofNEHostObject = 1 << 15,
    EQUndefined = 1 << 16,
    EQNull = 1 << 17,
    EQUndefinedOrNull = 1 << 18,
    NEUndefined = 1 << 19,
    NENull = 1 << 20,
    NEUndefinedOrNull = 1 << 21,
    Truthy = 1 << 22,
    Falsy = 1 << 23,
    IsUndefined = 1 << 24,
    IsNull = 1 << 25,
    IsUndefinedOrNull = IsUndefined | IsNull,
    All = (1 << 27) - 1,
    BaseStringStrictFacts = TypeofEQString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | NEUndefined | NENull | NEUndefinedOrNull,
    BaseStringFacts = BaseStringStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    StringStrictFacts = BaseStringStrictFacts | Truthy | Falsy,
    StringFacts = BaseStringFacts | Truthy,
    EmptyStringStrictFacts = BaseStringStrictFacts | Falsy,
    EmptyStringFacts = BaseStringFacts,
    NonEmptyStringStrictFacts = BaseStringStrictFacts | Truthy,
    NonEmptyStringFacts = BaseStringFacts | Truthy,
    BaseNumberStrictFacts = TypeofEQNumber | TypeofNEString | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | NEUndefined | NENull | NEUndefinedOrNull,
    BaseNumberFacts = BaseNumberStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    NumberStrictFacts = BaseNumberStrictFacts | Truthy | Falsy,
    NumberFacts = BaseNumberFacts | Truthy,
    ZeroNumberStrictFacts = BaseNumberStrictFacts | Falsy,
    ZeroNumberFacts = BaseNumberFacts,
    NonZeroNumberStrictFacts = BaseNumberStrictFacts | Truthy,
    NonZeroNumberFacts = BaseNumberFacts | Truthy,
    BaseBigIntStrictFacts = TypeofEQBigInt | TypeofNEString | TypeofNENumber | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | NEUndefined | NENull | NEUndefinedOrNull,
    BaseBigIntFacts = BaseBigIntStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    BigIntStrictFacts = BaseBigIntStrictFacts | Truthy | Falsy,
    BigIntFacts = BaseBigIntFacts | Truthy,
    ZeroBigIntStrictFacts = BaseBigIntStrictFacts | Falsy,
    ZeroBigIntFacts = BaseBigIntFacts,
    NonZeroBigIntStrictFacts = BaseBigIntStrictFacts | Truthy,
    NonZeroBigIntFacts = BaseBigIntFacts | Truthy,
    BaseBooleanStrictFacts = TypeofEQBoolean | TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | NEUndefined | NENull | NEUndefinedOrNull,
    BaseBooleanFacts = BaseBooleanStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    BooleanStrictFacts = BaseBooleanStrictFacts | Truthy | Falsy,
    BooleanFacts = BaseBooleanFacts | Truthy,
    FalseStrictFacts = BaseBooleanStrictFacts | Falsy,
    FalseFacts = BaseBooleanFacts,
    TrueStrictFacts = BaseBooleanStrictFacts | Truthy,
    TrueFacts = BaseBooleanFacts | Truthy,
    SymbolStrictFacts = TypeofEQSymbol | TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | NEUndefined | NENull | NEUndefinedOrNull | Truthy,
    SymbolFacts = SymbolStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    ObjectStrictFacts = TypeofEQObject | TypeofEQHostObject | TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEFunction | NEUndefined | NENull | NEUndefinedOrNull | Truthy,
    ObjectFacts = ObjectStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    FunctionStrictFacts = TypeofEQFunction | TypeofEQHostObject | TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | NEUndefined | NENull | NEUndefinedOrNull | Truthy,
    FunctionFacts = FunctionStrictFacts | EQUndefined | EQNull | EQUndefinedOrNull | Falsy,
    VoidFacts = TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | EQUndefined | EQUndefinedOrNull | NENull | Falsy,
    UndefinedFacts = TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | TypeofNEHostObject | EQUndefined | EQUndefinedOrNull | NENull | Falsy | IsUndefined,
    NullFacts = TypeofEQObject | TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEFunction | TypeofNEHostObject | EQNull | EQUndefinedOrNull | NEUndefined | Falsy | IsNull,
    EmptyObjectStrictFacts = All & ~(EQUndefined | EQNull | EQUndefinedOrNull | IsUndefinedOrNull),
    EmptyObjectFacts = All & ~IsUndefinedOrNull,
    UnknownFacts = All & ~IsUndefinedOrNull,
    AllTypeofNE = TypeofNEString | TypeofNENumber | TypeofNEBigInt | TypeofNEBoolean | TypeofNESymbol | TypeofNEObject | TypeofNEFunction | NEUndefined,
    // Masks
    OrFactsMask = TypeofEQFunction | TypeofNEObject,
    AndFactsMask = All & ~OrFactsMask,
}

// checker.go:483-505
[System.Flags]
public enum IterationUse : uint
{
    AllowsSyncIterablesFlag = 1 << 0,
    AllowsAsyncIterablesFlag = 1 << 1,
    AllowsStringInputFlag = 1 << 2,
    ForOfFlag = 1 << 3,
    YieldStarFlag = 1 << 4,
    SpreadFlag = 1 << 5,
    DestructuringFlag = 1 << 6,
    PossiblyOutOfBounds = 1 << 7,
    Element = AllowsSyncIterablesFlag,
    Spread = AllowsSyncIterablesFlag | SpreadFlag,
    Destructuring = AllowsSyncIterablesFlag | DestructuringFlag,
    ForOf = AllowsSyncIterablesFlag | AllowsStringInputFlag | ForOfFlag,
    ForAwaitOf = AllowsSyncIterablesFlag | AllowsAsyncIterablesFlag | AllowsStringInputFlag | ForOfFlag,
    YieldStar = AllowsSyncIterablesFlag | YieldStarFlag,
    AsyncYieldStar = AllowsSyncIterablesFlag | AllowsAsyncIterablesFlag | YieldStarFlag,
    GeneratorReturnType = AllowsSyncIterablesFlag,
    AsyncGeneratorReturnType = AllowsAsyncIterablesFlag,
    CacheFlags = AllowsSyncIterablesFlag | AllowsAsyncIterablesFlag | ForOfFlag,
}

// checker.go:513-519
public enum IterationTypeKind
{
    Yield,
    Return,
    Next,
}

// checker.go:25510-25516
public enum UnionReduction
{
    None,
    Literal,
    Subtype,
}

// checker.go:25928-25934
[System.Flags]
public enum IntersectionFlags : uint
{
    None = 0,
    NoSupertypeReduction = 1 << 0,
    NoConstraintReduction = 1 << 1,
}

// checker.go:17967-17974 (Go: thisAssignmentDeclarationKind, unexported)
public enum ThisAssignmentDeclarationKind
{
    None, // not (all) this.property assignments
    Typed, // typed; use the type annotation
    Constructor, // at least one in the constructor; use control flow
    Method, // methods only; look in base first, then union all declaration types plus undefined
}

// --- from relater.go ---

// relater.go:64-77
[System.Flags]
public enum RelationComparisonResult : uint
{
    None = 0,
    Succeeded = 1 << 0,
    Failed = 1 << 1,
    ReportsUnmeasurable = 1 << 3,
    ReportsUnreliable = 1 << 4,
    ComplexityOverflow = 1 << 5,
    StackDepthOverflow = 1 << 6,
    ReportsMask = ReportsUnmeasurable | ReportsUnreliable,
    Overflow = ComplexityOverflow | StackDepthOverflow,
}

// relater.go:55-61
[System.Flags]
public enum ExpandingFlags : byte
{
    None = 0,
    Source = 1 << 0,
    Target = 1 << 1,
    Both = Source | Target,
}

// relater.go:38-43
[System.Flags]
public enum IntersectionState : uint
{
    None = 0,
    Source = 1 << 0, // Source type is a constituent of an outer intersection
    Target = 1 << 1, // Target type is a constituent of an outer intersection
}

// relater.go:45-52
[System.Flags]
public enum RecursionFlags : uint
{
    None = 0,
    Source = 1 << 0,
    Target = 1 << 1,
    Both = Source | Target,
}

// --- from mapper.go ---

// mapper.go:11-17
public enum TypeMapperKind
{
    Unknown,
    Simple,
    Array,
    Merged,
}

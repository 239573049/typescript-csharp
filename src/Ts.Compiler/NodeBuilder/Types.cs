// Go: internal/nodebuilder/types.go
// Interfaces and types defining the node builder — concrete implementations sit on
// top of the checker, but these types are used by the emit resolver in the printer.
using Ts.Ast;

namespace Ts.NodeBuilder;

// TODO: previously all SymbolTracker methods were optional, but now they're required.
public interface ISymbolTracker
{
    public bool TrackSymbol(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning);

    public void ReportInaccessibleThisError();

    public void ReportPrivateInBaseOfClassExpression(string propertyName);

    public void ReportInaccessibleUniqueSymbolError();

    public void ReportCyclicStructureError();

    public void ReportLikelyUnsafeImportRequiredError(string specifier, string symbolName);

    public void ReportTruncationError();

    public void ReportNonlocalAugmentation(SourceFile containingFile, Symbol parentSymbol, Symbol augmentingSymbol);

    public void ReportNonSerializableProperty(string propertyName);

    public void ReportInferenceFallback(Node node);

    public void PushErrorFallbackNode(Node? node);

    public void PopErrorFallbackNode();
}

// NOTE: If modifying this enum, must modify TypeFormatFlags too!
[System.Flags]
public enum Flags : uint
{
    None = 0,

    // Options
    NoTruncation = 1 << 0,
    WriteArrayAsGenericType = 1 << 1,
    GenerateNamesForShadowedTypeParams = 1 << 2,
    UseStructuralFallback = 1 << 3,
    ForbidIndexedAccessSymbolReferences = 1 << 4,
    WriteTypeArgumentsOfSignature = 1 << 5,
    UseFullyQualifiedType = 1 << 6,
    UseOnlyExternalAliasing = 1 << 7,
    SuppressAnyReturnType = 1 << 8,
    WriteTypeParametersInQualifiedName = 1 << 9,
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

    // Error handling
    AllowThisInObjectLiteral = 1 << 15,
    AllowQualifiedNameInPlaceOfIdentifier = 1 << 16,
    AllowAnonymousIdentifier = 1 << 17,
    AllowEmptyUnionOrIntersection = 1 << 18,
    AllowEmptyTuple = 1 << 19,
    AllowUniqueESSymbolType = 1 << 20,
    AllowEmptyIndexInfoType = 1 << 21,

    // Errors (cont.)
    AllowNodeModulesRelativePaths = 1 << 26,
    IgnoreErrors = AllowThisInObjectLiteral | AllowQualifiedNameInPlaceOfIdentifier | AllowAnonymousIdentifier |
                   AllowEmptyUnionOrIntersection | AllowEmptyTuple | AllowEmptyIndexInfoType | AllowNodeModulesRelativePaths,

    // State
    InObjectTypeLiteral = 1 << 22,
    InTypeAlias = 1 << 23,
    InInitialEntityName = 1 << 24,
}

public enum InternalFlags
{
    None = 0,
    WriteComputedProps = 1 << 0,
    NoSyntacticPrinter = 1 << 1,
    DoNotIncludeSymbolChain = 1 << 2,
    AllowUnresolvedNames = 1 << 3,
}

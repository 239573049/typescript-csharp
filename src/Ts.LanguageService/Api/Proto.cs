// Ported from tsgo internal/api/proto.go (whole file).
//
// PORT-NOTE (JSON face): Go (un)marshals these types with struct tags via the internal
// json package. Per plan/07 §4.2 #5 the C# face serializes through ApiJsonContext
// (System.Text.Json source generation); every Go `json:"..."` tag is realized as an
// explicit [JsonPropertyName]. Go's `omitempty` on strings omits the zero value "" as
// well as null; the C# surface uses string? + WhenWritingNull ("" cannot be
// distinguished from null by STJ's ignore conditions), and response constructors leave
// unset strings null — see the registered deviation in the api wave report.
// Go `omitzero` on scalar struct fields maps to [JsonIgnore(WhenWritingDefault)].

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ts.Checker;
using Ts.Core;
using Ts.LsProto;

namespace Ts.Api;

// proto.go:21-23 — sentinel errors.
public static class ProtoErrors
{
    public const string ErrInvalidRequest = "api: invalid request";
    public const string ErrClientError = "api: client error";
}

// proto.go:25 — type Method string; the Go typed constants (proto.go:56-206) live on
// the struct as static members.
[JsonConverter(typeof(MethodJsonConverter))]
public readonly record struct Method(string Value)
{
    public override string ToString() => Value ?? "";
    public static implicit operator Method(string value) => new(value);

    public static readonly Method Release = new("release");

    // MethodGetServerTiming retrieves the server's collected per-request
    // processing-time totals and recent-request ring buffer. It is handled by
    // the connection itself (not the session) and is not recorded in the timing
    // it reports.
    public static readonly Method GetServerTiming = new("getServerTiming");

    // MethodResetServerTiming clears the server's collected timing totals and
    // recent-request ring buffer. Like MethodGetServerTiming, it is handled by
    // the connection itself and is not recorded.
    public static readonly Method ResetServerTiming = new("resetServerTiming");

    public static readonly Method Initialize = new("initialize");
    public static readonly Method UpdateSnapshot = new("updateSnapshot");
    public static readonly Method ParseConfigFile = new("parseConfigFile");
    public static readonly Method GetDefaultProjectForFile = new("getDefaultProjectForFile");
    public static readonly Method GetSymbolAtPosition = new("getSymbolAtPosition");
    public static readonly Method GetSymbolsAtPositions = new("getSymbolsAtPositions");
    public static readonly Method GetSymbolAtLocation = new("getSymbolAtLocation");
    public static readonly Method GetSymbolsAtLocations = new("getSymbolsAtLocations");
    public static readonly Method GetTypeOfSymbol = new("getTypeOfSymbol");
    public static readonly Method GetTypesOfSymbols = new("getTypesOfSymbols");
    public static readonly Method GetDeclaredTypeOfSymbol = new("getDeclaredTypeOfSymbol");
    public static readonly Method GetSourceFile = new("getSourceFile");
    public static readonly Method GetSourceFileNames = new("getSourceFileNames");
    public static readonly Method GetSourceFileMetadata = new("getSourceFileMetadata");
    public static readonly Method ResolveName = new("resolveName");
    public static readonly Method GetSignaturesOfType = new("getSignaturesOfType");
    public static readonly Method GetResolvedSignature = new("getResolvedSignature");
    public static readonly Method GetTypeAtLocation = new("getTypeAtLocation");
    public static readonly Method GetTypeAtLocations = new("getTypeAtLocations");
    public static readonly Method GetTypeAtPosition = new("getTypeAtPosition");
    public static readonly Method GetTypesAtPositions = new("getTypesAtPositions");

    // Symbol sub-property methods
    public static readonly Method GetParentOfSymbol = new("getParentOfSymbol");
    public static readonly Method GetMembersOfSymbol = new("getMembersOfSymbol");
    public static readonly Method GetExportsOfSymbol = new("getExportsOfSymbol");
    public static readonly Method GetExportSymbolOfSymbol = new("getExportSymbolOfSymbol");

    // Type sub-property methods
    public static readonly Method GetSymbolOfType = new("getSymbolOfType");
    public static readonly Method GetTargetOfType = new("getTargetOfType");
    public static readonly Method GetFreshTypeOfType = new("getFreshTypeOfType");
    public static readonly Method GetRegularTypeOfType = new("getRegularTypeOfType");
    public static readonly Method GetTypesOfType = new("getTypesOfType");
    public static readonly Method GetTypeParametersOfType = new("getTypeParametersOfType");
    public static readonly Method GetOuterTypeParametersOfType = new("getOuterTypeParametersOfType");
    public static readonly Method GetLocalTypeParametersOfType = new("getLocalTypeParametersOfType");
    public static readonly Method GetAliasTypeArgumentsOfType = new("getAliasTypeArgumentsOfType");
    public static readonly Method GetAliasSymbolOfType = new("getAliasSymbolOfType");
    public static readonly Method GetObjectTypeOfType = new("getObjectTypeOfType");
    public static readonly Method GetIndexTypeOfType = new("getIndexTypeOfType");
    public static readonly Method GetCheckTypeOfType = new("getCheckTypeOfType");
    public static readonly Method GetExtendsTypeOfType = new("getExtendsTypeOfType");
    public static readonly Method GetBaseTypeOfType = new("getBaseTypeOfType");
    public static readonly Method GetConstraintOfType = new("getConstraintOfType");

    // Signature sub-property methods
    public static readonly Method GetTypeParametersOfSignature = new("getTypeParametersOfSignature");
    public static readonly Method GetParametersOfSignature = new("getParametersOfSignature");
    public static readonly Method GetThisParameterOfSignature = new("getThisParameterOfSignature");
    public static readonly Method GetTargetOfSignature = new("getTargetOfSignature");

    // Checker methods
    public static readonly Method GetContextualType = new("getContextualType");
    public static readonly Method GetBaseTypeOfLiteralType = new("getBaseTypeOfLiteralType");
    public static readonly Method GetNonNullableType = new("getNonNullableType");
    public static readonly Method GetTypeFromTypeNode = new("getTypeFromTypeNode");
    public static readonly Method GetWidenedType = new("getWidenedType");
    public static readonly Method GetParameterType = new("getParameterType");
    public static readonly Method IsArrayLikeType = new("isArrayLikeType");
    public static readonly Method IsTypeAssignableTo = new("isTypeAssignableTo");
    public static readonly Method GetShorthandAssignmentValueSymbol = new("getShorthandAssignmentValueSymbol");
    public static readonly Method GetTypeOfSymbolAtLocation = new("getTypeOfSymbolAtLocation");
    public static readonly Method TypeToTypeNode = new("typeToTypeNode");
    public static readonly Method SignatureToSignatureDeclaration = new("signatureToSignatureDeclaration");
    public static readonly Method TypeToString = new("typeToString");
    public static readonly Method IsContextSensitive = new("isContextSensitive");
    public static readonly Method GetReturnTypeOfSignature = new("getReturnTypeOfSignature");
    public static readonly Method GetRestTypeOfSignature = new("getRestTypeOfSignature");
    public static readonly Method GetTypePredicateOfSignature = new("getTypePredicateOfSignature");
    public static readonly Method GetBaseTypes = new("getBaseTypes");
    public static readonly Method GetPropertiesOfType = new("getPropertiesOfType");
    public static readonly Method GetApparentType = new("getApparentType");
    public static readonly Method GetPropertyOfType = new("getPropertyOfType");
    public static readonly Method GetIndexInfosOfType = new("getIndexInfosOfType");
    public static readonly Method GetConstraintOfTypeParameter = new("getConstraintOfTypeParameter");
    public static readonly Method GetBaseConstraintOfType = new("getBaseConstraintOfType");
    public static readonly Method GetTypeArguments = new("getTypeArguments");
    public static readonly Method GetTrueTypeOfConditionalType = new("getTrueTypeOfConditionalType");
    public static readonly Method GetFalseTypeOfConditionalType = new("getFalseTypeOfConditionalType");
    public static readonly Method GetConstantValue = new("getConstantValue");
    public static readonly Method GetSignatureFromDeclaration = new("getSignatureFromDeclaration");
    public static readonly Method GetExportSpecifierLocalTarget = new("getExportSpecifierLocalTargetSymbol");
    public static readonly Method GetAliasedSymbol = new("getAliasedSymbol");
    public static readonly Method GetImmediateAliasedSymbol = new("getImmediateAliasedSymbol");
    public static readonly Method GetExportsOfModule = new("getExportsOfModule");
    public static readonly Method GetMemberInModuleExports = new("getMemberInModuleExports");
    public static readonly Method GetJSDocTags = new("getJsDocTags");
    public static readonly Method GetDocumentationComment = new("getDocumentationComment");
    public static readonly Method IsArrayType = new("isArrayType");
    public static readonly Method IsTupleType = new("isTupleType");

    // Reference methods
    public static readonly Method GetReferencesToSymbolInFile = new("getReferencesToSymbolInFile");
    public static readonly Method GetReferencedSymbolsForNode = new("getReferencedSymbolsForNode");
    public static readonly Method GetSignatureUsages = new("getSignatureUsages");

    // Language service methods
    public static readonly Method GetCompletionsAtPosition = new("getCompletionsAtPosition");

    // Diagnostic methods
    public static readonly Method GetSyntacticDiagnostics = new("getSyntacticDiagnostics");
    public static readonly Method GetBindDiagnostics = new("getBindDiagnostics");
    public static readonly Method GetSemanticDiagnostics = new("getSemanticDiagnostics");
    public static readonly Method GetSuggestionDiagnostics = new("getSuggestionDiagnostics");
    public static readonly Method GetDeclarationDiagnostics = new("getDeclarationDiagnostics");
    public static readonly Method GetProgramDiagnostics = new("getProgramDiagnostics");
    public static readonly Method GetGlobalDiagnostics = new("getGlobalDiagnostics");
    public static readonly Method GetConfigFileParsingDiagnostics = new("getConfigFileParsingDiagnostics");

    // Emitter methods
    public static readonly Method PrintNode = new("printNode");

    // Intrinsic type getters
    public static readonly Method GetAnyType = new("getAnyType");
    public static readonly Method GetStringType = new("getStringType");
    public static readonly Method GetNumberType = new("getNumberType");
    public static readonly Method GetBooleanType = new("getBooleanType");
    public static readonly Method GetVoidType = new("getVoidType");
    public static readonly Method GetUndefinedType = new("getUndefinedType");
    public static readonly Method GetNullType = new("getNullType");
    public static readonly Method GetNeverType = new("getNeverType");
    public static readonly Method GetUnknownType = new("getUnknownType");
    public static readonly Method GetBigIntType = new("getBigIntType");
    public static readonly Method GetESSymbolType = new("getESSymbolType");

    // Well-known per-checker symbols
    public static readonly Method GetWellKnownSymbols = new("getWellKnownSymbols");

    // Well-known per-checker signatures
    public static readonly Method GetWellKnownSignatures = new("getWellKnownSignatures");

    // Profiling methods
    public static readonly Method StartCPUProfile = new("startCPUProfile");
    public static readonly Method StopCPUProfile = new("stopCPUProfile");
    public static readonly Method SaveHeapProfile = new("saveHeapProfile");
}

// proto.go:27-34 — handle ID types.
[JsonConverter(typeof(SnapshotIdJsonConverter))]
public readonly record struct SnapshotId(ulong Value)
{
    // proto.go:28 — Go type SnapshotID uint64.
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(ProjectIdJsonConverter))]
public readonly record struct ProjectId(string Value)
{
    // proto.go:29 — Go type ProjectID string.
    public override string ToString() => Value ?? "";
}

[JsonConverter(typeof(SymbolIdJsonConverter))]
public readonly record struct SymbolId(ulong Value)
{
    // proto.go:30 — Go type SymbolID uint64.
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(TypeIdJsonConverter))]
public readonly record struct ApiTypeId(uint Value)
{
    // proto.go:31 — Go type TypeID uint32. (Named ApiTypeId: TypeId exists in
    // Ts.Checker for the checker-local id.)
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(SignatureIdJsonConverter))]
public readonly record struct ApiSignatureId(ulong Value)
{
    // proto.go:32 — Go type SignatureID uint64.
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(NodeHandleJsonConverter))]
public readonly record struct NodeHandle(string Value)
{
    // proto.go:33 — Go type NodeHandle string.
    public override string ToString() => Value ?? "";
}

// proto.go:36-54 — handle constructor functions (Go package-level functions hosted
// in a static class; C# has no namespace-level functions).
public static class HandleFunctions
{
    // proto.go:36-38 — ProjectHandle.
    public static ProjectId ProjectHandle(global::Ts.Project.Project p)
    {
        return new ProjectId(p.Id().Value);
    }

    // proto.go:40-42 — SymbolHandle.
    public static SymbolId SymbolHandle(global::Ts.Ast.Symbol symbol)
    {
        return new SymbolId(global::Ts.Ast.AstUtilities.GetSymbolId(symbol).Value);
    }

    // proto.go:44-46 — TypeHandle.
    public static ApiTypeId TypeHandle(global::Ts.Checker.Type t)
    {
        return new ApiTypeId(t.Id.Value);
    }

    // proto.go:48-50 — SignatureHandle.
    public static ApiSignatureId SignatureHandle(Signature sig)
    {
        return new ApiSignatureId(sig.Id().Value);
    }

    // proto.go:52-54 — parseProjectHandle.
    public static global::Ts.TsPath.Path ParseProjectHandle(ProjectId handle)
    {
        return new global::Ts.TsPath.Path(handle.Value);
    }
}

// InitializeResponse is returned by the initialize method. (proto.go:208-214)
public sealed class InitializeResponse
{
    // UseCaseSensitiveFileNames indicates whether the host file system is case-sensitive.
    [JsonPropertyName("useCaseSensitiveFileNames")]
    public bool UseCaseSensitiveFileNames;

    // CurrentDirectory is the server's current working directory.
    [JsonPropertyName("currentDirectory")]
    public string CurrentDirectory = "";
}

// DocumentIdentifier identifies a document by either a file name (plain string) or a URI object.
// On the wire it is string | { uri: string }. (proto.go:216-221)
[JsonConverter(typeof(DocumentIdentifierJsonConverter))]
public sealed class DocumentIdentifier
{
    [JsonPropertyName("fileName")]
    public string FileName = "";  // Go: string zero value (proto.go:219).

    [JsonPropertyName("uri")]
    public global::Ts.LsProto.DocumentUri URI;

    // proto.go:261-266.
    public string ToFileName()
    {
        if (URI.Value is not null and not "")
        {
            return URI.FileName();
        }
        return FileName ?? "";
    }

    // ToURI returns the document URI for this identifier. An explicitly provided URI
    // is returned as-is; a file name is first normalized to an absolute path against
    // cwd before being converted to a URI. (proto.go:268-276)
    public global::Ts.LsProto.DocumentUri ToUri(string cwd)
    {
        if (URI.Value is not null and not "")
        {
            return URI;
        }
        return global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(FileName ?? "", cwd));
    }

    // proto.go:278-283.
    public string ToAbsoluteFileName(string cwd)
    {
        if (URI.Value is not null and not "")
        {
            return URI.FileName();
        }
        return global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(FileName ?? "", cwd);
    }

    // proto.go:285-290.
    public override string ToString()
    {
        if (URI.Value is not null and not "")
        {
            return URI.Value;
        }
        return FileName ?? "";
    }
}

// APIFileChangeSummary lists documents that have been changed, created, or deleted.
// (proto.go:292-297)
public sealed class ApiFileChangeSummary
{
    [JsonPropertyName("changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? Changed;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? Created;

    [JsonPropertyName("deleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? Deleted;
}

// APIFileChanges describes file changes to apply when updating a snapshot.
// Either InvalidateAll is true (discard all caches) or Changed/Created/Deleted
// list individual documents. (proto.go:299-307)
public sealed class ApiFileChanges
{
    [JsonPropertyName("invalidateAll")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InvalidateAll;

    [JsonPropertyName("changed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? Changed;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? Created;

    [JsonPropertyName("deleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? Deleted;
}

// UpdateSnapshotParams are the parameters for creating a new snapshot.
// All fields are optional. With no fields set, the server adopts the latest LSP state.
// (proto.go:309-330)
public sealed class UpdateSnapshotParams
{
    // OpenProjects lists tsconfig.json files to open/load in the new snapshot.
    // Opens are ref-counted and persist across snapshots until closed.
    [JsonPropertyName("openProjects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? OpenProjects;

    // CloseProjects lists tsconfig.json files to release in the new snapshot.
    // A project is only unloaded once every API client that opened it closes it.
    [JsonPropertyName("closeProjects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? CloseProjects;

    // FileChanges describes file system changes since the last snapshot.
    [JsonPropertyName("fileChanges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiFileChanges? FileChanges;

    // OpenFiles lists files to keep open for the API client, mirroring LSP's
    // textDocument/didOpen. For each file, ancestor directories are searched for a
    // tsconfig that contains it; if found, that configured project is loaded and
    // becomes the file's default project. Otherwise the file is loaded into the
    // inferred project (e.g. a node_modules d.ts not in any project's import graph).
    // Opens persist across snapshots until the file is closed.
    [JsonPropertyName("openFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? OpenFiles;

    // CloseFiles lists files to release in the new snapshot. A file is only fully
    // closed once every API client that opened it closes it.
    [JsonPropertyName("closeFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier[]? CloseFiles;
}

// ProjectFileChanges describes what source files changed within a single project.
// (proto.go:332-338)
public sealed class ProjectFileChanges
{
    // ChangedFiles lists source file paths whose content differs.
    [JsonPropertyName("changedFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::Ts.TsPath.Path[]? ChangedFiles;

    // DeletedFiles lists source file paths removed from the project's program.
    [JsonPropertyName("deletedFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::Ts.TsPath.Path[]? DeletedFiles;
}

// SnapshotChanges describes what changed between the previous latest snapshot
// and the newly created snapshot. Changes are reported per-project so clients
// can track cache refs at the (snapshot, project) level. (proto.go:340-350)
public sealed class SnapshotChanges
{
    // ChangedProjects maps project handles to the file changes within that project.
    // Projects not listed here (and not in RemovedProjects) are unchanged.
    // PORT-NOTE: Go map[ProjectID]*ProjectFileChanges serializes as a JSON object
    // keyed by the project handle string; C# stores the same shape with string keys.
    [JsonPropertyName("changedProjects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Collections.Generic.Dictionary<string, ProjectFileChanges>? ChangedProjects;

    // RemovedProjects lists project handles that were present in the previous
    // snapshot but absent from the new one.
    [JsonPropertyName("removedProjects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectId[]? RemovedProjects;
}

// UpdateSnapshotResponse is returned by updateSnapshot. (proto.go:352-361)
public sealed class UpdateSnapshotResponse
{
    // Snapshot is the handle for the newly created snapshot.
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    // Projects is the list of projects in the snapshot.
    [JsonPropertyName("projects")]
    public ProjectResponse[] Projects = [];

    // Changes describes source file differences from the previous snapshot.
    // Nil for the first snapshot in a session.
    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapshotChanges? Changes;
}

// proto.go:483-485.
public sealed class ParseConfigFileParams
{
    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();
}

// ReleaseParams are the parameters for the release method. (proto.go:487-490)
public sealed class ReleaseParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;
}

// proto.go:492-494.
public sealed class ProfileParams
{
    [JsonPropertyName("dir")]
    public string Dir = "";
}

// proto.go:496-498.
public sealed class ProfileResult
{
    [JsonPropertyName("file")]
    public string File = "";
}

// proto.go:500-503.
public sealed class ConfigFileResponse
{
    [JsonPropertyName("fileNames")]
    public string[] FileNames = [];

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompilerOptions? Options;
}

// proto.go:505-508.
public sealed class GetDefaultProjectForFileParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();
}

// proto.go:510-515.
public sealed class ProjectResponse
{
    [JsonPropertyName("id")]
    public ProjectId Id;

    [JsonPropertyName("configFileName")]
    public string ConfigFileName = "";

    [JsonPropertyName("rootFiles")]
    public string[] RootFiles = [];

    [JsonPropertyName("compilerOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompilerOptions? CompilerOptions;
}

// proto.go:529-534.
public sealed class GetSymbolAtPositionParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();

    [JsonPropertyName("position")]
    public uint Position;
}

// proto.go:536-541.
public sealed class GetSymbolsAtPositionsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();

    [JsonPropertyName("positions")]
    public uint[] Positions = [];
}

// proto.go:543-547.
public sealed class GetSymbolAtLocationParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// proto.go:549-553.
public sealed class GetSymbolsAtLocationsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("locations")]
    public NodeHandle[] Locations = [];
}

// proto.go:555-565.
public sealed class SymbolResponse
{
    [JsonPropertyName("id")]
    public SymbolId Id;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("name")]
    public string Name = "";

    [JsonPropertyName("flags")]
    public uint Flags;

    [JsonPropertyName("checkFlags")]
    public uint CheckFlags;

    [JsonPropertyName("declarations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle[]? Declarations;

    [JsonPropertyName("valueDeclaration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? ValueDeclaration;

    [JsonPropertyName("parent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SymbolId Parent;

    [JsonPropertyName("exportSymbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SymbolId ExportSymbol;
}

// proto.go:578-582.
public sealed class GetTypeOfSymbolParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("symbol")]
    public SymbolId Symbol;
}

// proto.go:584-588.
public sealed class GetTypesOfSymbolsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("symbols")]
    public SymbolId[] Symbols = [];
}

// proto.go:590-642.
public sealed class TypeResponse
{
    [JsonPropertyName("id")]
    public ApiTypeId Id;

    [JsonPropertyName("flags")]
    public uint Flags;

    [JsonPropertyName("objectFlags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint ObjectFlags;

    // LiteralType data
    [JsonPropertyName("value")]
    public object? Value;

    // ObjectType / TypeReference / StringMappingType / IndexType target
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId Target;

    // InterfaceType type parameters
    [JsonPropertyName("typeParameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiTypeId[]? TypeParameters;

    [JsonPropertyName("outerTypeParameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiTypeId[]? OuterTypeParameters;

    [JsonPropertyName("localTypeParameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiTypeId[]? LocalTypeParameters;

    // TupleType data
    [JsonPropertyName("elementFlags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ElementFlags[]? ElementFlags;

    [JsonPropertyName("fixedLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FixedLength;

    [JsonPropertyName("readonly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TupleReadonly;

    // IndexedAccessType data
    [JsonPropertyName("objectType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId ObjectType;

    [JsonPropertyName("indexType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId IndexType;

    // ConditionalType data
    [JsonPropertyName("checkType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId CheckType;

    [JsonPropertyName("extendsType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId ExtendsType;

    // SubstitutionType data
    [JsonPropertyName("baseType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId BaseType;

    [JsonPropertyName("substConstraint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId SubstConstraint;

    // TemplateLiteralType text segments
    [JsonPropertyName("texts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Texts;

    // FreshableType data (LiteralType and computed enum types)
    [JsonPropertyName("freshType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId FreshType;

    [JsonPropertyName("regularType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiTypeId RegularType;

    // TypeParameter data
    [JsonPropertyName("isThisType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsThisType;

    // IntrinsicType data
    [JsonPropertyName("intrinsicName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntrinsicName;

    // TypeAlias data
    [JsonPropertyName("aliasTypeArguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiTypeId[]? AliasTypeArguments;

    [JsonPropertyName("aliasSymbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SymbolId AliasSymbol;

    // Symbol associated with structured types
    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SymbolId Symbol;
}

// proto.go:758-766.
public sealed class SignatureResponse
{
    [JsonPropertyName("id")]
    public ApiSignatureId Id;

    [JsonPropertyName("flags")]
    public uint Flags;

    [JsonPropertyName("declaration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? Declaration;

    [JsonPropertyName("typeParameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApiTypeId[]? TypeParameters;

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SymbolId[]? Parameters;

    [JsonPropertyName("thisParameter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SymbolId ThisParameter;

    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiSignatureId Target;
}

// proto.go:768-772.
public sealed class GetSourceFileParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();
}

// proto.go:774-777.
public sealed class GetSourceFileNamesParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;
}

// SourceFileMetadata carries program-stored metadata about a single source file.
// (proto.go:779-786)
public sealed class SourceFileMetadata
{
    [JsonPropertyName("isDefaultLibrary")]
    public bool IsDefaultLibrary;

    [JsonPropertyName("isFromExternalLibrary")]
    public bool IsFromExternalLibrary;

    [JsonPropertyName("packageJsonType")]
    public string PackageJsonType = "";

    [JsonPropertyName("packageJsonDirectory")]
    public string PackageJsonDirectory = "";

    [JsonPropertyName("impliedNodeFormat")]
    public global::Ts.Core.ModuleKind ImpliedNodeFormat; // Go ast.ResolutionMode (= ModuleKind, Ts.Compiler GlobalUsings.cs:9)
}

// proto.go:788-797.
public sealed class ResolveNameParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("name")]
    public string Name = "";

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? Location; // Optional: node handle for location context

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier? File; // Optional: file for location context (alternative to Location)

    [JsonPropertyName("position")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Position; // Optional: position in file for location context (with File)

    [JsonPropertyName("meaning")]
    public uint Meaning; // SymbolFlags for what kind of symbol to find

    [JsonPropertyName("excludeGlobals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExcludeGlobals; // Whether to exclude global symbols
}

// GetTypePropertyParams is used for all type sub-property endpoints. (proto.go:799-804)
public sealed class GetTypePropertyParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("objectId")]
    public ApiTypeId Type;
}

// GetSymbolPropertyParams is used for all symbol sub-property endpoints. (proto.go:806-811)
public sealed class GetSymbolPropertyParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("objectId")]
    public SymbolId Symbol;
}

// GetSignaturePropertyParams is used for all signature sub-property endpoints. (proto.go:813-818)
public sealed class GetSignaturePropertyParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("objectId")]
    public ApiSignatureId Signature;
}

// GetContextualTypeParams returns the contextual type for a node. (proto.go:820-825)
public sealed class GetContextualTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// GetTypeOfSymbolAtLocationParams returns the narrowed type of a symbol at a specific location.
// (proto.go:827-833)
public sealed class GetTypeOfSymbolAtLocationParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("symbol")]
    public SymbolId Symbol;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// GetReferencesToSymbolInFileParams are the parameters for the getReferencesToSymbolInFile method.
// (proto.go:835-841)
public sealed class GetReferencesToSymbolInFileParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();

    [JsonPropertyName("symbol")]
    public SymbolId Symbol;
}

// GetReferencedSymbolsForNodeParams are the parameters for the getReferencedSymbolsForNode method.
// (proto.go:843-849)
public sealed class GetReferencedSymbolsForNodeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("node")]
    public NodeHandle Node;

    [JsonPropertyName("position")]
    public int Position;
}

// ReferencedSymbolEntry represents a symbol definition and its references.
// (proto.go:851-856)
public sealed class ReferencedSymbolEntry
{
    [JsonPropertyName("definition")]
    public NodeHandle Definition;

    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SymbolResponse? Symbol;

    [JsonPropertyName("references")]
    public NodeHandle[] References = [];
}

// GetSignatureUsagesParams are the parameters for the getSignatureUsages method.
// (proto.go:858-863)
public sealed class GetSignatureUsagesParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("signatureDecl")]
    public NodeHandle SignatureDecl;
}

// SignatureUsageResponse represents a single usage of a signature as a name-call pair.
// (proto.go:865-869)
public sealed class SignatureUsageResponse
{
    [JsonPropertyName("name")]
    public NodeHandle Name;

    [JsonPropertyName("call")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? Call;
}

// GetCompletionsAtPositionParams are the parameters for the getCompletionsAtPosition method.
// (proto.go:871-879)
public sealed class GetCompletionsAtPositionParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();

    [JsonPropertyName("position")]
    public uint Position;

    [JsonPropertyName("triggerCharacter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TriggerCharacter;

    [JsonPropertyName("includeSymbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeSymbol;
}

// CompletionEntryLabelDetailsResponse holds additional label display text for a completion entry.
// (proto.go:881-885)
public sealed class CompletionEntryLabelDetailsResponse
{
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description;
}

// CompletionEntryResponse represents a single completion item. (proto.go:887-897)
public sealed class CompletionEntryResponse
{
    [JsonPropertyName("name")]
    public string Name = "";

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Kind;

    [JsonPropertyName("sortText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SortText;

    [JsonPropertyName("insertText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InsertText;

    [JsonPropertyName("filterText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterText;

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail;

    [JsonPropertyName("labelDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionEntryLabelDetailsResponse? LabelDetails;

    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SymbolResponse? Symbol;
}

// CompletionInfoResponse wraps a list of completion entries. (proto.go:899-903)
public sealed class CompletionInfoResponse
{
    [JsonPropertyName("isIncomplete")]
    public bool IsIncomplete;

    [JsonPropertyName("entries")]
    public CompletionEntryResponse[] Entries = [];
}

// GetIntrinsicTypeParams is used for intrinsic type getters (anyType, stringType, etc.).
// (proto.go:905-909)
public sealed class GetIntrinsicTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;
}

// WellKnownSymbolsResponse carries the handle ids of the per-checker singleton
// symbols (unknown, undefined, arguments) so the client can identify them by id
// without a round-trip on every check. (proto.go:911-918)
public sealed class WellKnownSymbolsResponse
{
    [JsonPropertyName("unknown")]
    public SymbolId Unknown;

    [JsonPropertyName("undefined")]
    public SymbolId Undefined;

    [JsonPropertyName("arguments")]
    public SymbolId Arguments;
}

// WellKnownSignaturesResponse carries the handle id of the per-checker singleton
// unknown signature (the signature the checker yields when a call cannot be
// resolved) so the client can identify it by id without a round-trip on every check.
// (proto.go:920-925)
public sealed class WellKnownSignaturesResponse
{
    [JsonPropertyName("unknown")]
    public ApiSignatureId Unknown;
}

// GetBaseTypeOfLiteralTypeParams returns the base type of a literal type. (proto.go:927-932)
public sealed class GetBaseTypeOfLiteralTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;
}

// GetNonNullableTypeParams are the parameters for the getNonNullableType method. (proto.go:934-939)
public sealed class GetNonNullableTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;
}

// GetTypeFromTypeNodeParams are the parameters for the getTypeFromTypeNode method. (proto.go:941-946)
public sealed class GetTypeFromTypeNodeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// GetWidenedTypeParams are the parameters for the getWidenedType method. (proto.go:948-953)
public sealed class GetWidenedTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;
}

// GetParameterTypeParams are the parameters for the getParameterType method. (proto.go:955-961)
public sealed class GetParameterTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("signature")]
    public ApiSignatureId Signature;

    [JsonPropertyName("index")]
    public int Index;
}

// IsArrayLikeTypeParams checks whether a type is array-like. (proto.go:963-968)
public sealed class IsArrayLikeTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;
}

// IsTypeAssignableToParams checks assignability between two types. (proto.go:970-976)
public sealed class IsTypeAssignableToParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("source")]
    public ApiTypeId Source;

    [JsonPropertyName("target")]
    public ApiTypeId Target;
}

// proto.go:978-983.
public sealed class GetSignaturesOfTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;

    [JsonPropertyName("kind")]
    public int Kind;
}

// proto.go:985-989.
public sealed class GetResolvedSignatureParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// proto.go:991-995.
public sealed class GetTypeAtLocationParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// proto.go:997-1001.
public sealed class GetTypeAtLocationsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("locations")]
    public NodeHandle[] Locations = [];
}

// proto.go:1003-1008.
public sealed class GetTypeAtPositionParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();

    [JsonPropertyName("position")]
    public uint Position;
}

// proto.go:1010-1015.
public sealed class GetTypesAtPositionsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    public DocumentIdentifier File = new();

    [JsonPropertyName("positions")]
    public uint[] Positions = [];
}

// TypeToTypeNodeParams are the parameters for the typeToTypeNode method. (proto.go:1017-1024)
public sealed class TypeToTypeNodeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? Location;

    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Flags;
}

// SignatureToSignatureDeclarationParams are the parameters for the signatureToSignatureDeclaration method.
// (proto.go:1026-1034)
public sealed class SignatureToSignatureDeclarationParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("signature")]
    public ApiSignatureId Signature;

    [JsonPropertyName("kind")]
    public int Kind;

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? Location;

    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Flags;
}

// PrintNodeParams are the parameters for the printNode method. (proto.go:1036-1042)
public sealed class PrintNodeParams
{
    [JsonPropertyName("data")]
    public string Data = ""; // base64-encoded binary AST data

    [JsonPropertyName("preserveSourceNewlines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreserveSourceNewlines;

    [JsonPropertyName("neverAsciiEscape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeverAsciiEscape;

    [JsonPropertyName("terminateUnterminatedLiterals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TerminateUnterminatedLiterals;
}

// CheckerTypeParams are parameters for checker methods that operate on a type. (proto.go:1044-1049)
public sealed class CheckerTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;
}

// GetPropertyOfTypeParams are parameters for getPropertyOfType (a named property of a type).
// (proto.go:1051-1057)
public sealed class GetPropertyOfTypeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("type")]
    public ApiTypeId Type;

    [JsonPropertyName("name")]
    public string Name = "";
}

// GetMemberInModuleExportsParams are parameters for getMemberInModuleExports. (proto.go:1059-1065)
public sealed class GetMemberInModuleExportsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("symbol")]
    public SymbolId Symbol;

    [JsonPropertyName("name")]
    public string Name = "";
}

// CheckerNodeParams are parameters for checker methods that operate on a node location.
// (proto.go:1067-1072)
public sealed class CheckerNodeParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("location")]
    public NodeHandle Location;
}

// CheckerSymbolParams are parameters for checker methods that operate on a symbol.
// (proto.go:1074-1079)
public sealed class CheckerSymbolParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("symbol")]
    public SymbolId Symbol;
}

// JSDocTagInfo is a single JSDoc tag, mirroring Strada's JSDocTagInfo but with the tag text
// rendered as a plain string rather than SymbolDisplayPart[]. (proto.go:1081-1086)
public sealed class JSDocTagInfo
{
    [JsonPropertyName("name")]
    public string Name = "";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text;
}

// CheckerSignatureParams are parameters for checker methods that operate on a signature.
// (proto.go:1088-1093)
public sealed class CheckerSignatureParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("signature")]
    public ApiSignatureId Signature;
}

// TypePredicateResponse is the response for getTypePredicateOfSignature. (proto.go:1095-1101)
public sealed class TypePredicateResponse
{
    [JsonPropertyName("kind")]
    public int Kind;

    [JsonPropertyName("parameterIndex")]
    public int ParameterIndex;

    [JsonPropertyName("parameterName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParameterName;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypeResponse? Type;
}

// IndexInfoResponse represents a single index signature. (proto.go:1103-1109)
public sealed class IndexInfoResponse
{
    [JsonPropertyName("keyType")]
    public TypeResponse KeyType = null!;

    [JsonPropertyName("valueType")]
    public TypeResponse ValueType = null!;

    [JsonPropertyName("isReadonly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsReadonly;

    [JsonPropertyName("declaration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeHandle? Declaration;
}

// SourceFileResponse contains the binary-encoded AST data for a source file.
// The Data field is base64-encoded binary data in the encoder's format.
// (proto.go:1111-1115)
public sealed class SourceFileResponse
{
    [JsonPropertyName("data")]
    public string Data = "";
}

// GetDiagnosticsParams are parameters for per-file diagnostic methods. (proto.go:1117-1122)
public sealed class GetDiagnosticsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentIdentifier? File;
}

// GetProjectDiagnosticsParams are parameters for project-wide diagnostic methods.
// (proto.go:1124-1128)
public sealed class GetProjectDiagnosticsParams
{
    [JsonPropertyName("snapshot")]
    public SnapshotId Snapshot;

    [JsonPropertyName("project")]
    public ProjectId Project;
}

// DiagnosticResponse is the API response for a single diagnostic. (proto.go:1130-1152)
public sealed class DiagnosticResponse
{
    // FileName is the path of the file this diagnostic belongs to, if any.
    [JsonPropertyName("fileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName;

    // Pos is the start position of the diagnostic in the source file.
    [JsonPropertyName("pos")]
    public int Pos;

    // End is the end position of the diagnostic in the source file.
    [JsonPropertyName("end")]
    public int End;

    // Code is the diagnostic error code.
    [JsonPropertyName("code")]
    public int Code;

    // Category is the diagnostic category (error, warning, suggestion, message).
    [JsonPropertyName("category")]
    public global::Ts.Diagnostics.Category Category;

    // Text is the localized diagnostic message text.
    [JsonPropertyName("text")]
    public string Text = "";

    // ReportsUnnecessary indicates this diagnostic highlights unnecessary code.
    [JsonPropertyName("reportsUnnecessary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReportsUnnecessary;

    // ReportsDeprecated indicates this diagnostic highlights deprecated code.
    [JsonPropertyName("reportsDeprecated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReportsDeprecated;

    // MessageChain contains chained diagnostic messages, if any.
    [JsonPropertyName("messageChain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticResponse[]? MessageChain;

    // RelatedInformation contains related diagnostic information, if any.
    [JsonPropertyName("relatedInformation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticResponse[]? RelatedInformation;
}
// proto.go:1154-1205 + package-level helpers — Go's package functions hosted in a
// static class (C# has no namespace-level functions).
public static class ProtoFunctions
{
    // proto.go:517-527 — NewProjectResponse.
    public static ProjectResponse NewProjectResponse(global::Ts.Project.Project p)
    {
        if (p is null || p.CommandLine is null)
        {
            // Go: panic("NewProjectResponse called with unloaded project").
            throw new InvalidOperationException("NewProjectResponse called with unloaded project");
        }
        return new ProjectResponse
        {
            Id = HandleFunctions.ProjectHandle(p),
            ConfigFileName = p.Name(),
            RootFiles = p.CommandLine.FileNames() ?? [],
            CompilerOptions = p.CommandLine.CompilerOptions(),
        };
    }

    // proto.go:567-576 — symbolHandles.
    public static SymbolId[]? SymbolHandles(global::Ts.Ast.Symbol[] symbols)
    {
        if (symbols is null || symbols.Length == 0)
        {
            return null;
        }
        var handles = new SymbolId[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            handles[i] = HandleFunctions.SymbolHandle(symbols[i]);
        }
        return handles;
    }

    // proto.go:644-728 — newTypeResponse.
    public static TypeResponse NewTypeResponse(global::Ts.Checker.Type t, ApiTypeId id)
    {
        var resp = new TypeResponse
        {
            Id = id,
            Flags = (uint)t.Flags,
        };

        if (t.Symbol is not null)
        {
            resp.Symbol = HandleFunctions.SymbolHandle(t.Symbol);
        }

        if (t.Alias is not null)
        {
            resp.AliasTypeArguments = TypeHandles(t.Alias.TypeArguments());
            if (t.Alias.Symbol() is not null)
            {
                resp.AliasSymbol = HandleFunctions.SymbolHandle(t.Alias.Symbol()!);
            }
        }

        TypeFlags flags = t.Flags;
        if ((flags & TypeFlags.Freshable) != 0)
        {
            LiteralType lit = t.AsLiteralType();
            if ((flags & TypeFlags.Literal) != 0)
            {
                resp.Value = LiteralValueToJson(lit.Value());
            }
            if (lit.FreshType() is not null)
            {
                resp.FreshType = HandleFunctions.TypeHandle(lit.FreshType()!);
            }
            if (lit.RegularType() is not null)
            {
                resp.RegularType = HandleFunctions.TypeHandle(lit.RegularType()!);
            }
        }
        else if ((flags & TypeFlags.Object) != 0)
        {
            resp.ObjectFlags = (uint)t.ObjectFlags;
            ObjectFlags objectFlags = t.ObjectFlags;
            if ((objectFlags & ObjectFlags.Reference) != 0)
            {
                TypeReference @ref;
                if ((objectFlags & ObjectFlags.Tuple) != 0)
                {
                    TupleType tuple = t.AsTupleType();
                    @ref = tuple.AsTypeReference()!;
                    resp.ElementFlags = tuple.ElementFlags();
                    int fixedLen = tuple.FixedLength();
                    resp.FixedLength = fixedLen;
                    bool isReadonly = tuple.IsReadonly();
                    resp.TupleReadonly = isReadonly;
                }
                else
                {
                    @ref = t.AsTypeReference()!;
                }
                if (@ref.Target() is not null)
                {
                    resp.Target = HandleFunctions.TypeHandle(@ref.Target()!);
                }
            }
            if ((objectFlags & ObjectFlags.ClassOrInterface) != 0)
            {
                InterfaceType iface = t.AsInterfaceType()!;
                resp.TypeParameters = TypeHandles(iface.TypeParameters());
                resp.OuterTypeParameters = TypeHandles(iface.OuterTypeParameters());
                resp.LocalTypeParameters = TypeHandles(iface.LocalTypeParameters());
            }
        }
        else if ((flags & TypeFlags.UnionOrIntersection) != 0)
        {
            // types omitted; fetched via separate request
        }
        else if ((flags & TypeFlags.Index) != 0)
        {
            resp.Target = HandleFunctions.TypeHandle(t.AsIndexType().Target());
        }
        else if ((flags & TypeFlags.IndexedAccess) != 0)
        {
            IndexedAccessType data = t.AsIndexedAccessType();
            // Go: TypeHandle(data.ObjectType()) with no nil guard — nil would panic in
            // TypeHandle; the nil-forgiving passes preserve that contract.
            resp.ObjectType = HandleFunctions.TypeHandle(data.ObjectType()!);
            resp.IndexType = HandleFunctions.TypeHandle(data.IndexType()!);
        }
        else if ((flags & TypeFlags.Conditional) != 0)
        {
            ConditionalType data = t.AsConditionalType();
            // Go: TypeHandle(data.CheckType()) with no nil guard — same contract as above.
            resp.CheckType = HandleFunctions.TypeHandle(data.CheckType()!);
            resp.ExtendsType = HandleFunctions.TypeHandle(data.ExtendsType()!);
        }
        else if ((flags & TypeFlags.Substitution) != 0)
        {
            SubstitutionType data = t.AsSubstitutionType();
            resp.BaseType = HandleFunctions.TypeHandle(data.BaseType());
            resp.SubstConstraint = HandleFunctions.TypeHandle(data.SubstConstraint());
        }
        else if ((flags & TypeFlags.TemplateLiteral) != 0)
        {
            TemplateLiteralType tl = t.AsTemplateLiteralType();
            resp.Texts = tl.Texts();
            // types omitted; fetched via separate request
        }
        else if ((flags & TypeFlags.StringMapping) != 0)
        {
            resp.Target = HandleFunctions.TypeHandle(t.AsStringMappingType().Target());
        }
        else if ((flags & TypeFlags.TypeParameter) != 0)
        {
            resp.IsThisType = t.AsTypeParameter().IsThisType();
        }
        else if ((flags & TypeFlags.Intrinsic) != 0)
        {
            resp.IntrinsicName = t.AsIntrinsicType().IntrinsicName();
        }

        return resp;
    }

    // proto.go:730-739 — typeHandles.
    public static ApiTypeId[]? TypeHandles(global::Ts.Checker.Type[]? types)
    {
        if (types is null || types.Length == 0)
        {
            return null;
        }
        var handles = new ApiTypeId[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            handles[i] = HandleFunctions.TypeHandle(types[i]);
        }
        return handles;
    }

    // proto.go:741-756 — literalValueToJSON.
    public static object? LiteralValueToJson(object? value)
    {
        return value switch
        {
            string s => s,
            global::Ts.JsNum.Number n => n.Value,
            bool b => b,
            global::Ts.JsNum.PseudoBigInt big =>
                // Encode bigint literals as a signed decimal string (e.g. "-123"); the
                // API client decodes this back into a real bigint. JSON has no bigint.
                big.ToString(),
            _ => null,
        };
    }

    // proto.go:1154-1193 — NewDiagnosticResponse converts an ast.Diagnostic to a
    // DiagnosticResponse.
    // PORT-NOTE (iron law 3): Go stores AST positions as UTF-8 byte offsets and
    // converts them via file.GetPositionMap().UTF8ToUTF16 here; in the C# port every
    // AST position is already a UTF-16 code-unit offset (plan/01 §UTF-16), so the
    // conversion is realized by the position system itself and is a no-op here.
    public static DiagnosticResponse NewDiagnosticResponse(global::Ts.Ast.Diagnostic d)
    {
        int pos = d.Pos();
        int end = d.End();
        global::Ts.Ast.SourceFile? file = d.File();

        var resp = new DiagnosticResponse
        {
            Pos = pos,
            End = end,
            Code = d.Code(),
            Category = d.Category(),
            Text = d.Localize(global::Ts.Locale.Locale.Default),
            ReportsUnnecessary = d.ReportsUnnecessary(),
            ReportsDeprecated = d.ReportsDeprecated(),
        };

        if (file is not null)
        {
            resp.FileName = file.FileName();
        }

        if (d.MessageChain() is { Length: > 0 } chain)
        {
            resp.MessageChain = new DiagnosticResponse[chain.Length];
            for (int i = 0; i < chain.Length; i++)
            {
                resp.MessageChain[i] = NewDiagnosticResponse(chain[i]);
            }
        }

        if (d.RelatedInformation() is { Length: > 0 } related)
        {
            resp.RelatedInformation = new DiagnosticResponse[related.Length];
            for (int i = 0; i < related.Length; i++)
            {
                resp.RelatedInformation[i] = NewDiagnosticResponse(related[i]);
            }
        }

        return resp;
    }

    // proto.go:1195-1205 — NewDiagnosticResponses converts a slice of ast.Diagnostics
    // to DiagnosticResponses.
    public static DiagnosticResponse[] NewDiagnosticResponses(global::Ts.Ast.Diagnostic[]? diags)
    {
        if (diags is null || diags.Length == 0)
        {
            // Go: make([]DiagnosticResponse, 0) — empty, non-nil (marshals as []).
            return [];
        }
        var result = new DiagnosticResponse[diags.Length];
        for (int i = 0; i < diags.Length; i++)
        {
            result[i] = NewDiagnosticResponse(diags[i]);
        }
        return result;
    }
}

// proto.go:1207-1213 — unmarshalPayload dispatches a raw JSON payload to the typed
// params struct for the method. Errors throw ApiRequestException with Go's text.
public static class ProtoDispatch
{
    public static object? UnmarshalPayload(string method, byte[]? payload)
    {
        if (!Unmarshalers.Table.TryGetValue(method, out Func<byte[]?, object?>? unmarshaler))
        {
            throw new ApiRequestException($"unknown API method \"{method}\"");
        }
        return unmarshaler(payload);
    }

    // proto.go:1223-1225 — noParams.
    internal static object? NoParams(byte[]? data)
    {
        return null;
    }
}

public static class Unmarshalers
{
    private static Dictionary<string, Func<byte[]?, object?>>? table;

    public static System.Collections.Generic.IReadOnlyDictionary<string, Func<byte[]?, object?>> Table =>
        table ??= Build();

    private static Func<byte[]?, object?> Typed<T>(JsonTypeInfo<T> info)
    {
        return data => System.Text.Json.JsonSerializer.Deserialize(data ?? "null"u8.ToArray(), info);
    }

    private static Dictionary<string, Func<byte[]?, object?>> Build()
    {
        ApiJsonContext ctx = ApiJsonContext.Default;
        return new Dictionary<string, Func<byte[]?, object?>>
        {
            [Method.Release.Value] = Typed(ctx.ReleaseParams),
            [Method.Initialize.Value] = ProtoDispatch.NoParams,
            [Method.UpdateSnapshot.Value] = Typed(ctx.UpdateSnapshotParams),
            [Method.ParseConfigFile.Value] = Typed(ctx.ParseConfigFileParams),
            [Method.GetDefaultProjectForFile.Value] = Typed(ctx.GetDefaultProjectForFileParams),
            [Method.GetSourceFile.Value] = Typed(ctx.GetSourceFileParams),
            [Method.GetSourceFileNames.Value] = Typed(ctx.GetSourceFileNamesParams),
            [Method.GetSourceFileMetadata.Value] = Typed(ctx.GetSourceFileParams),
            [Method.GetSymbolAtPosition.Value] = Typed(ctx.GetSymbolAtPositionParams),
            [Method.GetSymbolsAtPositions.Value] = Typed(ctx.GetSymbolsAtPositionsParams),
            [Method.GetSymbolAtLocation.Value] = Typed(ctx.GetSymbolAtLocationParams),
            [Method.GetSymbolsAtLocations.Value] = Typed(ctx.GetSymbolsAtLocationsParams),
            [Method.GetTypeOfSymbol.Value] = Typed(ctx.GetTypeOfSymbolParams),
            [Method.GetTypesOfSymbols.Value] = Typed(ctx.GetTypesOfSymbolsParams),
            [Method.GetDeclaredTypeOfSymbol.Value] = Typed(ctx.GetTypeOfSymbolParams),
            [Method.ResolveName.Value] = Typed(ctx.ResolveNameParams),
            [Method.GetSignaturesOfType.Value] = Typed(ctx.GetSignaturesOfTypeParams),
            [Method.GetResolvedSignature.Value] = Typed(ctx.GetResolvedSignatureParams),
            [Method.GetTypeAtLocation.Value] = Typed(ctx.GetTypeAtLocationParams),
            [Method.GetTypeAtLocations.Value] = Typed(ctx.GetTypeAtLocationsParams),
            [Method.GetTypeAtPosition.Value] = Typed(ctx.GetTypeAtPositionParams),
            [Method.GetTypesAtPositions.Value] = Typed(ctx.GetTypesAtPositionsParams),

            [Method.GetParentOfSymbol.Value] = Typed(ctx.GetSymbolPropertyParams),
            [Method.GetMembersOfSymbol.Value] = Typed(ctx.GetSymbolPropertyParams),
            [Method.GetExportsOfSymbol.Value] = Typed(ctx.GetSymbolPropertyParams),
            [Method.GetExportSymbolOfSymbol.Value] = Typed(ctx.GetSymbolPropertyParams),

            [Method.GetSymbolOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetTargetOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetFreshTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetRegularTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetTypesOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetTypeParametersOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetOuterTypeParametersOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetLocalTypeParametersOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetAliasTypeArgumentsOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetAliasSymbolOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetObjectTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetIndexTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetCheckTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetExtendsTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetBaseTypeOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetConstraintOfType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetTrueTypeOfConditionalType.Value] = Typed(ctx.GetTypePropertyParams),
            [Method.GetFalseTypeOfConditionalType.Value] = Typed(ctx.GetTypePropertyParams),

            [Method.GetTypeParametersOfSignature.Value] = Typed(ctx.GetSignaturePropertyParams),
            [Method.GetParametersOfSignature.Value] = Typed(ctx.GetSignaturePropertyParams),
            [Method.GetThisParameterOfSignature.Value] = Typed(ctx.GetSignaturePropertyParams),
            [Method.GetTargetOfSignature.Value] = Typed(ctx.GetSignaturePropertyParams),

            [Method.GetContextualType.Value] = Typed(ctx.GetContextualTypeParams),
            [Method.GetBaseTypeOfLiteralType.Value] = Typed(ctx.GetBaseTypeOfLiteralTypeParams),
            [Method.GetNonNullableType.Value] = Typed(ctx.GetNonNullableTypeParams),
            [Method.GetTypeFromTypeNode.Value] = Typed(ctx.GetTypeFromTypeNodeParams),
            [Method.GetWidenedType.Value] = Typed(ctx.GetWidenedTypeParams),
            [Method.GetParameterType.Value] = Typed(ctx.GetParameterTypeParams),
            [Method.IsArrayLikeType.Value] = Typed(ctx.IsArrayLikeTypeParams),
            [Method.IsTypeAssignableTo.Value] = Typed(ctx.IsTypeAssignableToParams),
            [Method.GetShorthandAssignmentValueSymbol.Value] = Typed(ctx.GetTypeAtLocationParams),
            [Method.GetTypeOfSymbolAtLocation.Value] = Typed(ctx.GetTypeOfSymbolAtLocationParams),
            [Method.TypeToTypeNode.Value] = Typed(ctx.TypeToTypeNodeParams),
            [Method.SignatureToSignatureDeclaration.Value] = Typed(ctx.SignatureToSignatureDeclarationParams),
            [Method.TypeToString.Value] = Typed(ctx.TypeToTypeNodeParams),
            [Method.IsContextSensitive.Value] = Typed(ctx.GetContextualTypeParams),
            [Method.GetReturnTypeOfSignature.Value] = Typed(ctx.CheckerSignatureParams),
            [Method.GetRestTypeOfSignature.Value] = Typed(ctx.CheckerSignatureParams),
            [Method.GetTypePredicateOfSignature.Value] = Typed(ctx.CheckerSignatureParams),
            [Method.GetBaseTypes.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetPropertiesOfType.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetApparentType.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetPropertyOfType.Value] = Typed(ctx.GetPropertyOfTypeParams),
            [Method.GetIndexInfosOfType.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetConstraintOfTypeParameter.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetBaseConstraintOfType.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetTypeArguments.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetConstantValue.Value] = Typed(ctx.CheckerNodeParams),
            [Method.GetSignatureFromDeclaration.Value] = Typed(ctx.CheckerNodeParams),
            [Method.GetExportSpecifierLocalTarget.Value] = Typed(ctx.CheckerNodeParams),
            [Method.GetAliasedSymbol.Value] = Typed(ctx.CheckerSymbolParams),
            [Method.GetImmediateAliasedSymbol.Value] = Typed(ctx.CheckerSymbolParams),
            [Method.GetExportsOfModule.Value] = Typed(ctx.CheckerSymbolParams),
            [Method.GetMemberInModuleExports.Value] = Typed(ctx.GetMemberInModuleExportsParams),
            [Method.GetJSDocTags.Value] = Typed(ctx.CheckerSymbolParams),
            [Method.GetDocumentationComment.Value] = Typed(ctx.CheckerSymbolParams),
            [Method.IsArrayType.Value] = Typed(ctx.CheckerTypeParams),
            [Method.IsTupleType.Value] = Typed(ctx.CheckerTypeParams),
            [Method.GetReferencesToSymbolInFile.Value] = Typed(ctx.GetReferencesToSymbolInFileParams),
            [Method.GetReferencedSymbolsForNode.Value] = Typed(ctx.GetReferencedSymbolsForNodeParams),
            [Method.GetSignatureUsages.Value] = Typed(ctx.GetSignatureUsagesParams),
            [Method.GetCompletionsAtPosition.Value] = Typed(ctx.GetCompletionsAtPositionParams),
            [Method.PrintNode.Value] = Typed(ctx.PrintNodeParams),
            [Method.GetAnyType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetStringType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetNumberType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetBooleanType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetVoidType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetUndefinedType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetNullType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetNeverType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetUnknownType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetBigIntType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetESSymbolType.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetWellKnownSymbols.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetWellKnownSignatures.Value] = Typed(ctx.GetIntrinsicTypeParams),
            [Method.GetSyntacticDiagnostics.Value] = Typed(ctx.GetDiagnosticsParams),
            [Method.GetBindDiagnostics.Value] = Typed(ctx.GetDiagnosticsParams),
            [Method.GetSemanticDiagnostics.Value] = Typed(ctx.GetDiagnosticsParams),
            [Method.GetSuggestionDiagnostics.Value] = Typed(ctx.GetDiagnosticsParams),
            [Method.GetDeclarationDiagnostics.Value] = Typed(ctx.GetDiagnosticsParams),
            [Method.GetProgramDiagnostics.Value] = Typed(ctx.GetProjectDiagnosticsParams),
            [Method.GetGlobalDiagnostics.Value] = Typed(ctx.GetProjectDiagnosticsParams),
            [Method.GetConfigFileParsingDiagnostics.Value] = Typed(ctx.GetProjectDiagnosticsParams),
            [Method.StartCPUProfile.Value] = Typed(ctx.ProfileParams),
            [Method.StopCPUProfile.Value] = ProtoDispatch.NoParams,
            [Method.SaveHeapProfile.Value] = Typed(ctx.ProfileParams),
        };
    }
}

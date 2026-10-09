// ApiJsonContext — the System.Text.Json source-generation context for the api JSON face
// (plan/06 §9.1 / plan/07-aot-constraints.md §4.2 #5: "api JSON-RPC → ApiJsonContext :
// JsonSerializerContext（请求/响应封套 + proto 类型）"). One context per serialization
// face; no reflection serializer anywhere (plan/07 §4.1).
//
// The Go side (de)serializes proto.go types via struct tags with the internal json
// package; here every tag is an explicit [JsonPropertyName] on the type (Proto.cs) and
// this context only registers the contracts. Write-side values use
// SerializePolymorphic/SerializeToBytes for Go's `any`-typed slots (params envelopes,
// TypeResponse.Value).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ts.Api;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    // proto.go DTOs are plain Go structs → C# public fields (plan/05 §3); without this
    // the source generator ignores fields entirely and both faces emit/expect `{}`.
    IncludeFields = true)]
[JsonSerializable(typeof(InitializeResponse), TypeInfoPropertyName = "InitializeResponse")]
[JsonSerializable(typeof(DocumentIdentifier), TypeInfoPropertyName = "DocumentIdentifier")]
[JsonSerializable(typeof(ApiFileChangeSummary), TypeInfoPropertyName = "ApiFileChangeSummary")]
[JsonSerializable(typeof(ApiFileChanges), TypeInfoPropertyName = "ApiFileChanges")]
[JsonSerializable(typeof(UpdateSnapshotParams), TypeInfoPropertyName = "UpdateSnapshotParams")]
[JsonSerializable(typeof(ProjectFileChanges), TypeInfoPropertyName = "ProjectFileChanges")]
[JsonSerializable(typeof(SnapshotChanges), TypeInfoPropertyName = "SnapshotChanges")]
[JsonSerializable(typeof(UpdateSnapshotResponse), TypeInfoPropertyName = "UpdateSnapshotResponse")]
[JsonSerializable(typeof(ParseConfigFileParams), TypeInfoPropertyName = "ParseConfigFileParams")]
[JsonSerializable(typeof(ReleaseParams), TypeInfoPropertyName = "ReleaseParams")]
[JsonSerializable(typeof(ProfileParams), TypeInfoPropertyName = "ProfileParams")]
[JsonSerializable(typeof(ProfileResult), TypeInfoPropertyName = "ProfileResult")]
[JsonSerializable(typeof(ConfigFileResponse), TypeInfoPropertyName = "ConfigFileResponse")]
[JsonSerializable(typeof(GetDefaultProjectForFileParams), TypeInfoPropertyName = "GetDefaultProjectForFileParams")]
[JsonSerializable(typeof(ProjectResponse), TypeInfoPropertyName = "ProjectResponse")]
[JsonSerializable(typeof(ProjectResponse[]), TypeInfoPropertyName = "ProjectResponseArray")]
[JsonSerializable(typeof(GetSymbolAtPositionParams), TypeInfoPropertyName = "GetSymbolAtPositionParams")]
[JsonSerializable(typeof(GetSymbolsAtPositionsParams), TypeInfoPropertyName = "GetSymbolsAtPositionsParams")]
[JsonSerializable(typeof(GetSymbolAtLocationParams), TypeInfoPropertyName = "GetSymbolAtLocationParams")]
[JsonSerializable(typeof(GetSymbolsAtLocationsParams), TypeInfoPropertyName = "GetSymbolsAtLocationsParams")]
[JsonSerializable(typeof(SymbolResponse), TypeInfoPropertyName = "SymbolResponse")]
[JsonSerializable(typeof(SymbolResponse[]), TypeInfoPropertyName = "SymbolResponseArray")]
[JsonSerializable(typeof(GetTypeOfSymbolParams), TypeInfoPropertyName = "GetTypeOfSymbolParams")]
[JsonSerializable(typeof(GetTypesOfSymbolsParams), TypeInfoPropertyName = "GetTypesOfSymbolsParams")]
[JsonSerializable(typeof(TypeResponse), TypeInfoPropertyName = "TypeResponse")]
[JsonSerializable(typeof(TypeResponse[]), TypeInfoPropertyName = "TypeResponseArray")]
[JsonSerializable(typeof(SignatureResponse), TypeInfoPropertyName = "SignatureResponse")]
[JsonSerializable(typeof(SignatureResponse[]), TypeInfoPropertyName = "SignatureResponseArray")]
[JsonSerializable(typeof(GetSourceFileParams), TypeInfoPropertyName = "GetSourceFileParams")]
[JsonSerializable(typeof(GetSourceFileNamesParams), TypeInfoPropertyName = "GetSourceFileNamesParams")]
[JsonSerializable(typeof(SourceFileMetadata), TypeInfoPropertyName = "SourceFileMetadata")]
[JsonSerializable(typeof(global::Ts.Core.ModuleKind), TypeInfoPropertyName = "ModuleKind")]
[JsonSerializable(typeof(ResolveNameParams), TypeInfoPropertyName = "ResolveNameParams")]
[JsonSerializable(typeof(GetTypePropertyParams), TypeInfoPropertyName = "GetTypePropertyParams")]
[JsonSerializable(typeof(GetSymbolPropertyParams), TypeInfoPropertyName = "GetSymbolPropertyParams")]
[JsonSerializable(typeof(GetSignaturePropertyParams), TypeInfoPropertyName = "GetSignaturePropertyParams")]
[JsonSerializable(typeof(GetContextualTypeParams), TypeInfoPropertyName = "GetContextualTypeParams")]
[JsonSerializable(typeof(GetTypeOfSymbolAtLocationParams), TypeInfoPropertyName = "GetTypeOfSymbolAtLocationParams")]
[JsonSerializable(typeof(GetReferencesToSymbolInFileParams), TypeInfoPropertyName = "GetReferencesToSymbolInFileParams")]
[JsonSerializable(typeof(GetReferencedSymbolsForNodeParams), TypeInfoPropertyName = "GetReferencedSymbolsForNodeParams")]
[JsonSerializable(typeof(ReferencedSymbolEntry), TypeInfoPropertyName = "ReferencedSymbolEntry")]
[JsonSerializable(typeof(ReferencedSymbolEntry[]), TypeInfoPropertyName = "ReferencedSymbolEntryArray")]
[JsonSerializable(typeof(GetSignatureUsagesParams), TypeInfoPropertyName = "GetSignatureUsagesParams")]
[JsonSerializable(typeof(SignatureUsageResponse), TypeInfoPropertyName = "SignatureUsageResponse")]
[JsonSerializable(typeof(SignatureUsageResponse[]), TypeInfoPropertyName = "SignatureUsageResponseArray")]
[JsonSerializable(typeof(GetCompletionsAtPositionParams), TypeInfoPropertyName = "GetCompletionsAtPositionParams")]
[JsonSerializable(typeof(CompletionEntryLabelDetailsResponse), TypeInfoPropertyName = "CompletionEntryLabelDetailsResponse")]
[JsonSerializable(typeof(CompletionEntryResponse), TypeInfoPropertyName = "CompletionEntryResponse")]
[JsonSerializable(typeof(CompletionInfoResponse), TypeInfoPropertyName = "CompletionInfoResponse")]
[JsonSerializable(typeof(GetIntrinsicTypeParams), TypeInfoPropertyName = "GetIntrinsicTypeParams")]
[JsonSerializable(typeof(WellKnownSymbolsResponse), TypeInfoPropertyName = "WellKnownSymbolsResponse")]
[JsonSerializable(typeof(WellKnownSignaturesResponse), TypeInfoPropertyName = "WellKnownSignaturesResponse")]
[JsonSerializable(typeof(GetBaseTypeOfLiteralTypeParams), TypeInfoPropertyName = "GetBaseTypeOfLiteralTypeParams")]
[JsonSerializable(typeof(GetNonNullableTypeParams), TypeInfoPropertyName = "GetNonNullableTypeParams")]
[JsonSerializable(typeof(GetTypeFromTypeNodeParams), TypeInfoPropertyName = "GetTypeFromTypeNodeParams")]
[JsonSerializable(typeof(GetWidenedTypeParams), TypeInfoPropertyName = "GetWidenedTypeParams")]
[JsonSerializable(typeof(GetParameterTypeParams), TypeInfoPropertyName = "GetParameterTypeParams")]
[JsonSerializable(typeof(IsArrayLikeTypeParams), TypeInfoPropertyName = "IsArrayLikeTypeParams")]
[JsonSerializable(typeof(IsTypeAssignableToParams), TypeInfoPropertyName = "IsTypeAssignableToParams")]
[JsonSerializable(typeof(GetSignaturesOfTypeParams), TypeInfoPropertyName = "GetSignaturesOfTypeParams")]
[JsonSerializable(typeof(GetResolvedSignatureParams), TypeInfoPropertyName = "GetResolvedSignatureParams")]
[JsonSerializable(typeof(GetTypeAtLocationParams), TypeInfoPropertyName = "GetTypeAtLocationParams")]
[JsonSerializable(typeof(GetTypeAtLocationsParams), TypeInfoPropertyName = "GetTypeAtLocationsParams")]
[JsonSerializable(typeof(GetTypeAtPositionParams), TypeInfoPropertyName = "GetTypeAtPositionParams")]
[JsonSerializable(typeof(GetTypesAtPositionsParams), TypeInfoPropertyName = "GetTypesAtPositionsParams")]
[JsonSerializable(typeof(TypeToTypeNodeParams), TypeInfoPropertyName = "TypeToTypeNodeParams")]
[JsonSerializable(typeof(SignatureToSignatureDeclarationParams), TypeInfoPropertyName = "SignatureToSignatureDeclarationParams")]
[JsonSerializable(typeof(PrintNodeParams), TypeInfoPropertyName = "PrintNodeParams")]
[JsonSerializable(typeof(CheckerTypeParams), TypeInfoPropertyName = "CheckerTypeParams")]
[JsonSerializable(typeof(GetPropertyOfTypeParams), TypeInfoPropertyName = "GetPropertyOfTypeParams")]
[JsonSerializable(typeof(GetMemberInModuleExportsParams), TypeInfoPropertyName = "GetMemberInModuleExportsParams")]
[JsonSerializable(typeof(CheckerNodeParams), TypeInfoPropertyName = "CheckerNodeParams")]
[JsonSerializable(typeof(CheckerSymbolParams), TypeInfoPropertyName = "CheckerSymbolParams")]
[JsonSerializable(typeof(JSDocTagInfo), TypeInfoPropertyName = "JSDocTagInfo")]
[JsonSerializable(typeof(JSDocTagInfo[]), TypeInfoPropertyName = "JSDocTagInfoArray")]
[JsonSerializable(typeof(CheckerSignatureParams), TypeInfoPropertyName = "CheckerSignatureParams")]
[JsonSerializable(typeof(TypePredicateResponse), TypeInfoPropertyName = "TypePredicateResponse")]
[JsonSerializable(typeof(IndexInfoResponse), TypeInfoPropertyName = "IndexInfoResponse")]
[JsonSerializable(typeof(IndexInfoResponse[]), TypeInfoPropertyName = "IndexInfoResponseArray")]
[JsonSerializable(typeof(SourceFileResponse), TypeInfoPropertyName = "SourceFileResponse")]
[JsonSerializable(typeof(GetDiagnosticsParams), TypeInfoPropertyName = "GetDiagnosticsParams")]
[JsonSerializable(typeof(GetProjectDiagnosticsParams), TypeInfoPropertyName = "GetProjectDiagnosticsParams")]
[JsonSerializable(typeof(DiagnosticResponse), TypeInfoPropertyName = "DiagnosticResponse")]
[JsonSerializable(typeof(DiagnosticResponse[]), TypeInfoPropertyName = "DiagnosticResponseArray")]
[JsonSerializable(typeof(ServerTimingInfo), TypeInfoPropertyName = "ServerTimingInfo")]
// PORT-NOTE(api): Go's JsonSerializable list includes bool/string for the `any` slots;
// the C# polymorphic writer handles those with Utf8JsonWriter primitives
// (SerializePolymorphic), and the SDK's source generator emits net11-only
// JsonMetadataServices members for bare primitives — so no primitive registrations.
public sealed partial class ApiJsonContext : JsonSerializerContext
{
    // plan/07 §4.1 #4 — api writers use UnsafeRelaxedJsonEscaping (byte-fidelity with the
    // Go internal json writer, which does not HTML-escape).
    public static readonly JsonWriterOptions ApiWriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // SerializePolymorphic writes one `any`-typed value (Go json.Marshal(result)) using
    // the source-generated contract for its runtime type. Registered covers every type
    // the api handlers place in an any slot; anything else falls back to the primitive
    // writers the protocol layers already handle (RawBinary/byte[]/string/bool).
    public static void SerializePolymorphic(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            // Primitives ride Utf8JsonWriter directly (no source-generated contract —
            // see the registration note above).
            case bool b: writer.WriteBooleanValue(b); return;
            case string s: writer.WriteStringValue(s); return;
        }

        // AOT-safe exact-type dispatch over every registered contract
        // (JsonSerializerContext.GetTypeInfo is filled by the source generator).
        System.Text.Json.Serialization.Metadata.JsonTypeInfo? info = Default.GetTypeInfo(value.GetType());
        if (info is null)
        {
            throw new JsonException($"api: no source-generated contract for value of type {value.GetType()}");
        }
        JsonSerializer.Serialize(writer, value, info);
    }


    // SerializeToBytes marshals a params/result value to JSON bytes (Go json.Marshal).
    public static byte[] SerializeToBytes(object value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ApiWriterOptions))
        {
            SerializePolymorphic(writer, value);
        }
        return buffer.WrittenSpan.ToArray();
    }
}

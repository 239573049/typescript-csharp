// gen:lsp — LSP metamodel family codegen (plan/04-codegen.md §5).
//
// Port of tsgo internal/lsp/lsproto/_generate/generate.mts (3,437 lines): the
// language-agnostic model-patching half is ported line-by-line (plan/04 §5.2);
// the Go-emitting half is re-targeted to emit C# per plan/04 §5.3–§5.5.
//
// Inputs (all in-repo copies, plan/04 §5.1):
//   metaModel.json          — vscode-languageserver-node metaModel at the pinned version
//   metaModelSchema.ts      — copy of tsgo _generate/metaModelSchema.mts (omitzeroValue-patched)
//   lsp-model-version.json  — explicit protocol pin
//
// Outputs (src/Ts.LanguageService/Lsp/):
//   Protocol.g.cs           — the Go handwritten-base surface (Method, DocumentUri,
//                             URI, Null, NoParams, EmptyObject, Tuple2Uinteger,
//                             IHas* interfaces, RequestInfo/NotificationInfo),
//                             structures, enums, literal types, Method constants,
//                             the RequestInfo/NotificationInfo registry,
//                             RegisterOptions
//   Protocol.Unions.g.cs    — one class per OR type (naming algorithm ported 1:1)
//   Protocol.Json.g.cs      — one JsonConverter<T> per union + base-surface
//                             converters + the single LspJsonContext tail
//
// AOT policy (plan/07): STJ source generation only — no reflection serializers.
// Every union gets a concrete generated JsonConverter subclass; the shared
// low-level scan helpers it calls are hand-written in Lsp/UnionSerialization.cs.

import fs from "node:fs";
import path from "node:path";
import url from "node:url";

import type {
    Enumeration,
    MetaModel,
    Notification,
    OrType,
    Property,
    ReferenceType,
    Request,
    Structure,
    Type,
    TypeAlias,
} from "./metaModelSchema.ts";

const __filename = url.fileURLToPath(new URL(import.meta.url));
const __dirname = path.dirname(__filename);

const metaModelPath = path.resolve(__dirname, "metaModel.json");
const lspOutDir = path.resolve(__dirname, "../../src/Ts.LanguageService/Lsp");

if (!fs.existsSync(metaModelPath)) {
    console.error("metaModel.json not found; run `npm run gen:lsp:fetch` first.");
    process.exit(1);
}

const model: MetaModel = JSON.parse(fs.readFileSync(metaModelPath, "utf-8"));

// ---------------------------------------------------------------------------
// Model data — ported verbatim from generate.mts:36-864 (plan/04 §5.2).
// ---------------------------------------------------------------------------

// Custom structures to add to the model
const customStructures: Structure[] = [
    {
        name: "InitializationOptions",
        properties: [
            {
                name: "disablePushDiagnostics",
                type: { kind: "base", name: "boolean" },
                optional: true,
                documentation: "DisablePushDiagnostics disables automatic pushing of diagnostics to the client.",
            },
            {
                name: "codeLensShowLocationsCommandName",
                type: { kind: "base", name: "string" },
                optional: true,
                documentation: "The client-side command name that resolved references/implementations `CodeLens` should trigger. Arguments passed will be `(DocumentUri, Position, Location[])`.",
            },
            {
                name: "userPreferences",
                type: { kind: "reference", name: "any" },
                optional: true,
                documentation: "userPreferences and/or formatting options if provided at initialization.",
            },
            {
                name: "enableTelemetry",
                type: { kind: "base", name: "boolean" },
                optional: true,
                documentation: "EnableTelemetry enables sending telemetry events from the server to the client.",
            },
            {
                name: "logVerbosity",
                type: { kind: "reference", name: "LogVerbosity" },
                optional: true,
                documentation: "The initial log verbosity level, matching the client's output channel log level at startup. Subsequent changes are sent via custom/setLogVerbosity.",
            },
        ],
        documentation: "InitializationOptions contains user-provided initialization options.",
    },
    {
        name: "AutoImportFix",
        properties: [
            {
                name: "kind",
                type: { kind: "reference", name: "AutoImportFixKind" },
                omitzeroValue: true,
            },
            {
                name: "name",
                type: { kind: "base", name: "string" },
                omitzeroValue: true,
            },
            {
                name: "importKind",
                type: { kind: "reference", name: "ImportKind" },
            },
            {
                name: "useRequire",
                type: { kind: "base", name: "boolean" },
                omitzeroValue: true,
            },
            {
                name: "addAsTypeOnly",
                type: { kind: "reference", name: "AddAsTypeOnly" },
            },
            {
                name: "moduleSpecifier",
                type: { kind: "base", name: "string" },
                documentation: "The module specifier for this auto-import.",
                omitzeroValue: true,
            },
            {
                name: "importIndex",
                type: { kind: "base", name: "integer" },
                documentation: "Index of the import to modify when adding to an existing import declaration.",
            },
            {
                name: "usagePosition",
                type: { kind: "reference", name: "Position" },
                optional: true,
            },
            {
                name: "namespacePrefix",
                type: { kind: "base", name: "string" },
                omitzeroValue: true,
            },
        ],
        documentation: "AutoImportFix contains information about an auto-import suggestion.",
    },
    {
        name: "CompletionItemData",
        properties: [
            {
                name: "fileName",
                type: { kind: "base", name: "string" },
                documentation: "The file name where the completion was requested.",
                omitzeroValue: true,
            },
            {
                name: "position",
                type: { kind: "base", name: "integer" },
                documentation: "The position where the completion was requested.",
                omitzeroValue: true,
            },
            {
                name: "source",
                type: { kind: "base", name: "string" },
                documentation: "Special source value for disambiguation.",
                omitzeroValue: true,
            },
            {
                name: "name",
                type: { kind: "base", name: "string" },
                documentation: "The name of the completion item.",
                omitzeroValue: true,
            },
            {
                name: "autoImport",
                type: { kind: "reference", name: "AutoImportFix" },
                optional: true,
                documentation: "Auto-import data for this completion item.",
            },
        ],
        documentation: "CompletionItemData is preserved on a CompletionItem between CompletionRequest and CompletionResolveRequest.",
    },
    {
        name: "CodeLensData",
        properties: [
            {
                name: "kind",
                type: { kind: "reference", name: "CodeLensKind" },
                documentation: `The kind of the code lens ("references" or "implementations").`,
            },
            {
                name: "uri",
                type: { kind: "base", name: "DocumentUri" },
                documentation: `The document in which the code lens and its range are located.`,
            },
        ],
    },
    {
        name: "ExperimentalServerCapabilities",
        properties: [
            {
                name: "customSourceDefinitionProvider",
                type: { kind: "base", name: "boolean" },
                optional: true,
                documentation: "The server provides source definition support via custom/textDocument/sourceDefinition.",
            },
            {
                name: "customMultiDocumentHighlightProvider",
                type: { kind: "base", name: "boolean" },
                optional: true,
                documentation: "The server provides multi-document highlight support via custom/textDocument/multiDocumentHighlight.",
            },
        ],
        documentation: "ExperimentalServerCapabilities contains experimental capabilities under development.",
    },
    {
        name: "ExperimentalClientCapabilities",
        properties: [
            {
                name: "hoverVerbosityLevel",
                type: { kind: "base", name: "boolean" },
                optional: true,
                documentation: "The client supports hover verbosityLevel requests and canIncreaseVerbosity responses.",
            },
        ],
        documentation: "ExperimentalClientCapabilities contains experimental capabilities under development.",
    },
    {
        name: "VSOnAutoInsertOptions",
        properties: [
            {
                name: "_vs_triggerCharacters",
                type: { kind: "array", element: { kind: "base", name: "string" } },
                documentation: "List of trigger characters that trigger auto-insert.",
            },
        ],
        documentation: "Options for the textDocument/_vs_onAutoInsert provider capability.",
    },
    {
        name: "VSReferenceItem",
        properties: [
            {
                name: "_vs_id",
                type: { kind: "base", name: "integer" },
                documentation: "Unique identifier for this reference item.",
            },
            {
                name: "_vs_definitionId",
                type: { kind: "base", name: "integer" },
                optional: true,
                documentation: "The ID of the definition item this reference belongs to. Absent for definition items themselves.",
            },
            {
                name: "_vs_kind",
                type: { kind: "array", element: { kind: "reference", name: "VSReferenceKind" } },
                optional: true,
                documentation: "The kind(s) of this reference (read, write, etc.).",
            },
            {
                name: "_vs_location",
                type: { kind: "reference", name: "Location" },
                documentation: "The location of this reference.",
            },
            {
                name: "_vs_definitionText",
                type: { kind: "reference", name: "VSClassifiedTextElement" },
                optional: true,
                documentation: "Classified display text for the definition (used for grouping headers in the UI).",
            },
            {
                name: "_vs_projectName",
                type: { kind: "base", name: "string" },
                optional: true,
                documentation: "The project name for this reference.",
            },
            {
                name: "_vs_containingType",
                type: { kind: "base", name: "string" },
                optional: true,
                documentation: "The containing type for this reference.",
            },
        ],
        documentation: "A VS-specific reference item with grouping support for Find All References.",
    },
    {
        name: "VSOnAutoInsertParams",
        properties: [
            {
                name: "_vs_textDocument",
                type: { kind: "reference", name: "TextDocumentIdentifier" },
                documentation: "The text document.",
            },
            {
                name: "_vs_position",
                type: { kind: "reference", name: "Position" },
                documentation: "The position inside the text document.",
            },
            {
                name: "_vs_ch",
                type: { kind: "base", name: "string" },
                documentation: "The character that triggered the auto-insert.",
            },
        ],
        documentation: "Parameters for the textDocument/_vs_onAutoInsert request.",
    },
    {
        name: "VSOnAutoInsertResponseItem",
        properties: [
            {
                name: "_vs_textEditFormat",
                type: { kind: "reference", name: "InsertTextFormat" },
                documentation: "The format of the text edit (plaintext or snippet).",
            },
            {
                name: "_vs_textEdit",
                type: { kind: "reference", name: "TextEdit" },
                documentation: "The text edit to apply for the auto-insertion.",
            },
        ],
        documentation: "Response item for the textDocument/_vs_onAutoInsert request.",
    },
    {
        name: "RequestFailureTelemetryEvent",
        properties: [
            {
                name: "eventName",
                type: { kind: "stringLiteral", value: "languageServer.errorResponse" },
                documentation: "The name of the telemetry event.",
            },
            {
                name: "telemetryPurpose",
                type: { kind: "stringLiteral", value: "error" },
                documentation: "Indicates whether the reason for generating the event (e.g. general usage telemetry or errors).",
            },
            {
                name: "properties",
                type: { kind: "reference", name: "RequestFailureTelemetryProperties" },
                documentation: "The properties associated with the event.",
            },
        ],
        documentation: "A RequestFailureTelemetryEvent is sent when a request fails and the server recovers.",
    },
    {
        name: "RequestFailureTelemetryProperties",
        properties: [
            {
                name: "errorCode",
                type: { kind: "base", name: "string" },
                documentation: "The error code associated with the event.",
            },
            {
                name: "requestMethod",
                type: { kind: "base", name: "string" },
                documentation: "The method of the request that caused the event.",
            },
            {
                name: "stack",
                type: { kind: "base", name: "string" },
                documentation: "The stack trace associated with the event.",
            },
        ],
        documentation: "RequestFailureTelemetryProperties contains failure information when an LSP request manages to recover.",
    },
    {
        name: "ProfileParams",
        properties: [
            {
                name: "dir",
                type: { kind: "base", name: "string" },
                documentation: "The directory path where the profile should be saved.",
            },
        ],
        documentation: "Parameters for profiling requests.",
    },
    {
        name: "ProfileResult",
        properties: [
            {
                name: "file",
                type: { kind: "base", name: "string" },
                documentation: "The file path where the profile was saved.",
            },
        ],
        documentation: "Result of a profiling request.",
    },
    {
        name: "InitializeAPISessionParams",
        properties: [
            {
                name: "pipe",
                type: { kind: "base", name: "string" },
                optional: true,
                documentation: "Optional path to use for the named pipe or Unix domain socket. If not provided, a unique path will be generated.",
            },
        ],
        documentation: "Parameters for the initializeAPISession request.",
    },
    {
        name: "InitializeAPISessionResult",
        properties: [
            {
                name: "sessionId",
                type: { kind: "base", name: "string" },
                documentation: "The unique identifier for this API session.",
            },
            {
                name: "pipe",
                type: { kind: "base", name: "string" },
                documentation: "The path to the named pipe or Unix domain socket for API communication.",
            },
        ],
        documentation: "Result for the initializeAPISession request.",
    },
    {
        name: "ProjectInfoParams",
        properties: [
            {
                name: "textDocument",
                type: { kind: "reference", name: "TextDocumentIdentifier" },
                documentation: "The text document to get project info for.",
            },
        ],
        documentation: "Parameters for the custom/projectInfo request.",
    },
    {
        name: "ProjectInfoResult",
        properties: [
            {
                name: "configFilePath",
                type: { kind: "base", name: "string" },
                documentation: "The absolute path to the config file (e.g. /path/to/tsconfig.json) for the project that contains this file, or an empty string if the file is in an inferred project.",
            },
        ],
        documentation: "Result for the custom/projectInfo request.",
    },
    {
        name: "SetLogVerbosityParams",
        properties: [
            {
                name: "verbosity",
                type: { kind: "reference", name: "LogVerbosity" },
                documentation: "The log verbosity level.",
            },
        ],
        documentation: "Parameters for the custom/setLogVerbosity notification.",
    },
    {
        name: "PerformanceStatsTelemetryEvent",
        properties: [
            {
                name: "eventName",
                type: { kind: "stringLiteral", value: "languageServer.performanceStats" },
                documentation: "The name of the telemetry event.",
            },
            {
                name: "telemetryPurpose",
                type: { kind: "stringLiteral", value: "usage" },
                documentation: "Indicates this is a usage telemetry event.",
            },
            {
                name: "measurements",
                type: { kind: "reference", name: "PerformanceStatsTelemetryMeasurements" },
                documentation: "Numeric measurements for this telemetry event.",
            },
        ],
        documentation: "A PerformanceStatsTelemetryEvent is sent periodically with performance and resource usage statistics.",
    },
    {
        name: "PerformanceStatsTelemetryMeasurements",
        properties: [
            { name: "openFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of files currently open in the editor." },
            { name: "uptimeSeconds", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Seconds since the session was initialized." },
            { name: "projectCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of loaded projects." },
            { name: "configCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of loaded config files." },
            { name: "cachedDiskFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of files cached from disk." },
            { name: "memoryUsedBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total memory mapped by the Go runtime in bytes." },
            { name: "goMemLimit", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "GOMEMLIMIT value in bytes, or 0 if not set." },
            { name: "goGCPercent", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "GOGC percentage value configured for the GC." },
            { name: "heapGoalBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Heap size target the GC is working toward in bytes." },
            { name: "heapLiveBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Bytes of live (reachable) heap objects." },
            { name: "heapObjectCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of live or unswept objects occupying heap memory." },
            { name: "heapStackBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Heap memory reserved for goroutine stacks." },
            { name: "heapReleasedBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Heap memory returned to the OS." },
            { name: "heapFreeBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Heap memory that is free and eligible to be returned to the OS." },
            { name: "gcScanHeapBytes", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total scannable heap bytes — how much the GC must traverse." },
            { name: "goMaxProcs", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "The current GOMAXPROCS value." },
            { name: "goroutineCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Current number of goroutines." },
            { name: "gcCyclesTotal", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total completed GC cycles." },
            { name: "gcCPUSeconds", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Cumulative CPU time spent in GC in seconds." },
            { name: "userCPUSeconds", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Cumulative CPU time spent in user Go code in seconds." },
            { name: "systemMemTotal", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total physical memory on the system in bytes." },
            { name: "systemMemUsed", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Used physical memory on the system in bytes." },
            { name: "autoImportProjectBucketCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of auto-import project buckets." },
            { name: "autoImportNodeModulesBucketCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of auto-import node_modules buckets." },
            { name: "autoImportUniquePackageCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Unique packages across all node_modules buckets." },
            { name: "autoImportProjectExportCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total indexed exports from project files." },
            { name: "autoImportNodeModulesExportCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total indexed exports from node_modules." },
            { name: "autoImportProjectFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total files tracked across project buckets." },
            { name: "autoImportNodeModulesFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Total files tracked across node_modules buckets." },
            { name: "autoImportNodeModulesUnfilteredBucketCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true, documentation: "Number of node_modules buckets with no package.json filter." },
        ],
        documentation: "Numeric measurements for PerformanceStatsTelemetryEvent.",
    },
    {
        name: "ProjectInfoTelemetryEvent",
        properties: [
            {
                name: "eventName",
                type: { kind: "stringLiteral", value: "languageServer.projectInfo" },
                documentation: "The name of the telemetry event.",
            },
            {
                name: "telemetryPurpose",
                type: { kind: "stringLiteral", value: "usage" },
                documentation: "Indicates this is a usage telemetry event.",
            },
            {
                name: "properties",
                type: { kind: "map", key: { kind: "base", name: "string" }, value: { kind: "base", name: "string" } },
                documentation: "String properties for this telemetry event. Complex values (compilerOptions, fileStats) are JSON-stringified.",
            },
            {
                name: "measurements",
                type: { kind: "reference", name: "ProjectInfoTelemetryMeasurements" },
                documentation: "Numeric measurements for this telemetry event.",
            },
        ],
        documentation: "A ProjectInfoTelemetryEvent is sent once per project when it is first loaded.",
    },
    {
        name: "ProjectInfoTelemetryMeasurements",
        properties: [
            { name: "jsFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "jsFileSize", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "jsxFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "jsxFileSize", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "tsFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "tsFileSize", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "tsxFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "tsxFileSize", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "dtsFileCount", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
            { name: "dtsFileSize", type: { kind: "base", name: "decimal" }, omitzeroValue: true },
        ],
        documentation: "Numeric measurements for ProjectInfoTelemetryEvent.",
    },
    {
        name: "MultiDocumentHighlight",
        properties: [
            {
                name: "uri",
                type: { kind: "base", name: "DocumentUri" },
                documentation: "The URI of the document containing the highlights.",
            },
            {
                name: "highlights",
                type: { kind: "array", element: { kind: "reference", name: "DocumentHighlight" } },
                documentation: "The highlights for the document.",
            },
        ],
        documentation: "Represents a collection of document highlights from a single document, used in multi-document highlight responses.",
    },
    {
        name: "MultiDocumentHighlightParams",
        properties: [
            {
                name: "textDocument",
                type: { kind: "reference", name: "TextDocumentIdentifier" },
                documentation: "The text document.",
            },
            {
                name: "position",
                type: { kind: "reference", name: "Position" },
                documentation: "The position inside the text document.",
            },
            {
                name: "filesToSearch",
                type: { kind: "array", element: { kind: "base", name: "DocumentUri" } },
                documentation: "The list of file URIs to search for highlights across.",
            },
        ],
        documentation: "Parameters for the custom/textDocument/multiDocumentHighlight request.",
    },
    {
        name: "VSClassifiedTextRun",
        properties: [
            {
                name: "ClassificationTypeName",
                type: { kind: "base", name: "string" },
                documentation: "The classification type name (e.g. 'keyword', 'class name', 'parameter name').",
            },
            {
                name: "Text",
                type: { kind: "base", name: "string" },
                documentation: "The text content of this run.",
            },
            {
                name: "MarkerTagType",
                type: { kind: "base", name: "string" },
                optional: true,
                documentation: "Optional marker tag type.",
            },
            {
                name: "Style",
                type: { kind: "base", name: "integer" },
                optional: true,
                omitzeroValue: true,
                documentation: "The style of this text run.",
            },
            {
                name: "_vs_type",
                type: { kind: "stringLiteral", value: "ClassifiedTextRun" },
                documentation: "VS type discriminator required by ObjectContentConverter for deserialization.",
            },
        ],
        documentation: "A classified text run with text and classification type, used for colorized display in VS.",
    },
    {
        name: "VSClassifiedTextElement",
        properties: [
            {
                name: "Runs",
                type: { kind: "array", element: { kind: "reference", name: "VSClassifiedTextRun" } },
                documentation: "The classified text runs that make up this element.",
            },
            {
                name: "_vs_type",
                type: { kind: "stringLiteral", value: "ClassifiedTextElement" },
                documentation: "VS type discriminator required by ObjectContentConverter for deserialization.",
            },
        ],
        documentation: "A VS classified text element containing an array of classified text runs, used for colorized labels in VS.",
    },
];

const customEnumerations: Enumeration[] = [
    {
        name: "LogVerbosity",
        type: { kind: "base", name: "integer" },
        values: [
            { name: "Off", value: 0, documentation: "All logging disabled." },
            { name: "Trace", value: 1, documentation: "Most verbose; includes LSP request/response traces." },
            { name: "Debug", value: 2, documentation: "Verbose server logs." },
            { name: "Info", value: 3, documentation: "Normal server logs." },
            { name: "Warning", value: 4, documentation: "Warnings only." },
            { name: "Error", value: 5, documentation: "Errors only." },
        ],
        documentation: "Log verbosity level, mirroring the VS Code LogLevel enum values.",
    },
    {
        name: "VSReferenceKind",
        type: { kind: "base", name: "integer" },
        values: [
            { name: "Inactive", value: 0 },
            { name: "Comment", value: 1 },
            { name: "String", value: 2 },
            { name: "Read", value: 3 },
            { name: "Write", value: 4 },
            { name: "Reference", value: 5 },
            { name: "Name", value: 6 },
            { name: "Qualified", value: 7 },
            { name: "TypeArgument", value: 8 },
            { name: "TypeConstraint", value: 9 },
            { name: "BaseType", value: 10 },
            { name: "Constructor", value: 11 },
            { name: "Destructor", value: 12 },
            { name: "Import", value: 13 },
            { name: "Declaration", value: 14 },
            { name: "AddressOf", value: 15 },
            { name: "NotReference", value: 16 },
            { name: "Unknown", value: 17 },
        ],
    },
    {
        name: "CodeLensKind",
        type: {
            kind: "base",
            name: "string",
        },
        values: [
            {
                name: "References",
                value: "references",
            },
            {
                name: "Implementations",
                value: "implementations",
            },
        ],
    },
    {
        name: "AutoImportFixKind",
        type: { kind: "base", name: "integer" },
        values: [
            { name: "UseNamespace", value: 0, documentation: "Augment an existing namespace import." },
            { name: "JsdocTypeImport", value: 1, documentation: "Add a JSDoc-only type import." },
            { name: "AddToExisting", value: 2, documentation: "Insert into an existing import declaration." },
            { name: "AddNew", value: 3, documentation: "Create a fresh import statement." },
            { name: "PromoteTypeOnly", value: 4, documentation: "Promote a type-only import when necessary." },
        ],
    },
    {
        name: "ImportKind",
        type: { kind: "base", name: "integer" },
        values: [
            { name: "Named", value: 0, documentation: "Adds a named import." },
            { name: "Default", value: 1, documentation: "Adds a default import." },
            { name: "Namespace", value: 2, documentation: "Adds a namespace import." },
            { name: "CommonJS", value: 3, documentation: "Adds a CommonJS import assignment." },
        ],
    },
    {
        name: "AddAsTypeOnly",
        type: { kind: "base", name: "integer" },
        values: [
            { name: "Allowed", value: 1, documentation: "Import may be marked type-only if needed." },
            { name: "Required", value: 2, documentation: "Import must be marked type-only." },
            { name: "NotAllowed", value: 4, documentation: "Import cannot be marked type-only." },
        ],
    },
    {
        name: "ClassificationTypeName",
        type: { kind: "base", name: "string" },
        values: [
            { name: "Keyword", value: "keyword", documentation: "Language keyword (e.g., function, const, class)." },
            { name: "Punctuation", value: "punctuation", documentation: "Punctuation characters (e.g., parentheses, commas, semicolons)." },
            { name: "Operator", value: "operator", documentation: "Operators (e.g., =, +, ?)." },
            { name: "WhiteSpace", value: "whitespace", documentation: "Whitespace including spaces and line breaks." },
            { name: "Text", value: "text", documentation: "Plain text with no special classification." },
            { name: "String", value: "string", documentation: "String and literal values." },
            { name: "Number", value: "number", documentation: "Numeric literal values." },
            { name: "Comment", value: "comment", documentation: "Comment text." },
            { name: "ClassName", value: "class name", documentation: "Class names." },
            { name: "InterfaceName", value: "interface name", documentation: "Interface names." },
            { name: "EnumName", value: "enum name", documentation: "Enum names." },
            { name: "ModuleName", value: "module name", documentation: "Module/namespace names." },
            { name: "MethodName", value: "method name", documentation: "Method and function names." },
            { name: "ParameterName", value: "parameter name", documentation: "Parameter names." },
            { name: "PropertyName", value: "property name", documentation: "Property and accessor names." },
            { name: "FieldName", value: "field name", documentation: "Field names (e.g., enum members)." },
            { name: "LocalName", value: "local name", documentation: "Local variable names." },
            { name: "TypeParameterName", value: "type parameter name", documentation: "Type parameter names." },
            { name: "Identifier", value: "identifier", documentation: "General identifiers (e.g., type aliases, imports)." },
        ],
        documentation: "Roslyn classification type names used by VS for syntax coloring in tooltips and other UI elements.",
    },
];
const customRequests: Request[] = [
    {
        method: "custom/runGC",
        typeName: "RunGCRequest",
        messageDirection: "clientToServer",
        result: { kind: "base", name: "null" },
        documentation: "Triggers garbage collection in the language server.",
    },
    {
        method: "custom/saveHeapProfile",
        typeName: "SaveHeapProfileRequest",
        params: { kind: "reference", name: "ProfileParams" },
        messageDirection: "clientToServer",
        result: { kind: "reference", name: "ProfileResult" },
        documentation: "Saves a heap profile to the specified directory.",
    },
    {
        method: "custom/saveAllocProfile",
        typeName: "SaveAllocProfileRequest",
        params: { kind: "reference", name: "ProfileParams" },
        messageDirection: "clientToServer",
        result: { kind: "reference", name: "ProfileResult" },
        documentation: "Saves an allocation profile to the specified directory.",
    },
    {
        method: "custom/startCPUProfile",
        typeName: "StartCPUProfileRequest",
        params: { kind: "reference", name: "ProfileParams" },
        messageDirection: "clientToServer",
        result: { kind: "base", name: "null" },
        documentation: "Starts CPU profiling, writing to the specified directory when stopped.",
    },
    {
        method: "custom/stopCPUProfile",
        typeName: "StopCPUProfileRequest",
        messageDirection: "clientToServer",
        result: { kind: "reference", name: "ProfileResult" },
        documentation: "Stops CPU profiling and saves the profile.",
    },
    {
        method: "custom/initializeAPISession",
        typeName: "CustomInitializeAPISessionRequest",
        params: { kind: "reference", name: "InitializeAPISessionParams" },
        result: { kind: "reference", name: "InitializeAPISessionResult" },
        messageDirection: "clientToServer",
        documentation: "Custom request to initialize an API session.",
    },
    {
        method: "custom/projectInfo",
        typeName: "CustomProjectInfoRequest",
        params: { kind: "reference", name: "ProjectInfoParams" },
        result: { kind: "reference", name: "ProjectInfoResult" },
        messageDirection: "clientToServer",
        documentation: "Returns project information (e.g. the tsconfig.json path) for a given text document.",
    },
    {
        method: "custom/textDocument/sourceDefinition",
        typeName: "CustomTextDocumentSourceDefinitionRequest",
        params: { kind: "reference", name: "TextDocumentPositionParams" },
        result: { kind: "reference", name: "LocationOrLocationsOrDefinitionLinksOrNull" },
        messageDirection: "clientToServer",
        documentation: "Request to get source definitions for a position.",
    },
    {
        method: "custom/textDocument/multiDocumentHighlight",
        typeName: "CustomMultiDocumentHighlightRequest",
        params: { kind: "reference", name: "MultiDocumentHighlightParams" },
        result: {
            kind: "or",
            items: [
                { kind: "array", element: { kind: "reference", name: "MultiDocumentHighlight" } },
                { kind: "base", name: "null" },
            ],
        },
        messageDirection: "clientToServer",
        documentation: "Request to get document highlights across multiple files.",
    },
    {
        method: "textDocument/_vs_onAutoInsert",
        typeName: "VSOnAutoInsertRequest",
        params: { kind: "reference", name: "VSOnAutoInsertParams" },
        result: {
            kind: "or",
            items: [
                { kind: "reference", name: "VSOnAutoInsertResponseItem" },
                { kind: "base", name: "null" },
            ],
        },
        messageDirection: "clientToServer",
        documentation: "Request for auto-insert when a trigger character is typed (VS-specific).",
    },
    {
        method: "textDocument/_vs_references",
        typeName: "VSReferencesRequest",
        params: { kind: "reference", name: "ReferenceParams" },
        result: {
            kind: "or",
            items: [
                { kind: "array", element: { kind: "reference", name: "VSReferenceItem" } },
                { kind: "base", name: "null" },
            ],
        },
        messageDirection: "clientToServer",
        documentation: "VS-specific request for Find All References with grouped reference items.",
    },
];

const customNotifications: Notification[] = [
    {
        method: "custom/setLogVerbosity",
        typeName: "CustomSetLogVerbosityNotification",
        params: { kind: "reference", name: "SetLogVerbosityParams" },
        messageDirection: "clientToServer",
        documentation: "Notification to set the server's log verbosity level based on the output channel's log level.",
    },
];

// compareStructures is the set of generated structures for which a Compare method should be emitted.
// The Compare method defines a total ordering by comparing fields in declaration order.
// All listed structures (and any structure-typed fields they reference) must contain only
// comparable fields: base scalar types, or other structures that are themselves in this set.
const compareStructures = new Set<string>([
    "Position",
    "Range",
    "TextEdit",
]);

const customTypeAliases: TypeAlias[] = [
    {
        name: "TelemetryEvent",
        type: {
            kind: "or",
            items: [
                { kind: "reference", name: "RequestFailureTelemetryEvent" },
                { kind: "reference", name: "PerformanceStatsTelemetryEvent" },
                { kind: "reference", name: "ProjectInfoTelemetryEvent" },
                { kind: "base", name: "null" },
            ],
        },
    },
];

// ---------------------------------------------------------------------------
// Temporary skip list — types currently hand-hosted in Ts.Ls subsets
// (CodeActions.cs, InlayHints.Support.cs, SourceDefinition.Support.cs,
// SemanticTokens.Support.cs, CallHierarchy.Support.cs) which this session is
// not allowed to edit, plus their transitive reference closure (computed in
// computeSkipSet). Everything else the generated surface references — including
// the Go handwritten-base plumbing (Method, RequestInfo/NotificationInfo, IHas*,
// NoParams, Null, EmptyObject, Tuple2Uinteger, URI, DocumentUri) — is emitted in
// Protocol.g.cs. Types still hosted in Lsp/Protocol.cs (InitializeParams,
// InitializeResult, ClientCapabilities, ServerCapabilities, CompletionItem,
// CompletionList) land here through the cascade; their [JsonSerializable]
// registrations are added to LspJsonContext in emitJson.
// ---------------------------------------------------------------------------
// The Ts.Ls subsets have been absorbed into the generated surface (lsp server wave);
// the skip machinery is kept so a future temporary host can reuse it.
const temporarilyHostedInLs = new Set<string>([]);

// Go's Resolved*ClientCapabilities + resolve() generation (generate.mts:3337-3376).
const emitResolvedCapabilities = true;

// Go valueTypes (generate.mts:1686-1696) become C# readonly record structs per
// plan/04 §5.3 — but Position/Range/Location/... are consumed as classes by the
// not-yet-absorbed Ts.Ls subsets, so the struct switch ships behind this flag.
const emitValueTypesAsStructs = false;

// Arrays of DocumentSymbol must stay List<DocumentSymbol>: the Go surface keeps
// []*DocumentSymbol slices that Ts.Ls mutates in place (document-symbol outline).
function isArrayElementTypeMutableList(name: string): boolean {
    return name === "DocumentSymbol";
}

// ---------------------------------------------------------------------------
// Model preprocessing — port of patchAndPreprocessModel (generate.mts:880-1304).
// ---------------------------------------------------------------------------

// Track which custom Data structures were declared explicitly
const explicitDataStructures = new Set(customStructures.map(s => s.name));

// Map from registration method → { fieldName, optionsTypeName }
// Built during patchAndPreprocessModel, used during code generation.
interface RegistrationMethodInfo {
    registrationMethod: string;
    fieldName: string;
    optionsTypeName: string;
    isRegistrationOnly?: boolean;
}
let registrationMethods: RegistrationMethodInfo[] = [];

// Patch and preprocess the model
function patchAndPreprocessModel() {
    // Track which Data types we need to create as placeholders
    const neededDataStructures = new Set<string>();

    // Collect all registration option types from requests and notifications
    const registrationOptionTypes: Type[] = [];
    for (const request of [...model.requests, ...model.notifications]) {
        if (request.registrationOptions) {
            registrationOptionTypes.push(request.registrationOptions);
        }
    }

    // Create synthetic structures for "and" types in registration options
    const syntheticStructures: Structure[] = [];
    for (let i = 0; i < registrationOptionTypes.length; i++) {
        const regOptType = registrationOptionTypes[i];
        if (regOptType.kind === "and") {
            // Find which request/notification this registration option belongs to
            const owner = [...model.requests, ...model.notifications].find(r => r.registrationOptions === regOptType);
            if (!owner) {
                throw new Error("Could not find owner for 'and' type registration option");
            }

            // Determine the proper name based on the typeName or method
            let structureName: string;
            if (owner.typeName) {
                // Use typeName as base: "ColorPresentationRequest" -> "ColorPresentationRegistrationOptions"
                structureName = owner.typeName.replace(/Request$/, "").replace(/Notification$/, "") + "RegistrationOptions";
            }
            else {
                // Fall back to method: "textDocument/colorPresentation" -> "ColorPresentationRegistrationOptions"
                const methodParts = owner.method.split("/");
                const lastPart = methodParts[methodParts.length - 1];
                structureName = titleCase(lastPart) + "RegistrationOptions";
            }

            // Extract all reference types from the "and"
            const refTypes = regOptType.items.filter((item): item is ReferenceType => item.kind === "reference");

            // Create a synthetic structure that combines all the referenced structures
            syntheticStructures.push({
                name: structureName,
                properties: [],
                extends: refTypes,
                documentation: `Registration options for ${owner.method}.`,
            });

            // Replace the "and" type with a reference to the synthetic structure
            registrationOptionTypes[i] = { kind: "reference", name: structureName };
            // Also update the model so the request/notification has the resolved type
            owner.registrationOptions = registrationOptionTypes[i];
        }
    }

    for (const structure of model.structures) {
        // Patch ServerCapabilities to add custom tsgo capability flags
        if (structure.name === "ServerCapabilities") {
            structure.properties.push({
                name: "_vs_onAutoInsertProvider",
                type: { kind: "reference", name: "VSOnAutoInsertOptions" },
                optional: true,
                documentation: "Provider options for the VS auto-insert feature via textDocument/_vs_onAutoInsert.",
            });
            structure.properties.push({
                name: "_vs_referencesProvider",
                type: { kind: "base", name: "boolean" },
                optional: true,
                documentation: "The server provides VS-specific grouped references via textDocument/_vs_references.",
            });
        }

        // Patch HoverParams to add verbosityLevel
        if (structure.name === "HoverParams") {
            structure.properties.push({
                name: "verbosityLevel",
                type: { kind: "base", name: "integer" },
                optional: true,
                documentation: "Controls how many levels of type definitions will be expanded. Default is 0.",
            });
        }

        // Patch Hover to add canIncreaseVerbosity
        if (structure.name === "Hover") {
            structure.properties.push(
                {
                    name: "canIncreaseVerbosity",
                    type: { kind: "base", name: "boolean" },
                    omitzeroValue: true,
                    documentation: "Whether the verbosity level can be increased for this hover.",
                },
            );
        }

        // Patch ClientCapabilities to add VS-specific client capabilities
        if (structure.name === "ClientCapabilities") {
            structure.properties.push(
                {
                    name: "_vs_supportsVisualStudioExtensions",
                    type: { kind: "base", name: "boolean" },
                    optional: true,
                    documentation: "Whether the client supports Visual Studio extensions.",
                },
                {
                    name: "_vs_supportedSnippetVersion",
                    type: { kind: "base", name: "integer" },
                    optional: true,
                    documentation: "The snippet version supported by the client.",
                },
                {
                    name: "_vs_supportsNotIncludingTextInTextDocumentDidOpen",
                    type: { kind: "base", name: "boolean" },
                    optional: true,
                    documentation: "Whether the client supports not including text in textDocument/didOpen notifications.",
                },
                {
                    name: "_vs_supportsIconExtensions",
                    type: { kind: "base", name: "boolean" },
                    optional: true,
                    documentation: "Whether the client supports icon extensions.",
                },
                {
                    name: "_vs_supportsDiagnosticRequests",
                    type: { kind: "base", name: "boolean" },
                    optional: true,
                    documentation: "Whether the client supports diagnostic requests.",
                },
            );
        }

        // Patch SignatureInformation to add VS-specific colorized label
        if (structure.name === "SignatureInformation") {
            structure.properties.push({
                name: "_vs_colorizedLabel",
                type: { kind: "reference", name: "VSClassifiedTextElement" },
                optional: true,
                documentation: "A colorized label for the signature, providing classified text runs for VS syntax coloring.",
            });
        }

        for (const prop of structure.properties) {
            // Replace initializationOptions type with custom InitializationOptions.
            // The spec types this field as LSPAny?, which includes null, so keep
            // it nullable so a null value sent by loose clients is accepted.
            if (prop.name === "initializationOptions" && prop.type.kind === "reference" && prop.type.name === "LSPAny") {
                prop.type = {
                    kind: "or",
                    items: [
                        { kind: "reference", name: "InitializationOptions" },
                        { kind: "base", name: "null" },
                    ],
                };
            }

            // Replace Data *any fields with custom typed Data fields
            if (prop.name === "data" && prop.type.kind === "reference" && prop.type.name === "LSPAny") {
                const customDataType = `${structure.name}Data`;
                prop.type = { kind: "reference", name: customDataType };

                // If we haven't explicitly declared this Data structure, we'll need a placeholder
                if (!explicitDataStructures.has(customDataType)) {
                    neededDataStructures.add(customDataType);
                }
            }

            // Registration.registerOptions and Registration.method are handled specially:
            // registerOptions becomes a custom struct, and method is derived from it.
            // Remove both from the structure so the normal generator skips them.
            if (structure.name === "Registration" && (prop.name === "registerOptions" || prop.name === "method")) {
                // Will be filtered out below
            }

            // Replace ProgressParams.value with a proper union type
            if (structure.name === "ProgressParams" && prop.name === "value" && prop.type.kind === "reference" && prop.type.name === "LSPAny") {
                prop.type = {
                    kind: "or",
                    items: [
                        { kind: "reference", name: "WorkDoneProgressBegin" },
                        { kind: "reference", name: "WorkDoneProgressReport" },
                        { kind: "reference", name: "WorkDoneProgressEnd" },
                    ],
                };
            }
        }
    }

    for (const notification of model.notifications) {
        if (notification.typeName === "TelemetryEventNotification") {
            notification.params = {
                kind: "reference",
                name: "TelemetryEvent",
            };
        }
    }

    // Create placeholder structures for Data types that weren't explicitly declared
    for (const dataTypeName of neededDataStructures) {
        const baseName = dataTypeName.replace(/Data$/, "");
        customStructures.push({
            name: dataTypeName,
            properties: [],
            documentation: `${dataTypeName} is a placeholder for custom data preserved on a ${baseName}.`,
        });
    }

    // Add custom enumerations, custom structures, custom requests, and synthetic structures to the model
    model.enumerations.push(...customEnumerations);
    model.structures.push(...customStructures, ...syntheticStructures);
    model.requests.push(...customRequests);
    model.notifications.push(...customNotifications);

    // Build structure map for preprocessing
    const structureMap = new Map<string, Structure>();
    for (const structure of model.structures) {
        structureMap.set(structure.name, structure);
    }

    function collectInheritedProperties(structure: Structure, visited = new Set<string>()): Property[] {
        if (visited.has(structure.name)) {
            return []; // Avoid circular dependencies
        }
        visited.add(structure.name);

        const properties: Property[] = [];
        const inheritanceTypes = [...(structure.extends || []), ...(structure.mixins || [])];

        for (const type of inheritanceTypes) {
            if (type.kind === "reference") {
                const inheritedStructure = structureMap.get(type.name);
                if (inheritedStructure) {
                    properties.push(
                        ...collectInheritedProperties(inheritedStructure, new Set(visited)),
                        ...inheritedStructure.properties,
                    );
                }
            }
        }

        return properties;
    }

    // Inline inheritance for each structure
    for (const structure of model.structures) {
        const inheritedProperties = collectInheritedProperties(structure);

        // Merge properties with structure's own properties taking precedence
        const propertyMap = new Map<string, Property>();

        inheritedProperties.forEach(prop => propertyMap.set(prop.name, prop));
        structure.properties.forEach(prop => propertyMap.set(prop.name, prop));

        structure.properties = Array.from(propertyMap.values());
        structure.extends = undefined;
        structure.mixins = undefined;

        // Replace experimental LSPAny with typed ExperimentalClientCapabilities in ClientCapabilities
        if (structure.name === "ClientCapabilities") {
            const expProp = structure.properties.find(p => p.name === "experimental");
            if (expProp) {
                expProp.type = { kind: "reference", name: "ExperimentalClientCapabilities" };
                expProp.optional = true;
            }
        }

        // Replace experimental LSPAny with typed ExperimentalServerCapabilities in ServerCapabilities
        if (structure.name === "ServerCapabilities") {
            const expProp = structure.properties.find(p => p.name === "experimental");
            if (expProp) {
                expProp.type = { kind: "reference", name: "ExperimentalServerCapabilities" };
                expProp.optional = true;
            }
        }

        // Remove method and registerOptions from Registration (handled by custom codegen)
        if (structure.name === "Registration") {
            structure.properties = structure.properties.filter(p => p.name !== "method" && p.name !== "registerOptions");
        }
    }

    // Remove _InitializeParams structure after flattening (it was only needed for inheritance)
    model.structures = model.structures.filter(s => s.name !== "_InitializeParams");

    // Remove all notebook-related features from the model
    function isNotebookRelatedName(name: string): boolean {
        const lower = name.toLowerCase();
        return lower.includes("notebook");
    }

    function isNotebookRelatedMethod(method: string): boolean {
        return method.toLowerCase().startsWith("notebookdocument/");
    }

    function typeReferencesNotebook(type: Type): boolean {
        if (type.kind === "reference") return isNotebookRelatedName(type.name);
        if (type.kind === "array") return typeReferencesNotebook(type.element);
        if (type.kind === "or" || type.kind === "and") return type.items.some(typeReferencesNotebook);
        if (type.kind === "map") return typeReferencesNotebook(type.key) || typeReferencesNotebook(type.value);
        return false;
    }

    function isEntirelyNotebookType(type: Type): boolean {
        if (type.kind === "reference") return isNotebookRelatedName(type.name);
        if (type.kind === "array") return isEntirelyNotebookType(type.element);
        if (type.kind === "or" || type.kind === "and") return type.items.every(isEntirelyNotebookType);
        return false;
    }

    function removeNotebookFromType(type: Type): Type {
        if (type.kind === "or") {
            const filtered = type.items.filter(item => !typeReferencesNotebook(item)).map(removeNotebookFromType);
            if (filtered.length === 1) return filtered[0];
            if (filtered.length < type.items.length) {
                return { ...type, items: filtered };
            }
        }
        if (type.kind === "and") {
            const filtered = type.items.filter(item => !typeReferencesNotebook(item)).map(removeNotebookFromType);
            if (filtered.length === 1) return filtered[0];
            if (filtered.length < type.items.length) {
                return { ...type, items: filtered };
            }
        }
        return type;
    }

    // Filter out notebook structures (and notebook-only structures like ExecutionSummary)
    const notebookOnlyStructures = new Set(["ExecutionSummary"]);
    model.structures = model.structures.filter(s => !isNotebookRelatedName(s.name) && !notebookOnlyStructures.has(s.name));

    // Remove notebook properties from remaining structures
    for (const structure of model.structures) {
        structure.properties = structure.properties.filter(p => {
            if (isNotebookRelatedName(p.name)) return false;
            // Only remove properties whose type is entirely notebook-related
            if (isEntirelyNotebookType(p.type)) return false;
            return true;
        });
        // Clean up union types in remaining properties to remove notebook members
        for (const prop of structure.properties) {
            prop.type = removeNotebookFromType(prop.type);
        }
    }

    // Filter out notebook notifications and requests
    model.notifications = model.notifications.filter(n => !isNotebookRelatedMethod(n.method));
    model.requests = model.requests.filter(r => !isNotebookRelatedMethod(r.method));

    // Filter out notebook enumerations
    model.enumerations = model.enumerations.filter(e => !isNotebookRelatedName(e.name));

    // Remove notebook-related values from remaining enumerations
    for (const enumeration of model.enumerations) {
        enumeration.values = enumeration.values.filter(v => !isNotebookRelatedName(v.name));
    }

    // Filter out notebook type aliases
    model.typeAliases = model.typeAliases.filter(ta => !isNotebookRelatedName(ta.name));

    // Clean up type aliases that reference notebook types (e.g., DocumentFilter)
    for (const ta of model.typeAliases) {
        if (ta.type.kind === "or") {
            ta.type.items = ta.type.items.filter(item => !typeReferencesNotebook(item));
            // If only one item remains, unwrap the union
            if (ta.type.items.length === 1) {
                ta.type = ta.type.items[0];
            }
        }
    }

    // Build the registration method map (after notebook filtering).
    // Each unique registration method gets a field in the generated RegisterOptions struct.
    const regMethodSeen = new Set<string>();
    for (const request of [...model.requests, ...model.notifications]) {
        if (!request.registrationOptions) continue;
        const regMethod = (request as any).registrationMethod || request.method;

        if (regMethodSeen.has(regMethod)) continue;
        regMethodSeen.add(regMethod);

        // Resolve the options type name
        const ro = request.registrationOptions;
        let optionsTypeName: string;
        if (ro.kind === "reference") {
            optionsTypeName = ro.name;
        }
        else {
            throw new Error(`Unexpected registrationOptions kind '${ro.kind}' for ${request.method}; expected all to be resolved to references`);
        }

        registrationMethods.push({
            registrationMethod: regMethod,
            fieldName: methodNameIdentifier(regMethod),
            optionsTypeName,
        });
    }

    // Identify registration-only methods (not also a request/notification method).
    // These need their own Method constant emitted.
    const allRequestMethods = new Set([...model.requests, ...model.notifications].map(r => r.method));
    for (const reg of registrationMethods) {
        (reg as any).isRegistrationOnly = !allRequestMethods.has(reg.registrationMethod);
    }

    // Merge LSPErrorCodes into ErrorCodes and remove LSPErrorCodes
    const errorCodesEnum = model.enumerations.find(e => e.name === "ErrorCodes");
    const lspErrorCodesEnum = model.enumerations.find(e => e.name === "LSPErrorCodes");
    if (errorCodesEnum && lspErrorCodesEnum) {
        // Merge LSPErrorCodes values into ErrorCodes
        errorCodesEnum.values.push(...lspErrorCodesEnum.values);
        // Remove LSPErrorCodes from the model
        model.enumerations = model.enumerations.filter(e => e.name !== "LSPErrorCodes");
    }

    // Singularize plural enum names (e.g., "ErrorCodes" -> "ErrorCode")
    for (const enumeration of model.enumerations) {
        if (enumeration.name.endsWith("Codes")) {
            enumeration.name = enumeration.name.slice(0, -1); // "Codes" -> "Code"
        }
        else if (enumeration.name.endsWith("Modifiers")) {
            enumeration.name = enumeration.name.slice(0, -1); // "Modifiers" -> "Modifier"
        }
        else if (enumeration.name.endsWith("Types")) {
            enumeration.name = enumeration.name.slice(0, -1); // "Types" -> "Type"
        }
    }
}

patchAndPreprocessModel();

// Validate that telemetry events in the TelemetryEvent union have properly shaped
// measurements and properties fields. measurements struct fields must only contain
// numeric types (decimal/integer/uinteger).
function validateTelemetryEvents() {
    const telemetryAlias = customTypeAliases.find(a => a.name === "TelemetryEvent");
    if (!telemetryAlias || telemetryAlias.type.kind !== "or") return;

    const structureMap = new Map(model.structures.map(s => [s.name, s]));

    for (const item of telemetryAlias.type.items) {
        if (item.kind !== "reference") continue;
        const eventStruct = structureMap.get(item.name);
        if (!eventStruct) continue;

        for (const prop of eventStruct.properties) {
            if (prop.name === "measurements" && prop.type.kind === "reference") {
                const measurementsStruct = structureMap.get(prop.type.name);
                if (!measurementsStruct) continue;
                for (const mp of measurementsStruct.properties) {
                    if (mp.type.kind !== "base" || !["decimal", "integer", "uinteger"].includes(mp.type.name)) {
                        throw new Error(
                            `Telemetry measurements struct ${prop.type.name}.${mp.name} must be a numeric type ` +
                                `(decimal/integer/uinteger), got ${mp.type.kind === "base" ? mp.type.name : mp.type.kind}`,
                        );
                    }
                }
            }
        }
    }
}

validateTelemetryEvents();

// ---------------------------------------------------------------------------
// Type resolution — port of resolveType (generate.mts:1341-1484), remapped to C#.
// ---------------------------------------------------------------------------

interface CsType {
    name: string;
    // needsPointer mirrors Go: true when the Go surface uses *T. In C# this means
    // the type is a value type (record struct / enum / tuple) and optional fields
    // need a `?` suffix; reference-type fields are nullable by declaration.
    needsPointer: boolean;
    // Set for generated union types so the converter emitter can find them.
    unionName?: string;
}

interface TypeInfo {
    types: Map<string, CsType>;
    literalTypes: Map<string, string>;
    unionTypes: Map<string, { name: string; type: Type; containedNull: boolean; }[]>;
    typeAliasMap: Map<string, Type>;
}

const typeInfo: TypeInfo = {
    types: new Map(),
    literalTypes: new Map(),
    unionTypes: new Map(),
    typeAliasMap: new Map(),
};

function titleCase(s: string) {
    return s.charAt(0).toUpperCase() + s.slice(1);
}

function goFieldName(prop: Property): string {
    if (prop.name.startsWith("_vs_")) {
        return "VS" + titleCase(prop.name.slice(4));
    }
    return titleCase(prop.name);
}

// C# forbids a member named like its enclosing type (CS0542): Go's Command.Command
// becomes Command.CommandName (the spelling the Ts.Ls consumers already use).
function csMemberName(structure: Structure, prop: Property): string {
    const name = goFieldName(prop);
    return name === structure.name ? name + "Name" : name;
}

function resolveType(type: Type): CsType {
    switch (type.kind) {
        case "base":
            switch (type.name) {
                case "integer":
                    return { name: "int", needsPointer: false };
                case "uinteger":
                    return { name: "uint", needsPointer: false };
                case "string":
                    return { name: "string", needsPointer: false };
                case "boolean":
                    return { name: "bool", needsPointer: false };
                case "URI":
                    return { name: "URI", needsPointer: true };
                case "DocumentUri":
                    return { name: "DocumentUri", needsPointer: true };
                case "decimal":
                    return { name: "double", needsPointer: false };
                case "null":
                    return { name: "object", needsPointer: false };
                default:
                    throw new Error(`Unsupported base type: ${type.name}`);
            }

        case "reference": {
            const typeAliasOverride = typeAliasOverrides.get(type.name);
            if (typeAliasOverride) {
                return typeAliasOverride;
            }

            const nonResolved = nonResolvedAliases.has(type.name);
            if (nonResolved) {
                // Custom type aliases (Go `type TelemetryEvent = <union>`) have no C#
                // equivalent; references resolve transparently to the union type.
                const aliased = typeInfo.typeAliasMap.get(type.name);
                if (aliased) {
                    return resolveType(aliased);
                }
                return { name: type.name, needsPointer: false };
            }

            // Check if this is a type alias that resolves to a union type
            const aliasedType = typeInfo.typeAliasMap.get(type.name);
            if (aliasedType) {
                return resolveType(aliasedType);
            }

            let refType = typeInfo.types.get(type.name);
            if (!refType) {
                refType = { name: type.name, needsPointer: !isCsReferenceType(type.name) };
                typeInfo.types.set(type.name, refType);
            }
            return refType;
        }

        case "array": {
            const elementType = resolveType(type.element);
            const elementName = elementType.name;
            if (isArrayElementTypeMutableList(structureNameOf(type.element))) {
                return { name: `List<${elementName}>`, needsPointer: false };
            }
            return {
                name: `${elementName}[]`,
                needsPointer: false,
            };
        }

        case "map": {
            const keyType = resolveType(type.key);
            const valueType = resolveType(type.value);
            return {
                name: `Dictionary<${keyType.name}, ${valueType.name}>`,
                needsPointer: false,
            };
        }

        case "tuple": {
            if (
                type.items.length === 2 &&
                type.items[0].kind === "base" && type.items[0].name === "uinteger" &&
                type.items[1].kind === "base" && type.items[1].name === "uinteger"
            ) {
                return { name: "Tuple2Uinteger", needsPointer: true };
            }

            throw new Error("Unsupported tuple type: " + JSON.stringify(type));
        }

        case "stringLiteral": {
            const typeName = `StringLiteral${type.value.split(".").map(titleCase).join("")}`;
            typeInfo.literalTypes.set(String(type.value), typeName);
            return { name: typeName, needsPointer: true };
        }

        case "integerLiteral": {
            const typeName = `IntegerLiteral${type.value}`;
            typeInfo.literalTypes.set(String(type.value), typeName);
            return { name: typeName, needsPointer: true };
        }

        case "booleanLiteral": {
            const typeName = `BooleanLiteral${type.value ? "True" : "False"}`;
            typeInfo.literalTypes.set(String(type.value), typeName);
            return { name: typeName, needsPointer: true };
        }
        case "literal":
            if (type.value.properties.length === 0) {
                return { name: "EmptyObject", needsPointer: true };
            }

            throw new Error("Unexpected non-empty literal object: " + JSON.stringify(type.value));

        case "or": {
            return handleOrType(type);
        }

        default:
            throw new Error(`Unsupported type kind: ${type.kind}`);
    }
}

function structureNameOf(type: Type): string {
    return type.kind === "reference" ? type.name : "";
}

// C# value types (need `?` when optional): record structs + enums.
// Deviation note (plan/04 §5.3): Go's valueTypes set would make Position/Range/
// Location/... readonly record structs, but the not-yet-absorbed Ts.Ls subsets
// consume them as classes, so they ship as classes behind emitValueTypesAsStructs.
const csValueTypeNames = new Set<string>([
    "URI",
    "DocumentUri",
    "EmptyObject",
    "Tuple2Uinteger",
]);

function isCsReferenceType(name: string): boolean {
    if (csValueTypeNames.has(name)) return false;
    const enumeration = model.enumerations.find(e => e.name === name);
    if (enumeration) return false;
    if (emitValueTypesAsStructs && valueTypes.has(name)) return false;
    return true;
}

const valueTypes = new Set([
    "Position",
    "Range",
    "Location",
    "Color",
    "TextDocumentIdentifier",
    "PreviousResultId",
    "VersionedTextDocumentIdentifier",
    "OptionalVersionedTextDocumentIdentifier",
    "ExportInfoMapKey",
]);

function flattenOrTypes(types: Type[]): Type[] {
    const flattened = new Set<Type>();

    for (const rawType of types) {
        let type = rawType;

        // Dereference reference types that point to OR types
        if (rawType.kind === "reference") {
            const aliasedType = typeInfo.typeAliasMap.get(rawType.name);
            if (aliasedType && aliasedType.kind === "or") {
                type = aliasedType;
            }
        }

        if (type.kind === "or") {
            // Recursively flatten OR types
            for (const subType of flattenOrTypes(type.items)) {
                flattened.add(subType);
            }
        }
        else {
            flattened.add(rawType);
        }
    }

    return Array.from(flattened);
}

function pluralize(name: string): string {
    // Handle common irregular plurals and special cases
    if (
        name.endsWith("s") || name.endsWith("x") || name.endsWith("z") ||
        name.endsWith("ch") || name.endsWith("sh")
    ) {
        return name + "es";
    }
    if (name.endsWith("y") && name.length > 1 && !"aeiou".includes(name[name.length - 2])) {
        return name.slice(0, -1) + "ies";
    }
    return name + "s";
}

function handleOrType(orType: OrType): CsType {
    // First, flatten any nested OR types
    const types = flattenOrTypes(orType.items);

    // Check for nullable types (OR with null)
    const nullIndex = types.findIndex(item => item.kind === "base" && item.name === "null");
    let containedNull = nullIndex !== -1;

    // If it's nullable, remove the null type from the list
    let nonNullTypes = types;
    if (containedNull) {
        nonNullTypes = types.filter((_, i) => i !== nullIndex);
    }

    // If no types remain after filtering null, this shouldn't happen
    if (nonNullTypes.length === 0) {
        throw new Error("Union type with only null is not supported: " + JSON.stringify(types));
    }

    // Even if only one type remains after filtering null, we still need to create a union type
    // to preserve the nullable behavior (all fields nil = null)

    let memberNames = nonNullTypes.map(type => {
        if (type.kind === "reference") {
            return type.name;
        }
        else if (type.kind === "base") {
            return titleCase(type.name);
        }
        else if (
            type.kind === "array" &&
            (type.element.kind === "reference" || type.element.kind === "base")
        ) {
            return pluralize(titleCase(type.element.name));
        }
        else if (type.kind === "array") {
            // Handle more complex array types
            const elementType = resolveType(type.element);
            return `${elementType.name}Array`;
        }
        else if (type.kind === "literal" && type.value.properties.length === 0) {
            return "EmptyObject";
        }
        else if (type.kind === "tuple") {
            return "Tuple";
        }
        else {
            throw new Error(`Unsupported type kind in union: ${type.kind}`);
        }
    });

    // Find longest common prefix of member names chunked by PascalCase
    function findLongestCommonPrefix(names: string[]): string {
        if (names.length === 0) return "";
        if (names.length === 1) return "";

        // Split each name into PascalCase chunks
        function splitPascalCase(name: string): string[] {
            const chunks: string[] = [];
            let currentChunk = "";

            for (let i = 0; i < name.length; i++) {
                const char = name[i];
                if (char >= "A" && char <= "Z" && currentChunk.length > 0) {
                    // Start of a new chunk
                    chunks.push(currentChunk);
                    currentChunk = char;
                }
                else {
                    currentChunk += char;
                }
            }

            if (currentChunk.length > 0) {
                chunks.push(currentChunk);
            }

            return chunks;
        }

        const allChunks = names.map(splitPascalCase);
        const minChunkLength = Math.min(...allChunks.map(chunks => chunks.length));

        // Find the longest common prefix of chunks
        let commonChunks: string[] = [];
        for (let i = 0; i < minChunkLength; i++) {
            const chunk = allChunks[0][i];
            if (allChunks.every(chunks => chunks[i] === chunk)) {
                commonChunks.push(chunk);
            }
            else {
                break;
            }
        }

        return commonChunks.join("");
    }

    const commonPrefix = findLongestCommonPrefix(memberNames);

    let unionTypeName = "";

    if (commonPrefix.length > 0) {
        const trimmedMemberNames = memberNames.map(name => name.slice(commonPrefix.length));
        if (trimmedMemberNames.every(name => name)) {
            unionTypeName = commonPrefix + trimmedMemberNames.join("Or");
            memberNames = trimmedMemberNames;
        }
        else {
            unionTypeName = memberNames.join("Or");
        }
    }
    else {
        unionTypeName = memberNames.join("Or");
    }

    if (containedNull) {
        unionTypeName += "OrNull";
    }
    else {
        containedNull = false;
    }

    const union = memberNames.map((name, i) => ({ name, type: nonNullTypes[i], containedNull }));

    typeInfo.unionTypes.set(unionTypeName, union);

    return {
        name: unionTypeName,
        needsPointer: false,
        unionName: unionTypeName,
    };
}

const typeAliasOverrides = new Map([
    ["LSPAny", { name: "JsonElement?", needsPointer: false }],
    // A bare "any" reference (Go `any`, e.g. InitializationOptions.userPreferences in
    // generate.mts:54) is the same arbitrary-JSON-value slot as LSPAny; without this
    // entry resolveType's passthrough would emit the TS-style identifier `any` into C#.
    ["any", { name: "JsonElement?", needsPointer: false }],
    ["LSPArray", { name: "JsonElement?", needsPointer: false }],
    ["LSPObject", { name: "Dictionary<string, JsonElement?>", needsPointer: false }],
    ["uint64", { name: "ulong", needsPointer: false }],
]);

// These type aliases are intentionally not resolved to their underlying types.
// It means that we can end up with non-normalized union types in some places.
// Also, unlike other type aliases, these will get a type alias in the generated source code.
// We may want to eventually do this for all type aliases though.
const nonResolvedAliases = new Set(customTypeAliases.map(ta => ta.name));

/**
 * First pass: Resolve all type information
 */
function collectTypeDefinitions() {
    // Process all enumerations first to make them available for struct fields
    for (const enumeration of model.enumerations) {
        typeInfo.types.set(enumeration.name, {
            name: enumeration.name,
            needsPointer: false,
        });
    }

    // Process all structures
    for (const structure of model.structures) {
        typeInfo.types.set(structure.name, {
            name: structure.name,
            needsPointer: !isCsReferenceType(structure.name),
        });
    }

    // Process all type aliases
    for (const typeAlias of model.typeAliases) {
        if (typeAliasOverrides.has(typeAlias.name)) {
            continue;
        }

        // Store the alias mapping so we can resolve it later
        typeInfo.typeAliasMap.set(typeAlias.name, typeAlias.type);
    }

    // Merge the custom type aliases (Go `type TelemetryEvent = <union>` in the
    // hand-written base) into the alias map so references resolve transparently to
    // the union type (matches Go lsp_generated.go:11562 naming); the union itself is
    // then registered in typeInfo.unionTypes and emitted like any other OR type.
    for (const alias of customTypeAliases) {
        if (!typeInfo.typeAliasMap.has(alias.name)) {
            typeInfo.typeAliasMap.set(alias.name, alias.type);
        }
    }
}

function formatDocumentation(s: string | undefined): string[] {
    if (!s) return [];

    let lines: string[] = [];

    for (let line of s.split("\n")) {
        line = line.trimEnd();
        line = line.replace(/(\w ) +/g, "$1");
        // Some upstream docs include dangling block comment delimiters; remove them
        // so they don't leak into generated `//` comments.
        line = line.replace(/\s*\/\*+\s*/g, " ");
        line = line.replace(/\s*\*+\/\s*/g, " ");
        line = line.replace(/\s{2,}/g, " ").trimEnd();
        line = line.replace(/\{@link(?:code)?.*?([^} ]+)\}/g, "$1");
        line = line.replace(/^@(since|proposed|deprecated)(.*)/, (_, tag, rest) => {
            lines.push("");
            return `${titleCase(tag)}${rest ? ":" + rest : "."}`;
        });
        lines.push(line);
    }

    // filter out contiguous empty lines
    while (true) {
        const toRemove = lines.findIndex((line, index) => {
            if (line) return false;
            if (index === 0) return true;
            if (index === lines.length - 1) return true;
            return !(lines[index - 1] && lines[index + 1]);
        });
        if (toRemove === -1) break;
        lines.splice(toRemove, 1);
    }

    return lines.map(l => "// " + l);
}

function methodNameIdentifier(name: string) {
    return name.split("/").map(v => {
        if (v === "$") return "";
        // Mirror goFieldName: "_vs_foo" -> "VSFoo".
        if (v.startsWith("_vs_")) return "VS" + titleCase(v.slice(4));
        return titleCase(v);
    }).join("");
}

/**
 * Returns the JSON token kind ("string", "number", "object", "array", "boolean")
 * for a given meta model Type, or undefined if the kind cannot be statically determined.
 */
function jsonKindForType(type: Type): string | undefined {
    switch (type.kind) {
        case "base":
            switch (type.name) {
                case "integer":
                case "uinteger":
                case "decimal":
                    return "number";
                case "string":
                case "URI":
                case "DocumentUri":
                    return "string";
                case "boolean":
                    return "boolean";
                default:
                    return undefined;
            }
        case "reference": {
            if (typeAliasOverrides.has(type.name)) {
                return undefined;
            }
            if (model.structures.some(s => s.name === type.name)) {
                return "object";
            }
            const enumeration = model.enumerations.find(e => e.name === type.name);
            if (enumeration) {
                switch (enumeration.type.name) {
                    case "string":
                        return "string";
                    case "integer":
                    case "uinteger":
                        return "number";
                    default:
                        return undefined;
                }
            }
            const aliasType = typeInfo.typeAliasMap.get(type.name);
            if (aliasType) return jsonKindForType(aliasType);
            return undefined;
        }
        case "array":
            return "array";
        case "map":
            return "object";
        case "tuple":
            return "array";
        case "stringLiteral":
            return "string";
        case "integerLiteral":
            return "number";
        case "booleanLiteral":
            return "boolean";
        case "literal":
            return "object";
        case "or": {
            const kinds = new Set(type.items.map(item => jsonKindForType(item)).filter(Boolean));
            return kinds.size === 1 ? [...kinds][0] : undefined;
        }
        default:
            return undefined;
    }
}

/**
 * Checks if a meta model Type can represent a JSON null value.
 * Used to determine whether to reject explicit JSON `null` for any field
 * that can otherwise decode `null` without a type error.
 */
function typeCanBeNull(type: Type): boolean {
    switch (type.kind) {
        case "base":
            return type.name === "null";
        case "reference": {
            // A bare "any" reference resolves to Go's `any` (interface), which can hold null.
            // Checked before the override lookup so the "any" → JsonElement? override added
            // to typeAliasOverrides cannot shadow this nullability answer.
            if (type.name === "any") {
                return true;
            }
            const override = typeAliasOverrides.get(type.name);
            if (override) {
                return override.name === "any";
            }
            if (nonResolvedAliases.has(type.name)) {
                const customAlias = customTypeAliases.find(t => t.name === type.name);
                if (customAlias) return typeCanBeNull(customAlias.type);
                return false;
            }
            const aliased = typeInfo.typeAliasMap.get(type.name);
            if (aliased) return typeCanBeNull(aliased);
            return false;
        }
        case "or":
            return type.items.some(item => typeCanBeNull(item));
        default:
            return false;
    }
}

// ---------------------------------------------------------------------------
// Skip-list cascade: remove types temporarily hosted in Ts.Ls subsets, plus
// everything that references them (transitively).
// ---------------------------------------------------------------------------

function computeSkipSet(): Set<string> {
    const skipped = new Set<string>();
    const referenceNames = (type: Type): string[] => {
        switch (type.kind) {
            case "reference":
                return [type.name];
            case "array":
                return referenceNames(type.element);
            case "map":
                return [...referenceNames(type.key), ...referenceNames(type.value)];
            case "tuple":
                return type.items.flatMap(referenceNames);
            case "or":
            case "and":
                return type.items.flatMap(referenceNames);
            default:
                return [];
        }
    };

    const structRefs = new Map<string, string[]>();
    for (const s of model.structures) {
        structRefs.set(s.name, s.properties.flatMap(p => referenceNames(p.type)));
    }
    const unionRefs = new Map<string, string[]>();
    for (const [name, members] of typeInfo.unionTypes) {
        unionRefs.set(name, members.flatMap(m => referenceNames(m.type)));
    }
    const enumBases = new Map<string, string[]>();
    for (const e of model.enumerations) {
        enumBases.set(e.name, []);
    }

    let changed = true;
    const seed = (name: string) => {
        if (!skipped.has(name)) {
            skipped.add(name);
            changed = true;
        }
    };
    for (const name of temporarilyHostedInLs) seed(name);

    while (changed) {
        changed = false;
        for (const [name, refs] of structRefs) {
            if (!skipped.has(name) && refs.some(r => skipped.has(r))) seed(name);
        }
        for (const [name, refs] of unionRefs) {
            if (!skipped.has(name) && refs.some(r => skipped.has(r))) seed(name);
        }
        // Requests/notifications whose params/result/registrationOptions reference skipped types.
        for (const r of [...model.requests, ...model.notifications]) {
            const key = `request:${r.method}`;
            const rResult = "result" in r ? (r as Request).result : undefined;
            const refs = [
                ...(r.params && !Array.isArray(r.params) ? referenceNames(r.params) : []),
                ...(rResult && !Array.isArray(rResult) ? referenceNames(rResult) : []),
                ...(r.registrationOptions ? referenceNames(r.registrationOptions) : []),
            ];
            if (!skipped.has(key) && refs.some(ref => skipped.has(ref))) seed(key);
        }
    }
    return skipped;
}

// ---------------------------------------------------------------------------
// Emission
// ---------------------------------------------------------------------------

function collectTypeDefinitionsForGeneration() {
    // Resolve pass in the same order Go's generateCode resolves, so the union
    // registry (typeInfo.unionTypes) has identical ordering: structures first
    // (properties in order), then requests/notifications (result, params), then
    // custom type aliases.
    for (const structure of model.structures) {
        for (const prop of structure.properties) {
            resolveType(prop.type);
        }
    }
    const requestsAndNotifications: (Request | Notification)[] = [...model.requests, ...model.notifications];
    for (const request of requestsAndNotifications) {
        if ("result" in request && request.result) {
            if (!(request.result.kind === "base" && request.result.name === "null")) {
                resolveType(request.result);
            }
        }
        if (request.params && !Array.isArray(request.params)) {
            resolveType(request.params);
        }
    }
    for (const alias of customTypeAliases) {
        resolveType(alias.type);
    }
}

// ---------------------------------------------------------------------------
// C# emission helpers
// ---------------------------------------------------------------------------

// plan/04 §9.1: every .g.cs opens with the <auto-generated> block followed by an
// explicit `#nullable enable` — without the directive the whole file drops out of
// the nullable annotation context and every `?` annotation silently stops working
// (CS8669 under TreatWarningsAsErrors). No timestamps/hashes: byte-stable reruns.
function emitGeneratorHeader(w: (s?: string) => void) {
    w("// <auto-generated>");
    w("//     Code generated by tools/generator/generate-lsp.ts (plan/04-codegen.md §5); DO NOT EDIT.");
    w("//     Inputs: tools/generator/metaModel.json (pinned by tools/generator/lsp-model-version.json)");
    w("// </auto-generated>");
    w("#nullable enable");
    w();
}

function emitDocs(out: string[], docs: string[], indent = "") {
    for (const line of docs) {
        out.push(indent + line);
    }
}

// C# type text for a property, following plan/04 §5.4:
//   required           → `required` modifier (STJ rejects absence);
//                        spec-nullable required → `required T?`
//   optional           → `T?` + [JsonIgnore(WhenWritingNull)]
//   omitzeroValue      → non-nullable `T` + [JsonIgnore(WhenWritingDefault)]
function propTypeText(prop: Property): { text: string; required: boolean; omitzero: boolean; isValue: boolean; resolved: CsType; } {
    const resolved = resolveType(prop.type);
    const omitzero = !!prop.omitzeroValue;
    const required = !prop.optional && !omitzero;
    let text = resolved.name;
    // Already-nullable spellings (e.g. "JsonElement?" for LSPAny/any) must not get a
    // second "?" — C# has no nullable-of-nullable (same rule as the union emitter).
    if (omitzero) {
        // non-nullable, omitted when the value equals the type default
    }
    else if (required) {
        if (typeCanBeNull(prop.type) && !text.endsWith("?")) {
            text += "?";
        }
    }
    else if (!text.endsWith("?")) {
        text += "?";
    }
    return { text, required, omitzero, isValue: resolved.needsPointer, resolved };
}

function propAttributes(prop: Property, info: ReturnType<typeof propTypeText>): string[] {
    const attrs = [`[JsonPropertyName(${JSON.stringify(prop.name)})]`];
    if (info.omitzero) {
        attrs.push("[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]");
    }
    else if (info.required) {
        // Go's generated unmarshalers reject missing/null required properties
        // (generate.mts requiredProps / lsp.go errMissing); [JsonRequired] is the
        // STJ equivalent and the house pattern in the hand-written face.
        attrs.push("[JsonRequired]");
    }
    else {
        attrs.push("[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]");
    }
    return attrs;
}

// C# value types default correctly; reference-typed required properties need a
// null-forgiving initializer (the `required` keyword would forbid the
// parameterless `new Position { }` construction style the Ls consumers use).
const csPrimitiveTypeNames = new Set(["int", "uint", "bool", "double"]);

function isCsValueType(name: string): boolean {
    if (csValueTypeNames.has(name)) return true;
    return model.enumerations.some(e => e.name === name);
}

function propInitializer(text: string): string {
    if (text.endsWith("?")) return "";
    if (text === "string") return ' = "";'; // Go zero string
    if (csPrimitiveTypeNames.has(text)) return "";
    if (isCsValueType(text)) return "";
    // Literal types (StringLiteral*/IntegerLiteral*/BooleanLiteral*) are readonly record structs.
    if ([...typeInfo.literalTypes.values()].includes(text)) return "";
    return " = null!;";
}

// The accessor name for a type's JsonTypeInfo<T> property on LspJsonContext. The
// attribute emission pins this name explicitly (TypeInfoPropertyName), so this
// function is the single source of truth for both sides. The C# type spelling is
// folded into a valid identifier: primitives become their CLR names, arrays gain
// an "Array" suffix, List<T>/HashSet<T> concatenate, and each "?" (nullable) gains
// a "Nullable" suffix — innermost first (JsonElement?[]? → JsonElementNullableArrayNullable).
function contextAccessorFor(csType: string): string {
    if (csType.endsWith("?")) {
        return contextAccessorFor(csType.slice(0, -1)) + "Nullable";
    }
    if (csType.endsWith("[]")) {
        return contextAccessorFor(csType.slice(0, -2)) + "Array";
    }
    if (csType.startsWith("List<") && csType.endsWith(">")) {
        return "List" + contextAccessorFor(csType.slice("List<".length, -1));
    }
    if (csType.startsWith("HashSet<") && csType.endsWith(">")) {
        return "HashSet" + contextAccessorFor(csType.slice("HashSet<".length, -1));
    }
    switch (csType) {
        case "string": return "String";
        case "bool": return "Boolean";
        case "int": return "Int32";
        case "uint": return "UInt32";
        case "long": return "Int64";
        case "ulong": return "UInt64";
        case "double": return "Double";
        case "float": return "Single";
        case "object": return "Object";
    }
    return csType;
}

function hasSomeProp(structure: Structure, propName: string, propTypeName: string) {
    return structure.properties?.some(p =>
        !p.optional &&
        p.name === propName &&
        p.type.kind === "reference" &&
        p.type.name === propTypeName
    );
}

function hasTextDocumentURI(structure: Structure) {
    return hasSomeProp(structure, "textDocument", "TextDocumentIdentifier") ||
        hasSomeProp(structure, "_vs_textDocument", "TextDocumentIdentifier");
}

function hasTextDocumentPosition(structure: Structure) {
    return hasSomeProp(structure, "position", "Position") ||
        hasSomeProp(structure, "_vs_position", "Position");
}

function getLocationUriProperty(structure: Structure) {
    const prop = structure.properties?.find(p =>
        !p.optional &&
        titleCase(p.name).endsWith("Uri") &&
        p.type.kind === "base" &&
        p.type.name === "DocumentUri"
    );
    if (
        prop &&
        structure.properties.some(p =>
            !p.optional &&
            titleCase(p.name) === titleCase(prop.name).replace(/Uri$/, "Range") &&
            p.type.kind === "reference" &&
            p.type.name === "Range"
        )
    ) {
        return titleCase(prop.name);
    }
    return undefined;
}

// countRequiredFields / discriminator helpers — port of generate.mts:2004-2124
// (decision logic only; the Go code text emission is replaced by C# emission).
function countRequiredFields(entry: { originalType: Type; }): number {
    if (entry.originalType.kind !== "reference") return 0;
    const structure = model.structures.find(s => s.name === (entry.originalType as ReferenceType).name);
    if (!structure) return 0;
    return structure.properties.filter(p => !p.optional && !p.omitzeroValue).length;
}

function findDiscriminatorField(entries: { fieldName: string; typeName: string; originalType: Type; }[]): {
    fieldName: string;
    mapping: Map<string, { fieldName: string; typeName: string; originalType: Type; }>;
    unmapped: { fieldName: string; typeName: string; originalType: Type; }[];
} | null {
    const fieldCandidates = new Map<string, Map<string, typeof entries[0] | undefined>>();

    for (const entry of entries) {
        if (entry.originalType.kind !== "reference") continue;
        const structure = model.structures.find(s => s.name === (entry.originalType as ReferenceType).name);
        if (!structure) continue;

        for (const prop of structure.properties) {
            if (prop.type.kind === "stringLiteral") {
                if (!fieldCandidates.has(prop.name)) {
                    fieldCandidates.set(prop.name, new Map());
                }
                const mapping = fieldCandidates.get(prop.name)!;
                if (!mapping.has(prop.type.value)) {
                    mapping.set(prop.type.value, entry);
                }
                else {
                    // Two entries share the same literal value; invalidate this candidate.
                    mapping.set(prop.type.value, undefined);
                }
            }
        }
    }

    // Pick the discriminator field that covers the most entries.
    let bestField: string | null = null;
    let bestMapping: Map<string, typeof entries[0]> | null = null;

    for (const [fieldName, mapping] of fieldCandidates) {
        const validMapping = new Map<string, typeof entries[0]>();
        for (const [value, entry] of mapping) {
            if (entry !== undefined) validMapping.set(value, entry);
        }
        if (validMapping.size >= 2 && (!bestMapping || validMapping.size > bestMapping.size)) {
            bestField = fieldName;
            bestMapping = validMapping;
        }
    }

    if (!bestField || !bestMapping) return null;

    const mappedEntries = new Set(bestMapping.values());
    const unmapped = entries.filter(e => !mappedEntries.has(e));

    return { fieldName: bestField, mapping: bestMapping, unmapped };
}

function findPresenceDiscriminator(entries: { fieldName: string; typeName: string; originalType: Type; }[]): {
    checks: { jsonFieldName: string; entry: { fieldName: string; typeName: string; originalType: Type; }; }[];
    unmapped: { fieldName: string; typeName: string; originalType: Type; }[];
} | null {
    const variantProps = new Map<typeof entries[0], { required: Property[]; allNames: Set<string>; }>();
    for (const entry of entries) {
        if (entry.originalType.kind !== "reference") continue;
        const structure = model.structures.find(s => s.name === (entry.originalType as ReferenceType).name);
        if (!structure) continue;
        const required = structure.properties.filter(p => !p.optional && !p.omitzeroValue);
        const allNames = new Set(structure.properties.map(p => p.name));
        variantProps.set(entry, { required, allNames });
    }

    const checks: { jsonFieldName: string; entry: typeof entries[0]; }[] = [];
    const handled = new Set<typeof entries[0]>();

    for (const entry of entries) {
        const info = variantProps.get(entry);
        if (!info) continue;

        const otherEntries = entries.filter(e => e !== entry);
        for (const field of info.required) {
            const absentFromAllOthers = otherEntries.every(other => {
                const otherInfo = variantProps.get(other);
                if (!otherInfo) return false;
                return !otherInfo.allNames.has(field.name);
            });
            if (absentFromAllOthers) {
                checks.push({ jsonFieldName: field.name, entry });
                handled.add(entry);
                break;
            }
        }
    }

    if (checks.length === 0) return null;

    const unmapped = entries.filter(e => !handled.has(e));
    return { checks, unmapped };
}

function collectAllPresenceChecks(
    pres: NonNullable<ReturnType<typeof findPresenceDiscriminator>>,
): {
    allChecks: { jsonFieldName: string; entry: { fieldName: string; typeName: string; originalType: Type; }; }[];
    finalUnmapped: { fieldName: string; typeName: string; originalType: Type; }[];
} {
    const allChecks = [...pres.checks];
    let remaining = pres.unmapped;
    while (remaining.length > 1) {
        const next = findPresenceDiscriminator(remaining);
        if (!next) break;
        allChecks.push(...next.checks);
        remaining = next.unmapped;
    }
    return { allChecks, finalUnmapped: remaining };
}

// ---------------------------------------------------------------------------
// Protocol.g.cs — structures, enums, methods, infos, literals
// ---------------------------------------------------------------------------

function emitProtocol(skipped: Set<string>, skipNote: string[]): string {
    const out: string[] = [];
    const w = (s = "") => out.push(s);

    emitGeneratorHeader(w);
    w("// Port of tsgo internal/lsp/lsproto/lsp_generated.go (generate.mts), emitting C# per");
    w("// plan/04-codegen.md §5. Structures, enumerations, Method constants, the");
    w("// RequestInfo/NotificationInfo registry, literal types and RegisterOptions.");
    w("//");
    w("// Deviations from the Go surface (each temporary, see the generator source):");
    w("// - Go `type X = Y` response/telemetry aliases are inlined into the RequestInfo");
    w("//   generics (C# has no type aliases).");
    w("// - Position/Range/Location/… ship as classes, not readonly record structs");
    w("//   (plan/04 §5.3).");
    w("// - Go Resolved*ClientCapabilities are value structs; here they are classes whose");
    w("//   nested Resolved* members are never null, and (*X).resolve() is an extension.");
    if (skipNote.length > 0) {
        w("// - The following names are temporarily hosted as hand-written subsets in Ts.Ls");
        w("//   (CodeActions.cs / *Support.cs) and are skipped here, together with every type");
        w("//   that references them: " + skipNote.join(", "));
    }
    w();
    w("using System;")
    w("using System.Collections.Generic;")
    w("using System.Text.Json;")
    w("using System.Text.Json.Serialization;")
    w("using System.Text.Json.Serialization.Metadata;")
    w();
    w("namespace Ts.LsProto;");
    w();
    w("// Meta model version " + model.metaData.version);
    w();

    // Base protocol surface — port of the hand-written Go base (lsproto/lsp.go).
    // The generated protocol face references these names, so they are emitted here.
    // (Go keeps them hand-written beside lsp_generated.go; C# counterparts that
    // already exist hand-written — Lsp/UnionSerialization.cs, Util.cs, JsonRpc/* —
    // are NOT re-emitted.)
    w("// Base protocol surface (port of lsproto/lsp.go handwritten base).");
    w();

    // lsp.go:66-81 — structural interfaces the server's generic registration
    // helpers constrain on.
    w("public interface IHasTextDocumentUri");
    w("{");
    w("    public DocumentUri TextDocumentURI();");
    w("}");
    w();
    w("public interface IHasTextDocumentPosition : IHasTextDocumentUri");
    w("{");
    w("    public Position TextDocumentPosition();");
    w("}");
    w();
    w("public interface IHasLocations");
    w("{");
    w("    public Location[]? GetLocations();");
    w("}");
    w();
    w("public interface IHasLocation");
    w("{");
    w("    public Location GetLocation();");
    w("}");
    w();

    // lsp.go:85 — `type Method string`. The Methods.* constants below are const
    // string (the Go untyped-constant shape, so string switches can case on them);
    // this implicit conversion restores Method-typed assignments.
    w("public readonly record struct Method(string Value)");
    w("{");
    w("    public override string ToString() => Value ?? \"\";");
    w("    public static implicit operator Method(string value) => new(value);");
    w("}");
    w();

    // lsp.go:17 — `type DocumentUri string // !!!`
    w("[JsonConverter(typeof(DocumentUriJsonConverter))]");
    w("public readonly record struct DocumentUri");
    w("{");
    w("    public DocumentUri(string value)");
    w("    {");
    w("        Value = value ?? throw new ArgumentNullException(nameof(value));");
    w("    }");
    w();
    w("    public string Value { get; }");
    w();
    w("    public override string ToString() => Value ?? \"\";");
    w("    public static implicit operator DocumentUri(string value) => new(value);");
    w("}");
    w();

    // lsp.go:83 — `type URI string // !!!`
    w("[JsonConverter(typeof(URIJsonConverter))]");
    w("public readonly record struct URI");
    w("{");
    w("    public URI(string value)");
    w("    {");
    w("        Value = value ?? throw new ArgumentNullException(nameof(value));");
    w("    }");
    w();
    w("    public string Value { get; }");
    w();
    w("    public override string ToString() => Value ?? \"\";");
    w("    public static implicit operator URI(string value) => new(value);");
    w("}");
    w();

    // lsp.go:266-281 — a stand-in for JSON null (its converter enforces the literal).
    w("[JsonConverter(typeof(NullJsonConverter))]");
    w("public readonly struct Null");
    w("{");
    w("    public static Null Instance => default;");
    w("}");
    w();

    // lsp.go:283-285 — marker params type for methods declared without params.
    w("public readonly record struct NoParams");
    w("{");
    w("    public bool IsZero() => true;");
    w("}");
    w();

    // generate.mts:1554-1559 — the empty literal object ({}), used as a union arm.
    w("[JsonConverter(typeof(EmptyObjectJsonConverter))]");
    w("public readonly record struct EmptyObject;");
    w();

    // generate.mts:1441-1451 — [2]uinteger tuple → C# value type (plan/04 §5.3).
    w("[JsonConverter(typeof(Tuple2UintegerJsonConverter))]");
    w("public readonly record struct Tuple2Uinteger(uint Item1, uint Item2);");
    w();

    // lsp.go:186-225 — RequestInfo/NotificationInfo. The Go phantom fields
    // ([0]Params) exist only for generic inference; the C# shape carries the
    // JsonTypeInfo handles the source-generated context provides. The
    // UnmarshalResult/NewRequestMessage members ride the future lsproto/jsonrpc.go
    // port (they need the lsproto RequestMessage/ResponseMessage types).
    w("// Inspired by https://www.youtube.com/watch?v=dab3I-HcTVk");
    w("public sealed class RequestInfo<TParams, TResult>");
    w("{");
    w("    public RequestInfo(Method method, JsonTypeInfo<TParams> paramsTypeInfo, JsonTypeInfo<TResult> resultTypeInfo)");
    w("    {");
    w("        Method = method;");
    w("        ParamsTypeInfo = paramsTypeInfo;");
    w("        ResultTypeInfo = resultTypeInfo;");
    w("    }");
    w();
    w("    public Method Method { get; }");
    w();
    w("    public JsonTypeInfo<TParams> ParamsTypeInfo { get; }");
    w();
    w("    public JsonTypeInfo<TResult> ResultTypeInfo { get; }");
    w("}");
    w();
    w("public sealed class NotificationInfo<TParams>");
    w("{");
    w("    public NotificationInfo(Method method, JsonTypeInfo<TParams> paramsTypeInfo)");
    w("    {");
    w("        Method = method;");
    w("        ParamsTypeInfo = paramsTypeInfo;");
    w("    }");
    w();
    w("    public Method Method { get; }");
    w();
    w("    public JsonTypeInfo<TParams> ParamsTypeInfo { get; }");
    w("}");
    w();

    // Structures
    w("// Structures");
    w();
    for (const structure of model.structures) {
        if (skipped.has(structure.name)) {
            w("// skipped (temporarily hosted in Ts.Ls subsets): " + structure.name);
            w();
            continue;
        }

        emitDocs(out, formatDocumentation(structure.documentation));
        // Go lsp.go:66-82 declares the structural interfaces server.go's generic
        // registration helpers constrain on; C# needs them declared explicitly.
        const ifaceList: string[] = [];
        if (hasTextDocumentURI(structure)) {
            ifaceList.push("IHasTextDocumentUri");
            if (hasTextDocumentPosition(structure)) {
                ifaceList.push("IHasTextDocumentPosition");
            }
        }
        const locProp = getLocationUriProperty(structure);
        if (locProp) {
            ifaceList.push("IHasLocation");
        }
        w("public sealed partial class " + structure.name + (ifaceList.length ? " : " + ifaceList.join(", ") : ""));
        w("{");

        structure.properties.forEach((prop, index) => {
            if (index > 0) w();
            emitDocs(out, formatDocumentation(prop.documentation), "    ");
            const info = propTypeText(prop);
            for (const attr of propAttributes(prop, info)) {
                w("    " + attr);
            }
            const initializer = propInitializer(info.text);
            w(`    public ${info.text} ${csMemberName(structure, prop)} { get; set; }${initializer}`);
        });

        // Special: add RegisterOptions field to Registration
        if (structure.name === "Registration") {
            w();
            w("    // Options necessary for the registration. Determines the method.");
            w("    [JsonIgnore]");
            w("    public RegisterOptions? RegisterOptions { get; set; }");
        }

        if (hasTextDocumentURI(structure)) {
            const textDocProp = structure.properties?.find(p => (p.name === "textDocument" || p.name === "_vs_textDocument") && p.type.kind === "reference" && p.type.name === "TextDocumentIdentifier");
            const textDocFieldName = textDocProp ? goFieldName(textDocProp) : "TextDocument";
            w();
            w(`    public DocumentUri TextDocumentURI() => ${textDocFieldName}.Uri;`);

            if (hasTextDocumentPosition(structure)) {
                const posProp = structure.properties?.find(p => (p.name === "position" || p.name === "_vs_position") && p.type.kind === "reference" && p.type.name === "Position");
                const posFieldName = posProp ? goFieldName(posProp) : "Position";
                w();
                w(`    public Position TextDocumentPosition() => ${posFieldName};`);
            }
        }

        if (locProp) {
            w();
            if (locProp === "Uri" && structure.name === "Location") {
                w("    public Location GetLocation() => this;");
            }
            else {
                w("    public Location GetLocation() => new Location");
                w("    {");
                w(`        Uri = ${locProp},`);
                w(`        Range = ${locProp.replace(/Uri$/, "Range")},`);
                w("    };");
            }
        }

        w("}");
        w();

        if (compareStructures.has(structure.name)) {
            emitCompareMethod(out, structure);
        }
    }

    // Enumerations
    w("// Enumerations");
    w();
    for (const enumeration of model.enumerations) {
        if (skipped.has(enumeration.name)) {
            w("// skipped (temporarily hosted in Ts.Ls subsets): " + enumeration.name);
            w();
            continue;
        }

        emitDocs(out, formatDocumentation(enumeration.documentation));

        if (enumeration.type.name === "string") {
            // String "enum" → readonly record struct wrapper + generated converter
            // (plan/04 §5.5; JsonStringEnumConverter is deliberately not used).
            // The implicit string conversion mirrors Go, where string constants are
            // assignable to the named string type (e.g. MarkupContent.Kind = "plaintext").
            w(`[JsonConverter(typeof(${enumeration.name}JsonConverter))]`);
            w(`public readonly partial record struct ${enumeration.name}`);
            w("{");
            w(`    public ${enumeration.name}(string? value)`);
            w("    {");
            w("        Value = value;");
            w("    }");
            w();
            w("    public string? Value { get; }");
            w();
            w(`    public static implicit operator ${enumeration.name}(string? value) => new(value);`);
            w();
            for (const value of enumeration.values) {
                emitDocs(out, formatDocumentation(value.documentation), "    ");
                const memberName = enumMemberName(enumeration, value.name);
                w(`    public static ${enumeration.name} ${memberName} => new(${JSON.stringify(String(value.value))});`);
            }
            w("}");
            w();
            continue;
        }

        const baseType = enumeration.type.name === "integer" ? "int" : "uint";
        w(`public enum ${enumeration.name} : ${baseType}`);
        w("{");
        enumeration.values.forEach((value, index) => {
            emitDocs(out, formatDocumentation(value.documentation), "    ");
            const memberName = enumMemberName(enumeration, value.name);
            const comma = index < enumeration.values.length - 1 ? "," : ",";
            w(`    ${memberName} = ${Number(value.value)}${comma}`);
        });
        w("}");
        w();
    }

    // Methods
    w("// Methods");
    w();
    w("public static class Methods");
    w("{");
    const requestsAndNotifications: (Request | Notification)[] = [...model.requests, ...model.notifications];
    for (const request of requestsAndNotifications) {
        if (skipped.has(`request:${request.method}`)) {
            w(`    // skipped (references types temporarily hosted in Ts.Ls subsets): ${request.method}`);
            continue;
        }
        emitDocs(out, formatDocumentation(request.documentation), "    ");
        w(`    public static readonly Method ${methodNameIdentifier(request.method)} = new(${JSON.stringify(request.method)});`);
    }
    // Emit constants for registration-only methods (not also a request/notification)
    for (const reg of registrationMethods) {
        if (reg.isRegistrationOnly && !registrationMethodSkipped(reg, skipped)) {
            w(`    // Registration-only method for ${reg.registrationMethod}.`);
            w(`    public static readonly Method ${reg.fieldName} = new(${JSON.stringify(reg.registrationMethod)});`);
        }
    }
    w("}");
    w();

    // RequestInfo/NotificationInfo registry
    w("// Request response types and method info registry. Go emits `type XResponse = Y`");
    w("// aliases plus `var XInfo = RequestInfo[...]{};` — C# has no type aliases, so the");
    w("// response type rides the RequestInfo generic parameters.");
    w();
    w("public static class Infos");
    w("{");
    for (const request of requestsAndNotifications) {
        if (skipped.has(`request:${request.method}`)) {
            continue;
        }
        const methodName = methodNameIdentifier(request.method);
        const isRequest = "result" in request;

        if (isRequest) {
            const result = (request as Request).result!;
            const resultType = result.kind === "base" && result.name === "null" ? "Null" : resolveType(result).name;
            const resultRef = resultType === "Null" ? "Null" : resultType + "?";
            const paramsType = request.params && !Array.isArray(request.params) ? resolveType(request.params).name : "NoParams";
            w(`    // Type mapping info for \`${request.method}\` (response alias: ${methodName}Response).`);
            w(`    public static readonly RequestInfo<${paramsType}, ${resultRef}> ${methodName}Info =`);
            w(`        new(Methods.${methodName}, LspJsonContext.Default.${contextAccessorFor(paramsType)}, LspJsonContext.Default.${contextAccessorFor(resultType)});`);
            w();
        }
        else {
            const paramsType = request.params && !Array.isArray(request.params) ? resolveType(request.params).name : "NoParams";
            w(`    // Type mapping info for \`${request.method}\`.`);
            w(`    public static readonly NotificationInfo<${paramsType}> ${methodName}Info =`);
            w(`        new(Methods.${methodName}, LspJsonContext.Default.${contextAccessorFor(paramsType)});`);
            w();
        }
    }
    w("}");
    w();

    // Literal types
    w("// Literal types");
    w();
    for (const [value, name] of typeInfo.literalTypes) {
        if (skipped.has(name)) continue;
        const jsonValue = JSON.stringify(value);
        w(`// ${name} is a literal type for ${jsonValue}.`);
        w(`[JsonConverter(typeof(${name}JsonConverter))]`);
        w(`public readonly record struct ${name};`);
        w();
    }

    // RegisterOptions
    w("// RegisterOptions is an externally-tagged union representing the options for a capability registration.");
    w("// Exactly one field should be set. The set field determines the method for the registration.");
    w("public sealed class RegisterOptions");
    w("{");
    for (const reg of registrationMethods) {
        if (registrationMethodSkipped(reg, skipped)) {
            w(`    // skipped (references types temporarily hosted in Ts.Ls subsets): ${reg.registrationMethod}`);
            continue;
        }
        w(`    public ${reg.optionsTypeName}? ${reg.fieldName} { get; set; }`);
    }
    w("}");
    w();

    if (emitResolvedCapabilities) {
        emitResolvedClientCapabilities(out);
    }

    return out.join("\n") + "\n";
}

// generate.mts:2126-2258, 3337-3376 — Resolved*ClientCapabilities.
function emitResolvedClientCapabilities(out: string[]) {
    const w = (s = "") => out.push(s);
    const clientCaps = model.structures.find(s => s.name === "ClientCapabilities");
    if (!clientCaps) return;

    const structureByName = (name: string) => model.structures.find(s => s.name === name);

    function collectDeps(structure: Structure, visited = new Set<string>()): Structure[] {
        if (visited.has(structure.name)) return [];
        visited.add(structure.name);
        const deps: Structure[] = [];
        for (const prop of structure.properties) {
            if (prop.type.kind === "reference") {
                const ref = structureByName(prop.type.name);
                if (ref) {
                    deps.push(...collectDeps(ref, new Set(visited)));
                    deps.push(ref);
                }
            }
        }
        return deps;
    }

    function resolvedFieldType(prop: Property): { text: string; init: string; structure?: Structure; } {
        if (prop.type.kind === "reference") {
            const ref = structureByName(prop.type.name);
            if (ref) return { text: `Resolved${ref.name}`, init: " = new();", structure: ref };
        }
        // Go derefOr(ptr): the zero value. C# value types default; strings become "";
        // reference-shaped values (arrays, unions, maps) stay nullable (Go nil).
        const text = resolveType(prop.type).name;
        if (text.endsWith("?")) return { text, init: "" };
        if (text === "string") return { text, init: ' = "";' };
        if (csPrimitiveTypeNames.has(text) || isCsValueType(text) || [...typeInfo.literalTypes.values()].includes(text)) {
            return { text, init: "" };
        }
        return { text: text + "?", init: "" };
    }

    function emitResolved(structure: Structure, isMain: boolean) {
        const typeName = `Resolved${structure.name}`;
        if (!isMain) {
            w(`// ${typeName} is a resolved version of ${structure.name} with all optional fields`);
            w(`// converted to non-pointer values for easier access.`);
            const docs = formatDocumentation(structure.documentation);
            if (docs.length > 0) {
                w("//");
                emitDocs(out, docs);
            }
        }
        w(`public sealed class ${typeName}`);
        w("{");
        structure.properties.forEach((prop, index) => {
            if (index > 0) w();
            emitDocs(out, formatDocumentation(prop.documentation), "    ");
            const field = resolvedFieldType(prop);
            w(`    public ${field.text} ${csMemberName(structure, prop)} { get; set; }${field.init}`);
        });
        w("}");
        w();
    }

    function emitResolveConversion(structure: Structure, isMain: boolean) {
        const typeName = `Resolved${structure.name}`;
        const methodName = isMain ? "Resolve" : "ResolveInternal";
        const access = isMain ? "public" : "internal";
        w(`    ${access} static ${typeName} ${methodName}(this ${structure.name}? v)`);
        w("    {");
        w("        if (v is null)");
        w("        {");
        w(`            return new ${typeName}();`);
        w("        }");
        w(`        return new ${typeName}`);
        w("        {");
        for (const prop of structure.properties) {
            const name = csMemberName(structure, prop);
            const field = resolvedFieldType(prop);
            if (field.structure) {
                w(`            ${name} = v.${name}.ResolveInternal(),`);
                continue;
            }
            const info = propTypeText(prop);
            if (field.text === "string" && info.text.endsWith("?")) {
                w(`            ${name} = v.${name} ?? "",`);
            }
            else if (info.text.endsWith("?") && !field.text.endsWith("?")) {
                w(`            ${name} = v.${name} ?? default,`);
            }
            else {
                w(`            ${name} = v.${name},`);
            }
        }
        w("        };");
        w("    }");
    }

    const deps = collectDeps(clientCaps);
    const uniqueDeps = Array.from(new Map(deps.map(d => [d.name, d])).values());
    for (const dep of uniqueDeps) {
        emitResolved(dep, false);
    }

    w("// ResolvedClientCapabilities is a version of ClientCapabilities where all nested");
    w("// fields are values (not pointers), making it easier to access deeply nested capabilities.");
    w("// Use ClientCapabilities.Resolve() to convert from ClientCapabilities.");
    const docs = formatDocumentation(clientCaps.documentation);
    if (docs.length > 0) {
        w("//");
        emitDocs(out, docs);
    }
    emitResolved(clientCaps, true);

    w("public static class ResolvedClientCapabilitiesExtensions");
    w("{");
    for (const dep of uniqueDeps) {
        emitResolveConversion(dep, false);
        w();
    }
    emitResolveConversion(clientCaps, true);
    w("}");
    w();
}

function registrationMethodSkipped(reg: RegistrationMethodInfo, skipped: Set<string>): boolean {
    return skipped.has(`request:${reg.registrationMethod}`) || skipped.has(reg.optionsTypeName);
}

// A "Value" member only collides for string enums, which are emitted as a
// wrapper record struct with a Value property; plain C# enums have no wrapper.
function enumMemberName(enumeration: Enumeration, name: string): string {
    const member = titleCase(name);
    if (member === "Value" && enumeration.type.name === "string") {
        throw new Error(`enum ${enumeration.name} has a value named "Value" which collides with the wrapper property; extend enumMemberName`);
    }
    return member;
}

function emitCompareMethod(out: string[], structure: Structure) {
    const props = structure.properties ?? [];
    out.push(`public partial class ${structure.name}`);
    out.push("{");
    out.push("    // Compare defines a total ordering by comparing fields in declaration order (generate.mts compareStructures).");
    out.push(`    public int Compare(${structure.name} other)`);
    out.push("    {");
    for (let i = 0; i < props.length; i++) {
        const prop = props[i];
        const isLast = i === props.length - 1;
        const fieldName = goFieldName(prop);
        const expr = compareExpressionForProperty(structure.name, prop, fieldName);
        if (isLast) {
            out.push(`        return ${expr};`);
        }
        else {
            out.push(`        var c${i} = ${expr};`);
            out.push(`        if (c${i} != 0)`);
            out.push(`        {`);
            out.push(`            return c${i};`);
            out.push(`        }`);
        }
    }
    out.push("    }");
    out.push("}");
    out.push("");
}

function compareExpressionForProperty(structName: string, prop: Property, fieldName: string): string {
    const resolved = resolveType(prop.type);
    const isPointerField = (prop.optional || resolved.needsPointer) && !prop.omitzeroValue;

    if (prop.type.kind === "reference") {
        const refName = prop.type.name;
        if (compareStructures.has(refName)) {
            // Instance Compare(other) emitted by this same family (generate.mts compareStructures).
            return `${fieldName}.Compare(other.${fieldName})`;
        }
    }

    if (prop.type.kind === "base") {
        switch (prop.type.name) {
            case "string":
            case "URI":
            case "DocumentUri":
                return `string.CompareOrdinal(${fieldName}${isPointerField ? ".Value" : ""}, other.${fieldName}${isPointerField ? ".Value" : ""})`;
            case "integer":
            case "uinteger":
            case "decimal":
                return `${fieldName}.CompareTo(other.${fieldName})`;
        }
    }

    throw new Error(`Cannot generate Compare for ${structName}.${fieldName}: unsupported field type ${JSON.stringify(prop.type)}.`);
}

// ---------------------------------------------------------------------------
// Protocol.Unions.g.cs — one class per OR type
// ---------------------------------------------------------------------------

function emitUnions(skipped: Set<string>): string {
    const out: string[] = [];
    const w = (s = "") => out.push(s);

    emitGeneratorHeader(w);
    w("// OR-type surface: one class per union, naming algorithm ported 1:1 from");
    w("// generate.mts (flattenOrTypes/pluralize/common-prefix/OrNull). Each union carries");
    w("// a generated JsonConverter (Protocol.Json.g.cs); the shared scan helpers live in");
    w("// the hand-written Lsp/UnionSerialization.cs.");
    w();
    w("using System.Collections.Generic;");
    w("using System.Text.Json;");
    w("using System.Text.Json.Serialization;");
    w();
    w("namespace Ts.LsProto;");
    w();
    w("// Union types");
    w();

    for (const [name, members] of typeInfo.unionTypes) {
        if (skipped.has(name)) {
            w(`// skipped (references types temporarily hosted in Ts.Ls subsets): ${name}`);
            w();
            continue;
        }

        const uniqueTypeFields = new Map<string, string>(); // resolved C# type name -> field name
        const uniqueTypeToOriginal = new Map<string, Type>();

        let hasLocations = false;
        const fields: { fieldName: string; csType: string; originalType: Type; }[] = [];
        for (const member of members) {
            const type = resolveType(member.type);
            const memberType = type.name;

            // If this type name already exists in our map, skip it
            if (!uniqueTypeFields.has(memberType)) {
                const fieldName = titleCase(member.name);
                uniqueTypeFields.set(memberType, fieldName);
                uniqueTypeToOriginal.set(memberType, member.type);
                fields.push({ fieldName, csType: memberType, originalType: member.type });
                if (fieldName === "Locations" && memberType === "Location[]") {
                    hasLocations = true;
                }
            }
        }

        w(`[JsonConverter(typeof(${name}Converter))]`);
        // Go lsp.go:75-77 — HasLocations constrains unions exposing *[]Location.
        w(`public sealed partial class ${name}${hasLocations ? " : IHasLocations" : ""}`);
        w("{");
        fields.forEach((field, index) => {
            if (index > 0) w();
            // Already-nullable member spellings (e.g. "JsonElement?" for LSPAny) must not
            // get a second "?" — C# has no nullable-of-nullable.
            const wrapperType = field.csType.endsWith("?") ? field.csType : `${field.csType}?`;
            w(`    public ${wrapperType} ${field.fieldName} { get; set; }`);
        });
        w();

        fields.forEach((field, index) => {
            if (index > 0) w();
            const factoryName = "From" + titleCase(field.fieldName === "String" ? "String" : field.fieldName);
            w(`    public static ${name} ${factoryName}(${field.csType} value) => new() { ${field.fieldName} = value };`);
        });
        w("}");
        w();

        if (hasLocations) {
            w(`public partial class ${name}`);
            w("{");
            w("    public Location[]? GetLocations() => Locations;");
            w("}");
            w();
        }
    }

    return out.join("\n") + "\n";
}

// ---------------------------------------------------------------------------
// Protocol.Json.g.cs — union converters + literal/enum/URI converters + context
// ---------------------------------------------------------------------------

interface UnionEntry {
    fieldName: string;
    typeName: string; // resolved C# type name
    originalType: Type;
}

function decodeExpr(entry: UnionEntry, reader: string): string {
    const t = entry.originalType;
    if (t.kind === "base") {
        switch (t.name) {
            case "integer":
                return `${reader}.GetInt32()`;
            case "uinteger":
                return `${reader}.GetUInt32()`;
            case "decimal":
                return `${reader}.GetDouble()`;
            case "string":
                return `${reader}.GetString()!`;
            case "boolean":
                return `${reader}.GetBoolean()`;
            case "URI":
            case "DocumentUri":
                return `new ${t.name}(${reader}.GetString()!)`;
            default:
                throw new Error("unsupported base in union arm: " + t.name);
        }
    }
    return `JsonSerializer.Deserialize(ref ${reader}, LspJsonContext.Default.${contextAccessorFor(entry.typeName)})`;
}

function writeExpr(entry: UnionEntry, valueExpr: string): string {
    // Union arms are nullable fields; value-typed arms (bool?/int?/uint?/double?, URI?,
    // DocumentUri?) must be unwrapped after the caller's `is not null` guard.
    const t = entry.originalType;
    if (t.kind === "base") {
        switch (t.name) {
            case "integer":
            case "uinteger":
            case "decimal":
                return `writer.WriteNumberValue(${valueExpr}.Value);`;
            case "string":
                return `writer.WriteStringValue(${valueExpr});`;
            case "boolean":
                return `writer.WriteBooleanValue(${valueExpr}.Value);`;
            case "URI":
            case "DocumentUri":
                return `writer.WriteStringValue(${valueExpr}.Value.Value);`;
            default:
                throw new Error("unsupported base in union arm: " + t.name);
        }
    }
    return `JsonSerializer.Serialize(writer, ${valueExpr}, LspJsonContext.Default.${contextAccessorFor(entry.typeName)});`;
}

function emitUnionConverter(name: string, members: { name: string; type: Type; containedNull: boolean; }[]): string[] {
    const out: string[] = [];
    const w = (s = "") => out.push(s);

    const containedNull = members.some(member => member.containedNull);

    // Build unique field entries keyed by resolved type name (port of uniqueTypeFields).
    const uniqueTypeFields = new Map<string, string>();
    const uniqueTypeToOriginal = new Map<string, Type>();
    for (const member of members) {
        const resolved = resolveType(member.type);
        if (!uniqueTypeFields.has(resolved.name)) {
            uniqueTypeFields.set(resolved.name, titleCase(member.name));
            uniqueTypeToOriginal.set(resolved.name, member.type);
        }
    }
    const fieldEntries: UnionEntry[] = [...uniqueTypeFields.entries()].map(([typeName, fieldName]) => ({
        fieldName,
        typeName,
        originalType: uniqueTypeToOriginal.get(typeName)!,
    }));

    // Group entries by expected JSON kind; sort ambiguous groups by required count desc.
    const kindMap = new Map<string, UnionEntry[]>();
    const unknownKindEntries: UnionEntry[] = [];
    for (const entry of fieldEntries) {
        const kind = jsonKindForType(entry.originalType);
        if (!kind) {
            unknownKindEntries.push(entry);
        }
        else {
            if (!kindMap.has(kind)) kindMap.set(kind, []);
            kindMap.get(kind)!.push(entry);
        }
    }
    for (const [, entries] of kindMap) {
        if (entries.length > 1) {
            entries.sort((a, b) => countRequiredFields(b) - countRequiredFields(a));
        }
    }
    {
        const sorted: UnionEntry[] = [];
        const seen = new Set<string>();
        for (const [, entries] of kindMap) {
            for (const entry of entries) {
                sorted.push(entry);
                seen.add(entry.fieldName);
            }
        }
        for (const entry of unknownKindEntries) {
            if (!seen.has(entry.fieldName)) {
                sorted.push(entry);
            }
        }
        fieldEntries.length = 0;
        fieldEntries.push(...sorted);
    }

    const hasUnknownKinds = unknownKindEntries.length > 0;
    const distinctKinds = kindMap.size + (containedNull ? 1 : 0);
    const canDispatch = !hasUnknownKinds && distinctKinds >= 2;
    const allUnambiguous = canDispatch && [...kindMap.values()].every(entries => entries.length === 1);

    w(`public sealed class ${name}Converter : JsonConverter<${name}>`);
    w("{");
    w(`    public override ${name}? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)`);
    w("    {");

    let needsFallbackThrow = false;
    if (canDispatch && allUnambiguous) {
        if (containedNull) {
            w("        if (reader.TokenType == JsonTokenType.Null)");
            w("        {");
            w("            return new " + name + "();");
            w("        }");
            w();
        }
        w("        switch (reader.TokenType)");
        w("        {");
        for (const [kind, entries] of kindMap) {
            const entry = entries[0];
            switch (kind) {
                case "string":
                    w("            case JsonTokenType.String:");
                    break;
                case "number":
                    w("            case JsonTokenType.Number:");
                    break;
                case "object":
                    w("            case JsonTokenType.StartObject:");
                    break;
                case "array":
                    w("            case JsonTokenType.StartArray:");
                    break;
                case "boolean":
                    w("            case JsonTokenType.True:");
                    w("            case JsonTokenType.False:");
                    break;
            }
            if (kind === "boolean") {
                w(`                return new ${name} { ${entry.fieldName} = ${decodeExpr(entry, "reader")} };`);
            }
            else if (entry.originalType.kind === "base") {
                w(`                return new ${name} { ${entry.fieldName} = ${decodeExpr(entry, "reader")} };`);
            }
            else {
                w(`                return new ${name} { ${entry.fieldName} = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.${contextAccessorFor(entry.typeName)}) };`);
            }
        }
        w("            default:");
        w(`                throw new JsonException("invalid ${name}: got " + reader.TokenType.ToString());`);
        w("        }");
    }
    else if (canDispatch) {
        if (containedNull) {
            w("        if (reader.TokenType == JsonTokenType.Null)");
            w("        {");
            w("            return new " + name + "();");
            w("        }");
            w();
        }
        w("        switch (reader.TokenType)");
        w("        {");
        for (const [kind, entries] of kindMap) {
            switch (kind) {
                case "string":
                    w("            case JsonTokenType.String:");
                    break;
                case "number":
                    w("            case JsonTokenType.Number:");
                    break;
                case "object":
                    w("            case JsonTokenType.StartObject:");
                    break;
                case "array":
                    w("            case JsonTokenType.StartArray:");
                    break;
                case "boolean":
                    w("            case JsonTokenType.True:");
                    w("            case JsonTokenType.False:");
                    break;
            }
            if (entries.length === 1) {
                const entry = entries[0];
                w(`                return new ${name} { ${entry.fieldName} = ${decodeExpr(entry, "reader")} };`);
            }
            else {
                // Braced section: `using var` is not allowed directly in a switch section
                // (CS8647), and the buffered `root` must stay in scope for the fallback throw,
                // which also terminates the section (CS0163) when the dispatch can fall through.
                w("            {");
                w("                using var document = JsonDocument.ParseValue(ref reader);");
                w("                var root = document.RootElement;");
                if (emitAmbiguousDispatch(out, name, entries, "                ", false)) {
                    w(`                throw new JsonException("invalid ${name}: " + root.GetRawText());`);
                }
                w("            }");
            }
        }
        w("            default:");
        w(`                throw new JsonException("invalid ${name}: got " + reader.TokenType.ToString());`);
        w("        }");
    }
    else {
        // Fallback: unknown kinds present (e.g. LSPAny), buffer the value and try-each.
        w("        using var document = JsonDocument.ParseValue(ref reader);");
        w("        var root = document.RootElement;");
        if (containedNull) {
            w("        if (root.ValueKind == JsonValueKind.Null)");
            w("        {");
            w("            return new " + name + "();");
            w("        }");
            w();
        }
        needsFallbackThrow = emitAmbiguousDispatch(out, name, fieldEntries, "        ", true);
    }

    if (needsFallbackThrow) {
        // emitAmbiguousDispatch ends with a throw on all paths when exhaustive;
        // C# requires a definite return/throw at method end regardless.
        w(`        throw new JsonException("invalid ${name}: " + root.GetRawText());`);
    }

    w("    }");
    w();
    w(`    public override void Write(Utf8JsonWriter writer, ${name} value, JsonSerializerOptions options)`);
    w("    {");
    for (const [typeName, fieldName] of uniqueTypeFields) {
        const entry: UnionEntry = { fieldName, typeName, originalType: uniqueTypeToOriginal.get(typeName)! };
        w(`        if (value.${fieldName} is not null)`);
        w("        {");
        w("            " + writeExpr(entry, `value.${fieldName}`));
        w("            return;");
        w("        }");
        w();
    }
    if (containedNull) {
        w("        writer.WriteNullValue();");
        w("        return;");
    }
    w(`        throw new JsonException("exactly one element of ${name} should be set");`);
    w("    }");
    w("}");
    w();
    return out;
}

// Emits the generation-time-resolved dispatch chain (discriminator → presence →
// try-each) for a group of same-JSON-kind entries over a JsonElement `root`.
function emitAmbiguousDispatch(
    out: string[],
    name: string,
    entries: UnionEntry[],
    indent: string,
    allowTryEachFinal: boolean,
): boolean {
    // True when the emitted chain can reach the method tail (break-out-of-switch or
    // try-each exhaustion); the caller must then emit the fallback throw, and only then
    // — an unconditional throw after an exhaustive return chain is unreachable (CS0162).
    let mayFallThrough = false;
    const disc = findDiscriminatorField(entries);
    let remaining = entries;
    if (disc) {
        out.push(`${indent}// Discriminator dispatch on "${disc.fieldName}" (generate.mts findDiscriminatorField).`);
        out.push(`${indent}switch (UnionSerialization.JsonObjectRawField(root, ${JSON.stringify(disc.fieldName)}))`);
        out.push(`${indent}{`);
        for (const [value, entry] of disc.mapping) {
            out.push(`${indent}    case ${JSON.stringify(JSON.stringify(value))}:`);
            out.push(`${indent}        return new ${name} { ${entry.fieldName} = root.Deserialize(LspJsonContext.Default.${contextAccessorFor(entry.typeName)}) };`);
        }
        if (disc.unmapped.length > 0) {
            out.push(`${indent}    default:`);
            remaining = disc.unmapped;
            if (remaining.length === 1) {
                const entry = remaining[0];
                out.push(`${indent}        return new ${name} { ${entry.fieldName} = root.Deserialize(LspJsonContext.Default.${contextAccessorFor(entry.typeName)}) };`);
                remaining = [];
            }
        }
        else {
            remaining = [];
            out.push(`${indent}    default:`);
            out.push(`${indent}        break;`);
            mayFallThrough = true;
        }
        out.push(`${indent}}`);
    }

    if (remaining.length === 0) {
        return mayFallThrough;
    }

    const pres = findPresenceDiscriminator(remaining);
    if (pres) {
        const { allChecks, finalUnmapped } = collectAllPresenceChecks(pres);
        out.push(`${indent}// Presence dispatch (generate.mts findPresenceDiscriminator).`);
        out.push(`${indent}switch (UnionSerialization.JsonObjectHasKey(root, ${allChecks.map(c => JSON.stringify(c.jsonFieldName)).join(", ")}))`);
        out.push(`${indent}{`);
        allChecks.forEach((check, i) => {
            out.push(`${indent}    case ${i}: // ${check.jsonFieldName}`);
            out.push(`${indent}        return new ${name} { ${check.entry.fieldName} = root.Deserialize(LspJsonContext.Default.${contextAccessorFor(check.entry.typeName)}) };`);
        });
        out.push(`${indent}    default:`);
        if (finalUnmapped.length === 1) {
            const entry = finalUnmapped[0];
            out.push(`${indent}        return new ${name} { ${entry.fieldName} = root.Deserialize(LspJsonContext.Default.${contextAccessorFor(entry.typeName)}) };`);
        }
        else if (finalUnmapped.length > 1) {
            emitTryEach(out, name, finalUnmapped, indent + "    ");
            mayFallThrough = true;
        }
        else {
            out.push(`${indent}        break;`);
            mayFallThrough = true;
        }
        out.push(`${indent}}`);
        return mayFallThrough;
    }

    if (remaining.length > 0) {
        if (remaining.length === 1) {
            const entry = remaining[0];
            out.push(`${indent}return new ${name} { ${entry.fieldName} = root.Deserialize(LspJsonContext.Default.${contextAccessorFor(entry.typeName)}) };`);
            return false;
        }
        if (allowTryEachFinal || remaining.length > 1) {
            emitTryEach(out, name, remaining, indent);
            return true;
        }
    }
    return true;
}

function emitTryEach(out: string[], name: string, entries: UnionEntry[], indent: string): void {
    out.push(`${indent}// Try-each fallback (generate.mts:2052-2059); order is required-count descending.`);
    for (const entry of entries) {
        out.push(`${indent}try`);
        out.push(`${indent}{`);
        out.push(`${indent}    return new ${name} { ${entry.fieldName} = root.Deserialize(LspJsonContext.Default.${contextAccessorFor(entry.typeName)}) };`);
        out.push(`${indent}}`);
        out.push(`${indent}catch (JsonException)`);
        out.push(`${indent}{`);
        out.push(`${indent}}`);
    }
}

function emitJson(skipped: Set<string>): { text: string; accessors: Map<string, string>; } {
    const out: string[] = [];
    const w = (s = "") => out.push(s);

    emitGeneratorHeader(w);
    w("// Union converters (one JsonConverter<T> per OR type, plan/04 §5.4), literal/enum");
    w("// converters, the Registration externally-tagged converter, and the STJ source-gen");
    w("// context tail. Shared scan helpers are hand-written in Lsp/UnionSerialization.cs.");
    w();
    w("using System;");
    w("using System.Collections.Generic;");
    w("using System.Text.Json;");
    w("using System.Text.Json.Serialization;");
    w("using System.Text.Json.Serialization.Metadata;");
    w();
    w("namespace Ts.LsProto;");
    w();

    // Union converters
    for (const [name, members] of typeInfo.unionTypes) {
        if (skipped.has(name)) continue;
        out.push(...emitUnionConverter(name, members));
    }

    // Literal type converters
    for (const [value, litName] of typeInfo.literalTypes) {
        if (skipped.has(litName)) continue;
        const jsonValue = JSON.stringify(value);
        // Token-level match per literal kind (Utf8JsonReader has no raw-text accessor).
        let readCheck: string;
        let writeStmt: string;
        if (litName.startsWith("IntegerLiteral")) {
            readCheck = `reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var v) || v != ${value}`;
            writeStmt = `writer.WriteNumberValue(${value});`;
        }
        else if (litName.startsWith("BooleanLiteral")) {
            readCheck = `reader.TokenType != JsonTokenType.${value === "true" ? "True" : "False"}`;
            writeStmt = `writer.WriteBooleanValue(${value});`;
        }
        else {
            readCheck = `reader.TokenType != JsonTokenType.String || reader.GetString() != ${jsonValue}`;
            writeStmt = `writer.WriteStringValue(${jsonValue});`;
        }
        w(`public sealed class ${litName}JsonConverter : JsonConverter<${litName}>`);
        w("{");
        w(`    public override ${litName} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)`);
        w("    {");
        w(`        if (${readCheck})`);
        w("        {");
        w(`            throw new JsonException("expected ${litName} value ${jsonValue.replace(/"/g, '\\"')}, got " + reader.TokenType.ToString());`);
        w("        }");
        w("        return default;");
        w("    }");
        w();
        w(`    public override void Write(Utf8JsonWriter writer, ${litName} value, JsonSerializerOptions options)`);
        w("    {");
        w(`        ${writeStmt}`);
        w("    }");
        w("}");
        w();
    }

    // String-enum wrapper converters
    for (const enumeration of model.enumerations) {
        if (enumeration.type.name !== "string" || skipped.has(enumeration.name)) continue;
        const n = enumeration.name;
        w(`public sealed class ${n}JsonConverter : JsonConverter<${n}>`);
        w("{");
        w(`    public override ${n} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)`);
        w("    {");
        w("        if (reader.TokenType != JsonTokenType.String)");
        w("        {");
        w(`            throw new JsonException("${n} must be a string");`);
        w("        }");
        w(`        return new ${n}(reader.GetString()); // A String token is never null unless its value is JSON null.`);
        w("    }");
        w();
        w(`    public override void Write(Utf8JsonWriter writer, ${n} value, JsonSerializerOptions options)`);
        w("    {");
        w("        writer.WriteStringValue(value.Value ?? \"\");");
        w("    }");
        w("}");
        w();
    }

    // DocumentUri + URI converters
    w("public sealed class DocumentUriJsonConverter : JsonConverter<DocumentUri>");
    w("{");
    w("    public override DocumentUri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        if (reader.TokenType != JsonTokenType.String)");
    w("        {");
    w('            throw new JsonException("DocumentUri must be a string");');
    w("        }");
    w("        return new DocumentUri(reader.GetString()!); // A String token is never null unless its value is JSON null.");
    w("    }");
    w();
    w("    public override void Write(Utf8JsonWriter writer, DocumentUri value, JsonSerializerOptions options)");
    w("    {");
    w("        writer.WriteStringValue(value.Value);");
    w("    }");
    w();
    w("    // WorkspaceEdit.Changes uses DocumentUri as a JSON object key (Go map[DocumentUri][]*TextEdit).");
    w("    public override DocumentUri ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        return new DocumentUri(reader.GetString()!); // A PropertyName token is never null.");
    w("    }");
    w();
    w("    public override void WriteAsPropertyName(Utf8JsonWriter writer, DocumentUri value, JsonSerializerOptions options)");
    w("    {");
    w("        writer.WritePropertyName(value.Value);");
    w("    }");
    w("}");
    w();

    w("public sealed class URIJsonConverter : JsonConverter<URI>");
    w("{");
    w("    public override URI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        if (reader.TokenType != JsonTokenType.String)");
    w("        {");
    w('            throw new JsonException("URI must be a string");');
    w("        }");
    w("        return new URI(reader.GetString()!); // A String token is never null unless its value is JSON null.");
    w("    }");
    w();
    w("    public override void Write(Utf8JsonWriter writer, URI value, JsonSerializerOptions options)");
    w("    {");
    w("        writer.WriteStringValue(value.Value);");
    w("    }");
    w("}");
    w();

    // Base-surface converters (Protocol.g.cs declares the types they convert).
    w("public sealed class NullJsonConverter : JsonConverter<Null>");
    w("{");
    w("    public override Null Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        if (reader.TokenType != JsonTokenType.Null)");
    w("        {");
    w('            throw new JsonException("expected null, got " + reader.TokenType.ToString());');
    w("        }");
    w("        reader.Skip();");
    w("        return default;");
    w("    }");
    w();
    w("    public override void Write(Utf8JsonWriter writer, Null value, JsonSerializerOptions options)");
    w("    {");
    w("        writer.WriteNullValue();");
    w("    }");
    w("}");
    w();

    w("public sealed class EmptyObjectJsonConverter : JsonConverter<EmptyObject>");
    w("{");
    w("    public override EmptyObject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        if (reader.TokenType != JsonTokenType.StartObject)");
    w("        {");
    w('            throw new JsonException("expected empty object, got " + reader.TokenType.ToString());');
    w("        }");
    w("        reader.Skip();");
    w("        return default;");
    w("    }");
    w();
    w("    public override void Write(Utf8JsonWriter writer, EmptyObject value, JsonSerializerOptions options)");
    w("    {");
    w("        writer.WriteStartObject();");
    w("        writer.WriteEndObject();");
    w("    }");
    w("}");
    w();

    // Wire shape of Go's [2]uint32: a two-element JSON array.
    w("public sealed class Tuple2UintegerJsonConverter : JsonConverter<Tuple2Uinteger>");
    w("{");
    w("    public override Tuple2Uinteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        if (reader.TokenType != JsonTokenType.StartArray)");
    w("        {");
    w('            throw new JsonException("expected a two-element array, got " + reader.TokenType.ToString());');
    w("        }");
    w("        reader.Read();");
    w("        var item1 = reader.GetUInt32();");
    w("        reader.Read();");
    w("        var item2 = reader.GetUInt32();");
    w("        reader.Read();");
    w("        if (reader.TokenType != JsonTokenType.EndArray)");
    w("        {");
    w('            throw new JsonException("expected a two-element array");');
    w("        }");
    w("        return new Tuple2Uinteger(item1, item2);");
    w("    }");
    w();
    w("    public override void Write(Utf8JsonWriter writer, Tuple2Uinteger value, JsonSerializerOptions options)");
    w("    {");
    w("        writer.WriteStartArray();");
    w("        writer.WriteNumberValue(value.Item1);");
    w("        writer.WriteNumberValue(value.Item2);");
    w("        writer.WriteEndArray();");
    w("    }");
    w("}");
    w();

    // Registration converter (externally-tagged; port of the custom Registration
    // marshal/unmarshal in generate.mts:2393-2537).
    const activeRegistrations = registrationMethods.filter(reg => !registrationMethodSkipped(reg, skipped));
    w("public sealed class RegistrationConverter : JsonConverter<Registration>");
    w("{");
    w("    public override Registration? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    w("    {");
    w("        using var document = JsonDocument.ParseValue(ref reader);");
    w("        var root = document.RootElement;");
    w("        if (root.ValueKind != JsonValueKind.Object)");
    w("        {");
    w('            throw new JsonException("expected object start, but encountered " + root.ValueKind);');
    w("        }");
    w();
    w('        string? id = null;');
    w('        string? method = null;');
    w('        JsonElement rawRegisterOptions = default;');
    w('        var hasRegisterOptions = false;');
    w("        foreach (var property in root.EnumerateObject())");
    w("        {");
    w('            switch (property.Name)');
    w("            {");
    w('                case "id":');
    w("                    id = property.Value.GetString();");
    w("                    break;");
    w('                case "method":');
    w("                    method = property.Value.GetString();");
    w("                    break;");
    w('                case "registerOptions":');
    w("                    rawRegisterOptions = property.Value.Clone();");
    w("                    hasRegisterOptions = true;");
    w("                    break;");
    w("                default:");
    w("                    break;");
    w("            }");
    w("        }");
    w();
    w("        var missingProps = new List<string>();");
    w("        if (id == null)");
    w("        {");
    w('            missingProps.Add("id");');
    w("        }");
    w("        if (method == null)");
    w("        {");
    w('            missingProps.Add("method");');
    w("        }");
    w("        if (missingProps.Count > 0)");
    w("        {");
    w('            throw new JsonException("missing required properties: " + string.Join(", ", missingProps));');
    w("        }");
    w();
    w("        if (!hasRegisterOptions)");
    w("        {");
    w('            throw new JsonException("missing registerOptions for method: " + method);');
    w("        }");
    w();
    w("        var result = new Registration");
    w("        {");
    w("            Id = id!, // non-null: the missing-props check above threw otherwise");
    w("            RegisterOptions = new RegisterOptions(),");
    w("        };");
    w("        switch (method)");
    w("        {");
    for (const reg of activeRegistrations) {
        // `Methods.*` are static readonly (not const), so the case label is the wire string.
        w(`            case ${JSON.stringify(reg.registrationMethod)}: // Methods.${reg.fieldName}`);
        w(`                result.RegisterOptions.${reg.fieldName} = rawRegisterOptions.Deserialize(LspJsonContext.Default.${contextAccessorFor(reg.optionsTypeName)});`);
        w("                break;");
    }
    w("            default:");
    w('                throw new JsonException("unknown registration method: " + method);');
    w("        }");
    w("        return result;");
    w("    }");
    w();
    w("    public override void Write(Utf8JsonWriter writer, Registration value, JsonSerializerOptions options)");
    w("    {");
    w("        if (value.RegisterOptions == null)");
    w("        {");
    w('            throw new JsonException("RegisterOptions must be set");');
    w("        }");
    w();
    w("        // Exactly one arm must be set (Go countNonNil + assertOnlyOne).");
    w("        var count = 0;");
    for (const reg of activeRegistrations) {
        w(`        if (value.RegisterOptions.${reg.fieldName} != null)`);
        w("        {");
        w("            count++;");
        w("        }");
    }
    w("        if (count != 1)");
    w("        {");
    w('            throw new JsonException("exactly one element of RegisterOptions should be set");');
    w("        }");
    w();
    w("        writer.WriteStartObject();");
    w('        writer.WritePropertyName("id");');
    w("        JsonSerializer.Serialize(writer, value.Id, LspJsonContext.Default.String);");
    w('        writer.WritePropertyName("method");');
    w("        var method = \"\";");
    for (const reg of activeRegistrations) {
        w(`        if (value.RegisterOptions.${reg.fieldName} != null)`);
        w("        {");
        w(`            method = Methods.${reg.fieldName}.Value;`);
        w("        }");
    }
    w("        writer.WriteStringValue(method);");
    w('        writer.WritePropertyName("registerOptions");');
    for (const reg of activeRegistrations) {
        w(`        if (value.RegisterOptions.${reg.fieldName} != null)`);
        w("        {");
        w(`            JsonSerializer.Serialize(writer, value.RegisterOptions.${reg.fieldName}, LspJsonContext.Default.${contextAccessorFor(reg.optionsTypeName)});`);
        w("        }");
    }
    w("        writer.WriteEndObject();");
    w("    }");
    w("}");
    w();

    // STJ source-generation context tail (plan/04 §5.5, plan/07 §4). This is the
    // SINGLE LspJsonContext: the former hand-written LspJsonContext.cs is retired so
    // hint names can never collide (STJ CS8785). No GenerationMode pin — the default
    // (metadata + serialization) is required because the generated union converters
    // deserialize through the context's JsonTypeInfo accessors.
    w("[JsonSourceGenerationOptions(");
    w("    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,");
    w("    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]");
    let serializableTypes: string[] = [];
    for (const structure of model.structures) {
        if (!skipped.has(structure.name)) serializableTypes.push(`typeof(${structure.name})`);
    }
    for (const enumeration of model.enumerations) {
        if (!skipped.has(enumeration.name)) serializableTypes.push(`typeof(${enumeration.name})`);
    }
    for (const name of typeInfo.unionTypes.keys()) {
        if (!skipped.has(name)) serializableTypes.push(`typeof(${name})`);
    }
    for (const [, litName] of typeInfo.literalTypes) {
        if (!skipped.has(litName)) serializableTypes.push(`typeof(${litName})`);
    }
    serializableTypes.push("typeof(DocumentUri)");
    serializableTypes.push("typeof(URI)");
    serializableTypes.push("typeof(Null)");
    serializableTypes.push("typeof(NoParams)");
    serializableTypes.push("typeof(EmptyObject)");
    serializableTypes.push("typeof(Tuple2Uinteger)");
    serializableTypes.push("typeof(string)");
    serializableTypes.push("typeof(int)");
    serializableTypes.push("typeof(uint)");
    serializableTypes.push("typeof(double)");
    serializableTypes.push("typeof(bool)");
    serializableTypes.push("typeof(JsonElement)");
    serializableTypes.push("typeof(JsonElement?)");
    serializableTypes.push("typeof(JsonElement?[])");
    serializableTypes.push("typeof(DocumentUri[])");
    serializableTypes.push("typeof(Location[])");
    serializableTypes.push("typeof(TextEdit[])");
    serializableTypes.push("typeof(List<DocumentSymbol>)");
    serializableTypes.push("typeof(DocumentSymbol[])");
    serializableTypes.push("typeof(HashSet<string>)");
    // Array/List members that appear as union arms are decoded via explicit context
    // accessors (contextAccessorFor) inside the generated converters — register them.
    // The Infos registry (Protocol.g.cs) also reaches params/result types through
    // Default.<accessor>; register those types explicitly.
    for (const request of [...model.requests, ...model.notifications]) {
        if (skipped.has(`request:${request.method}`)) continue;
        const req = request as Request;
        if (req.result) {
            const resultType = req.result.kind === "base" && req.result.name === "null"
                ? "Null"
                : resolveType(req.result).name;
            serializableTypes.push(`typeof(${resultType})`);
        }
        if (req.params && !Array.isArray(req.params)) {
            serializableTypes.push(`typeof(${resolveType(req.params).name})`);
        }
    }
    for (const [name, members] of typeInfo.unionTypes) {
        if (skipped.has(name)) continue;
        for (const member of members) {
            const resolved = resolveType(member.type).name;
            if (resolved.endsWith("[]") || resolved.startsWith("List<")) {
                serializableTypes.push(`typeof(${resolved})`);
            }
        }
    }
    // Types still hand-hosted in Lsp/Protocol.cs (skip-list cascade: they reference
    // Ts.Ls-hosted subsets). The retired hand-written context registered them; the
    // generated context must keep exposing them for Lsp/Server.cs and the tests.
    serializableTypes.push("typeof(InitializeParams)");
    serializableTypes.push("typeof(InitializeResult)");
    serializableTypes.push("typeof(ClientCapabilities)");
    serializableTypes.push("typeof(ServerCapabilities)");
    serializableTypes.push("typeof(CompletionItem)");
    serializableTypes.push("typeof(CompletionList)");
    // Pin the accessor name so contextAccessorFor is authoritative for both the
    // registration and every LspJsonContext.Default.<name> use site; relying on
    // STJ's derived names would leave primitives/arrays/nullables unspellable.
    // serializableTypes can contain the same type twice (tail + union arms); dedupe.
    serializableTypes = [...new Set(serializableTypes)];
    const accessorNames = new Map<string, string>();
    for (const type of serializableTypes) {
        const csType = type.startsWith("typeof(") && type.endsWith(")")
            ? type.slice("typeof(".length, -1)
            : null;
        if (csType !== null) {
            const accessor = contextAccessorFor(csType);
            const previous = accessorNames.get(accessor);
            if (previous !== undefined) {
                throw new Error(`duplicate LspJsonContext accessor name "${accessor}" for ${previous} and ${csType}`);
            }
            accessorNames.set(accessor, csType);
            w(`[JsonSerializable(${type}, TypeInfoPropertyName = "${accessor}")]`);
        }
        else {
            w(`[JsonSerializable(${type})]`);
        }
    }
    w("public sealed partial class LspJsonContext : JsonSerializerContext");
    w("{");
    w("}");
    w();

    return { text: out.join("\n") + "\n", accessors: accessorNames };
}

function main() {
    collectTypeDefinitions();
    collectTypeDefinitionsForGeneration();

    const skipped = computeSkipSet();
    const skipNote = [...temporarilyHostedInLs].sort();

    const protocolCs = emitProtocol(skipped, skipNote);
    const unionsCs = emitUnions(skipped);
    const { text: jsonCs, accessors } = emitJson(skipped);

    // Generation-time closure check: every `LspJsonContext.Default.X` referenced by
    // the emitted code must have a [JsonSerializable] registration with the pinned
    // accessor name X — otherwise the C# build fails with CS1061 (or, worse, STJ
    // would throw at runtime for a type reachable only through a converter).
    const usedAccessors = new Map<string, number>();
    for (const [text, source] of [[protocolCs, "Protocol.g.cs"], [unionsCs, "Protocol.Unions.g.cs"], [jsonCs, "Protocol.Json.g.cs"]] as const) {
        for (const match of text.matchAll(/LspJsonContext\.Default\.([A-Za-z0-9_]+)/g)) {
            usedAccessors.set(match[1], (usedAccessors.get(match[1]) ?? 0) + 1);
        }
    }
    const missing = [...usedAccessors.keys()].filter(name => !accessors.has(name)).sort();
    if (missing.length > 0) {
        throw new Error(`LspJsonContext.Default.${missing.join(", LspJsonContext.Default.")} used by generated code but not registered on the context; add typeof(...) registrations to emitJson`);
    }

    fs.mkdirSync(lspOutDir, { recursive: true });
    fs.writeFileSync(path.join(lspOutDir, "Protocol.g.cs"), protocolCs);
    fs.writeFileSync(path.join(lspOutDir, "Protocol.Unions.g.cs"), unionsCs);
    fs.writeFileSync(path.join(lspOutDir, "Protocol.Json.g.cs"), jsonCs);

    console.log(`generate-lsp: wrote Protocol.g.cs (${protocolCs.split("\n").length} lines), Protocol.Unions.g.cs (${unionsCs.split("\n").length} lines), Protocol.Json.g.cs (${jsonCs.split("\n").length} lines)`);
    console.log(`generate-lsp: LspJsonContext registrations: ${accessors.size} (all ${usedAccessors.size} referenced accessors covered)`);
    if (skipNote.length > 0) {
        console.log(`generate-lsp: skipped ${skipped.size} names (Ls-hosted subsets + cascade): ${[...skipped].sort().join(", ")}`);
    }
}

main();

// Ported from tsgo internal/lsp/server.go (stdio lifecycle/dispatch skeleton).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Ts.Lsp;

public sealed class ServerOptions
{
    public Stream In { get; set; } = null!;
    public Stream Out { get; set; } = null!;
    public TextWriter Err { get; set; } = TextWriter.Null;
    public string Cwd { get; set; } = "";
    public global::Ts.Vfs.IFileSystem? FileSystem { get; set; }
    public string DefaultLibraryPath { get; set; } = "";
}

public sealed class Server
{
    private readonly global::Ts.JsonRpc.Reader reader;
    private readonly global::Ts.JsonRpc.Writer writer;
    private readonly TextWriter stderr;
    private readonly global::Ts.Vfs.IFileSystem? baseFileSystem;
    private readonly string defaultLibraryPath;
    private readonly Dictionary<string, OpenDocument> openDocuments = new(StringComparer.Ordinal);

    private string currentDirectory;
    private bool initializeStarted;
    private bool shutdownRequested;
    private global::Ts.Compiler.Program? program;
    private global::Ts.Ls.LanguageService? languageService;

    public Server(ServerOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (options.In is null)
        {
            throw new ArgumentException("In is required", nameof(options));
        }
        if (options.Out is null)
        {
            throw new ArgumentException("Out is required", nameof(options));
        }
        if (string.IsNullOrEmpty(options.Cwd))
        {
            throw new ArgumentException("Cwd is required", nameof(options));
        }

        reader = new global::Ts.JsonRpc.Reader(options.In);
        writer = new global::Ts.JsonRpc.Writer(options.Out);
        stderr = options.Err;
        baseFileSystem = options.FileSystem;
        currentDirectory = global::Ts.TsPath.TsPath.NormalizePath(options.Cwd);
        defaultLibraryPath = string.IsNullOrEmpty(options.DefaultLibraryPath)
            ? global::Ts.TsPath.TsPath.CombinePaths(currentDirectory, "lib")
            : global::Ts.TsPath.TsPath.NormalizePath(options.DefaultLibraryPath);
    }

    // Run mirrors server.go's framed request loop. Clean stdin EOF and the exit
    // notification both end the server normally.
    public int Run(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (data, ok) = reader.Read();
            if (!ok)
            {
                return 0;
            }

            using var message = JsonDocument.Parse(data!); // ok=true asserts a non-null frame body.
            if (HandleMessage(message.RootElement, cancellationToken))
            {
                return shutdownRequested ? 0 : 1;
            }
        }
        return 0;
    }

    private bool HandleMessage(JsonElement root, CancellationToken cancellationToken)
    {
        JsonElement id = default;
        var hasId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out id);
        try
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.ValueKind != JsonValueKind.String ||
                !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal) ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                if (hasId)
                {
                    WriteError(id, global::Ts.JsonRpc.ErrorCodes.CodeInvalidRequest, "Invalid Request");
                }
                return false;
            }

            var method = methodElement.GetString()!; // String ValueKind is asserted above.
            if (shutdownRequested && !string.Equals(method, global::Ts.LsProto.Methods.Exit, StringComparison.Ordinal))
            {
                if (hasId)
                {
                    WriteError(id, global::Ts.JsonRpc.ErrorCodes.CodeInvalidRequest, "Server has shut down");
                }
                return false;
            }

            switch (method)
            {
                case global::Ts.LsProto.Methods.Initialize:
                    HandleInitialize(root, id, hasId);
                    return false;
                case global::Ts.LsProto.Methods.Initialized:
                    return false;
                case global::Ts.LsProto.Methods.Shutdown:
                    shutdownRequested = true;
                    if (hasId)
                    {
                        WriteNullResponse(id);
                    }
                    return false;
                case global::Ts.LsProto.Methods.Exit:
                    return true;
                case global::Ts.LsProto.Methods.TextDocumentDidOpen:
                    HandleDidOpen(ParseDidOpenParams(GetParams(root)), cancellationToken);
                    return false;
                case global::Ts.LsProto.Methods.TextDocumentDidChange:
                    HandleDidChange(ParseDidChangeParams(GetParams(root)), cancellationToken);
                    return false;
                case global::Ts.LsProto.Methods.TextDocumentDidClose:
                    HandleDidClose(ParseDidCloseParams(GetParams(root)));
                    return false;
                case global::Ts.LsProto.Methods.TextDocumentHover:
                    RequireRequest(hasId);
                    HandleHover(id, ParseHoverParams(GetParams(root)), cancellationToken);
                    return false;
                case global::Ts.LsProto.Methods.TextDocumentDefinition:
                    RequireRequest(hasId);
                    HandleDefinition(id, ParseDefinitionParams(GetParams(root)), cancellationToken);
                    return false;
                case global::Ts.LsProto.Methods.TextDocumentCompletion:
                    RequireRequest(hasId);
                    HandleCompletion(id, ParseCompletionParams(GetParams(root)), cancellationToken);
                    return false;
                default:
                    if (hasId)
                    {
                        WriteError(id, global::Ts.JsonRpc.ErrorCodes.CodeMethodNotFound, "Method not found");
                    }
                    return false;
            }
        }
        catch (JsonException exception)
        {
            if (hasId)
            {
                WriteError(id, global::Ts.JsonRpc.ErrorCodes.CodeInvalidParams, exception.Message);
            }
            else
            {
                stderr.WriteLine(exception.Message);
            }
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            if (hasId)
            {
                WriteError(id, global::Ts.JsonRpc.ErrorCodes.CodeInternalError, exception.Message);
            }
            else
            {
                stderr.WriteLine(exception.Message);
            }
            return false;
        }
    }

    private void HandleInitialize(JsonElement root, JsonElement id, bool hasId)
    {
        RequireRequest(hasId);
        if (initializeStarted)
        {
            WriteError(id, global::Ts.JsonRpc.ErrorCodes.CodeInvalidRequest, "initialize may only be sent once");
            return;
        }

        var parameters = ParseInitializeParams(GetParams(root));
        initializeStarted = true;
        if (parameters.RootUri is { } rootUri)
        {
            var requestedDirectory = DocumentUriToFileName(rootUri);
            if (!string.IsNullOrEmpty(requestedDirectory))
            {
                currentDirectory = global::Ts.TsPath.TsPath.NormalizePath(requestedDirectory);
            }
        }

        var result = new global::Ts.LsProto.InitializeResult
        {
            Capabilities = new global::Ts.LsProto.ServerCapabilities
            {
                PositionEncoding = "utf-16",
                TextDocumentSync = global::Ts.LsProto.TextDocumentSyncOptionsOrKind.FromOptions(
                    new global::Ts.LsProto.TextDocumentSyncOptions
                    {
                        OpenClose = true,
                        Change = global::Ts.LsProto.TextDocumentSyncKind.Incremental,
                        Save = global::Ts.LsProto.BooleanOrSaveOptions.FromBoolean(true),
                    }),
                HoverProvider = global::Ts.LsProto.BooleanOrHoverOptions.FromBoolean(true),
                DefinitionProvider = global::Ts.LsProto.BooleanOrDefinitionOptions.FromBoolean(true),
                CompletionProvider = new global::Ts.LsProto.CompletionOptions
                {
                    TriggerCharacters = [".", "\"", "'", "`", "/", "@", "<", "#", " "],
                    ResolveProvider = false,
                },
            },
            ServerInfo = new global::Ts.LsProto.ServerInfo
            {
                Name = "typescript-go",
                Version = global::Ts.Core.TsVersion.Version(),
            },
        };
        WriteResponse(id, result, global::Ts.LsProto.LspJsonContext.Default.InitializeResult);
    }

    private void HandleDidOpen(global::Ts.LsProto.DidOpenTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var item = parameters.TextDocument!; // lsp:"required"; source-gen required validation enforces presence.
        var uri = item.Uri.Value;
        openDocuments[uri] = new OpenDocument(item.Uri, DocumentUriToFileName(item.Uri), item.LanguageId, item.Version, item.Text);
        RebuildProgram();
        PublishDiagnostics(openDocuments[uri], cancellationToken);
    }

    private void HandleDidChange(global::Ts.LsProto.DidChangeTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var uri = parameters.TextDocument.Uri.Value;
        if (!openDocuments.TryGetValue(uri, out var document))
        {
            throw new InvalidOperationException("document is not open: " + uri);
        }

        var text = document.Text;
        foreach (var change in parameters.ContentChanges)
        {
            if (change.WholeDocument is not null)
            {
                text = change.WholeDocument.Text;
                continue;
            }

            var partial = change.Partial!; // The union converter guarantees exactly one variant.
            var start = PositionToOffset(text, partial.Range.Start);
            var end = PositionToOffset(text, partial.Range.End);
            if (end < start)
            {
                throw new JsonException("content change range end precedes start");
            }
            text = text.Remove(start, end - start).Insert(start, partial.Text);
        }

        var updated = document with { Version = parameters.TextDocument.Version, Text = text };
        openDocuments[uri] = updated;
        RebuildProgram();
        PublishDiagnostics(updated, cancellationToken);
    }

    private void HandleDidClose(global::Ts.LsProto.DidCloseTextDocumentParams parameters)
    {
        EnsureInitialized();
        var uri = parameters.TextDocument.Uri.Value;
        if (openDocuments.Remove(uri, out var document))
        {
            RebuildProgram();
            WriteNotification(global::Ts.LsProto.Methods.TextDocumentPublishDiagnostics, new global::Ts.LsProto.PublishDiagnosticsParams
            {
                Uri = document.Uri,
                Diagnostics = [],
            }, global::Ts.LsProto.LspJsonContext.Default.PublishDiagnosticsParams);
        }
    }

    private void HandleHover(JsonElement id, global::Ts.LsProto.HoverParams parameters, CancellationToken cancellationToken)
    {
        var (document, service) = GetDocumentAndService(parameters.TextDocument.Uri);
        var offset = PositionToOffset(document.Text, parameters.Position);
        var quickInfo = service.GetQuickInfoAtPosition(document.FileName, offset, cancellationToken);
        if (quickInfo is null)
        {
            WriteNullResponse(id);
            return;
        }

        var hover = new global::Ts.LsProto.Hover
        {
            Contents = global::Ts.LsProto.MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings.FromMarkupContent(
                new global::Ts.LsProto.MarkupContent { Kind = "plaintext", Value = quickInfo.DisplayString }),
            Range = TextRangeToRange(document.Text, quickInfo.TextSpan),
        };
        WriteResponse(id, hover, global::Ts.LsProto.LspJsonContext.Default.Hover);
    }

    private void HandleDefinition(JsonElement id, global::Ts.LsProto.DefinitionParams parameters, CancellationToken cancellationToken)
    {
        var (document, service) = GetDocumentAndService(parameters.TextDocument.Uri);
        var offset = PositionToOffset(document.Text, parameters.Position);
        var definitions = service.GetDefinitionAtPosition(document.FileName, offset, cancellationToken);
        var locations = new global::Ts.LsProto.Location[definitions.Length];
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var sourceFile = service.GetProgram().GetSourceFile(definition.FileName)!; // Go unconditionally dereferences a declaration source file.
            locations[index] = new global::Ts.LsProto.Location
            {
                Uri = FileNameToDocumentUri(definition.FileName),
                Range = TextRangeToRange(sourceFile.Text(), definition.TextSpan),
            };
        }
        WriteResponse(id, global::Ts.LsProto.Definition.FromLocations(locations), global::Ts.LsProto.LspJsonContext.Default.Definition);
    }

    private void HandleCompletion(JsonElement id, global::Ts.LsProto.CompletionParams parameters, CancellationToken cancellationToken)
    {
        var (document, service) = GetDocumentAndService(parameters.TextDocument.Uri);
        var offset = PositionToOffset(document.Text, parameters.Position);
        var completion = service.GetCompletionsAtPosition(document.FileName, offset, cancellationToken);
        if (completion is null)
        {
            WriteNullResponse(id);
            return;
        }

        var items = new global::Ts.LsProto.CompletionItem[completion.Entries.Length];
        for (var index = 0; index < completion.Entries.Length; index++)
        {
            var entry = completion.Entries[index];
            items[index] = new global::Ts.LsProto.CompletionItem
            {
                Label = entry.Name,
                Kind = CompletionItemKind(entry.Kind),
                SortText = entry.SortText,
                Preselect = entry.IsRecommended,
            };
        }
        WriteResponse(id, new global::Ts.LsProto.CompletionList
        {
            IsIncomplete = false,
            Items = items,
        }, global::Ts.LsProto.LspJsonContext.Default.CompletionList);
    }

    private void RebuildProgram()
    {
        if (openDocuments.Count == 0)
        {
            program = null;
            languageService = null;
            return;
        }

        global::Ts.Core.LargeStackExecutor.Run(() =>
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var rootNames = new List<string>(openDocuments.Count);
            // map-iter: 已排序 — root file order is made deterministic below.
            foreach (var document in openDocuments.Values)
            {
                files[document.FileName] = document.Text;
                rootNames.Add(document.FileName);
            }
            rootNames.Sort(StringComparer.Ordinal);

            var fs = new OverlayFileSystem(baseFileSystem, files);
            var compilerHost = global::Ts.Compiler.CompilerHosts.NewCompilerHost(
                currentDirectory,
                fs,
                defaultLibraryPath,
                extendedConfigCache: null,
                trace: null);
            var compilerOptions = new global::Ts.Core.CompilerOptions
            {
                NoLib = global::Ts.Core.Tristate.True,
                NoEmit = global::Ts.Core.Tristate.True,
                Module = global::Ts.Core.ModuleKind.CommonJS,
                Types = [],
            };
            var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
                compilerOptions,
                rootNames.ToArray(),
                new global::Ts.TsPath.ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = fs.UseCaseSensitiveFileNames(),
                    CurrentDirectory = currentDirectory,
                });

            program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
            {
                Host = compilerHost,
                Config = config,
                SingleThreaded = global::Ts.Core.Tristate.True,
            });
            languageService = global::Ts.Ls.LanguageService.NewLanguageService(program);
        });
    }

    private void PublishDiagnostics(OpenDocument document, CancellationToken cancellationToken)
    {
        var currentProgram = program!; // RebuildProgram establishes a Program for every open document.
        var sourceFile = currentProgram.GetSourceFile(document.FileName)!; // Open documents are explicit root files.
        var compilerDiagnostics = global::Ts.Core.LargeStackExecutor.Run(() =>
        {
            var syntactic = currentProgram.GetSyntacticDiagnostics(cancellationToken, sourceFile);
            var semantic = currentProgram.GetSemanticDiagnostics(cancellationToken, sourceFile);
            var result = new List<global::Ts.Ast.Diagnostic>(syntactic.Length + semantic.Length);
            result.AddRange(syntactic);
            result.AddRange(semantic);
            return result.ToArray();
        });

        var diagnostics = new global::Ts.LsProto.Diagnostic[compilerDiagnostics.Length];
        for (var index = 0; index < compilerDiagnostics.Length; index++)
        {
            var diagnostic = compilerDiagnostics[index];
            diagnostics[index] = new global::Ts.LsProto.Diagnostic
            {
                Range = TextRangeToRange(document.Text, diagnostic.Loc()),
                Severity = DiagnosticSeverity(diagnostic.Category()),
                Code = global::Ts.LsProto.IntegerOrString.FromInteger(diagnostic.Code()),
                Source = "typescript",
                Message = global::Ts.LsProto.StringOrMarkupContent.FromString(
                    global::Ts.DiagnosticWriter.DiagnosticWriter.FlattenDiagnosticMessage(
                        diagnostic,
                        "\n",
                        global::Ts.Locale.Locale.Default)),
            };
        }

        WriteNotification(global::Ts.LsProto.Methods.TextDocumentPublishDiagnostics, new global::Ts.LsProto.PublishDiagnosticsParams
        {
            Uri = document.Uri,
            Version = document.Version,
            Diagnostics = diagnostics,
        }, global::Ts.LsProto.LspJsonContext.Default.PublishDiagnosticsParams);
    }

    private (OpenDocument Document, global::Ts.Ls.LanguageService Service) GetDocumentAndService(global::Ts.LsProto.DocumentUri uri)
    {
        EnsureInitialized();
        if (!openDocuments.TryGetValue(uri.Value, out var document))
        {
            throw new InvalidOperationException("document is not open: " + uri.Value);
        }
        return (document, languageService!); // An open document and language service are rebuilt atomically.
    }

    private void EnsureInitialized()
    {
        if (!initializeStarted)
        {
            throw new InvalidOperationException("server has not been initialized");
        }
    }

    private static void RequireRequest(bool hasId)
    {
        if (!hasId)
        {
            throw new JsonException("method requires a request id");
        }
    }

    private static JsonElement GetParams(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException("params must be an object");
        }
        return parameters;
    }

    private static global::Ts.LsProto.InitializeParams ParseInitializeParams(JsonElement parameters)
    {
        var processIdElement = GetRequiredProperty(parameters, "processId");
        int? processId = processIdElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when processIdElement.TryGetInt32(out var value) => value,
            _ => throw new JsonException("processId must be an integer or null"),
        };
        var rootUriElement = GetRequiredProperty(parameters, "rootUri");
        global::Ts.LsProto.DocumentUri? rootUri = rootUriElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => new global::Ts.LsProto.DocumentUri(rootUriElement.GetString()!),
            _ => throw new JsonException("rootUri must be a string or null"),
        };
        var capabilities = GetRequiredProperty(parameters, "capabilities");
        if (capabilities.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("capabilities must be an object");
        }

        return new global::Ts.LsProto.InitializeParams
        {
            ProcessId = processId,
            RootUri = rootUri,
            Capabilities = new global::Ts.LsProto.ClientCapabilities(),
            Locale = GetOptionalString(parameters, "locale"),
            RootPath = GetOptionalString(parameters, "rootPath"),
            Trace = GetOptionalString(parameters, "trace"),
        };
    }

    private static global::Ts.LsProto.DidOpenTextDocumentParams ParseDidOpenParams(JsonElement parameters)
    {
        var textDocument = GetRequiredObject(parameters, "textDocument");
        return new global::Ts.LsProto.DidOpenTextDocumentParams
        {
            TextDocument = new global::Ts.LsProto.TextDocumentItem
            {
                Uri = GetRequiredDocumentUri(textDocument, "uri"),
                LanguageId = GetRequiredString(textDocument, "languageId"),
                Version = GetRequiredInt32(textDocument, "version"),
                Text = GetRequiredString(textDocument, "text"),
            },
        };
    }

    private static global::Ts.LsProto.DidChangeTextDocumentParams ParseDidChangeParams(JsonElement parameters)
    {
        var textDocument = GetRequiredObject(parameters, "textDocument");
        var contentChanges = GetRequiredProperty(parameters, "contentChanges");
        if (contentChanges.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("contentChanges must be an array");
        }

        var changes = new List<global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument>();
        foreach (var change in contentChanges.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("content change must be an object");
            }
            if (change.TryGetProperty("range", out var range))
            {
                changes.Add(global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument.FromPartial(
                    new global::Ts.LsProto.TextDocumentContentChangePartial
                    {
                        Range = ParseRange(range),
                        RangeLength = GetOptionalUInt32(change, "rangeLength"),
                        Text = GetRequiredString(change, "text"),
                    }));
            }
            else
            {
                changes.Add(global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(
                    new global::Ts.LsProto.TextDocumentContentChangeWholeDocument
                    {
                        Text = GetRequiredString(change, "text"),
                    }));
            }
        }

        return new global::Ts.LsProto.DidChangeTextDocumentParams
        {
            TextDocument = new global::Ts.LsProto.VersionedTextDocumentIdentifier
            {
                Uri = GetRequiredDocumentUri(textDocument, "uri"),
                Version = GetRequiredInt32(textDocument, "version"),
            },
            ContentChanges = changes.ToArray(),
        };
    }

    private static global::Ts.LsProto.DidCloseTextDocumentParams ParseDidCloseParams(JsonElement parameters)
    {
        var textDocument = GetRequiredObject(parameters, "textDocument");
        return new global::Ts.LsProto.DidCloseTextDocumentParams
        {
            TextDocument = new global::Ts.LsProto.TextDocumentIdentifier
            {
                Uri = GetRequiredDocumentUri(textDocument, "uri"),
            },
        };
    }

    private static global::Ts.LsProto.HoverParams ParseHoverParams(JsonElement parameters)
    {
        var (textDocument, position) = ParseTextDocumentPositionParams(parameters);
        return new global::Ts.LsProto.HoverParams
        {
            TextDocument = textDocument,
            Position = position,
            VerbosityLevel = GetOptionalInt32(parameters, "verbosityLevel"),
        };
    }

    private static global::Ts.LsProto.DefinitionParams ParseDefinitionParams(JsonElement parameters)
    {
        var (textDocument, position) = ParseTextDocumentPositionParams(parameters);
        return new global::Ts.LsProto.DefinitionParams
        {
            TextDocument = textDocument,
            Position = position,
        };
    }

    private static global::Ts.LsProto.CompletionParams ParseCompletionParams(JsonElement parameters)
    {
        var (textDocument, position) = ParseTextDocumentPositionParams(parameters);
        global::Ts.LsProto.CompletionContext? context = null;
        if (parameters.TryGetProperty("context", out var contextElement) && contextElement.ValueKind != JsonValueKind.Null)
        {
            if (contextElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("context must be an object");
            }
            context = new global::Ts.LsProto.CompletionContext
            {
                TriggerKind = (global::Ts.LsProto.CompletionTriggerKind)GetRequiredUInt32(contextElement, "triggerKind"),
                TriggerCharacter = GetOptionalString(contextElement, "triggerCharacter"),
            };
        }
        return new global::Ts.LsProto.CompletionParams
        {
            TextDocument = textDocument,
            Position = position,
            Context = context,
        };
    }

    private static (global::Ts.LsProto.TextDocumentIdentifier TextDocument, global::Ts.LsProto.Position Position)
        ParseTextDocumentPositionParams(JsonElement parameters)
    {
        var textDocument = GetRequiredObject(parameters, "textDocument");
        return (
            new global::Ts.LsProto.TextDocumentIdentifier
            {
                Uri = GetRequiredDocumentUri(textDocument, "uri"),
            },
            ParsePosition(GetRequiredProperty(parameters, "position")));
    }

    private static global::Ts.LsProto.Range ParseRange(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("range must be an object");
        }
        return new global::Ts.LsProto.Range
        {
            Start = ParsePosition(GetRequiredProperty(element, "start")),
            End = ParsePosition(GetRequiredProperty(element, "end")),
        };
    }

    private static global::Ts.LsProto.Position ParsePosition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("position must be an object");
        }
        return new global::Ts.LsProto.Position
        {
            Line = GetRequiredUInt32(element, "line"),
            Character = GetRequiredUInt32(element, "character"),
        };
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new JsonException("missing required property: " + name);
        }
        return value;
    }

    private static JsonElement GetRequiredObject(JsonElement element, string name)
    {
        var value = GetRequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(name + " must be an object");
        }
        return value;
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        var value = GetRequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(name + " must be a string");
        }
        return value.GetString()!; // String ValueKind is asserted above.
    }

    private static string? GetOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(name + " must be a string or null");
        }
        return value.GetString();
    }

    private static int GetRequiredInt32(JsonElement element, string name)
    {
        var value = GetRequiredProperty(element, name);
        if (!value.TryGetInt32(out var result))
        {
            throw new JsonException(name + " must be an integer");
        }
        return result;
    }

    private static int? GetOptionalInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!value.TryGetInt32(out var result))
        {
            throw new JsonException(name + " must be an integer or null");
        }
        return result;
    }

    private static uint GetRequiredUInt32(JsonElement element, string name)
    {
        var value = GetRequiredProperty(element, name);
        if (!value.TryGetUInt32(out var result))
        {
            throw new JsonException(name + " must be an unsigned integer");
        }
        return result;
    }

    private static uint? GetOptionalUInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!value.TryGetUInt32(out var result))
        {
            throw new JsonException(name + " must be an unsigned integer or null");
        }
        return result;
    }

    private static global::Ts.LsProto.DocumentUri GetRequiredDocumentUri(JsonElement element, string name)
    {
        return new global::Ts.LsProto.DocumentUri(GetRequiredString(element, name));
    }

    private static int PositionToOffset(string text, global::Ts.LsProto.Position position)
    {
        if (position.Line > int.MaxValue || position.Character > int.MaxValue)
        {
            throw new JsonException("position is outside the document");
        }
        var lineStarts = global::Ts.Core.CoreUtil.ComputeECMALineStarts(text);
        var line = (int)position.Line;
        var character = (int)position.Character;
        if ((uint)line >= (uint)lineStarts.Length)
        {
            throw new JsonException("position line is outside the document");
        }

        var lineStart = lineStarts[line];
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : text.Length;
        while (lineEnd > lineStart && global::Ts.StringUtil.StringUtil.IsLineBreak(text[lineEnd - 1]))
        {
            lineEnd--;
        }
        if (character > lineEnd - lineStart)
        {
            throw new JsonException("position character is outside the line");
        }
        return lineStart + character;
    }

    private static global::Ts.LsProto.Position OffsetToPosition(string text, int offset)
    {
        if ((uint)offset > (uint)text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        var (line, character) = global::Ts.Core.CoreUtil.PositionToLineAndByteOffset(
            offset,
            global::Ts.Core.CoreUtil.ComputeECMALineStarts(text));
        return new global::Ts.LsProto.Position { Line = (uint)line, Character = (uint)character };
    }

    private static global::Ts.LsProto.Range TextRangeToRange(string text, global::Ts.Core.TextRange range)
    {
        return new global::Ts.LsProto.Range
        {
            Start = OffsetToPosition(text, range.Pos),
            End = OffsetToPosition(text, range.End),
        };
    }

    private global::Ts.LsProto.DocumentUri FileNameToDocumentUri(string fileName)
    {
        // map-iter: 序无关 — exact filename identity lookup returns the sole matching URI.
        foreach (var document in openDocuments.Values)
        {
            if (string.Equals(document.FileName, fileName, StringComparison.Ordinal))
            {
                return document.Uri;
            }
        }
        return new global::Ts.LsProto.DocumentUri(new Uri(fileName, UriKind.Absolute).AbsoluteUri);
    }

    private static string DocumentUriToFileName(global::Ts.LsProto.DocumentUri uri)
    {
        var value = uri.Value;
        if (value.StartsWith("file://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            {
                throw new JsonException("invalid file URI: " + value);
            }
            if (!string.IsNullOrEmpty(parsed.Host))
            {
                return global::Ts.TsPath.TsPath.NormalizePath("//" + parsed.Host + parsed.AbsolutePath);
            }
            return global::Ts.TsPath.TsPath.NormalizePath(parsed.LocalPath);
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            throw new JsonException("invalid URI: " + value);
        }
        var scheme = value[..colon];
        var path = value[(colon + 1)..];
        var authority = "ts-nul-authority";
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var slash = path.IndexOf('/', 2);
            if (slash < 0)
            {
                throw new JsonException("invalid URI: " + value);
            }
            authority = path[2..slash];
            path = path[slash..];
        }
        return "^/" + scheme + "/" + authority + "/" + path;
    }

    private static global::Ts.LsProto.CompletionItemKind CompletionItemKind(string kind)
    {
        return kind switch
        {
            "keyword" => global::Ts.LsProto.CompletionItemKind.Keyword,
            "method" => global::Ts.LsProto.CompletionItemKind.Method,
            "function" => global::Ts.LsProto.CompletionItemKind.Function,
            "class" => global::Ts.LsProto.CompletionItemKind.Class,
            "interface" => global::Ts.LsProto.CompletionItemKind.Interface,
            "module" => global::Ts.LsProto.CompletionItemKind.Module,
            "property" => global::Ts.LsProto.CompletionItemKind.Property,
            "const" => global::Ts.LsProto.CompletionItemKind.Constant,
            "var" or "let" => global::Ts.LsProto.CompletionItemKind.Variable,
            _ => global::Ts.LsProto.CompletionItemKind.Text,
        };
    }

    private static global::Ts.LsProto.DiagnosticSeverity DiagnosticSeverity(global::Ts.Diagnostics.Category category)
    {
        return category switch
        {
            global::Ts.Diagnostics.Category.Error => global::Ts.LsProto.DiagnosticSeverity.Error,
            global::Ts.Diagnostics.Category.Warning => global::Ts.LsProto.DiagnosticSeverity.Warning,
            global::Ts.Diagnostics.Category.Suggestion => global::Ts.LsProto.DiagnosticSeverity.Hint,
            global::Ts.Diagnostics.Category.Message => global::Ts.LsProto.DiagnosticSeverity.Information,
            _ => throw new InvalidOperationException("Unhandled diagnostic category"),
        };
    }

    private void WriteNullResponse(JsonElement id)
    {
        WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("id");
            id.WriteTo(json);
            json.WriteNull("result");
            json.WriteEndObject();
        });
    }

    private void WriteResponse<T>(JsonElement id, T result, JsonTypeInfo<T> typeInfo)
    {
        WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("id");
            id.WriteTo(json);
            json.WritePropertyName("result");
            JsonSerializer.Serialize(json, result, typeInfo);
            json.WriteEndObject();
        });
    }

    private void WriteNotification<T>(string method, T parameters, JsonTypeInfo<T> typeInfo)
    {
        WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteString("method", method);
            json.WritePropertyName("params");
            JsonSerializer.Serialize(json, parameters, typeInfo);
            json.WriteEndObject();
        });
    }

    private void WriteError(JsonElement id, int code, string message)
    {
        WriteMessage(json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("id");
            id.WriteTo(json);
            json.WritePropertyName("error");
            json.WriteStartObject();
            json.WriteNumber("code", code);
            json.WriteString("message", message);
            json.WriteEndObject();
            json.WriteEndObject();
        });
    }

    private void WriteMessage(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            write(json);
        }
        writer.Write(buffer.WrittenSpan.ToArray());
    }

    private sealed record OpenDocument(
        global::Ts.LsProto.DocumentUri Uri,
        string FileName,
        string LanguageId,
        int Version,
        string Text);
}

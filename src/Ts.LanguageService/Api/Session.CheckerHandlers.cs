// Ported from tsgo internal/api/session.go (lines 1792-2710: the checker-facing handler
// family — contextual/literal/non-nullable/widened types, assignability, signature
// properties, type/string/print conversions, intrinsics, well-known symbols, JSDoc).
// The former PORT-NOTE(api) seams on this family are closed: checker exports surface via
// Checker.ApiExports.cs (Ts.Compiler) and the binary AST via Api/Encoder (this assembly).
// Go function order preserved.

using System;
using System.Collections.Generic;
using Ts.Checker;

namespace Ts.Api;

public sealed partial class Session
{
    // handleGetContextualType returns the contextual type for a node.
    // (session.go:1794-1818)
    private TypeResponse? HandleGetContextualType(global::Ts.Core.RequestContext ctx, GetContextualTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);
            if (node is null)
            {
                return null;
            }

            global::Ts.Checker.Type? t = setup.Checker.GetContextualType(node, ContextFlags.None);
            if (t is null)
            {
                return null;
            }

            return setup.NewTypeResponse(t);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetBaseTypeOfLiteralType returns the base type of a literal type (e.g. number
    // for 42). (session.go:1820-1834)
    private TypeResponse? HandleGetBaseTypeOfLiteralType(global::Ts.Core.RequestContext ctx, GetBaseTypeOfLiteralTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            // Go: setup.checker.GetBaseTypeOfLiteralType(t) — internal core
            // (Checker.Inference.Deps.cs:40) surfaced via CheckerApiExports.GetBaseTypeOfLiteralType.
            return setup.NewTypeResponse(setup.Checker.GetBaseTypeOfLiteralType(t));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetNonNullableType returns the type with null and undefined removed.
    // (session.go:1836-1850)
    private TypeResponse? HandleGetNonNullableType(global::Ts.Core.RequestContext ctx, GetNonNullableTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            return setup.NewTypeResponse(setup.Checker.GetNonNullableType(t));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeFromTypeNode returns the type for a type node. (session.go:1852-1866)
    private TypeResponse? HandleGetTypeFromTypeNode(global::Ts.Core.RequestContext ctx, GetTypeFromTypeNodeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node node = setup.ResolveNode(@params.Location);

            return setup.NewTypeResponse(setup.Checker.GetTypeFromTypeNode(node));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetWidenedType returns the widened type. (session.go:1868-1882)
    private TypeResponse? HandleGetWidenedType(global::Ts.Core.RequestContext ctx, GetWidenedTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            return setup.NewTypeResponse(setup.Checker.GetWidenedType(t));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetParameterType returns the type of a parameter at a given index in a
    // signature. (session.go:1884-1904)
    private TypeResponse? HandleGetParameterType(global::Ts.Core.RequestContext ctx, GetParameterTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            Signature sig = setup.ResolveSignatureHandle(@params.Signature)!;

            if (@params.Index < 0)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: invalid parameter index");
            }

            // PORT-NOTE(api): Go setup.checker.GetTypeAtPosition(sig, int(params.Index)) —
            // the C# checker publishes the same core under the wrapper name
            // GetTypeAtPositionInSignature (Checker.Services.cs:799).
            return setup.NewTypeResponse(setup.Checker.GetTypeAtPositionInSignature(sig, @params.Index));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleIsArrayLikeType returns whether a type is array-like. (session.go:1906-1919)
    private bool HandleIsArrayLikeType(global::Ts.Core.RequestContext ctx, IsArrayLikeTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            return setup.Checker.IsArrayLikeType(t);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleIsTypeAssignableTo returns whether source is assignable to target.
    // (session.go:1921-1939)
    private bool HandleIsTypeAssignableTo(global::Ts.Core.RequestContext ctx, IsTypeAssignableToParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type source = setup.ResolveTypeHandle(@params.Source)!;
            global::Ts.Checker.Type target = setup.ResolveTypeHandle(@params.Target)!;

            return setup.Checker.IsTypeAssignableTo(source, target);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetShorthandAssignmentValueSymbol returns the value symbol of a shorthand
    // property assignment. (session.go:1941-1960)
    private SymbolResponse? HandleGetShorthandAssignmentValueSymbol(global::Ts.Core.RequestContext ctx, GetTypeAtLocationParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);
            if (node is null)
            {
                return null;
            }

            global::Ts.Ast.Symbol? symbol = setup.Checker.GetShorthandAssignmentValueSymbol(node);
            if (symbol is null)
            {
                return null;
            }

            return setup.NewSymbolResponse(symbol);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeOfSymbolAtLocation returns the narrowed type of a symbol at a specific
    // location. (session.go:1962-1985)
    private TypeResponse? HandleGetTypeOfSymbolAtLocation(global::Ts.Core.RequestContext ctx, GetTypeOfSymbolAtLocationParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;

            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);

            return setup.NewTypeResponse(setup.Checker.GetTypeOfSymbolAtLocation(symbol, node));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleTypeToTypeNode converts a Type to a TypeNode AST and returns it as
    // binary-encoded data. (session.go:1987-2022)
    private object? HandleTypeToTypeNode(global::Ts.Core.RequestContext ctx, TypeToTypeNodeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Ast.Node? enclosingDeclaration = null;
            if (@params.Location is { Value: not null and not "" } locationHandle)
            {
                enclosingDeclaration = setup.Sd.ResolveNodeHandle(setup.Program, locationHandle);
            }

            global::Ts.Ast.Node? typeNode = setup.Checker.TypeToTypeNode(t, enclosingDeclaration, (global::Ts.NodeBuilder.Flags)@params.Flags, null /*idToSymbol*/);
            if (typeNode is null)
            {
                return null;
            }

            // Go: data, _, err := encoder.EncodeNode(typeNode.AsNode(), nil) — the C#
            // encoder signals malformed input by throwing instead of (data, error), so the
            // error-wrap tail has no counterpart (documented deviation).
            (byte[] data, _) = Ts.Api.Encoder.Encoder.EncodeNode(typeNode, null /*sourceFile*/);

            if (useBinaryResponses)
            {
                return new RawBinary(data);
            }
            return new SourceFileResponse
            {
                Data = Convert.ToBase64String(data),
            };
        }
        finally
        {
            setup.Done();
        }
    }

    // session.go:2024-2062.
    private object? HandleSignatureToSignatureDeclaration(global::Ts.Core.RequestContext ctx, SignatureToSignatureDeclarationParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            Signature sig = setup.ResolveSignatureHandle(@params.Signature)!;

            global::Ts.Ast.Node? enclosingDeclaration = null;
            if (@params.Location is { Value: not null and not "" } locationHandle)
            {
                enclosingDeclaration = setup.Sd.ResolveNodeHandle(setup.Program, locationHandle);
            }

            // Go: node := setup.checker.SignatureToSignatureDeclaration(sig, ast.Kind(params.Kind),
            // enclosingDeclaration, nodebuilder.Flags(params.Flags)) (printer.go:423; public
            // wrapper landed in Checker.ApiExports.cs).
            global::Ts.Ast.Node? node = setup.Checker.SignatureToSignatureDeclaration(sig, (global::Ts.Ast.SyntaxKind)@params.Kind, enclosingDeclaration, (global::Ts.NodeBuilder.Flags)@params.Flags);
            if (node is null)
            {
                return null;
            }

            // Go: data, _, err := encoder.EncodeNode(node.AsNode(), nil) — the C# encoder
            // throws instead of returning an error (documented deviation).
            (byte[] data, _) = Ts.Api.Encoder.Encoder.EncodeNode(node, null /*sourceFile*/);

            if (useBinaryResponses)
            {
                return new RawBinary(data);
            }
            return new SourceFileResponse
            {
                Data = Convert.ToBase64String(data),
            };
        }
        finally
        {
            setup.Done();
        }
    }

    // handleTypeToString converts a Type to its string representation.
    // (session.go:2064-2096)
    private string? HandleTypeToString(global::Ts.Core.RequestContext ctx, TypeToTypeNodeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Ast.Node? enclosingDeclaration = null;
            if (@params.Location is { Value: not null and not "" } locationHandle)
            {
                enclosingDeclaration = setup.Sd.ResolveNodeHandle(setup.Program, locationHandle);
            }

            if (@params.Flags != 0)
            {
                return setup.Checker.TypeToStringEx(t, enclosingDeclaration, (TypeFormatFlags)@params.Flags, null /*verbosityContext*/);
            }
            return setup.Checker.TypeToStringEx(t, enclosingDeclaration, TypeFormatFlags.AllowUniqueESSymbolType | TypeFormatFlags.UseAliasDefinedOutsideCurrentScope, null /*verbosityContext*/);
        }
        finally
        {
            setup.Done();
        }
    }

    // handlePrintNode decodes a binary-encoded AST node and prints it to text.
    // (session.go:2098-2113)
    private string HandlePrintNode(global::Ts.Core.RequestContext ctx, PrintNodeParams @params)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(@params.Data);
        }
        catch (FormatException e)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: invalid base64 data: {e.Message}");
        }

        global::Ts.Ast.Node node;
        try
        {
            node = Ts.Api.Encoder.AstDecoder.DecodeNodes(data);
        }
        catch (Exception e)
        {
            // Go: fmt.Errorf("%w: failed to decode AST: %w", ErrClientError, err) — the C#
            // decoder signals malformed payloads with exceptions instead of (node, error)
            // (documented deviation).
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: failed to decode AST: {e.Message}");
        }

        // Go: printer.NewPrinter(printer.PrinterOptions{PreserveSourceNewlines,
        // NeverAsciiEscape, TerminateUnterminatedLiterals}, printer.PrintHandlers{}, nil).
        global::Ts.Printer.Printer p = global::Ts.Printer.Printer.NewPrinter(new global::Ts.Printer.PrinterOptions
        {
            PreserveSourceNewlines = @params.PreserveSourceNewlines,
            NeverAsciiEscape = @params.NeverAsciiEscape,
            TerminateUnterminatedLiterals = @params.TerminateUnterminatedLiterals,
        }, new global::Ts.Printer.PrintHandlers(), null /*emitContext*/);
        return p.Emit(node, null /*sourceFile*/);
    }

    // handleGetIntrinsicType returns an intrinsic type (any, string, number, etc.).
    // (session.go:2115-2130) — arms whose getter is public: getStringType/getNumberType/
    // getUndefinedType (CheckerLsExports.cs:41-47).
    private TypeResponse? HandleGetIntrinsicType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params, System.Func<global::Ts.Checker.Checker, global::Ts.Checker.Type?> getter)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type? t = getter(setup.Checker);
            if (t is null)
            {
                return null;
            }

            return setup.NewTypeResponse(t);
        }
        finally
        {
            setup.Done();
        }
    }

    // The eight intrinsic arms whose getters (Go exports.go getAnyType/getBooleanType/
    // getVoidType/getNullType/getNeverType/getUnknownType/getBigIntType/getESSymbolType)
    // are internal fields of the C# checker port, surfaced via CheckerApiExports /
    // CheckerLsExports extension accessors; each delegates to handleGetIntrinsicType.
    private TypeResponse? HandleGetAnyType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetAnyType());
    }

    private TypeResponse? HandleGetBooleanType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetBooleanType());
    }

    private TypeResponse? HandleGetVoidType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetVoidType());
    }

    private TypeResponse? HandleGetNullType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetNullType());
    }

    private TypeResponse? HandleGetNeverType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetNeverType());
    }

    private TypeResponse? HandleGetUnknownType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetUnknownType());
    }

    private TypeResponse? HandleGetBigIntType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetBigIntType());
    }

    private TypeResponse? HandleGetESSymbolType(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        return HandleGetIntrinsicType(ctx, @params, static c => c.GetESSymbolType());
    }

    // handleGetWellKnownSymbols returns the handle ids of the per-checker singleton
    // symbols (unknown, undefined, arguments) so the client can identify them by id.
    // (session.go:2132-2150)
    private WellKnownSymbolsResponse HandleGetWellKnownSymbols(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            // Go: unknown, _ := setup.sd.registerSymbol(...); undefined, _ := ...; arguments, _ := ...
            // (session.go:2140-2143) — the discarded second value is the canonical project.
            (SymbolId unknown, _) = setup.Sd.RegisterSymbol(setup.Checker.GetUnknownSymbol(), setup.ProjectId);
            (SymbolId undefined, _) = setup.Sd.RegisterSymbol(setup.Checker.GetUndefinedSymbol(), setup.ProjectId);
            (SymbolId arguments, _) = setup.Sd.RegisterSymbol(setup.Checker.GetArgumentsSymbol(), setup.ProjectId);
            return new WellKnownSymbolsResponse
            {
                Unknown = unknown,
                Undefined = undefined,
                Arguments = arguments,
            };
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetWellKnownSignatures returns the handle id of the per-checker unknown
    // signature so the client can identify it by id. (session.go:2152-2164)
    private WellKnownSignaturesResponse HandleGetWellKnownSignatures(global::Ts.Core.RequestContext ctx, GetIntrinsicTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            // Go: setup.sd.registerSignature(setup.projectID, setup.checker.GetUnknownSignature()).
            return new WellKnownSignaturesResponse
            {
                Unknown = setup.Sd.RegisterSignature(setup.ProjectId, setup.Checker.GetUnknownSignature()),
            };
        }
        finally
        {
            setup.Done();
        }
    }

    // handleIsContextSensitive returns whether a node is context-sensitive.
    // (session.go:2166-2183)
    private bool HandleIsContextSensitive(global::Ts.Core.RequestContext ctx, GetContextualTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);
            if (node is null)
            {
                return false;
            }

            // Go: setup.checker.IsContextSensitive(node) — internal core
            // (Checker.ContextSensitive.cs:27) surfaced via CheckerApiExports.IsContextSensitive.
            return setup.Checker.IsContextSensitive(node);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetReturnTypeOfSignature returns the return type of a signature.
    // (session.go:2185-2199)
    private TypeResponse? HandleGetReturnTypeOfSignature(global::Ts.Core.RequestContext ctx, CheckerSignatureParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            Signature sig = setup.ResolveSignatureHandle(@params.Signature)!;

            return setup.NewTypeResponse(setup.Checker.GetReturnTypeOfSignature(sig));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetRestTypeOfSignature returns the rest type of a signature.
    // (session.go:2201-2214)
    private TypeResponse? HandleGetRestTypeOfSignature(global::Ts.Core.RequestContext ctx, CheckerSignatureParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            Signature sig = setup.ResolveSignatureHandle(@params.Signature)!;

            // Go: setup.checker.GetRestTypeOfSignature(sig) (exports.go:297; checker.go:9607
            // core ported into Checker.ApiExports.cs).
            return setup.NewTypeResponse(setup.Checker.GetRestTypeOfSignature(sig));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypePredicateOfSignature returns the type predicate of a signature.
    // (session.go:2216-2248)
    private TypePredicateResponse? HandleGetTypePredicateOfSignature(global::Ts.Core.RequestContext ctx, CheckerSignatureParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            Signature sig = setup.ResolveSignatureHandle(@params.Signature)!;

            TypePredicate? pred = setup.Checker.GetTypePredicateOfSignature(sig);
            if (pred is null)
            {
                return null;
            }

            var resp = new TypePredicateResponse
            {
                Kind = (int)pred.Kind(),
                ParameterIndex = pred.ParameterIndex(),
                ParameterName = pred.ParameterName(),
            };
            global::Ts.Checker.Type? predType = pred.Type();
            if (predType is not null)
            {
                resp.Type = setup.NewTypeResponse(predType);
            }

            return resp;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleIsArrayType returns whether a type is Array<T> or ReadonlyArray<T>.
    // (session.go:2250-2263)
    private bool HandleIsArrayType(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            // Go: setup.checker.IsArrayType(t) — internal core (Checker.TypeNodes.cs:716)
            // surfaced via CheckerApiExports.IsArrayType.
            return setup.Checker.IsArrayType(t);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleIsTupleType returns whether a type is a tuple type. (session.go:2265-2277)
    private bool HandleIsTupleType(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            // Go: package-level checker.IsTupleType(t); the C# Type hosts the same core as
            // an instance method (Types.cs:211).
            return t.IsTupleType();
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetBaseTypes returns the base types of an interface/class type.
    // (session.go:2279-2304)
    private TypeResponse?[]? HandleGetBaseTypes(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Checker.Type[] baseTypes = setup.Checker.GetBaseTypes(t);
            if (baseTypes.Length == 0)
            {
                return null;
            }

            var results = new TypeResponse?[baseTypes.Length];
            for (int i = 0; i < baseTypes.Length; i++)
            {
                results[i] = setup.NewTypeResponse(baseTypes[i]);
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetPropertiesOfType returns the properties of a type. (session.go:2306-2331)
    private SymbolResponse?[]? HandleGetPropertiesOfType(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Ast.Symbol[] props = setup.Checker.GetPropertiesOfType(t);
            if (props.Length == 0)
            {
                return null;
            }

            var results = new SymbolResponse?[props.Length];
            for (int i = 0; i < props.Length; i++)
            {
                results[i] = setup.NewSymbolResponse(props[i]);
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetApparentType returns the apparent type of a type. (session.go:2333-2347)
    private TypeResponse? HandleGetApparentType(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            return setup.NewTypeResponse(setup.Checker.GetApparentType(t));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetIndexInfosOfType returns the index infos of a type. (session.go:2349-2380)
    private IndexInfoResponse?[]? HandleGetIndexInfosOfType(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Checker.IndexInfo[] infos = setup.Checker.GetIndexInfosOfType(t);
            if (infos.Length == 0)
            {
                return null;
            }

            var results = new IndexInfoResponse?[infos.Length];
            for (int i = 0; i < infos.Length; i++)
            {
                global::Ts.Checker.IndexInfo info = infos[i];
                // Go: KeyType: *setup.newTypeResponse(info.KeyType()) — the dereference
                // panics on nil in Go; `!` mirrors that (Key/ValueType are never nil for a
                // real index info).
                var resp = new IndexInfoResponse
                {
                    KeyType = setup.NewTypeResponse(info.KeyType())!,
                    ValueType = setup.NewTypeResponse(info.ValueType())!,
                    IsReadonly = info.IsReadonly(),
                };
                if (info.Declaration() is not null)
                {
                    resp.Declaration = setup.Sd.NodeHandleFrom(info.Declaration()!);
                }
                results[i] = resp;
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetConstraintOfTypeParameter returns the constraint of a type parameter.
    // (session.go:2382-2396)
    private TypeResponse? HandleGetConstraintOfTypeParameter(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Checker.Type? constraint = setup.Checker.GetConstraintOfTypeParameter(t);
            if (constraint is null)
            {
                return null;
            }

            return setup.NewTypeResponse(constraint);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetBaseConstraintOfType returns the base constraint of an instantiable type.
    // (session.go:2398-2412)
    private TypeResponse? HandleGetBaseConstraintOfType(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Checker.Type? constraint = setup.Checker.GetBaseConstraintOfType(t);
            if (constraint is null)
            {
                return null;
            }

            return setup.NewTypeResponse(constraint);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetPropertyOfType returns a named property symbol of a type.
    // (session.go:2414-2432)
    private SymbolResponse? HandleGetPropertyOfType(global::Ts.Core.RequestContext ctx, GetPropertyOfTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Ast.Symbol? prop = setup.Checker.GetPropertyOfType(t, @params.Name);
            if (prop is null)
            {
                return null;
            }

            return setup.NewSymbolResponse(prop);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetConstantValue returns the constant value of an enum member or const enum
    // access. (session.go:2434-2454)
    private object? HandleGetConstantValue(global::Ts.Core.RequestContext ctx, CheckerNodeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);
            if (node is null)
            {
                return null;
            }

            return ProtoFunctions.LiteralValueToJson(setup.Checker.GetConstantValue(node));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetSignatureFromDeclaration returns the signature of a function-like
    // declaration. (session.go:2456-2471)
    private SignatureResponse? HandleGetSignatureFromDeclaration(global::Ts.Core.RequestContext ctx, CheckerNodeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node node = setup.ResolveNode(@params.Location);

            return setup.NewSignatureResponse(setup.Checker.GetSignatureFromDeclaration(node));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetExportSpecifierLocalTargetSymbol returns the local target symbol of an
    // export specifier. (session.go:2473-2498)
    private SymbolResponse? HandleGetExportSpecifierLocalTargetSymbol(global::Ts.Core.RequestContext ctx, CheckerNodeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);
            if (node is null)
            {
                return null;
            }

            global::Ts.Ast.Symbol? symbol = setup.Checker.GetExportSpecifierLocalTargetSymbol(node);
            if (symbol is null)
            {
                return null;
            }

            return setup.NewSymbolResponse(symbol);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetAliasedSymbol resolves an alias symbol to its target. (session.go:2500-2514)
    private SymbolResponse? HandleGetAliasedSymbol(global::Ts.Core.RequestContext ctx, CheckerSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;

            return setup.NewSymbolResponse(setup.Checker.GetAliasedSymbol(symbol));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetImmediateAliasedSymbol resolves one level of alias indirection.
    // (session.go:2516-2536)
    private SymbolResponse? HandleGetImmediateAliasedSymbol(global::Ts.Core.RequestContext ctx, CheckerSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;
            if (symbol is null)
            {
                return null;
            }

            // Go: setup.checker.GetImmediateAliasedSymbol(symbol) — internal core
            // (Checker.SymbolResolution.cs:2097) surfaced via
            // CheckerFindRefsLsExports.GetImmediateAliasedSymbolEx.
            global::Ts.Ast.Symbol? aliased = setup.Checker.GetImmediateAliasedSymbolEx(symbol);
            if (aliased is null)
            {
                return null;
            }

            return setup.NewSymbolResponse(aliased);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetExportsOfModule returns the resolved exports of a module symbol,
    // including those introduced by `export *` and re-exports. (session.go:2538-2567)
    private SymbolResponse?[]? HandleGetExportsOfModule(global::Ts.Core.RequestContext ctx, CheckerSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;
            if (symbol is null)
            {
                return null;
            }

            Dictionary<string, global::Ts.Ast.Symbol> exports = setup.Checker.GetExportsOfModule(symbol);
            if (exports.Count == 0)
            {
                return null;
            }

            // Go: slices.SortFunc(exports, setup.checker.CompareSymbols) then map to
            // responses (session.go:2552-2565). Go GetExportsOfModule returns the table
            // converted by symbolsToArray (map order), then sorts; the C# core returns the
            // table itself, so iterate values and apply the same sort.
            var sorted = new List<global::Ts.Ast.Symbol>(exports.Count);
            // map-iter: 已排序 —— 迭代结果随后按 checker.CompareSymbols 排序（session.go:2552），顺序确定。
            foreach (global::Ts.Ast.Symbol exp in exports.Values)
            {
                sorted.Add(exp);
            }
            sorted.Sort((s1, s2) => setup.Checker.CompareSymbols(s1, s2));

            var results = new SymbolResponse?[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                results[i] = setup.NewSymbolResponse(sorted[i]);
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetMemberInModuleExports returns an export by name from a module symbol.
    // (session.go:2569-2592)
    private SymbolResponse? HandleGetMemberInModuleExports(global::Ts.Core.RequestContext ctx, GetMemberInModuleExportsParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;
            if (symbol is null)
            {
                return null;
            }

            global::Ts.Ast.Symbol? member = setup.Checker.TryGetMemberInModuleExports(@params.Name, symbol);
            if (member is null)
            {
                return null;
            }

            return setup.NewSymbolResponse(member);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetJSDocTags returns the JSDoc tags of a symbol as structured name/text pairs.
    // (session.go:2594-2627)
    private JSDocTagInfo?[]? HandleGetJSDocTags(global::Ts.Core.RequestContext ctx, CheckerSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;
            if (symbol is null)
            {
                return null;
            }

            // Seams (throws at runtime) until the LS host adapter lands; the tail below is
            // the Go body ported verbatim so it activates with the seam.
            global::Ts.Ls.LanguageService? langSvc = SetupLanguageService(setup.Sd, setup.Program, @params.Project, "");

            // PORT-NOTE(api): Go routes through langSvc.GetSymbolJSDocTags(symbol); the C#
            // LS hosts the same implementation as the static Ts.Ls.JsDocInfo
            // .GetSymbolJSDocTags (Ls/JsDoc.cs:22) with no extra instance state.
            global::Ts.Ls.JSDocTagInfo[] tags = global::Ts.Ls.JsDocInfo.GetSymbolJSDocTags(symbol);
            if (tags.Length == 0)
            {
                return null;
            }
            var results = new JSDocTagInfo?[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                results[i] = new JSDocTagInfo { Name = tags[i].Name, Text = tags[i].Text };
            }
            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetDocumentationComment returns the rendered documentation comment of a symbol
    // as plain text. (session.go:2629-2655)
    private string? HandleGetDocumentationComment(global::Ts.Core.RequestContext ctx, CheckerSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;
            if (symbol is null)
            {
                return null;
            }

            // Seams (throws at runtime) until the LS host adapter lands; the tail below is
            // the Go body ported verbatim so it activates with the seam.
            global::Ts.Ls.LanguageService? langSvc = SetupLanguageService(setup.Sd, setup.Program, @params.Project, "");

            return langSvc!.GetSymbolDocumentationComment(setup.Checker, symbol);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeArguments returns the type arguments of a type reference.
    // (session.go:2657-2682)
    private TypeResponse?[]? HandleGetTypeArguments(global::Ts.Core.RequestContext ctx, CheckerTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            global::Ts.Checker.Type[] typeArgs = setup.Checker.GetTypeArguments(t);
            if (typeArgs.Length == 0)
            {
                return null;
            }

            var results = new TypeResponse?[typeArgs.Length];
            for (int i = 0; i < typeArgs.Length; i++)
            {
                results[i] = setup.NewTypeResponse(typeArgs[i]);
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // session.go:2684-2697.
    private TypeResponse? HandleGetTrueTypeOfConditionalType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.Sd.ResolveTypeHandle(@params.Project, @params.Type)!;

            // Go: setup.checker.GetTrueTypeOfConditionalType(t) (exports.go:184-187;
            // checker.go:24446 core ported into Checker.ApiExports.cs).
            return setup.NewTypeResponse(setup.Checker.GetTrueTypeOfConditionalType(t));
        }
        finally
        {
            setup.Done();
        }
    }

    // session.go:2699-2712.
    private TypeResponse? HandleGetFalseTypeOfConditionalType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.Sd.ResolveTypeHandle(@params.Project, @params.Type)!;

            // Go: setup.checker.GetFalseTypeOfConditionalType(t) (checker.go:24455 core
            // ported into Checker.ApiExports.cs).
            return setup.NewTypeResponse(setup.Checker.GetFalseTypeOfConditionalType(t));
        }
        finally
        {
            setup.Done();
        }
    }
}

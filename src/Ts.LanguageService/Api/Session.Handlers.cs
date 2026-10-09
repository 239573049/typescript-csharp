// Ported from tsgo internal/api/session.go (lines 1129-1777: symbol/type lookup handlers
// and the property-resolver family; lines 2711-2805: computeSnapshotChanges, Close,
// releaseOpenRefs). The checker-side family continues in Session.CheckerHandlers.cs and the
// diagnostics/language-service family in Session.DiagnosticsHandlers.cs, preserving Go
// function order.
//
// PORT-NOTE (positions): Go converts client UTF-16 positions to UTF-8 byte offsets via
// sourceFile.GetPositionMap().UTF16ToUTF8 before AST navigation; the C# AST works in
// UTF-16 code units natively (plan/01 §UTF-16 与 plan/08 垫片是仅有的例外面), so the
// client position is used as-is at every former conversion site (registered deviation
// in the api wave report).

using System;
using System.Collections.Generic;
using Ts.Checker;

namespace Ts.Api;

public sealed partial class Session
{
    // handleGetSymbolAtPosition returns the symbol at a position in a file.
    // (session.go:1129-1154)
    private SymbolResponse? HandleGetSymbolAtPosition(global::Ts.Core.RequestContext ctx, GetSymbolAtPositionParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.SourceFile? sourceFile = setup.Program.GetSourceFile(@params.File.ToFileName());
            if (sourceFile is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {@params.File}");
            }

            // Go: positionMap.UTF16ToUTF8(int(params.Position)) — identity in C# (header note).
            global::Ts.Ast.Node node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, (int)@params.Position);
            if (node is null)
            {
                return null;
            }

            global::Ts.Ast.Symbol? symbol = setup.Checker.GetSymbolAtLocation(node);
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

    // handleGetSymbolsAtPositions returns symbols at multiple positions in a file.
    // (session.go:1156-1181)
    private SymbolResponse?[] HandleGetSymbolsAtPositions(global::Ts.Core.RequestContext ctx, GetSymbolsAtPositionsParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.SourceFile? sourceFile = setup.Program.GetSourceFile(@params.File.ToFileName());
            if (sourceFile is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {@params.File}");
            }

            var results = new SymbolResponse?[@params.Positions.Length];
            for (int i = 0; i < @params.Positions.Length; i++)
            {
                // Go: positionMap.UTF16ToUTF8(int(pos)) — identity in C# (header note).
                global::Ts.Ast.Node node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, (int)@params.Positions[i]);
                if (node is null)
                {
                    continue;
                }
                global::Ts.Ast.Symbol? symbol = setup.Checker.GetSymbolAtLocation(node);
                if (symbol is not null)
                {
                    results[i] = setup.NewSymbolResponse(symbol);
                }
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetSymbolAtLocation returns the symbol at a node location.
    // (session.go:1183-1204)
    private SymbolResponse? HandleGetSymbolAtLocation(global::Ts.Core.RequestContext ctx, GetSymbolAtLocationParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Location);
            if (node is null)
            {
                return null;
            }

            global::Ts.Ast.Symbol? symbol = setup.Checker.GetSymbolAtLocation(node);
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

    // handleGetSymbolsAtLocations returns symbols at multiple node locations.
    // (session.go:1206-1231)
    private SymbolResponse?[] HandleGetSymbolsAtLocations(global::Ts.Core.RequestContext ctx, GetSymbolsAtLocationsParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            var results = new SymbolResponse?[@params.Locations.Length];
            for (int i = 0; i < @params.Locations.Length; i++)
            {
                global::Ts.Ast.Node? node = setup.Sd.ResolveNodeHandle(setup.Program, @params.Locations[i]);
                if (node is null)
                {
                    continue;
                }
                global::Ts.Ast.Symbol? symbol = setup.Checker.GetSymbolAtLocation(node);
                if (symbol is not null)
                {
                    results[i] = setup.NewSymbolResponse(symbol);
                }
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeOfSymbol returns the type of a symbol. (session.go:1233-1247)
    private TypeResponse? HandleGetTypeOfSymbol(global::Ts.Core.RequestContext ctx, GetTypeOfSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;

            return setup.NewTypeResponse(setup.Checker.GetTypeOfSymbol(symbol));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypesOfSymbols returns the types of multiple symbols. (session.go:1249-1272)
    private TypeResponse?[] HandleGetTypesOfSymbols(global::Ts.Core.RequestContext ctx, GetTypesOfSymbolsParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            var results = new TypeResponse?[@params.Symbols.Length];
            for (int i = 0; i < @params.Symbols.Length; i++)
            {
                global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbols[i])!;
                // resolveSymbolHandle errors on an unresolvable handle and GetTypeOfSymbol
                // never returns nil, so every element resolves to a type (error type at worst).
                results[i] = setup.NewTypeResponse(setup.Checker.GetTypeOfSymbol(symbol));
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetDeclaredTypeOfSymbol returns the declared type of a symbol (e.g. the type
    // alias body for type alias symbols). (session.go:1274-1288)
    private TypeResponse? HandleGetDeclaredTypeOfSymbol(global::Ts.Core.RequestContext ctx, GetTypeOfSymbolParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;

            return setup.NewTypeResponse(setup.Checker.GetDeclaredTypeOfSymbol(symbol));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleResolveName resolves a name to a symbol at a given location. (session.go:1290-1319)
    private SymbolResponse? HandleResolveName(global::Ts.Core.RequestContext ctx, ResolveNameParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            // Resolve location node - either from node handle or from fileName+position
            global::Ts.Ast.Node? location = null;
            if (@params.Location is { Value: not null and not "" } locationHandle)
            {
                location = setup.Sd.ResolveNodeHandle(setup.Program, locationHandle);
            }
            else if (@params.File is not null && @params.Position is not null)
            {
                global::Ts.Ast.SourceFile? sourceFile = setup.Program.GetSourceFile(@params.File.ToFileName());
                if (sourceFile is null)
                {
                    throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {@params.File}");
                }
                // Go: positionMap.UTF16ToUTF8(int(*params.Position)) — identity in C# (header note).
                location = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, (int)@params.Position.Value);
            }

            global::Ts.Ast.Symbol? symbol = setup.Checker.ResolveName(@params.Name, location, (global::Ts.Ast.SymbolFlags)@params.Meaning, @params.ExcludeGlobals);
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

    // handleGetSignaturesOfType returns the call or construct signatures of a type.
    // (session.go:1321-1341)
    private SignatureResponse?[] HandleGetSignaturesOfType(global::Ts.Core.RequestContext ctx, GetSignaturesOfTypeParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Checker.Type t = setup.ResolveTypeHandle(@params.Type)!;

            Signature[] sigs = setup.Checker.GetSignaturesOfType(t, (SignatureKind)@params.Kind);
            var results = new SignatureResponse?[sigs.Length];
            for (int i = 0; i < sigs.Length; i++)
            {
                results[i] = setup.NewSignatureResponse(sigs[i]);
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetResolvedSignature returns the resolved signature of a call-like expression.
    // (session.go:1343-1358)
    private SignatureResponse? HandleGetResolvedSignature(global::Ts.Core.RequestContext ctx, GetResolvedSignatureParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node node = setup.ResolveNode(@params.Location);

            // Go: GetResolvedSignature may return nil and newSignatureResponse(nil) panics
            // in registerSignature — the nil-forgiving pass preserves that contract.
            return setup.NewSignatureResponse(setup.Checker.GetResolvedSignature(node)!);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeAtLocation returns the type at a node location. (session.go:1360-1374)
    private TypeResponse? HandleGetTypeAtLocation(global::Ts.Core.RequestContext ctx, GetTypeAtLocationParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Node node = setup.ResolveNode(@params.Location);

            return setup.NewTypeResponse(setup.Checker.GetTypeAtLocation(node));
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeAtLocations returns types at multiple node locations. (session.go:1376-1397)
    private TypeResponse?[] HandleGetTypesAtLocations(global::Ts.Core.RequestContext ctx, GetTypeAtLocationsParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            var results = new TypeResponse?[@params.Locations.Length];
            for (int i = 0; i < @params.Locations.Length; i++)
            {
                global::Ts.Ast.Node node = setup.ResolveNode(@params.Locations[i]);
                // resolveNodeHandle errors on an unresolvable handle and GetTypeAtLocation
                // never returns nil, so every element resolves to a type (error type at worst).
                results[i] = setup.NewTypeResponse(setup.Checker.GetTypeAtLocation(node));
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypeAtPosition returns the type at a position in a file. (session.go:1399-1426)
    private TypeResponse? HandleGetTypeAtPosition(global::Ts.Core.RequestContext ctx, GetTypeAtPositionParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.SourceFile? sourceFile = setup.Program.GetSourceFile(@params.File.ToFileName());
            if (sourceFile is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {@params.File}");
            }

            // Go: positionMap.UTF16ToUTF8(int(params.Position)) — identity in C# (header note).
            global::Ts.Ast.Node node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, (int)@params.Position);
            if (node is null)
            {
                return null;
            }

            global::Ts.Checker.Type t = setup.Checker.GetTypeAtLocation(node);
            return setup.NewTypeResponse(t);
        }
        finally
        {
            setup.Done();
        }
    }

    // handleGetTypesAtPositions returns types at multiple positions in a file.
    // (session.go:1428-1455)
    private TypeResponse?[] HandleGetTypesAtPositions(global::Ts.Core.RequestContext ctx, GetTypesAtPositionsParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.SourceFile? sourceFile = setup.Program.GetSourceFile(@params.File.ToFileName());
            if (sourceFile is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {@params.File}");
            }

            var results = new TypeResponse?[@params.Positions.Length];
            for (int i = 0; i < @params.Positions.Length; i++)
            {
                // Go: positionMap.UTF16ToUTF8(int(pos)) — identity in C# (header note).
                global::Ts.Ast.Node node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, (int)@params.Positions[i]);
                if (node is null)
                {
                    continue;
                }
                global::Ts.Checker.Type t = setup.Checker.GetTypeAtLocation(node);
                results[i] = setup.NewTypeResponse(t);
            }

            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // session.go:1457-1459.
    private SymbolResponse? HandleGetParentOfSymbol(global::Ts.Core.RequestContext ctx, GetSymbolPropertyParams @params)
    {
        return ResolveSymbolPropertyOfSymbol(@params, static sym => sym.Parent);
    }

    // session.go:1461-1463.
    private SymbolResponse?[]? HandleGetMembersOfSymbol(global::Ts.Core.RequestContext ctx, GetSymbolPropertyParams @params)
    {
        return ResolveSymbolTablePropertyOfSymbol(ctx, @params, static symbol => symbol.Members);
    }

    // session.go:1465-1467.
    private SymbolResponse?[]? HandleGetExportsOfSymbol(global::Ts.Core.RequestContext ctx, GetSymbolPropertyParams @params)
    {
        return ResolveSymbolTablePropertyOfSymbol(ctx, @params, static symbol => symbol.Exports);
    }

    // session.go:1469-1471.
    private SymbolResponse? HandleGetExportSymbolOfSymbol(global::Ts.Core.RequestContext ctx, GetSymbolPropertyParams @params)
    {
        return ResolveSymbolPropertyOfSymbol(@params, static sym => sym.ExportSymbol);
    }

    // session.go:1473-1475.
    private SymbolResponse? HandleGetSymbolOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveSymbolPropertyOfType(@params, static t => t.Symbol);
    }

    // session.go:1477-1479.
    private TypeResponse? HandleGetTargetOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.Target());
    }

    // session.go:1481-1483.
    private TypeResponse? HandleGetFreshTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsLiteralType().FreshType());
    }

    // session.go:1485-1487.
    private TypeResponse? HandleGetRegularTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsLiteralType().RegularType());
    }

    // session.go:1489-1491.
    private TypeResponse?[]? HandleGetTypesOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypeArrayPropertyOfType(@params, static t => t.Types());
    }

    // session.go:1493-1495.
    private TypeResponse?[]? HandleGetTypeParametersOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        // Go: t.AsInterfaceType() nil-deref panics for a non-interface type — same here.
        return ResolveTypeArrayPropertyOfType(@params, static t => t.AsInterfaceType()!.TypeParameters());
    }

    // session.go:1497-1499.
    private TypeResponse?[]? HandleGetOuterTypeParametersOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        // Go: t.AsInterfaceType() nil-deref panics for a non-interface type — same here.
        return ResolveTypeArrayPropertyOfType(@params, static t => t.AsInterfaceType()!.OuterTypeParameters());
    }

    // session.go:1501-1503.
    private TypeResponse?[]? HandleGetLocalTypeParametersOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        // Go: t.AsInterfaceType() nil-deref panics for a non-interface type — same here.
        return ResolveTypeArrayPropertyOfType(@params, static t => t.AsInterfaceType()!.LocalTypeParameters());
    }

    // session.go:1505-1512.
    private TypeResponse?[]? HandleGetAliasTypeArgumentsOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypeArrayPropertyOfType(@params, static t => t.Alias?.TypeArguments());
    }

    // session.go:1514-1521.
    private SymbolResponse? HandleGetAliasSymbolOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveSymbolPropertyOfType(@params, static t => t.Alias?.Symbol());
    }

    // session.go:1523-1525.
    private TypeResponse? HandleGetObjectTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsIndexedAccessType().ObjectType());
    }

    // session.go:1527-1529.
    private TypeResponse? HandleGetIndexTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsIndexedAccessType().IndexType());
    }

    // session.go:1531-1533.
    private TypeResponse? HandleGetCheckTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsConditionalType().CheckType());
    }

    // session.go:1535-1537.
    private TypeResponse? HandleGetExtendsTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsConditionalType().ExtendsType());
    }

    // session.go:1539-1541.
    private TypeResponse? HandleGetBaseTypeOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsSubstitutionType().BaseType());
    }

    // session.go:1543-1545.
    private TypeResponse? HandleGetConstraintOfType(global::Ts.Core.RequestContext ctx, GetTypePropertyParams @params)
    {
        return ResolveTypePropertyOfType(@params, static t => t.AsSubstitutionType().SubstConstraint());
    }

    // session.go:1547-1549.
    private TypeResponse?[]? HandleGetTypeParametersOfSignature(global::Ts.Core.RequestContext ctx, GetSignaturePropertyParams @params)
    {
        return ResolveTypeArrayPropertyOfSignature(@params, static signature => signature.TypeParameters());
    }

    // session.go:1551-1553.
    private SymbolResponse?[]? HandleGetParametersOfSignature(global::Ts.Core.RequestContext ctx, GetSignaturePropertyParams @params)
    {
        return ResolveSymbolArrayPropertyOfSignature(@params, static signature => signature.Parameters());
    }

    // session.go:1555-1557.
    private SymbolResponse? HandleGetThisParameterOfSignature(global::Ts.Core.RequestContext ctx, GetSignaturePropertyParams @params)
    {
        return ResolveSymbolPropertyOfSignature(@params, static signature => signature.ThisParameter());
    }

    // session.go:1559-1561.
    private SignatureResponse? HandleGetTargetOfSignature(global::Ts.Core.RequestContext ctx, GetSignaturePropertyParams @params)
    {
        return ResolveSignaturePropertyOfSignature(@params, static signature => signature.Target());
    }

    // resolveTypePropertyOfType resolves a type property of type `Type` and returns a type
    // response. (session.go:1564-1586)
    private TypeResponse? ResolveTypePropertyOfType(GetTypePropertyParams @params, System.Func<global::Ts.Checker.Type, global::Ts.Checker.Type?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Checker.Type t = sd.ResolveTypeHandle(@params.Project, @params.Type)!;

        global::Ts.Checker.Type? result = getter(t);
        if (result is null)
        {
            return null;
        }

        return sd.NewTypeResponse(@params.Project, result);
    }

    // resolveTypeArrayPropertyOfType resolves a type property of an array of types and
    // returns an array of type responses. (session.go:1588-1611)
    private TypeResponse?[]? ResolveTypeArrayPropertyOfType(GetTypePropertyParams @params, System.Func<global::Ts.Checker.Type, global::Ts.Checker.Type[]?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Checker.Type t = sd.ResolveTypeHandle(@params.Project, @params.Type)!;

        global::Ts.Checker.Type[]? types = getter(t);
        if (types is not { Length: > 0 })
        {
            return null;
        }

        var results = new TypeResponse?[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            results[i] = sd.NewTypeResponse(@params.Project, types[i]);
        }
        return results;
    }

    // resolveSymbolPropertyOfType resolves a type property of type `Symbol` and returns a
    // symbol response. (session.go:1613-1631)
    private SymbolResponse? ResolveSymbolPropertyOfType(GetTypePropertyParams @params, System.Func<global::Ts.Checker.Type, global::Ts.Ast.Symbol?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Checker.Type t = sd.ResolveTypeHandle(@params.Project, @params.Type)!;

        global::Ts.Ast.Symbol? result = getter(t);
        if (result is null)
        {
            return null;
        }
        return sd.NewSymbolResponse(result, @params.Project);
    }

    // resolveSymbolPropertyOfSymbol resolves a symbol property of type `Symbol` and returns
    // a symbol response. (session.go:1633-1650)
    private SymbolResponse? ResolveSymbolPropertyOfSymbol(GetSymbolPropertyParams @params, System.Func<global::Ts.Ast.Symbol, global::Ts.Ast.Symbol?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Ast.Symbol symbol = sd.ResolveSymbolHandle(@params.Symbol)!;

        global::Ts.Ast.Symbol? result = getter(symbol);
        if (result is null)
        {
            return null;
        }
        return sd.NewSymbolResponse(result, @params.Project);
    }

    // resolveSymbolTablePropertyOfSymbol resolves a symbol property of type `SymbolTable`
    // and returns an array of symbol responses. Results are sorted using the checker's
    // canonical symbol ordering so that API consumers receive a stable, deterministic
    // order instead of Go's randomized map iteration order.
    // (session.go:1652-1696)
    private SymbolResponse?[]? ResolveSymbolTablePropertyOfSymbol(global::Ts.Core.RequestContext ctx, GetSymbolPropertyParams @params, System.Func<global::Ts.Ast.Symbol, Dictionary<string, global::Ts.Ast.Symbol>?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Ast.Symbol symbol = sd.ResolveSymbolHandle(@params.Symbol)!;

        Dictionary<string, global::Ts.Ast.Symbol>? symbolTable = getter(symbol);
        int count = symbolTable?.Count ?? 0;
        if (count == 0)
        {
            return null;
        }
        if (count == 1)
        {
            // map-iter: 序无关 —— 单元素表取值与迭代序无关。
            foreach (global::Ts.Ast.Symbol sub in symbolTable!.Values)
            {
                return [sd.NewSymbolResponse(sub, @params.Project)];
            }
        }

        // More than one symbol, need a checker to sort (Go: slices.SortFunc with
        // setup.checker.CompareSymbols).
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            var symbols = new List<global::Ts.Ast.Symbol>(symbolTable!.Count);
            // map-iter: 已排序 —— Go map 迭代结果随后经 slices.SortFunc(symbols,
            // checker.CompareSymbols) 排序（session.go:1676-1678），顺序确定。
            foreach (global::Ts.Ast.Symbol sub in symbolTable.Values)
            {
                symbols.Add(sub);
            }
            symbols.Sort((s1, s2) => setup.Checker.CompareSymbols(s1, s2));

            var results = new SymbolResponse?[symbols.Count];
            for (int i = 0; i < symbols.Count; i++)
            {
                results[i] = setup.NewSymbolResponse(symbols[i]);
            }
            return results;
        }
        finally
        {
            setup.Done();
        }
    }

    // resolveSymbolArrayPropertyOfSignature resolves a signature property of an array of
    // symbols and returns an array of symbol responses. (session.go:1698-1719)
    private SymbolResponse?[]? ResolveSymbolArrayPropertyOfSignature(GetSignaturePropertyParams @params, System.Func<Signature, global::Ts.Ast.Symbol[]> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        Signature sig = sd.ResolveSignatureHandle(@params.Project, @params.Signature)!;

        global::Ts.Ast.Symbol[] symbols = getter(sig);
        if (symbols.Length == 0)
        {
            return null;
        }

        var results = new SymbolResponse?[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            results[i] = sd.NewSymbolResponse(symbols[i], @params.Project);
        }
        return results;
    }

    // resolveSymbolPropertyOfSignature resolves a signature property of type `Symbol` and
    // returns a symbol response. (session.go:1721-1738)
    private SymbolResponse? ResolveSymbolPropertyOfSignature(GetSignaturePropertyParams @params, System.Func<Signature, global::Ts.Ast.Symbol?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        Signature sig = sd.ResolveSignatureHandle(@params.Project, @params.Signature)!;

        global::Ts.Ast.Symbol? result = getter(sig);
        if (result is null)
        {
            return null;
        }
        return sd.NewSymbolResponse(result, @params.Project);
    }

    // resolveTypeArrayPropertyOfSignature resolves a signature property of an array of
    // types and returns an array of type responses. (session.go:1740-1761)
    private TypeResponse?[]? ResolveTypeArrayPropertyOfSignature(GetSignaturePropertyParams @params, System.Func<Signature, global::Ts.Checker.Type[]?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        Signature sig = sd.ResolveSignatureHandle(@params.Project, @params.Signature)!;

        global::Ts.Checker.Type[]? types = getter(sig);
        if (types is not { Length: > 0 })
        {
            return null;
        }

        var results = new TypeResponse?[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            results[i] = sd.NewTypeResponse(@params.Project, types[i]);
        }
        return results;
    }

    // resolveSignaturePropertyOfSignature resolves a signature property of type `Signature`
    // and returns a signature response. (session.go:1763-1779)
    private SignatureResponse? ResolveSignaturePropertyOfSignature(GetSignaturePropertyParams @params, System.Func<Signature, Signature?> getter)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        Signature sig = sd.ResolveSignatureHandle(@params.Project, @params.Signature)!;

        Signature? result = getter(sig);
        if (result is null)
        {
            return null;
        }
        return sd.NewSignatureResponse(@params.Project, result);
    }

    // computeSnapshotChanges computes the per-project source file differences between
    // two snapshots. It uses DiffOrderedMaps on projects to find changed/removed projects,
    // then DiffMaps on FilesByPath for each changed project to collect file-level changes.
    // (session.go:2713-2763)
    private static SnapshotChanges ComputeSnapshotChanges(global::Ts.Project.Snapshot prev, global::Ts.Project.Snapshot next)
    {
        global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, global::Ts.Project.Project> prevProjects = prev.ProjectCollection.ProjectsByPath();
        global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, global::Ts.Project.Project> nextProjects = next.ProjectCollection.ProjectsByPath();

        var changes = new SnapshotChanges();

        global::Ts.Collections.OrderedMap.DiffOrderedMaps(
            prevProjects, nextProjects,
            // onAdded: new project — nothing to retain from previous snapshot.
            static (_, _) => { },
            // onRemoved: project removed entirely.
            (global::Ts.TsPath.Path _, global::Ts.Project.Project oldProj) =>
            {
                changes.RemovedProjects = [.. changes.RemovedProjects ?? [], HandleFunctions.ProjectHandle(oldProj)];
            },
            // onModified: project changed, diff its files.
            (global::Ts.TsPath.Path _, global::Ts.Project.Project oldProj, global::Ts.Project.Project newProj) =>
            {
                if (ReferenceEquals(oldProj.GetProgram(), newProj.GetProgram()))
                {
                    return;
                }
                Dictionary<global::Ts.TsPath.Path, global::Ts.Ast.SourceFile>? oldFiles = oldProj.GetProgram()?.FilesByPath();
                Dictionary<global::Ts.TsPath.Path, global::Ts.Ast.SourceFile>? newFiles = newProj.GetProgram()?.FilesByPath();
                var projectChanges = new ProjectFileChanges();
                // map-iter: 序无关 —— Go 侧 core.DiffMaps 迭代 Go map（随机序），
                // DeletedFiles/ChangedFiles 的列表顺序在 Go 侧本身就是非确定的，
                // 消费端必须与序无关；C# 以 Dictionary 插入序确定性输出（偏差登记）。
                global::Ts.Core.CoreUtil.DiffMaps(
                    oldFiles, newFiles,
                    null, // onAdded: new file in project, not a change.
                    (path, _) => projectChanges.DeletedFiles = [.. projectChanges.DeletedFiles ?? [], path],
                    (path, _, _) => projectChanges.ChangedFiles = [.. projectChanges.ChangedFiles ?? [], path]);
                if ((projectChanges.ChangedFiles?.Length ?? 0) > 0 || (projectChanges.DeletedFiles?.Length ?? 0) > 0)
                {
                    changes.ChangedProjects ??= [];
                    changes.ChangedProjects[HandleFunctions.ProjectHandle(newProj).Value] = projectChanges;
                }
            });

        return changes;
    }

    // Close closes the session and releases all active snapshots,
    // regardless of their ref counts. (session.go:2765-2777)
    public void Close()
    {
        ReleaseOpenRefs();

        snapshotsMu.EnterWriteLock();
        try
        {
            // map-iter: 序无关 —— Go 侧 map 迭代序随机；逐项 Deref 后统一 delete，
            // 结果与迭代序无关（Go 在迭代中 delete，C# 以 Clear 等价实现）。
            foreach (KeyValuePair<SnapshotId, SnapshotData> entry in snapshots)
            {
                entry.Value.Snapshot.Deref(projectSession);
            }
            snapshots.Clear();
        }
        finally
        {
            snapshotsMu.ExitWriteLock();
        }
    }

    // releaseOpenRefs releases every project and file ref this session is holding open
    // in the project session. This keeps the API's ref counts balanced when an API
    // session is shut down while sharing a longer-lived project session (e.g. one
    // backing an LSP server), so API-opened projects and files aren't leaked. Only
    // refs the session currently holds are closed, so it never over-releases.
    // (session.go:2779-2807)
    private void ReleaseOpenRefs()
    {
        lock (updateMu)
        {
            if (global::Ts.Collections.SetExtensions.Len(openProjects) == 0 && global::Ts.Collections.SetExtensions.Len(openFiles) == 0)
            {
                return;
            }

            // PORT-NOTE(api): init-only properties (Project/Snapshot.cs:350-360) force
            // batched construction; Go assigns the struct fields (session.go:2779-2786).
            var apiRequest = new global::Ts.Project.Snapshot.APISnapshotRequest
            {
                CloseProjects = global::Ts.Collections.SetExtensions.Len(openProjects) > 0 ? global::Ts.Collections.SetExtensions.Clone(openProjects) : null,
                CloseFiles = global::Ts.Collections.SetExtensions.Len(openFiles) > 0 ? global::Ts.Collections.SetExtensions.Clone(openFiles) : null,
            };
            // Go: s.projectSession.APIUpdate(context.Background(), ...) — Go's APIUpdate
            // returns a ref'd snapshot even on error and this function derefs it before
            // checking err. The C# extension signals errors by throwing instead (no
            // snapshot escapes); releasing on its error path is owned by the project
            // wave — registered deviation in the api wave report.
            global::Ts.Project.Snapshot snapshot;
            try
            {
                snapshot = projectSession.APIUpdate(default(global::Ts.Core.RequestContext), new global::Ts.Project.FileChangeSummary(), apiRequest);
            }
            catch (System.Exception)
            {
                return;
            }
            // APIUpdate returns a ref'd snapshot; always release it.
            snapshot.Deref(projectSession);

            global::Ts.Collections.SetExtensions.Clear(openProjects);
            global::Ts.Collections.SetExtensions.Clear(openFiles);
        }
    }
}

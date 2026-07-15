// Ported from tsgo internal/checker/checker.go: the global-symbol-merge subsystem consumed by
// initializeChecker (mergeSymbolTable 14035 / mergeSymbol 14072 / reportMergeSymbolError 14127 /
// getFirstDeclaration 14206 / recordMergedSymbol 14298 / mergeGlobalSymbol 1386 /
// addUndefinedToGlobalsOrErrorOnRedeclaration). plan/03 §4.2 #10. Faithful per-function translation:
// same function order as Go, same branch structure; Go comments and `// !!!` markers preserved.
//
// §1.5: Binder statics are `global::Ts.Binder.Binder.X`-qualified; the no-collision hosts
// (AstUtilities/CoreUtil) are used directly. The conflict-reporting path (reportMergeSymbolError +
// addDuplicateDeclarationError*) is deferred to the diagnostics-detail wave and stubbed; it is reached
// only on duplicate-identifier merges, not on clean lib global merging.

using Ts.Ast;
using Ts.Core;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:14022
    internal SymbolTable? CombineSymbolTables(SymbolTable? first, SymbolTable? second)
    {
        if (first.Len() == 0)
        {
            return second;
        }
        if (second.Len() == 0)
        {
            return first;
        }
        var combined = new SymbolTable();
        MergeSymbolTable(combined, first!, false, null);
        MergeSymbolTable(combined, second!, false, null);
        return combined;
    }

    // checker.go:14035
    internal void MergeSymbolTable(SymbolTable target, SymbolTable source, bool unidirectional, Symbol? mergedParent)
    {
        // map-iter: 序无关 — each source entry is merged into target keyed by id; iteration order does not
        // affect the merged result (targets are keyed, conflicts are deterministic per key).
        foreach (var (id, sourceSymbol) in source)
        {
            var targetSymbol = target.GetValueOrDefault(id);
            Symbol merged;
            if (targetSymbol != null)
            {
                merged = MergeSymbol(targetSymbol, sourceSymbol, unidirectional);
            }
            else
            {
                merged = GetMergedSymbol(sourceSymbol)!;
            }
            if (mergedParent != null && targetSymbol != null)
            {
                // If a merge was performed on the target symbol, set its parent to the merged parent that initiated the merge
                // of its exports. Otherwise, `merged` came only from `sourceSymbol` and can keep its parent.
                if ((merged.Flags & SymbolFlags.Transient) != 0)
                {
                    merged.Parent = mergedParent;
                }
            }
            target[id] = merged;
        }
    }

    // checker.go:14072
    internal Symbol MergeSymbol(Symbol target, Symbol source, bool unidirectional)
    {
        if ((target.Flags & GetExcludedSymbolFlags(source.Flags)) == 0 || ((source.Flags | target.Flags) & SymbolFlags.Assignment) != 0)
        {
            if (source == target)
            {
                // This can happen when an export assigned namespace exports something also erroneously exported at the top level
                return target;
            }
            if ((target.Flags & SymbolFlags.Transient) == 0)
            {
                var resolvedTarget = ResolveSymbol(target)!;
                if (resolvedTarget == unknownSymbol)
                {
                    return source;
                }
                if ((resolvedTarget.Flags & GetExcludedSymbolFlags(source.Flags)) == 0 || ((source.Flags | resolvedTarget.Flags) & SymbolFlags.Assignment) != 0)
                {
                    target = CloneSymbol(resolvedTarget);
                }
                else
                {
                    ReportMergeSymbolError(target, source);
                    return source;
                }
            }
            // Javascript static-property-assignment declarations always merge, even though they are also values
            if ((source.Flags & SymbolFlags.ValueModule) != 0 && (target.Flags & SymbolFlags.ValueModule) != 0 && (target.Flags & SymbolFlags.ConstEnumOnlyModule) != 0 && (source.Flags & SymbolFlags.ConstEnumOnlyModule) == 0)
            {
                // reset flag when merging instantiated module into value module that has only const enums
                target.Flags &= ~SymbolFlags.ConstEnumOnlyModule;
            }
            var sourceFlags = source.Flags;
            if ((target.Flags & SymbolFlags.ConstEnumOnlyModule) == 0)
            {
                sourceFlags &= ~SymbolFlags.ConstEnumOnlyModule;
            }
            target.Flags |= sourceFlags;
            if (source.ValueDeclaration != null)
            {
                global::Ts.Binder.Binder.SetValueDeclaration(target, source.ValueDeclaration);
            }
            target.Declarations = CoreUtil.Concatenate(target.Declarations, source.Declarations);
            if (source.Members != null)
            {
                MergeSymbolTable(AstUtilities.GetSymbolTable(ref target.Members), source.Members, unidirectional, null);
            }
            if (source.Exports != null)
            {
                MergeSymbolTable(AstUtilities.GetSymbolTable(ref target.Exports), source.Exports, unidirectional, target);
            }
            if (!unidirectional)
            {
                RecordMergedSymbol(target, source);
            }
        }
        else if ((target.Flags & SymbolFlags.NamespaceModule) != 0)
        {
            // Do not report an error when merging `var globalThis` with the built-in `globalThis`,
            // as we will already report a "Declaration name conflicts..." error, and this error
            // won't make much sense.
            if (target != globalThisSymbol)
            {
                Error(AstUtilities.GetNameOfDeclaration(GetFirstDeclaration(source)), Diag.Cannot_augment_module_0_with_value_exports_because_it_resolves_to_a_non_module_entity, SymbolToString(target));
            }
        }
        else
        {
            ReportMergeSymbolError(target, source);
        }
        return target;
    }

    // checker.go:14206
    internal static Node? GetFirstDeclaration(Symbol symbol)
    {
        if (symbol.Declarations is { Length: > 0 })
        {
            return symbol.Declarations[0];
        }
        return null;
    }

    // checker.go:14298
    internal void RecordMergedSymbol(Symbol target, Symbol source)
    {
        mergedSymbols[source] = target;
    }

    // checker.go:1386
    internal void MergeGlobalSymbol(Symbol symbol)
    {
        var globalSymbol = globals.GetValueOrDefault(symbol.Name);
        Symbol merged;
        if (globalSymbol != null)
        {
            merged = MergeSymbol(globalSymbol, symbol, false /*unidirectional*/);
        }
        else
        {
            merged = GetMergedSymbol(symbol)!;
        }
        globals[symbol.Name] = merged;
    }

    // checker.go:1247 — addUndefinedToGlobalsOrErrorOnRedeclaration
    internal void AddUndefinedToGlobalsOrErrorOnRedeclaration()
    {
        var name = undefinedSymbol.Name;
        var targetSymbol = globals.GetValueOrDefault(name);
        if (targetSymbol != null)
        {
            foreach (var declaration in targetSymbol.Declarations.OrEmpty())
            {
                if (!AstUtilities.IsTypeDeclaration(declaration))
                {
                    AddDiagnostic(CreateDiagnosticForNode(declaration, Diag.Declaration_name_conflicts_with_built_in_global_identifier_0, name));
                }
            }
        }
        else
        {
            globals[name] = undefinedSymbol;
        }
    }

    // checker.go — reportMergeSymbolError.
    internal void ReportMergeSymbolError(Symbol target, Symbol source)
    {
        var isEitherEnum = (target.Flags & SymbolFlags.Enum) != 0 || (source.Flags & SymbolFlags.Enum) != 0;
        var isEitherBlockScoped = (target.Flags & SymbolFlags.BlockScopedVariable) != 0 || (source.Flags & SymbolFlags.BlockScopedVariable) != 0;
        global::Ts.Diagnostics.Message message;
        if (isEitherEnum)
        {
            message = Diag.Enum_declarations_can_only_merge_with_namespace_or_other_enum_declarations;
        }
        else if (isEitherBlockScoped)
        {
            message = Diag.Cannot_redeclare_block_scoped_variable_0;
        }
        else
        {
            message = Diag.Duplicate_identifier_0;
        }
        var sourceSymbolFile = AstUtilities.GetSourceFileOfNode(GetFirstDeclaration(source));
        var targetSymbolFile = AstUtilities.GetSourceFileOfNode(GetFirstDeclaration(target));
        var isSourcePlainJS = AstUtilities.IsPlainJSFile(sourceSymbolFile, compilerOptions.CheckJs);
        var isTargetPlainJS = AstUtilities.IsPlainJSFile(targetSymbolFile, compilerOptions.CheckJs);
        var symbolName = SymbolToString(source);
        if (!isSourcePlainJS)
        {
            AddDuplicateDeclarationErrorsForSymbols(source, message, symbolName, target);
        }
        if (!isTargetPlainJS)
        {
            AddDuplicateDeclarationErrorsForSymbols(target, message, symbolName, source);
        }
    }

    // checker.go — addDuplicateDeclarationErrorsForSymbols.
    private void AddDuplicateDeclarationErrorsForSymbols(Symbol target, global::Ts.Diagnostics.Message message, string symbolName, Symbol source)
    {
        foreach (var node in target.Declarations ?? [])
        {
            AddDuplicateDeclarationError(node, message, symbolName, source.Declarations);
        }
    }

    // checker.go — getAdjustedNodeForError (free func).
    private static Node GetAdjustedNodeForError(Node node) => AstUtilities.GetNameOfDeclaration(node) ?? node;

    // checker.go — addDuplicateDeclarationError.
    private void AddDuplicateDeclarationError(Node node, global::Ts.Diagnostics.Message message, string symbolName, Node[]? relatedNodes)
    {
        var errorNode = GetAdjustedNodeForError(node);
        var err = LookupOrIssueError(errorNode, message, symbolName);
        foreach (var relatedNode in relatedNodes ?? [])
        {
            var adjustedNode = GetAdjustedNodeForError(relatedNode);
            if (adjustedNode == errorNode)
            {
                continue;
            }
            var leadingMessage = CreateDiagnosticForNode(adjustedNode, Diag.X_0_was_also_declared_here, symbolName);
            var followOnMessage = CreateDiagnosticForNode(adjustedNode, Diag.X_and_here);
            var related = err.RelatedInformation();
            if ((related?.Length ?? 0) >= 5 || CoreUtil.Some(related, d => DiagnosticsCollection.CompareDiagnostics(d, followOnMessage) == 0 || DiagnosticsCollection.CompareDiagnostics(d, leadingMessage) == 0))
            {
                continue;
            }
            if ((related?.Length ?? 0) == 0)
            {
                err.AddRelatedInfo(leadingMessage);
            }
            else
            {
                err.AddRelatedInfo(followOnMessage);
            }
        }
    }

    // checker.go — lookupOrIssueError.
    private Diagnostic LookupOrIssueError(Node location, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var diagnostic = NewDiagnosticForNode(location, message, args);
        var existing = diagnostics.Lookup(diagnostic);
        if (existing != null)
        {
            return existing;
        }
        AddDiagnostic(diagnostic);
        return diagnostic;
    }
}

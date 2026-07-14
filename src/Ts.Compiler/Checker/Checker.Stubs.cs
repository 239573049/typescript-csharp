// Checker logic stubs (skeleton wave). plan/03 §4.2 #4–#22: the check*/get*/resolve*/instantiate*
// surface is generated as NotImplementedException stubs (real signatures, `throw`) to be filled by
// later clustered waves — mirroring the parser bring-up. This file carries (a) the stubs actually
// REFERENCED by the real skeleton (construction / keys / mappers / name resolver), which must exist
// for the assembly to compile, and (b) a representative set of the section entry-point functions
// anchored per §4.2 partial. It is not an exhaustive transcription of every checker.go function.

using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ===== Referenced by the real skeleton (must exist to compile) =====

    // Free functions used by Types.cs / TypeMapper.cs / Keys.cs.
    // isTupleType (checker.go:23397) implemented in Checker.TypeNodes.cs (#15).
    // utilities.go:990
    internal static bool IsThisTypeParameter(Type type) =>
        (type.flags & TypeFlags.TypeParameter) != 0 && type.AsTypeParameter().isThisType;

    // clearCachedInferences (inference.go:1609) implemented in Checker.Inference.cs (inference wave).

    // NameResolver closures (createNameResolver / createNameResolverForSuggestion).
    // GetSymbolOfDeclaration is now REAL in Checker.SymbolResolution.cs (checker.go:14316).

    // checker.go:13925-13929
    internal Diagnostic Error(Node? location, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var diagnostic = NewDiagnosticForNode(location, message, args);
        AddDiagnostic(diagnostic);
        return diagnostic;
    }

    // checker.go:2162-2180
    internal Symbol? GetSymbol(SymbolTable? symbols, string name, SymbolFlags meaning)
    {
        if ((meaning & SymbolFlags.All) != 0)
        {
            var symbol = GetMergedSymbol(symbols?.GetValueOrDefault(name));
            if (symbol != null)
            {
                if ((symbol.Flags & meaning) != 0)
                {
                    return symbol;
                }
                if ((symbol.Flags & SymbolFlags.Alias) != 0)
                {
                    var targetFlags = GetSymbolFlags(symbol);
                    // `targetFlags` will be `SymbolFlags.All` if an error occurred in alias resolution; this avoids cascading errors
                    if ((targetFlags & meaning) != 0)
                    {
                        return symbol;
                    }
                }
            }
        }
        // return nil if we can't find a symbol
        return null;
    }

    // GetSuggestionForSymbolNameLookup / CheckAndReportErrorForInvalidInitializer are now REAL in
    // Checker.NameLookup.cs (name-resolution wave).

    // checker.go:1564-1601
    internal void OnFailedToResolveSymbol(Node? errorLocation, string name, SymbolFlags meaning, global::Ts.Diagnostics.Message? nameNotFoundMessage)
    {
        if (errorLocation != null && (errorLocation.Parent!.Kind == SyntaxKind.JSDocLink ||
            CheckAndReportErrorForMissingPrefix(errorLocation, name) ||
            CheckAndReportErrorForExtendingInterface(errorLocation) ||
            CheckAndReportErrorForUsingTypeAsNamespace(errorLocation, name, meaning) ||
            CheckAndReportErrorForExportingPrimitiveType(errorLocation, name) ||
            CheckAndReportErrorForUsingNamespaceAsTypeOrValue(errorLocation, name, meaning) ||
            CheckAndReportErrorForUsingTypeAsValue(errorLocation, name, meaning) ||
            CheckAndReportErrorForUsingValueAsType(errorLocation, name, meaning)))
        {
            return;
        }
        var declarationName = name;
        if (errorLocation != null && NodeTests.IsIdentifier(errorLocation) && errorLocation.Text() == name)
        {
            declarationName = global::Ts.Scanner.Scanner.DeclarationNameToString(errorLocation); // use escape sequences from original file
        }
        // Report missing lib first
        var suggestedLib = GetSuggestedLibForNonExistentName(name);
        if (suggestedLib != "")
        {
            Error(errorLocation, nameNotFoundMessage!, declarationName, suggestedLib);
            return;
        }
        // Then spelling suggestions
        var suggestion = GetSuggestedSymbolForNonexistentSymbol(errorLocation, name, meaning);
        if (suggestion != null && !(suggestion.ValueDeclaration != null && AstUtilities.IsAmbientModule(suggestion.ValueDeclaration) && AstUtilities.IsGlobalScopeAugmentation(suggestion.ValueDeclaration)))
        {
            var suggestionName = SymbolToString(suggestion);
            var isUncheckedJS = IsUncheckedJSSuggestion(errorLocation, suggestion, false /*excludeClasses*/);
            var message = meaning == SymbolFlags.Namespace ? global::Ts.Diagnostics.Diagnostics.Cannot_find_namespace_0_Did_you_mean_1 :
                isUncheckedJS ? global::Ts.Diagnostics.Diagnostics.Could_not_find_name_0_Did_you_mean_1 : global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Did_you_mean_1;
            var diagnostic = NewDiagnosticForNode(errorLocation, message, declarationName, suggestionName);
            if (suggestion.ValueDeclaration != null)
            {
                diagnostic.AddRelatedInfo(NewDiagnosticForNode(suggestion.ValueDeclaration, global::Ts.Diagnostics.Diagnostics.X_0_is_declared_here, suggestionName));
            }
            AddErrorOrSuggestion(!isUncheckedJS, diagnostic);
            return;
        }
        // And then fall back to unspecified "not found"
        Error(errorLocation, nameNotFoundMessage!, declarationName);
    }

    // OnSuccessfullyResolvedSymbol and onFailedToResolveSymbol's diagnostic-refinement helpers
    // (checkAndReportErrorForMissingPrefix / *ForExtendingInterface / *ForUsingTypeAsNamespace /
    // *ForExportingPrimitiveType / *ForUsingNamespaceAsTypeOrValue / *ForUsingTypeAsValue /
    // *ForUsingValueAsType) are now REAL in Checker.NameLookup.cs (name-resolution wave).

    // checker.go:1719-1725 — lib-feature suggestion for a non-existent name (feature table in
    // Checker.Utilities.FeatureMap.cs).
    internal string GetSuggestedLibForNonExistentName(string name)
    {
        var featureMap = GetFeatureMap();
        if (featureMap.TryGetValue(name, out var typeFeatures))
        {
            return typeFeatures[0].Lib;
        }
        return "";
    }

    // checker.go:1727-1729
    internal Symbol? GetSuggestedSymbolForNonexistentSymbol(Node? location, string outerName, SymbolFlags meaning) =>
        resolveNameForSymbolSuggestion(location, outerName, meaning, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);

    // Constructor closures.
    // compareSymbolsWorker (utilities.go:343) implemented in Checker.TypeOps.Support.cs (needed by CompareTypes).
    internal int CompareSymbolChainsWorker(Symbol[] a, Symbol[] b) => throw new NotImplementedException("compareSymbolChainsWorker");

    // evaluateEntity (23932), getUniqueLiteralTypeForTypeParameter (24853), restrictiveMapperWorker
    // (24433), permissiveMapperWorker (24440) implemented in Checker.TypeNodes.cs (#15).
    // IsEmptyAnonymousObjectType (26365) implemented in Checker.UnionIntersection.cs (#17).
    // IsStringIndexSignatureOnlyTypeWorker (27231) implemented in Checker.IndexedAccess.cs (#18).
    // couldContainTypeVariablesWorker / instantiateType / getTypeArguments implemented by the #14
    // Instantiation wave (Checker.Instantiation.cs).
    // markNodeAssignmentsWorker: owned by flow.go — real body in Checker.Flow.cs.

    // compareTypesAssignableWorker owned by the relater wave (Checker.Relater.cs).

    // Mapper / key back-references.
    // inferFromIntraExpressionSites (inference.go:1268) / getInferredType (inference.go:1283)
    // implemented in Checker.Inference.cs (inference wave).

    // MapType is now REAL in Checker.Widening.cs (checker.go:25469).

    // ===== #3 construction (deep global resolution, reached only via memoized resolvers) =====

    // checker.go:1210-1229
    internal Type GetGlobalType(string name, int arity, bool reportErrors)
    {
        var symbol = GetGlobalSymbol(name, SymbolFlags.Type, reportErrors ? global::Ts.Diagnostics.Diagnostics.Cannot_find_global_type_0 : null);
        if (symbol != null)
        {
            if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
            {
                var t = GetDeclaredTypeOfSymbol(symbol);
                if (t.AsInterfaceType()!.TypeParameters().Len() == arity)
                {
                    return t;
                }
                if (reportErrors)
                {
                    Error(GetGlobalTypeDeclaration(symbol), global::Ts.Diagnostics.Diagnostics.Global_type_0_must_have_1_type_parameter_s, AstUtilities.SymbolName(symbol), arity);
                }
            }
            else if (reportErrors)
            {
                Error(GetGlobalTypeDeclaration(symbol), global::Ts.Diagnostics.Diagnostics.Global_type_0_must_be_a_class_or_interface_type, AstUtilities.SymbolName(symbol));
            }
        }
        if (arity != 0)
        {
            return emptyGenericType!;
        }
        return emptyObjectType!;
    }

    // checker.go:1231-1239
    private static Node? GetGlobalTypeDeclaration(Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations!)
        {
            switch (declaration.Kind)
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.TypeAliasDeclaration:
                    return declaration;
            }
        }
        return null;
    }

    // checker.go:1185-1200
    internal Symbol? GetGlobalTypeAliasSymbol(string name, int arity, bool reportErrors)
    {
        var symbol = GetGlobalSymbol(name, SymbolFlags.TypeAlias, reportErrors ? global::Ts.Diagnostics.Diagnostics.Cannot_find_global_type_0 : null);
        if (symbol == null)
        {
            return null;
        }
        // Resolve the declared type of the symbol. This resolves type parameters for the type alias so that we can check arity.
        GetDeclaredTypeOfSymbol(symbol);
        if (typeAliasLinks.Get(symbol).typeParameters.Len() != arity)
        {
            if (reportErrors)
            {
                var decl = global::Ts.Core.CoreUtil.Find(symbol.Declarations, NodeTests.IsTypeAliasDeclaration);
                Error(decl, global::Ts.Diagnostics.Diagnostics.Global_type_0_must_have_1_type_parameter_s, AstUtilities.SymbolName(symbol), arity);
            }
            return null;
        }
        return symbol;
    }

    // ===== #5 statements / source elements =====
    // CheckSourceFile / CheckSourceElement implemented in Checker.Statements.cs (#5).

    // ===== #6 unused identifiers =====

    // RegisterForUnusedIdentifiersCheck is now REAL in Checker.Statements.cs (checker.go:7017).

    // ===== #7 expressions =====
    // Dispatch trunk landed in Checker.Expressions.cs; these are the canonical entry wrappers
    // (checker.go:7530 checkExpression, 7490 checkExpressionCached).

    internal Type CheckExpression(Node node) => CheckExpressionCore(node);

    internal Type CheckExpressionCached(Node node) => CheckExpressionCachedEx(node, CheckMode.Normal);

    // ===== #8 operator expressions =====
    // Real bodies landed in Checker.OperatorExpressions.cs (checkBinaryExpression 12293,
    // getResolvedSymbol 13814).

    internal Type CheckBinaryExpression(Node node, CheckMode checkMode) => CheckBinaryExpressionCore(node, checkMode);

    internal Symbol GetResolvedSymbol(Node node) => GetResolvedSymbolCore(node);

    // ===== #9 diagnostics =====

    // checker.go:13875 GetDiagnostics — check the file, then return its collected diagnostics.
    internal Diagnostic[] GetDiagnostics(SourceFile? sourceFile) => GetDiagnosticsWorker(sourceFile!, diagnostics);

    // checker.go:13883 getDiagnostics
    private Diagnostic[] GetDiagnosticsWorker(SourceFile sourceFile, DiagnosticsCollection collection)
    {
        bool checkUnused = compilerOptions.NoUnusedLocals.IsTrue() || compilerOptions.NoUnusedParameters.IsTrue() || ReferenceEquals(collection, suggestionDiagnostics);
        CheckSourceFile(sourceFile, checkUnused);
        if (wasCanceled)
        {
            return [];
        }
        return collection.GetDiagnosticsForFile(sourceFile.FileName());
    }

    // checker.go:13894 GetGlobalDiagnostics
    public Diagnostic[] GetGlobalDiagnostics() => diagnostics.GetGlobalDiagnostics();

    // ===== #10 symbol resolution =====

    // checker.go:14354
    internal Symbol? ResolveSymbol(Symbol? symbol) => ResolveSymbolEx(symbol, false /*dontResolveAlias*/);

    // ===== #11 symbol types =====
    // getDeclaredTypeOfSymbol (checker.go:23578) is in the #15 line-range (22713-24910), implemented
    // in Checker.TypeNodes.cs despite the #11 label above (§4.2 is authoritative).

    // ===== #12 members + #13 signatures =====
    // Implemented in Checker.Members.cs / Checker.Signatures.cs (getPropertiesOfType,
    // getPropertyOfType, getSignaturesOfType, getSignatureFromDeclaration, getReturnTypeOfSignature).

    // ===== #14 instantiation =====
    // instantiateSymbol / getApparentType / getReducedType implemented by the #14 Instantiation wave.

    // ===== #15 type nodes =====
    // getTypeFromTypeNode and family implemented in Checker.TypeNodes.cs.

    // ===== #17 union / intersection =====
    // getIntersectionType (25946) implemented in Checker.UnionIntersection.cs (#17).

    // ===== #18 indexed access =====
    // getIndexType/getIndexTypeEx (26551/26555) implemented in Checker.IndexedAccess.cs (#18).

    // ===== #19 constraints =====
    // getBaseConstraintOfType (27302) implemented in Checker.Constraints.cs (#19).

    // ===== #21 contextual types =====
    // getContextualType and family: real bodies landed in Checker.ContextualType.cs (#21 wave).

    // ===== #22 awaited types =====
    // getAwaitedTypeEx and the full awaited chain: real bodies landed in Checker.AwaitedTypes.cs.
}

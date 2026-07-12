// Ported from tsgo internal/checker/types.go (link data classes) + jsx.go:34-39.
// plan/03 §5.4: all link types are `internal sealed class`, fields internal, no ctor logic,
// field defaults == Go zero values (null / 0 / false). Consumed via Ts.Core.LinkStore<K,V>
// whose Get() lazily `new`s a zero-valued instance.

using Ts.Ast;

namespace Ts.Checker;

// types.go:163-165
internal sealed class SymbolReferenceLinks
{
    internal SymbolFlags referenceKinds;
}

// types.go:169-177
internal sealed class ValueSymbolLinks
{
    internal Type? resolvedType;
    internal Type? writeType;
    internal Symbol? target;
    internal TypeMapper? mapper;
    internal Type? nameType;
    internal Type? containingType;
    internal bool functionOrConstructorChecked;
}

// types.go:181-184
internal sealed class MappedSymbolLinks
{
    internal Type? keyType;
    internal Symbol? syntheticOrigin;
}

// types.go:188-192
internal sealed class DeferredSymbolLinks
{
    internal Type? parent;
    internal Type[]? constituents;
    internal Type[]? writeConstituents;
}

// types.go:196-201
internal sealed class AliasSymbolLinks
{
    internal Symbol? immediateTarget;
    internal Symbol? aliasTarget;
    internal bool referenced;
    internal Node? typeOnlyDeclaration;
}

// types.go:205-209
internal sealed class ModuleSymbolLinks
{
    internal SymbolTable? resolvedExports;
    internal Dictionary<string, Node>? typeOnlyExportStarMap;
    internal bool exportsChecked;
}

// types.go:211-215
internal sealed class ReverseMappedSymbolLinks
{
    internal Type? propertyType;
    internal Type? mappedType;
    internal Type? constraintType;
}

// types.go:219-221
internal sealed class LateBoundLinks
{
    internal Symbol? lateSymbol;
}

// types.go:225-228
internal sealed class ExportTypeLinks
{
    internal Symbol? target;
    internal Node? originatingImport;
}

// types.go:232-237
internal sealed class TypeAliasLinks
{
    internal Type? declaredType;
    internal Type[]? typeParameters;
    internal Dictionary<CacheHashKey, Type>? instantiations; // lazy
    internal bool isConstructorDeclaredProperty;
}

// types.go:241-247
internal sealed class DeclaredTypeLinks
{
    internal Type? declaredType;
    internal bool interfaceChecked;
    internal bool indexSignaturesChecked;
    internal bool typeParametersChecked;
    internal bool enumChecked;
}

// types.go:260-266
internal sealed class SwitchStatementLinks
{
    internal ExhaustiveState exhaustiveState;
    internal bool switchTypesComputed;
    internal bool witnessesComputed;
    internal Type[]? switchTypes;
    internal string[]? witnesses;
}

// types.go:268-272
internal sealed class ArrayLiteralLinks
{
    internal bool indicesComputed;
    internal int firstSpreadIndex; // -1 when computed and none; default 0 matches Go zero value pre-compute
    internal int lastSpreadIndex;
}

// types.go:283 — `type MembersAndExportsLinks [2]ast.SymbolTable` indexed by
// MembersOrExportsResolutionKind (plan/03 §5.3 #18).
internal sealed class MembersAndExportsLinks
{
    private readonly SymbolTable?[] tables = new SymbolTable?[2];

    internal SymbolTable? this[MembersOrExportsResolutionKind kind]
    {
        get => tables[(int)kind];
        set => tables[(int)kind] = value;
    }
}

// types.go:287-290
internal sealed class SpreadLinks
{
    internal Symbol? leftSpread;
    internal Symbol? rightSpread;
}

// types.go:294-296
internal sealed class VarianceLinks
{
    internal VarianceFlags[]? variances;
}

// types.go:312-315
internal sealed class MarkedAssignmentSymbolLinks
{
    internal int lastAssignmentPos; // Go int32
    internal bool hasDefiniteAssignment;
}

// types.go:317-321 — key struct for accessibleChainCache (Node member = Go pointer identity).
internal readonly record struct AccessibleChainCacheKey(bool UseOnlyExternalAliasing, Node? Location, SymbolFlags Meaning);

// types.go:323-327
internal sealed class ContainingSymbolLinks
{
    internal Dictionary<NodeId, Symbol[]>? extendedContainersByFile;
    internal Symbol[]? extendedContainers; // Go `*[]*ast.Symbol`: null = unknown; non-null (possibly empty) = computed
    internal Dictionary<AccessibleChainCacheKey, Symbol[]>? accessibleChainCache;
}

// types.go:362-366
internal sealed class NodeLinks
{
    internal NodeCheckFlags flags;
    internal Ts.Core.Tristate declarationRequiresScopeChange;
    internal bool hasReportedStatementInAmbientContext;
}

// types.go:368-370
internal sealed class SymbolNodeLinks
{
    internal Symbol? resolvedSymbol;
}

// types.go:372-375
internal sealed class TypeNodeLinks
{
    internal Type? resolvedType;
    internal Type[]? outerTypeParameters;
}

// types.go:379-381
internal sealed class EnumMemberLinks
{
    internal Ts.Evaluator.Result value; // Constant value of enum member (value struct)
}

// types.go:385-387
internal sealed class AssertionLinks
{
    internal Type? exprType;
}

// types.go:391-403 — deferredNodes is insertion-ordered (checkDeferredNodes iterates it in
// order; plan/03 §5.3 #26 — determinism-bearing → OrderedSet).
internal sealed class SourceFileLinks
{
    internal bool typeChecked;
    internal bool unusedChecked;
    internal Symbol? externalHelpersModule;
    internal ExternalEmitHelpers requestedExternalEmitHelpers;
    internal readonly Ts.Collections.OrderedSet<Node> deferredNodes = new();
    internal Node[]? identifierCheckNodes;
    internal string localJsxNamespace = "";
    internal string localJsxFragmentNamespace = "";
    internal EntityName? localJsxFactory;
    internal EntityName? localJsxFragmentFactory;
    internal Type? jsxFragmentType;
}

// types.go:407-411
internal sealed class SignatureLinks
{
    internal Signature? resolvedSignature;
    internal Signature? effectsSignature;
    internal Signature? decoratorSignature;
}

// jsx.go:34-39 (plan/03 §5.3 #9).
internal sealed class JsxElementLinks
{
    internal JsxFlags jsxFlags;                        // Flags for the JSX element
    internal Type? resolvedJsxElementAttributesType;   // Resolved element attributes type of a JSX opening-like element
    internal Symbol? jsxNamespace;                     // Resolved JSX namespace symbol for this node
    internal Symbol? jsxImplicitImportContainer;       // Resolved module symbol the implicit JSX import of this file should refer to
}

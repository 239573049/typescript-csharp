// Ported from tsgo internal/pseudochecker/type.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

namespace Ts.PseudoChecker;

// `PseudoType`s are skeletons of types - partially interpreted expressions and type nodes
// composed to represent how you *should* construct a type out of them. They can be trivially
// mapped into actual types by a real `Checker`, or into a tree of `Node`s directly, without
// needing to make any intermediate types, by a `NodeBuilder`. Unlike checker `Type`s, these are
// never normalized, and multiple pseudo-types may refer to the same underlying `Type`.

// In strada, these were implicit in the AST nodes constructed in `expressionToTypeNode.ts`, which
// repurposed AST nodes for this purpose, but in so doing, often confused weather or not it had validated
// nested nodes for use at a given use-site. By keeping the mapping deferred like this, we can know we haven't
// done any use-site checks until we're ready to map the `PseudoType` into a `Node`, and can cache
// `PseudoType`s across multiple target positions.

public enum PseudoTypeKind : short
{
    Direct,
    Inferred,
    NoResult,
    MaybeConstLocation,
    Union,
    Undefined,
    Null,
    Any,
    String,
    Number,
    BigInt,
    Boolean,
    False,
    True,
    SingleCallSignature,
    Tuple,
    ObjectLiteral,
    StringLiteral,
    NumericLiteral,
    BigIntLiteral,
}

public partial class PseudoType
{
    public PseudoTypeKind Kind;

    protected PseudoType(PseudoTypeKind kind)
    {
        Kind = kind;
    }
}

public sealed partial class PseudoChecker
{
    private static PseudoType NewPseudoType(PseudoTypeKind kind)
    {
        return new PseudoTypeBase(kind);
    }
}

// PORT-NOTE: Go's pseudoTypeData interface and embedded PseudoType header/data backpointer
// map to ordinary C# inheritance; object identity and failed AsPseudoType* cast behavior remain the same.
public class PseudoTypeDefault : PseudoType
{
    protected PseudoTypeDefault(PseudoTypeKind kind)
        : base(kind)
    {
    }

    public PseudoType AsPseudoType() => this;
}

public class PseudoTypeBase : PseudoTypeDefault
{
    internal PseudoTypeBase(PseudoTypeKind kind)
        : base(kind)
    {
    }
}

public sealed partial class PseudoChecker
{
    public static readonly PseudoType PseudoTypeUndefined = NewPseudoType(PseudoTypeKind.Undefined);
    public static readonly PseudoType PseudoTypeNull = NewPseudoType(PseudoTypeKind.Null);
    public static readonly PseudoType PseudoTypeAny = NewPseudoType(PseudoTypeKind.Any);
    public static readonly PseudoType PseudoTypeString = NewPseudoType(PseudoTypeKind.String);
    public static readonly PseudoType PseudoTypeNumber = NewPseudoType(PseudoTypeKind.Number);
    public static readonly PseudoType PseudoTypeBigInt = NewPseudoType(PseudoTypeKind.BigInt);
    public static readonly PseudoType PseudoTypeBoolean = NewPseudoType(PseudoTypeKind.Boolean);
    public static readonly PseudoType PseudoTypeFalse = NewPseudoType(PseudoTypeKind.False);
    public static readonly PseudoType PseudoTypeTrue = NewPseudoType(PseudoTypeKind.True);
}

// PseudoTypeDirect directly encodes the type referred to by a given TypeNode
public sealed class PseudoTypeDirect(global::Ts.Ast.Node typeNode) : PseudoTypeBase(PseudoTypeKind.Direct)
{
    public global::Ts.Ast.Node TypeNode = typeNode;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeDirect(global::Ts.Ast.Node typeNode)
    {
        return new PseudoTypeDirect(typeNode);
    }
}

public partial class PseudoType
{
    public PseudoTypeDirect AsPseudoTypeDirect() => (PseudoTypeDirect)this;
}

// PseudoTypeInferred directly encodes the type referred to by a given Expression
// These represent cases where the expression was too complex for the pseudochecker.
// Most of the time, these locations will produce an error under ID.
// Specific error nodes (shorthand properties, spread assignments, etc.) are stored on the
// ErrorNodes field, collected during pseudochecker construction.
public sealed class PseudoTypeInferred(global::Ts.Ast.Node expression, global::Ts.Ast.Node[]? errorNodes, bool isSignatureReturn) : PseudoTypeBase(PseudoTypeKind.Inferred)
{
    public global::Ts.Ast.Node Expression = expression;
    public global::Ts.Ast.Node[]? ErrorNodes = errorNodes;
    public bool IsSignatureReturn = isSignatureReturn;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeInferred(global::Ts.Ast.Node expr, bool isSignatureReturn)
    {
        return new PseudoTypeInferred(expr, null, isSignatureReturn);
    }

    public static PseudoType NewPseudoTypeInferredWithErrors(global::Ts.Ast.Node expr, bool isSignatureReturn, global::Ts.Ast.Node[] errorNodes)
    {
        return new PseudoTypeInferred(expr, errorNodes, isSignatureReturn);
    }
}

public partial class PseudoType
{
    public PseudoTypeInferred AsPseudoTypeInferred() => (PseudoTypeInferred)this;
}

// PseudoTypeNoResult is analogous to PseudoTypeInferred in that it references a case
// where the type was too complex for the pseudochecker. Rather than an expression, however,
// it is referring to the return type of a signature or declaration.
public sealed class PseudoTypeNoResult(global::Ts.Ast.Node declaration) : PseudoTypeBase(PseudoTypeKind.NoResult)
{
    public global::Ts.Ast.Node Declaration = declaration;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeNoResult(global::Ts.Ast.Node decl)
    {
        return new PseudoTypeNoResult(decl);
    }
}

public partial class PseudoType
{
    public PseudoTypeNoResult AsPseudoTypeNoResult() => (PseudoTypeNoResult)this;
}

// PseudoTypeMaybeConstLocation encodes the const/regular types of a location so the builder
// can later select the appropriate pseudotype based on the location's context. This is used
// to ensure accuracy in nested expressions without exposing type-based functionality to the pseudochecker.
// A nodebuilder that doesn't do contextual typing would need to, as policy, reject these types if they
// are in a contextually typed position! (Otherwise they could pick one, but either type could be wrong, depending on context!)
// At the top-level, which is generally what ID is concerned with, nothing is contextually typed, so these cases don't generally
// cause problems. Once you get into reused nodes in nested expressions, however, this becomes important.
// In strada, checker `isConstContext` functionality exposed to the pseudochecker + type comparison sanity checking
// on nested results masks the need for this abstraction, but with it present it clearly highlights a shortcoming
// of the ID infernce model and how "standalone" it can(n't) truly be without substantial restrictions on expression inference.
public sealed class PseudoTypeMaybeConstLocation(global::Ts.Ast.Node node, PseudoType constType, PseudoType regularType) : PseudoTypeBase(PseudoTypeKind.MaybeConstLocation)
{
    public global::Ts.Ast.Node Node = node;
    public PseudoType ConstType = constType;
    public PseudoType RegularType = regularType;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeMaybeConstLocation(global::Ts.Ast.Node loc, PseudoType ct, PseudoType reg)
    {
        return new PseudoTypeMaybeConstLocation(loc, ct, reg);
    }
}

public partial class PseudoType
{
    public PseudoTypeMaybeConstLocation AsPseudoTypeMaybeConstLocation()
    {
        return (PseudoTypeMaybeConstLocation)this;
    }
}

// PseudoTypeUnion is a collection of psudotypes joined into a union
public sealed class PseudoTypeUnion(PseudoType[] types) : PseudoTypeBase(PseudoTypeKind.Union)
{
    public PseudoType[] Types = types;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeUnion(PseudoType[] types)
    {
        return new PseudoTypeUnion(types);
    }
}

public partial class PseudoType
{
    public PseudoTypeUnion AsPseudoTypeUnion()
    {
        return (PseudoTypeUnion)this;
    }
}

public sealed class PseudoParameter(bool rest, global::Ts.Ast.Node name, bool optional, PseudoType type)
{
    public bool Rest = rest;
    public global::Ts.Ast.Node Name = name;
    public bool Optional = optional;
    public PseudoType Type = type;
}

public sealed partial class PseudoChecker
{
    public static PseudoParameter NewPseudoParameter(bool isRest, global::Ts.Ast.Node name, bool isOptional, PseudoType t)
    {
        return new PseudoParameter(isRest, name, isOptional, t);
    }
}

// PseudoTypeSingleCallSignature represents an object type with a single call signature, like an arrow or function expression
public sealed class PseudoTypeSingleCallSignature(global::Ts.Ast.Node signature, PseudoParameter[]? parameters, global::Ts.Ast.TypeParameterDeclaration[]? typeParameters, PseudoType returnType) : PseudoTypeBase(PseudoTypeKind.SingleCallSignature)
{
    public global::Ts.Ast.Node Signature = signature;
    public PseudoParameter[]? Parameters = parameters;
    public global::Ts.Ast.TypeParameterDeclaration[]? TypeParameters = typeParameters;
    public PseudoType ReturnType = returnType;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeSingleCallSignature(global::Ts.Ast.Node signature, PseudoParameter[]? parameters, global::Ts.Ast.TypeParameterDeclaration[]? typeParameters, PseudoType returnType)
    {
        return new PseudoTypeSingleCallSignature(signature, parameters, typeParameters, returnType);
    }
}

public partial class PseudoType
{
    public PseudoTypeSingleCallSignature AsPseudoTypeSingleCallSignature()
    {
        return (PseudoTypeSingleCallSignature)this;
    }
}

// PseudoTypeTuple represents a tuple originaing from an `as const` array literal
public sealed class PseudoTypeTuple(PseudoType[] elements) : PseudoTypeBase(PseudoTypeKind.Tuple)
{
    public PseudoType[] Elements = elements;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeTuple(PseudoType[] elements)
    {
        return new PseudoTypeTuple(elements);
    }
}

public partial class PseudoType
{
    public PseudoTypeTuple AsPseudoTypeTuple()
    {
        return (PseudoTypeTuple)this;
    }
}

public partial class PseudoObjectElement
{
    public global::Ts.Ast.Node Name;
    public bool Optional;
    public PseudoObjectElementKind Kind;

    protected PseudoObjectElement(PseudoObjectElementKind kind, global::Ts.Ast.Node name, bool optional)
    {
        Kind = kind;
        Name = name;
        Optional = optional;
    }

    public PseudoObjectElement AsPseudoObjectElement() => this;

    public global::Ts.Ast.Node? Signature()
    {
        switch (Kind)
        {
            case PseudoObjectElementKind.Method:
                return AsPseudoObjectMethod().Signature;
            case PseudoObjectElementKind.SetAccessor:
                return AsPseudoSetAccessor().Signature;
            case PseudoObjectElementKind.GetAccessor:
                return AsPseudoGetAccessor().Signature;
            default:
                return null;
        }
    }
}

public enum PseudoObjectElementKind : sbyte
{
    Method,
    PropertyAssignment,
    SetAccessor,
    GetAccessor,
}

// PORT-NOTE: Go's pseudoObjectElementData interface and newPseudoObjectElement backpointer
// map to the derived-class constructors below, which set the same header fields in one step.

public sealed class PseudoObjectMethod : PseudoObjectElement
{
    public new global::Ts.Ast.Node Signature;
    public global::Ts.Ast.TypeParameterDeclaration[]? TypeParameters;
    public PseudoParameter[]? Parameters;
    public PseudoType ReturnType;

    internal PseudoObjectMethod(global::Ts.Ast.Node signature, global::Ts.Ast.Node name, bool optional, global::Ts.Ast.TypeParameterDeclaration[]? typeParameters, PseudoParameter[]? parameters, PseudoType returnType)
        : base(PseudoObjectElementKind.Method, name, optional)
    {
        Signature = signature;
        TypeParameters = typeParameters;
        Parameters = parameters;
        ReturnType = returnType;
    }
}

public sealed partial class PseudoChecker
{
    public static PseudoObjectElement NewPseudoObjectMethod(global::Ts.Ast.Node signature, global::Ts.Ast.Node name, bool optional, global::Ts.Ast.TypeParameterDeclaration[]? typeParameters, PseudoParameter[]? parameters, PseudoType returnType)
    {
        return new PseudoObjectMethod(signature, name, optional, typeParameters, parameters, returnType);
    }
}

public partial class PseudoObjectElement
{
    public PseudoObjectMethod AsPseudoObjectMethod()
    {
        return (PseudoObjectMethod)this;
    }
}

public sealed class PseudoPropertyAssignment : PseudoObjectElement
{
    public bool Readonly;
    public PseudoType Type;

    internal PseudoPropertyAssignment(bool readOnly, global::Ts.Ast.Node name, bool optional, PseudoType type)
        : base(PseudoObjectElementKind.PropertyAssignment, name, optional)
    {
        Readonly = readOnly;
        Type = type;
    }
}

public sealed partial class PseudoChecker
{
    public static PseudoObjectElement NewPseudoPropertyAssignment(bool readOnly, global::Ts.Ast.Node name, bool optional, PseudoType t)
    {
        return new PseudoPropertyAssignment(readOnly, name, optional, t);
    }
}

public partial class PseudoObjectElement
{
    public PseudoPropertyAssignment AsPseudoPropertyAssignment()
    {
        return (PseudoPropertyAssignment)this;
    }
}

public sealed class PseudoSetAccessor : PseudoObjectElement
{
    public new global::Ts.Ast.Node Signature;
    public PseudoParameter Parameter;

    internal PseudoSetAccessor(global::Ts.Ast.Node signature, global::Ts.Ast.Node name, bool optional, PseudoParameter parameter)
        : base(PseudoObjectElementKind.SetAccessor, name, optional)
    {
        Signature = signature;
        Parameter = parameter;
    }
}

public sealed partial class PseudoChecker
{
    public static PseudoObjectElement NewPseudoSetAccessor(global::Ts.Ast.Node signature, global::Ts.Ast.Node name, bool optional, PseudoParameter p)
    {
        return new PseudoSetAccessor(signature, name, optional, p);
    }
}

public partial class PseudoObjectElement
{
    public PseudoSetAccessor AsPseudoSetAccessor()
    {
        return (PseudoSetAccessor)this;
    }
}

public sealed class PseudoGetAccessor : PseudoObjectElement
{
    public new global::Ts.Ast.Node Signature;
    public PseudoType Type;

    internal PseudoGetAccessor(global::Ts.Ast.Node signature, global::Ts.Ast.Node name, bool optional, PseudoType type)
        : base(PseudoObjectElementKind.GetAccessor, name, optional)
    {
        Signature = signature;
        Type = type;
    }
}

public sealed partial class PseudoChecker
{
    public static PseudoObjectElement NewPseudoGetAccessor(global::Ts.Ast.Node signature, global::Ts.Ast.Node name, bool optional, PseudoType t)
    {
        return new PseudoGetAccessor(signature, name, optional, t);
    }
}

public partial class PseudoObjectElement
{
    public PseudoGetAccessor AsPseudoGetAccessor()
    {
        return (PseudoGetAccessor)this;
    }
}

// PseudoTypeObjectLiteral represents an object type originaing from an object literal
public sealed class PseudoTypeObjectLiteral(PseudoObjectElement[]? elements) : PseudoTypeBase(PseudoTypeKind.ObjectLiteral)
{
    public PseudoObjectElement[]? Elements = elements;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeObjectLiteral(PseudoObjectElement[]? elements)
    {
        return new PseudoTypeObjectLiteral(elements);
    }
}

public partial class PseudoType
{
    public PseudoTypeObjectLiteral AsPseudoTypeObjectLiteral()
    {
        return (PseudoTypeObjectLiteral)this;
    }
}

// PseudoTypeLiteral represents a literal type
public sealed class PseudoTypeLiteral(PseudoTypeKind kind, global::Ts.Ast.Node node) : PseudoTypeBase(kind)
{
    public global::Ts.Ast.Node Node = node;
}

public sealed partial class PseudoChecker
{
    public static PseudoType NewPseudoTypeStringLiteral(global::Ts.Ast.Node node)
    {
        return new PseudoTypeLiteral(PseudoTypeKind.StringLiteral, node);
    }

    public static PseudoType NewPseudoTypeNumericLiteral(global::Ts.Ast.Node node)
    {
        return new PseudoTypeLiteral(PseudoTypeKind.NumericLiteral, node);
    }

    public static PseudoType NewPseudoTypeBigIntLiteral(global::Ts.Ast.Node node)
    {
        return new PseudoTypeLiteral(PseudoTypeKind.BigIntLiteral, node);
    }
}

public partial class PseudoType
{
    public PseudoTypeLiteral AsPseudoTypeLiteral()
    {
        return (PseudoTypeLiteral)this;
    }
}

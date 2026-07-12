// Ported from tsgo internal/checker/checker.go (type-facts cluster, plan/03 §4.2):
//   getTypeFacts 30731 / hasTypeFacts 30735 / getTypeFactsWorker 30739 /
//   getIntersectionTypeFacts 30875 / isZeroBigInt 30893.
//
// Faithful per-function translation: same order, same switch structure. Go comments preserved.
//
// §1.5: no cross-package classes referenced here that collide with sibling namespaces; the
// TypeFacts/TypeFlags enum members and Type field accessors are all in-package.

using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:30731
    internal TypeFacts GetTypeFacts(Type t, TypeFacts mask)
    {
        return GetTypeFactsWorker(t, mask) & mask;
    }

    // checker.go:30735
    internal bool HasTypeFacts(Type t, TypeFacts mask)
    {
        return GetTypeFacts(t, mask) != 0;
    }

    // checker.go:30739
    internal TypeFacts GetTypeFactsWorker(Type t, TypeFacts callerOnlyNeeds)
    {
        if ((t.flags & (TypeFlags.Intersection | TypeFlags.Instantiable)) != 0)
        {
            t = GetBaseConstraintOfType(t)!;
            if (t == null)
            {
                t = unknownType!;
            }
        }
        var flags = t.flags;
        if ((flags & (TypeFlags.String | TypeFlags.StringMapping)) != 0)
        {
            if (strictNullChecks)
            {
                return TypeFacts.StringStrictFacts;
            }
            return TypeFacts.StringFacts;
        }
        if ((flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral)) != 0)
        {
            bool isEmpty = (flags & TypeFlags.StringLiteral) != 0 && GetStringLiteralValue(t) == "";
            if (strictNullChecks)
            {
                if (isEmpty)
                {
                    return TypeFacts.EmptyStringStrictFacts;
                }
                return TypeFacts.NonEmptyStringStrictFacts;
            }
            if (isEmpty)
            {
                return TypeFacts.EmptyStringFacts;
            }
            return TypeFacts.NonEmptyStringFacts;
        }
        if ((flags & (TypeFlags.Number | TypeFlags.Enum)) != 0)
        {
            if (strictNullChecks)
            {
                return TypeFacts.NumberStrictFacts;
            }
            return TypeFacts.NumberFacts;
        }
        if ((flags & TypeFlags.NumberLiteral) != 0)
        {
            bool isZero = GetNumberLiteralValue(t) == 0;
            if (strictNullChecks)
            {
                if (isZero)
                {
                    return TypeFacts.ZeroNumberStrictFacts;
                }
                return TypeFacts.NonZeroNumberStrictFacts;
            }
            if (isZero)
            {
                return TypeFacts.ZeroNumberFacts;
            }
            return TypeFacts.NonZeroNumberFacts;
        }
        if ((flags & TypeFlags.BigInt) != 0)
        {
            if (strictNullChecks)
            {
                return TypeFacts.BigIntStrictFacts;
            }
            return TypeFacts.BigIntFacts;
        }
        if ((flags & TypeFlags.BigIntLiteral) != 0)
        {
            bool isZero = IsZeroBigInt(t);
            if (strictNullChecks)
            {
                if (isZero)
                {
                    return TypeFacts.ZeroBigIntStrictFacts;
                }
                return TypeFacts.NonZeroBigIntStrictFacts;
            }
            if (isZero)
            {
                return TypeFacts.ZeroBigIntFacts;
            }
            return TypeFacts.NonZeroBigIntFacts;
        }
        if ((flags & TypeFlags.Boolean) != 0)
        {
            if (strictNullChecks)
            {
                return TypeFacts.BooleanStrictFacts;
            }
            return TypeFacts.BooleanFacts;
        }
        if ((flags & TypeFlags.BooleanLike) != 0)
        {
            bool isFalse = t == falseType || t == regularFalseType;
            if (strictNullChecks)
            {
                if (isFalse)
                {
                    return TypeFacts.FalseStrictFacts;
                }
                return TypeFacts.TrueStrictFacts;
            }
            if (isFalse)
            {
                return TypeFacts.FalseFacts;
            }
            return TypeFacts.TrueFacts;
        }
        if ((flags & TypeFlags.Object) != 0)
        {
            TypeFacts possibleFacts;
            if (strictNullChecks)
            {
                possibleFacts = TypeFacts.EmptyObjectStrictFacts | TypeFacts.FunctionStrictFacts | TypeFacts.ObjectStrictFacts;
            }
            else
            {
                possibleFacts = TypeFacts.EmptyObjectFacts | TypeFacts.FunctionFacts | TypeFacts.ObjectFacts;
            }
            if ((callerOnlyNeeds & possibleFacts) == 0)
            {
                // If the caller doesn't care about any of the facts that we could possibly produce,
                // return zero so we can skip resolving members.
                return TypeFacts.None;
            }
            if ((t.objectFlags & ObjectFlags.Anonymous) != 0 && IsEmptyObjectType(t))
            {
                if (strictNullChecks)
                {
                    return TypeFacts.EmptyObjectStrictFacts;
                }
                return TypeFacts.EmptyObjectFacts;
            }
            if (IsFunctionObjectType(t))
            {
                if (strictNullChecks)
                {
                    return TypeFacts.FunctionStrictFacts;
                }
                return TypeFacts.FunctionFacts;
            }
            if (strictNullChecks)
            {
                return TypeFacts.ObjectStrictFacts;
            }
            return TypeFacts.ObjectFacts;
        }
        if ((flags & TypeFlags.Void) != 0)
        {
            return TypeFacts.VoidFacts;
        }
        if ((flags & TypeFlags.Undefined) != 0)
        {
            return TypeFacts.UndefinedFacts;
        }
        if ((flags & TypeFlags.Null) != 0)
        {
            return TypeFacts.NullFacts;
        }
        if ((flags & TypeFlags.ESSymbolLike) != 0)
        {
            if (strictNullChecks)
            {
                return TypeFacts.SymbolStrictFacts;
            }
            else
            {
                return TypeFacts.SymbolFacts;
            }
        }
        if ((flags & TypeFlags.NonPrimitive) != 0)
        {
            if (strictNullChecks)
            {
                return TypeFacts.ObjectStrictFacts;
            }
            else
            {
                return TypeFacts.ObjectFacts;
            }
        }
        if ((flags & TypeFlags.Never) != 0)
        {
            return TypeFacts.None;
        }
        if ((flags & TypeFlags.Union) != 0)
        {
            var facts = TypeFacts.None;
            // map-iter: N/A — union constituents are an ordered array; OR-fold is order-independent.
            foreach (var constituent in t.Types())
            {
                facts |= GetTypeFactsWorker(constituent, callerOnlyNeeds);
            }
            return facts;
        }
        if ((flags & TypeFlags.Intersection) != 0)
        {
            return GetIntersectionTypeFacts(t, callerOnlyNeeds);
        }
        return TypeFacts.UnknownFacts;
    }

    // checker.go:30875
    internal TypeFacts GetIntersectionTypeFacts(Type t, TypeFacts callerOnlyNeeds)
    {
        // When an intersection contains a primitive type we ignore object type constituents as they are
        // presumably type tags. For example, in string & { __kind__: "name" } we ignore the object type.
        bool ignoreObjects = MaybeTypeOfKind(t, TypeFlags.Primitive);
        // When computing the type facts of an intersection type, certain type facts are computed as `and`
        // and others are computed as `or`.
        var oredFacts = TypeFacts.None;
        var andedFacts = TypeFacts.All;
        foreach (var constituent in t.Types())
        {
            if (!(ignoreObjects && (constituent.flags & TypeFlags.Object) != 0))
            {
                var f = GetTypeFactsWorker(constituent, callerOnlyNeeds);
                oredFacts |= f;
                andedFacts &= f;
            }
        }
        return oredFacts & TypeFacts.OrFactsMask | andedFacts & TypeFacts.AndFactsMask;
    }

    // checker.go:30893
    internal static bool IsZeroBigInt(Type t)
    {
        return GetBigIntLiteralValue(t) == new global::Ts.JsNum.PseudoBigInt();
    }
}

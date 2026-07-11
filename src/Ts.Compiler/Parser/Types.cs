// Ported from tsgo internal/parser/types.go
using System;

namespace Ts.Parser;

[Flags]
public enum ParseFlags : uint
{
    None = 0,
    Yield = 1 << 0,
    Await = 1 << 1,
    Type = 1 << 2,
    IgnoreMissingOpenBrace = 1 << 4,
    JSDoc = 1 << 5,
}

// Ported from tsgo internal/parser/jsdoc.go:48 (propertyLikeParse).
[Flags]
internal enum PropertyLikeParse : int
{
    Property = 1 << 0,
    Parameter = 1 << 1,
    CallbackParameter = 1 << 2,
}

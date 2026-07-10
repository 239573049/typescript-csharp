using System;

namespace Ts.StringUtil;

// Go: internal/stringutil/identifier.go
public static partial class StringUtil
{
    // IsUnicodeIdentifierStart reports whether ch may begin an ECMAScript
    // identifier, i.e. whether it has the Unicode ID_Start (or Other_ID_Start)
    // property. The range table is generated; see generate-unicode-data.mts.
    public static bool IsUnicodeIdentifierStart(int ch)
    {
        return Unicode.Is(UnicodeTables.IdentifierStartR16, UnicodeTables.IdentifierStartR32, ch);
    }

    // IsUnicodeIdentifierPart reports whether ch may appear after the first
    // character of an ECMAScript identifier, i.e. whether it has the Unicode
    // ID_Continue (or Other_ID_Continue) property, which also includes ID_Start.
    public static bool IsUnicodeIdentifierPart(int ch)
    {
        return Unicode.Is(UnicodeTables.IdentifierPartR16, UnicodeTables.IdentifierPartR32, ch);
    }
}

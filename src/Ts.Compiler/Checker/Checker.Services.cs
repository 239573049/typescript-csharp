// Ported from tsgo internal/checker/services.go (LS-facing checker services; hover wave opens this
// file with the lib-detection pair — later LS waves add their services here).

using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // services.go:1112-1125 — IsLibSymbolForHoverVerbosity returns true if a symbol is declared in a
    // lib file. Don't expand symbols like Array or Promise, instead treating them as opaque.
    public bool IsLibSymbolForHoverVerbosity(Symbol? symbol)
    {
        if (symbol == null)
        {
            return false;
        }
        foreach (var decl in symbol.Declarations ?? [])
        {
            var sf = AstUtilities.GetSourceFileOfNode(decl);
            if (sf != null && program.IsSourceFileDefaultLibrary(sf.Path()))
            {
                return true;
            }
        }
        return false;
    }

    // services.go:1127-1140 — IsLibTypeForHoverVerbosity returns true if a type is declared in a lib
    // file. Don't expand types like Array or Promise, instead treating them as opaque.
    public bool IsLibTypeForHoverVerbosity(Type t)
    {
        Symbol? symbol;
        if ((t.objectFlags & ObjectFlags.Reference) != 0)
        {
            symbol = t.Target().symbol;
        }
        else
        {
            symbol = t.symbol;
        }
        if (IsLibSymbolForHoverVerbosity(symbol))
        {
            return true;
        }
        return IsTupleType(t);
    }
}

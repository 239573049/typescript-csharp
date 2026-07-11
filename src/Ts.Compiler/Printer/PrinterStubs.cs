// Stubs for references from the printer package (factory.go/helpers.go wave) into functionality that has
// not yet been ported. Each stub throws NotImplementedException naming the Go symbol it stands in for, so a
// hit at runtime is obvious and the remaining work is discoverable via grep.

using System;
using Ts.Ast;

namespace Ts.Printer;

internal static class PrinterStubs
{
    // Go: ast.TryGetPropertyNameOfBindingOrAssignmentElement (internal/ast/utilities.go:3900).
    //
    // Called by NodeFactory.NewRestHelper. Not yet ported to the C# ast package: its fallback branch depends
    // on ast.GetTargetOfBindingOrAssignmentElement, which is also unported. Porting it would mean pulling an
    // unrelated slice of ast/utilities.go into this printer wave, so it is stubbed here rather than
    // partially/inconsistently ported. Relocate to Ts.Ast.AstUtilities when that ast helper chain lands.
    internal static Node? TryGetPropertyNameOfBindingOrAssignmentElement(Node bindingElement)
    {
        throw new NotImplementedException("TryGetPropertyNameOfBindingOrAssignmentElement: requires ast.GetTargetOfBindingOrAssignmentElement (internal/ast/utilities.go), not yet ported");
    }
}

// Ported from tsgo internal/ast/utilities.go — module-symbol source-file helpers used by the
// modulespecifiers package. (The checker localized private copies of these; the faithful ast-level
// home is here.)

using Ts.Core;

namespace Ts.Ast;

public static partial class AstUtilities
{
    // ast/utilities.go — IsExternalModuleAugmentation.
    public static bool IsExternalModuleAugmentation(Node node) =>
        IsAmbientModule(node) && IsModuleAugmentationExternal(node);

    // ast/utilities.go — GetNonAugmentationDeclaration.
    public static Node? GetNonAugmentationDeclaration(Symbol symbol) =>
        CoreUtil.Find(symbol.Declarations, static d => !IsExternalModuleAugmentation(d) && !IsGlobalScopeAugmentation(d));

    // ast/utilities.go — GetSourceFileOfModule.
    public static SourceFile? GetSourceFileOfModule(Symbol module) =>
        GetSourceFileOfNode(module.ValueDeclaration ?? GetNonAugmentationDeclaration(module));
}

// SourceFile structurally satisfies HasFileName (FileName()/Path()); the inheritance is declared here
// so instances pass to host methods typed on HasFileName (modulespecifiers/outputpaths).
public sealed partial class SourceFile : HasFileName
{
}

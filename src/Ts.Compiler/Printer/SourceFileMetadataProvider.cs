// Ported from tsgo internal/printer/sourcefilemetadataprovider.go

using Ts.Ast;
using Ts.TsPath;

namespace Ts.Printer;

public interface SourceFileMetaDataProvider
{
    // PORT-NOTE: Go returns *ast.SourceFileMetaData (nullable pointer); modeled as nullable struct.
    SourceFileMetaData? GetSourceFileMetaData(Path path);
}

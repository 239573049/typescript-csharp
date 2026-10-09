// Ported from tsgo internal/ls/file_rename.go (1:1).
//
// Go's `pathUpdater` closure type maps to the PathUpdater delegate below. The Go
// converters parameter threading (lsconv.Converters) dissolves: the C# conversion
// channel is the static LsDeps.PositionToLineAndCharacter / ToLspRange pair (UTF-16
// direct). change.NewTracker(ctx, ...) drops the ctx (see
// CodeActions.FixClassIncorrectlyImplementsInterface.cs:5 for the precedent). Positions
// are UTF-16 code units throughout.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods

namespace Ts.Ls;

// file_rename.go:20 — pathUpdater.
internal delegate (string Path, bool Ok) PathUpdater(string path);

// file_rename.go:22-25 — toImport.
internal sealed record ToImport(string NewFileName, bool Updated);

public sealed partial class LanguageService
{
    // file_rename.go:27-79 — GetEditsForFileRename.
    public List<global::Ts.LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile> GetEditsForFileRename(
        CancellationToken ctx,
        global::Ts.LsProto.DocumentUri oldURI,
        global::Ts.LsProto.DocumentUri newURI)
    {
        var program = GetProgram();
        var oldPath = oldURI.FileName();
        var newPath = newURI.FileName();

        var oldToNew = CreatePathUpdater(oldPath, newPath);

        var changeTracker = global::Ts.Ls.Change.Tracker.NewTracker(program.Options(), FormatOptions(), Converters());
        UpdateTsconfigFiles(program, changeTracker, oldToNew, oldPath, newPath);
        UpdateImportsForFileRename(program, changeTracker, oldToNew);

        var documentChanges = new List<global::Ts.LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile>();

        // When renaming e.g. `foo.d.css.ts` -> `bar.d.css.ts`, also rename `foo.css` -> `bar.css` if it exists.
        if (global::Ts.TsPath.TsPath.IsDeclarationFileName(oldPath) && global::Ts.TsPath.TsPath.IsDeclarationFileName(newPath))
        {
            var dtsExt = global::Ts.TsPath.TsPath.GetDeclarationFileExtension(oldPath);
            var originalExtensions = global::Ts.TsPath.TsPath.GetPossibleOriginalInputExtensionForExtension(dtsExt);
            foreach (var ext in originalExtensions)
            {
                var oldOriginalPath = global::Ts.TsPath.TsPath.ChangeFullExtension(oldPath, ext);
                if (host.FileExists(oldOriginalPath))
                {
                    var newDtsExt = global::Ts.TsPath.TsPath.GetDeclarationFileExtension(oldPath);
                    var newOriginalExtensions = global::Ts.TsPath.TsPath.GetPossibleOriginalInputExtensionForExtension(newDtsExt);
                    if (System.Linq.Enumerable.Contains(newOriginalExtensions, ext))
                    {
                        var newOriginalPath = global::Ts.TsPath.TsPath.ChangeFullExtension(newPath, ext);
                        documentChanges.Add(new global::Ts.LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile
                        {
                            RenameFile = new global::Ts.LsProto.RenameFile
                            {
                                OldUri = LsDeps.FileNameToDocumentURI(oldOriginalPath),
                                NewUri = LsDeps.FileNameToDocumentURI(newOriginalPath),
                            },
                        });
                    }
                }
            }
        }

        // map-iter: 序无关 —— 各文件的编辑互不依赖，文档变更的先后由消费方处理
        foreach (var (fileName, edits) in changeTracker.GetChanges())
        {
            var uri = LsDeps.FileNameToDocumentURI(fileName);
            var lspEdits = new List<global::Ts.LsProto.TextEditOrAnnotatedTextEditOrSnippetTextEdit>(edits.Count);
            foreach (var edit in edits)
            {
                lspEdits.Add(global::Ts.LsProto.TextEditOrAnnotatedTextEditOrSnippetTextEdit.FromTextEdit(edit));
            }
            documentChanges.Add(new global::Ts.LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile
            {
                TextDocumentEdit = new global::Ts.LsProto.TextDocumentEdit
                {
                    TextDocument = new global::Ts.LsProto.OptionalVersionedTextDocumentIdentifier { Uri = uri },
                    Edits = lspEdits.ToArray(),
                },
            });
        }

        return documentChanges;
    }

    // file_rename.go:81-92 — createPathUpdater.
    private PathUpdater CreatePathUpdater(string oldPath, string newPath)
    {
        var compareOptions = new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = UseCaseSensitiveFileNames() };
        return path =>
        {
            if (global::Ts.TsPath.TsPath.ComparePaths(path, oldPath, compareOptions) == 0)
            {
                return (newPath, true);
            }
            if (global::Ts.TsPath.TsPath.StartsWithDirectory(path, oldPath, UseCaseSensitiveFileNames()))
            {
                return (newPath + path[oldPath.Length..], true);
            }
            return ("", false);
        };
    }

    // file_rename.go:94-157 — updateTsconfigFiles.
    private void UpdateTsconfigFiles(global::Ts.Compiler.Program program, global::Ts.Ls.Change.Tracker changeTracker, PathUpdater oldToNew, string oldPath, string newPath)
    {
        var commandLine = program.CommandLine();
        if (commandLine == null || commandLine.ConfigFile == null)
        {
            return;
        }

        var configFile = commandLine.ConfigFile.SourceFile;
        if (configFile == null)
        {
            return;
        }
        var configDir = global::Ts.TsPath.TsPath.GetDirectoryPath(configFile.FileName());
        var jsonObjectLiteral = GetTsConfigObjectLiteralExpression(configFile);
        if (jsonObjectLiteral == null)
        {
            return;
        }

        ForEachObjectProperty(jsonObjectLiteral, (property, propertyName) =>
        {
            switch (propertyName)
            {
                case "files":
                case "include":
                case "exclude":
                    var foundExactMatch = UpdatePathsProperty(configFile, configDir, property, changeTracker, oldToNew, UseCaseSensitiveFileNames());
                    if (foundExactMatch || propertyName != "include" || !NodeTests.IsArrayLiteralExpression(property.Initializer!))
                    {
                        return;
                    }
                    var (oldSpec, isDefault) = commandLine.GetMatchedIncludeSpec(oldPath);
                    if (oldSpec != "" && !isDefault)
                    {
                        var (newSpec, _) = commandLine.GetMatchedIncludeSpec(newPath);
                        if (newSpec == "")
                        {
                            var elements = property.Initializer!.AsArrayLiteralExpression().Elements()!;
                            if (elements.Length > 0)
                            {
                                changeTracker.InsertNodeAfter(
                                    configFile,
                                    elements[elements.Length - 1],
                                    changeTracker.NodeFactory.NewStringLiteral(RelativePathFromDirectory(configDir, newPath, UseCaseSensitiveFileNames()), TokenFlags.None));
                            }
                        }
                    }
                    break;
                case "compilerOptions":
                    if (!NodeTests.IsObjectLiteralExpression(property.Initializer!))
                    {
                        return;
                    }
                    ForEachObjectProperty(property.Initializer!.AsObjectLiteralExpression(), (property, propertyName) =>
                    {
                        var option = global::Ts.TsOptions.TsConfigParsing.CommandLineCompilerOptionsMap.Get(propertyName);
                        if (option != null)
                        {
                            var elementOption = option.Elements();
                            if (option.IsFilePath || (option.Kind == global::Ts.TsOptions.CommandLineOptionKind.CommandLineOptionTypeList && elementOption != null && elementOption.IsFilePath))
                            {
                                UpdatePathsProperty(configFile, configDir, property, changeTracker, oldToNew, UseCaseSensitiveFileNames());
                                return;
                            }
                        }

                        if (propertyName != "paths" || !NodeTests.IsObjectLiteralExpression(property.Initializer!))
                        {
                            return;
                        }
                        ForEachObjectProperty(property.Initializer!.AsObjectLiteralExpression(), (pathsProperty, _) =>
                        {
                            if (!NodeTests.IsArrayLiteralExpression(pathsProperty.Initializer!))
                            {
                                return;
                            }
                            foreach (var element in pathsProperty.Initializer!.AsArrayLiteralExpression().Elements()!)
                            {
                                TryUpdateConfigString(configFile, configDir, element, changeTracker, oldToNew, UseCaseSensitiveFileNames());
                            }
                        });
                    });
                    break;
            }
        });
    }

    // file_rename.go:159-170 — updatePathsProperty.
    private static bool UpdatePathsProperty(SourceFile configFile, string configDir, global::Ts.Ast.PropertyAssignment property, global::Ts.Ls.Change.Tracker changeTracker, PathUpdater oldToNew, bool useCaseSensitiveFileNames)
    {
        var elements = new List<Node> { property.Initializer! };
        if (NodeTests.IsArrayLiteralExpression(property.Initializer!))
        {
            elements = new List<Node>(property.Initializer!.AsArrayLiteralExpression().Elements()!);
        }

        var foundExactMatch = false;
        foreach (var element in elements)
        {
            foundExactMatch = TryUpdateConfigString(configFile, configDir, element, changeTracker, oldToNew, useCaseSensitiveFileNames) || foundExactMatch;
        }
        return foundExactMatch;
    }

    // file_rename.go:172-188 — tryUpdateConfigString.
    private static bool TryUpdateConfigString(SourceFile configFile, string configDir, Node element, global::Ts.Ls.Change.Tracker changeTracker, PathUpdater oldToNew, bool useCaseSensitiveFileNames)
    {
        if (!NodeTests.IsStringLiteral(element))
        {
            return false;
        }

        var elementFileName = global::Ts.TsPath.TsPath.NormalizePath(global::Ts.TsPath.TsPath.CombinePaths(configDir, element.Text()));
        var (updated, ok) = oldToNew(elementFileName);
        if (!ok)
        {
            return false;
        }

        changeTracker.ReplaceRangeWithText(configFile, new global::Ts.LsProto.Range
        {
            Start = LsDeps.PositionToLineAndCharacter(configFile, global::Ts.Scanner.Scanner.GetTokenPosOfNode(element, configFile, false /*includeJSDoc*/) + 1),
            End = LsDeps.PositionToLineAndCharacter(configFile, element.End - 1),
        }, RelativePathFromDirectory(configDir, updated, useCaseSensitiveFileNames));
        return true;
    }

    // file_rename.go:190-197 — updateRelativePath.
    private string UpdateRelativePath(PathUpdater oldToNew, string oldImportFromPath, string newImportFromPath, string relativeSpecifier)
    {
        var oldAbsolute = global::Ts.TsPath.TsPath.NormalizePath(global::Ts.TsPath.TsPath.CombinePaths(global::Ts.TsPath.TsPath.GetDirectoryPath(oldImportFromPath), relativeSpecifier));
        var (newAbsolute, ok) = oldToNew(oldAbsolute);
        if (!ok)
        {
            newAbsolute = oldAbsolute;
        }
        return RelativeImportPathFromDirectory(global::Ts.TsPath.TsPath.GetDirectoryPath(newImportFromPath), newAbsolute, UseCaseSensitiveFileNames());
    }

    // file_rename.go:199-230 — updateImportsForFileRename.
    private void UpdateImportsForFileRename(global::Ts.Compiler.Program program, global::Ts.Ls.Change.Tracker changeTracker, PathUpdater oldToNew)
    {
        var allFiles = program.SourceFiles();
        var (checker, done) = program.GetTypeChecker(CancellationToken.None); // Go: context.Background()
        try
        {
            var moduleSpecifierPreferences = UserPreferences().ModuleSpecifierPreferences();

            foreach (var sourceFile in allFiles)
            {
                var oldFileName = sourceFile.FileName();
                var (newFromOld, fileMoved) = oldToNew(sourceFile.FileName());
                var newImportFromPath = sourceFile.FileName();
                if (fileMoved)
                {
                    newImportFromPath = newFromOld;
                }

                foreach (var reference in sourceFile.ReferencedFiles ?? [])
                {
                    if (!global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(reference.FileName))
                    {
                        continue;
                    }
                    var updated = UpdateRelativePath(oldToNew, oldFileName, newImportFromPath, reference.FileName);
                    if (updated != reference.FileName)
                    {
                        changeTracker.ReplaceRangeWithText(sourceFile, LsDeps.ToLspRange(sourceFile, reference.TextRange), updated);
                    }
                }

                foreach (var importStringLiteral in sourceFile.Imports() ?? [])
                {
                    var updatedImport = GetUpdatedImportSpecifier(program, checker, sourceFile, importStringLiteral, oldToNew, newImportFromPath, fileMoved, moduleSpecifierPreferences);
                    if (updatedImport != "" && updatedImport != importStringLiteral.Text())
                    {
                        changeTracker.ReplaceRangeWithText(sourceFile, LsDeps.ToLspRange(sourceFile, CreateStringTextRange(sourceFile, importStringLiteral)), updatedImport);
                    }
                }
            }
        }
        finally
        {
            done();
        }
    }

    // file_rename.go:232-280 — getUpdatedImportSpecifier.
    // We assume the source file did not move to a different program.
    private string GetUpdatedImportSpecifier(
        global::Ts.Compiler.Program program,
        global::Ts.Checker.Checker checker,
        SourceFile sourceFile, // old importing source file
        StringLiteralLikeNode importLiteral,
        PathUpdater oldToNew,
        string newImportFromPath,
        bool importingSourceFileMoved,
        global::Ts.ModuleSpecifiers.UserPreferences userPreferences)
    {
        var importedModuleSymbol = checker.GetSymbolAtLocation(importLiteral);
        if (IsAmbientModuleSymbol(importedModuleSymbol))
        {
            return "";
        }

        var target = GetSourceFileToImport(program, sourceFile, importLiteral, oldToNew);

        if (target == null)
        {
            // First fall back: try every file in the program to see if any of them would match the import specifier, and if so, obtain the updated specifier for that file.
            var updatedFromMoved = GetUpdatedImportSpecifierFromMovedSourceFiles(program, sourceFile, importLiteral, oldToNew, newImportFromPath, userPreferences);
            if (updatedFromMoved != "" && updatedFromMoved != importLiteral.Text())
            {
                return updatedFromMoved;
            }
            // Fall back to a regular path update for unresolved module.
            if (global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(importLiteral.Text()))
            {
                return UpdateRelativePath(oldToNew, sourceFile.FileName(), newImportFromPath, importLiteral.Text());
            }
            return "";
        }

        // Optimization: neither the importing or imported file changed.
        if (!target.Updated && !(importingSourceFileMoved && global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(importLiteral.Text())))
        {
            return "";
        }

        var updatedSpecifier = global::Ts.ModuleSpecifiers.ModuleSpecifiersUpdate.UpdateModuleSpecifier(
            program.Options(),
            program,
            sourceFile,
            newImportFromPath,
            importLiteral.Text(),
            target.NewFileName,
            userPreferences,
            new global::Ts.ModuleSpecifiers.ModuleSpecifierOptions
            {
                OverrideImportMode = program.GetModeForUsageLocation(sourceFile, importLiteral),
            });
        return updatedSpecifier;
    }

    // file_rename.go:282-297 — getSourceFileToImport.
    private static ToImport? GetSourceFileToImport(global::Ts.Compiler.Program program, SourceFile sourceFile, StringLiteralLikeNode importLiteral, PathUpdater oldToNew)
    {
        if (program.GetResolvedModuleFromModuleSpecifier(sourceFile, importLiteral) is { } resolved && resolved.ResolvedFileName != "")
        {
            var oldFileName = resolved.ResolvedFileName;
            var (newFileName, ok) = oldToNew(oldFileName);
            if (ok)
            {
                return new ToImport(newFileName, true);
            }
            return new ToImport(oldFileName, false);
        }

        return null;
    }

    // file_rename.go:299-339 — getUpdatedImportSpecifierFromMovedSourceFiles.
    // As a fall back for unresolved modules, we'll check all files in the program to see if any of them would match
    // the import specifier, and if so, we'll obtain the updated specifier for that file.
    private static string GetUpdatedImportSpecifierFromMovedSourceFiles(global::Ts.Compiler.Program program, SourceFile sourceFile, StringLiteralLikeNode importLiteral, PathUpdater oldToNew, string importingSourceFileName, global::Ts.ModuleSpecifiers.UserPreferences userPreferences)
    {
        var resolutionMode = program.GetModeForUsageLocation(sourceFile, importLiteral);
        foreach (var candidate in program.SourceFiles())
        {
            var (newFileName, ok) = oldToNew(candidate.FileName());
            if (!ok)
            {
                continue;
            }

            var oldSpecifier = global::Ts.ModuleSpecifiers.ModuleSpecifiersUpdate.UpdateModuleSpecifier(
                program.Options(),
                program,
                sourceFile,
                importingSourceFileName,
                importLiteral.Text(),
                candidate.FileName(),
                userPreferences,
                new global::Ts.ModuleSpecifiers.ModuleSpecifierOptions
                {
                    OverrideImportMode = resolutionMode,
                });
            if (oldSpecifier != importLiteral.Text())
            {
                continue;
            }

            return global::Ts.ModuleSpecifiers.ModuleSpecifiersUpdate.UpdateModuleSpecifier(
                program.Options(),
                program,
                sourceFile,
                importingSourceFileName,
                importLiteral.Text(),
                newFileName,
                userPreferences,
                new global::Ts.ModuleSpecifiers.ModuleSpecifierOptions
                {
                    OverrideImportMode = resolutionMode,
                });
        }
        return "";
    }

    // file_rename.go:341-343 — createStringTextRange (Go types the node as
    // ast.LiteralLikeNode; the only caller passes a StringLiteralLikeNode).
    private static global::Ts.Core.TextRange CreateStringTextRange(SourceFile sourceFile, Node node)
    {
        return new global::Ts.Core.TextRange(global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false /*includeJSDoc*/) + 1, node.End - 1);
    }

    // file_rename.go:345-353 — getTsConfigObjectLiteralExpression.
    private static global::Ts.Ast.ObjectLiteralExpression? GetTsConfigObjectLiteralExpression(SourceFile tsConfigSourceFile)
    {
        if (tsConfigSourceFile != null && tsConfigSourceFile.Statements != null && tsConfigSourceFile.Statements.Nodes.Length > 0)
        {
            var expression = tsConfigSourceFile.Statements.Nodes[0].Expression();
            if (NodeTests.IsObjectLiteralExpression(expression!))
            {
                return expression!.AsObjectLiteralExpression();
            }
        }
        return null;
    }

    // file_rename.go:355-367 — forEachObjectProperty.
    private static void ForEachObjectProperty(global::Ts.Ast.ObjectLiteralExpression objectLiteral, Action<global::Ts.Ast.PropertyAssignment, string> cb)
    {
        if (objectLiteral == null)
        {
            return;
        }
        foreach (var property in objectLiteral.Properties!.Nodes)
        {
            if (!NodeTests.IsPropertyAssignment(property))
            {
                continue;
            }
            if (AstUtilities.TryGetTextOfPropertyName(property.Name()!, out var name))
            {
                cb(property.AsPropertyAssignment(), name);
            }
        }
    }

    // file_rename.go:369-371 — relativePathFromDirectory.
    private static string RelativePathFromDirectory(string fromDirectory, string to, bool useCaseSensitiveFileNames)
    {
        return global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(fromDirectory, to, new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = useCaseSensitiveFileNames });
    }

    // file_rename.go:373-375 — relativeImportPathFromDirectory.
    private static string RelativeImportPathFromDirectory(string fromDirectory, string to, bool useCaseSensitiveFileNames)
    {
        return global::Ts.TsPath.TsPath.EnsurePathIsNonModuleName(RelativePathFromDirectory(fromDirectory, to, useCaseSensitiveFileNames));
    }

    // file_rename.go:377-382 — isAmbientModuleSymbol.
    private static bool IsAmbientModuleSymbol(global::Ts.Ast.Symbol? symbol)
    {
        if (symbol == null)
        {
            return false;
        }
        // Go: slices.ContainsFunc(symbol.Declarations, ast.IsModuleWithStringLiteralName)
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (AstUtilities.IsModuleWithStringLiteralName(declaration))
            {
                return true;
            }
        }
        return false;
    }
}

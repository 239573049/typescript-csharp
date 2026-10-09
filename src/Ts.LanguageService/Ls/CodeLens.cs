// Ported from tsgo internal/ls/codelens.go (1:1).
//
// The lsproto.CodeLens / CodeLensesOrNull hosts live in the Ts.LsProto block below —
// the generated surface skips them ("temporarily hosted in Ts.Ls subsets", see
// Protocol.g.cs), following the Command-in-CodeActions.cs pattern. Go's
// (T, error) pairs collapse to the response values; `locale.FromContext(ctx)` maps to
// Locale.Default (see CodeActions.FixClassIncorrectlyImplementsInterface.cs:5) and the
// cross-project orchestrator rides CrossProject.cs. Positions are UTF-16 code units
// throughout (LsDeps converters).

using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Core; // Tristate extension methods (IsTrue/IsFalse)
using Ts.Diagnostics; // Message.Localize extension

namespace Ts.Ls
{
    public sealed partial class LanguageService
    {
        // codelens.go:15-55 — ProvideCodeLenses.
        public Ts.LsProto.CodeLensesOrNull ProvideCodeLenses(CancellationToken ctx, Ts.LsProto.DocumentUri documentURI)
        {
            var file = GetProgramAndFile(documentURI);

            var userPrefs = UserPreferences().CodeLens;
            if (!userPrefs.ReferencesCodeLensEnabled.IsTrue() && !userPrefs.ImplementationsCodeLensEnabled.IsTrue())
            {
                return new Ts.LsProto.CodeLensesOrNull();
            }

            // Keeps track of the last symbol to avoid duplicating code lenses across overloads.
            global::Ts.Ast.Symbol? lastSymbol = null;
            var result = new List<Ts.LsProto.CodeLens>();

            bool Visit(Node node)
            {
                if (ctx.IsCancellationRequested)
                {
                    return true;
                }

                var currentSymbol = node.Symbol();
                if (!ReferenceEquals(lastSymbol, currentSymbol))
                {
                    lastSymbol = currentSymbol;

                    if (userPrefs.ReferencesCodeLensEnabled.IsTrue() && IsValidReferenceLensNode(node, userPrefs))
                    {
                        result.Add(NewCodeLensForNode(documentURI, file, node, Ts.LsProto.CodeLensKind.References));
                    }

                    if (userPrefs.ImplementationsCodeLensEnabled.IsTrue() && IsValidImplementationsCodeLensNode(node, userPrefs))
                    {
                        result.Add(NewCodeLensForNode(documentURI, file, node, Ts.LsProto.CodeLensKind.Implementations));
                    }
                }

                var savedLastSymbol = lastSymbol;
                node.ForEachChild(Visit);
                lastSymbol = savedLastSymbol;
                return false;
            }

            Visit(file.AsNode());

            return new Ts.LsProto.CodeLensesOrNull
            {
                CodeLenses = result.ToArray(),
            };
        }

        // codelens.go:57-130 — ResolveCodeLens.
        public Ts.LsProto.CodeLens ResolveCodeLens(
            CancellationToken ctx,
            Ts.LsProto.CodeLens codeLens,
            string? showLocationsCommandName,
            CrossProjectOrchestrator? orchestrator)
        {
            var uri = codeLens.Data!.Uri;
            var textDoc = new Ts.LsProto.TextDocumentIdentifier { Uri = uri };
            // PORT-NOTE: locale.FromContext(ctx) → Locale.Default.
            var locale = global::Ts.Locale.Locale.Default;
            Ts.LsProto.Location[]? locs = null;
            var lensTitle = "";
            // Go switches on the CodeLensKind string enum.
            if (codeLens.Data.Kind == Ts.LsProto.CodeLensKind.References)
            {
                var referencesResp = ProvideReferences(ctx, new Ts.LsProto.ReferenceParams
                {
                    TextDocument = textDoc,
                    Position = codeLens.Range.Start,
                    Context = new Ts.LsProto.ReferenceContext
                    {
                        // Don't include the declaration in the references count.
                        IncludeDeclaration = false,
                    },
                }, orchestrator);
                if (referencesResp.Locations != null)
                {
                    locs = referencesResp.Locations;
                }

                if ((locs?.Length ?? 0) == 1)
                {
                    lensTitle = global::Ts.Diagnostics.Diagnostics.X_1_reference.Localize(locale);
                }
                else
                {
                    lensTitle = global::Ts.Diagnostics.Diagnostics.X_0_references.Localize(locale, locs?.Length ?? 0);
                }
            }
            else if (codeLens.Data.Kind == Ts.LsProto.CodeLensKind.Implementations)
            {
                var implementations = ProvideImplementationsEx(
                    ctx,
                    new Ts.LsProto.ImplementationParams
                    {
                        TextDocument = textDoc,
                        Position = codeLens.Range.Start,
                    },
                    // "Force" link support to be false so that we only get `Locations` back,
                    // and don't include the "current" node in the results.
                    new SymbolEntryTransformOptions
                    {
                        RequireLocationsResult = true,
                        DropOriginNodes = true,
                    },
                    orchestrator);

                if (implementations.Locations != null)
                {
                    locs = implementations.Locations;
                }

                if ((locs?.Length ?? 0) == 1)
                {
                    lensTitle = global::Ts.Diagnostics.Diagnostics.X_1_implementation.Localize(locale);
                }
                else
                {
                    lensTitle = global::Ts.Diagnostics.Diagnostics.X_0_implementations.Localize(locale, locs?.Length ?? 0);
                }
            }

            var cmd = new Ts.LsProto.Command
            {
                Title = lensTitle,
            };
            if ((locs?.Length ?? 0) > 0 && showLocationsCommandName != null)
            {
                cmd.CommandName = showLocationsCommandName;
                // locs is non-null here: the guard above requires a positive length.
                // Go: []any{uri, codeLens.Range.Start, locs} — LSPAny elements are
                // JsonElement in the source-generated surface.
                cmd.Arguments =
                [
                    System.Text.Json.JsonSerializer.SerializeToElement(uri, Ts.LsProto.LspJsonContext.Default.DocumentUri),
                    System.Text.Json.JsonSerializer.SerializeToElement(codeLens.Range.Start, Ts.LsProto.LspJsonContext.Default.Position),
                    System.Text.Json.JsonSerializer.SerializeToElement(locs!, Ts.LsProto.LspJsonContext.Default.LocationArray),
                ];
            }

            codeLens.Command = cmd;
            return codeLens;
        }

        // codelens.go:132-150 — newCodeLensForNode.
        private Ts.LsProto.CodeLens NewCodeLensForNode(Ts.LsProto.DocumentUri fileUri, SourceFile file, Node node, Ts.LsProto.CodeLensKind kind)
        {
            var nodeForRange = node;
            var nodeName = node.Name();
            if (nodeName != null)
            {
                nodeForRange = nodeName;
            }
            var pos = global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), nodeForRange.Pos);

            return new Ts.LsProto.CodeLens
            {
                Range = new Ts.LsProto.Range
                {
                    Start = LsDeps.PositionToLineAndCharacter(file, pos),
                    End = LsDeps.PositionToLineAndCharacter(file, node.End),
                },
                Data = new Ts.LsProto.CodeLensData
                {
                    Kind = kind,
                    Uri = fileUri,
                },
            };
        }

        // codelens.go:152-177 — isValidImplementationsCodeLensNode.
        private static bool IsValidImplementationsCodeLensNode(Node node, CodeLensUserPreferences userPrefs)
        {
            switch (node.Kind)
            {
                // Always show on interfaces
                case SyntaxKind.InterfaceDeclaration:
                    // TODO: ast.KindTypeAliasDeclaration?
                    return true;

                // If configured, show on interface methods
                case SyntaxKind.MethodSignature:
                    return userPrefs.ImplementationsCodeLensShowOnInterfaceMethods.IsTrue() && node.Parent!.Kind == SyntaxKind.InterfaceDeclaration;

                // If configured, show on all class methods - but not private ones.
                case SyntaxKind.MethodDeclaration:
                    if (userPrefs.ImplementationsCodeLensShowOnAllClassMethods.IsTrue() && node.Parent!.Kind == SyntaxKind.ClassDeclaration)
                    {
                        return !HasModifier(node, ModifierFlags.Private) && node.Name()!.Kind != SyntaxKind.PrivateIdentifier;
                    }
                    goto case SyntaxKind.ClassDeclaration; // Go fallthrough

                // Always show on abstract classes/properties/methods
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.Constructor:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.PropertyDeclaration:
                    return HasModifier(node, ModifierFlags.Abstract);
            }

            return false;
        }

        // codelens.go:179-207 — isValidReferenceLensNode.
        private static bool IsValidReferenceLensNode(Node node, CodeLensUserPreferences userPrefs)
        {
            switch (node.Kind)
            {
                case SyntaxKind.FunctionDeclaration:
                    if (userPrefs.ReferencesCodeLensShowOnAllFunctions.IsTrue())
                    {
                        return true;
                    }
                    goto case SyntaxKind.VariableDeclaration; // Go fallthrough

                case SyntaxKind.VariableDeclaration:
                    return (AstUtilities.GetCombinedModifierFlags(node) & ModifierFlags.Export) != 0;

                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.TypeAliasDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.EnumMember:
                    return true;

                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.MethodSignature:
                case SyntaxKind.Constructor:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.PropertySignature:
                    // Don't show if child and parent have same start
                    // For https://github.com/microsoft/vscode/issues/90396
                    // !!!

                    switch (node.Parent!.Kind)
                    {
                        case SyntaxKind.ClassDeclaration:
                        case SyntaxKind.InterfaceDeclaration:
                        case SyntaxKind.TypeLiteral:
                            return true;
                    }
                    break;
            }

            return false;
        }

        // ast/utilities.go:4113-4115 — HasModifier (the Ast cluster hosts HasSyntacticModifier
        // under this name; the Go HasModifier wrapper is hosted here for the codelens gates).
        private static bool HasModifier(Node node, ModifierFlags flags) => (node.ModifierFlags() & flags) != 0;
    }
}

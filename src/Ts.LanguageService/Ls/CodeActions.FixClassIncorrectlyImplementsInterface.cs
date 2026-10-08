// Ported from tsgo internal/ls/codeactions_fixclassincorrectlyimplementsinterface.go (whole
// file): the FixClassIncorrectlyImplementsInterfaceProvider — "implement interface" quick fixes
// that synthesize missing members through the missing-member fixer.
//
// PORT-NOTE: `locale.FromContext(ctx)` → Locale.Default, `change.NewTracker(ctx, ...)` drops
// ctx (see Tracker.cs header), and `(T, error)` returns → nullable/throw per plan/05 §4.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Checker; // CheckerLsExports extension methods (GetNumberType, GetStringType, ...)
using Ts.Core;
using Ts.Diagnostics; // Message.Localize extension

namespace Ts.Ls;

// codeactions_fixclassincorrectlyimplementsinterface.go:19-24 — fix id and error codes.
public static partial class CodeActions
{
    internal const string fixClassIncorrectlyImplementsInterfaceFixID = "fixClassIncorrectlyImplementsInterface";

    internal static readonly int[] fixClassIncorrectlyImplementsInterfaceErrorCodes =
    [
        global::Ts.Diagnostics.Diagnostics.Class_0_incorrectly_implements_interface_1.Code,
        global::Ts.Diagnostics.Diagnostics.Class_0_incorrectly_implements_class_1_Did_you_mean_to_extend_1_and_inherit_its_members_as_a_subclass.Code,
    ];

    // codeactions_fixclassincorrectlyimplementsinterface.go:26-31 —
    // FixClassIncorrectlyImplementsInterfaceProvider is the CodeFixProvider for
    // "class incorrectly implements interface" fixes.
    public static readonly CodeFixProvider FixClassIncorrectlyImplementsInterfaceProvider = new()
    {
        ErrorCodes = fixClassIncorrectlyImplementsInterfaceErrorCodes,
        GetCodeActions = LanguageService.GetCodeActionsToFixClassIncorrectlyImplementsInterface,
        FixIds = [fixClassIncorrectlyImplementsInterfaceFixID],
        GetAllCodeActions = LanguageService.GetAllCodeActionsToFixClassIncorrectlyImplementsInterface,
    };
}

public sealed partial class LanguageService
{
    // codeactions_fixclassincorrectlyimplementsinterface.go:33-67 —
    // getCodeActionsToFixClassIncorrectlyImplementsInterface produces one action per implemented
    // interface with unimplemented members.
    internal static CodeAction[]? GetCodeActionsToFixClassIncorrectlyImplementsInterface(CancellationToken context, CodeFixContext fixContext)
    {
        var classDeclaration = CodeActions.GetClass(fixContext.SourceFile, fixContext.Span);
        if (classDeclaration == null)
        {
            return null;
        }

        var implementsTypes = AstUtilities.GetImplementsTypeNodes(classDeclaration);
        var locale = global::Ts.Locale.Locale.Default;

        var (typeChecker, done) = fixContext.Program.GetTypeCheckerForFile(context, fixContext.SourceFile);
        try
        {
            var actions = new List<CodeAction>();
            foreach (var implementedTypeNode in implementsTypes)
            {
                var changeTracker = global::Ts.Ls.Change.Tracker.NewTracker(fixContext.Program.Options(), fixContext.LS.FormatOptions(), fixContext.LS.Converters());
                var importAdder = CodeActions.CreateImportAdder(context, fixContext, typeChecker);

                CodeActions.AddChanges(context, fixContext, changeTracker, importAdder, typeChecker, classDeclaration, implementedTypeNode);
                var changes = CodeActions.GetChanges(changeTracker, importAdder, fixContext.SourceFile);
                if (changes == null || changes.Count == 0)
                {
                    continue;
                }

                actions.Add(new CodeAction
                {
                    Description = global::Ts.Diagnostics.Diagnostics.Implement_interface_0.Localize(locale, global::Ts.Scanner.Scanner.GetTextOfNode(implementedTypeNode)),
                    Changes = [.. changes],
                    FixID = CodeActions.fixClassIncorrectlyImplementsInterfaceFixID,
                    FixAllDescription = global::Ts.Diagnostics.Diagnostics.Implement_all_unimplemented_interfaces.Localize(locale),
                });
            }
            return actions.ToArray();
        }
        finally
        {
            done();
        }
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:69-105 —
    // getAllCodeActionsToFixClassIncorrectlyImplementsInterface folds every fix-all-eligible
    // class in the file into a single combined action.
    internal static CombinedCodeActions? GetAllCodeActionsToFixClassIncorrectlyImplementsInterface(CancellationToken context, CodeFixContext fixContext)
    {
        var (typeChecker, done) = fixContext.Program.GetTypeCheckerForFile(context, fixContext.SourceFile);
        try
        {
            var changeTracker = global::Ts.Ls.Change.Tracker.NewTracker(fixContext.Program.Options(), fixContext.LS.FormatOptions(), fixContext.LS.Converters());
            var importAdder = CodeActions.CreateImportAdder(context, fixContext, typeChecker);

            var seenClassDeclarations = new global::Ts.Collections.Set<Node>();

            foreach (var diag in GetAllDiagnostics(context, fixContext.Program, fixContext.SourceFile))
            {
                if (CodeActions.ContainsErrorCode(CodeActions.fixClassIncorrectlyImplementsInterfaceErrorCodes, diag.Code()))
                {
                    var classDeclaration = CodeActions.GetClass(fixContext.SourceFile, new TextRange(diag.Pos(), diag.End() - diag.Pos()));
                    if (classDeclaration == null)
                    {
                        continue;
                    }
                    if (seenClassDeclarations.AddIfAbsent(classDeclaration))
                    {
                        var implementsTypes = AstUtilities.GetImplementsTypeNodes(classDeclaration);
                        foreach (var implementedTypeNode in implementsTypes)
                        {
                            CodeActions.AddChanges(context, fixContext, changeTracker, importAdder, typeChecker, classDeclaration, implementedTypeNode);
                        }
                    }
                }
            }

            var changes = CodeActions.GetChanges(changeTracker, importAdder, fixContext.SourceFile);
            if (changes == null || changes.Count == 0)
            {
                return null;
            }

            return new CombinedCodeActions
            {
                Description = global::Ts.Diagnostics.Diagnostics.Implement_all_unimplemented_interfaces.Localize(global::Ts.Locale.Locale.Default),
                Changes = [.. changes],
            };
        }
        finally
        {
            done();
        }
    }
}

// Package-level helpers of codeactions_fixclassincorrectlyimplementsinterface.go (Go package
// funcs → static partial class, mirroring the other CodeActions* port files).
public static partial class CodeActions
{
    // codeactions_fixclassincorrectlyimplementsinterface.go:107-134 — addChanges synthesizes the
    // index signatures and members that `classDeclaration` is missing against
    // `implementedTypeNode`.
    internal static void AddChanges(CancellationToken context, CodeFixContext fixContext, global::Ts.Ls.Change.Tracker changeTracker,
        global::Ts.Ls.AutoImport.ImportAdder? importAdder, global::Ts.Checker.Checker typeChecker, Node classDeclaration, Node implementedTypeNode)
    {
        var missingMemberFixer = new MissingMemberFixer(changeTracker, fixContext.Program, typeChecker, fixContext.LS.UserPreferences(), importAdder, global::Ts.Locale.Locale.Default);
        var constructor = GetConstructor(classDeclaration);
        var implementedType = typeChecker.GetTypeAtLocation(implementedTypeNode);
        var classType = typeChecker.GetTypeAtLocation(classDeclaration);

        if (typeChecker.GetNumberIndexType(classType) == null)
        {
            var member = missingMemberFixer.CreateIndexSignatureDeclarationFromType(classDeclaration, implementedType, typeChecker.GetNumberType()!);
            if (member != null)
            {
                InsertInterfaceMemberNode(changeTracker, fixContext.SourceFile, classDeclaration, constructor, member);
            }
        }

        if (typeChecker.GetStringIndexType(classType) == null)
        {
            var member = missingMemberFixer.CreateIndexSignatureDeclarationFromType(classDeclaration, implementedType, typeChecker.GetStringType()!);
            if (member != null)
            {
                InsertInterfaceMemberNode(changeTracker, fixContext.SourceFile, classDeclaration, constructor, member);
            }
        }

        var missingMembers = GetMissingMembers(typeChecker, classDeclaration, [implementedType]);
        foreach (var member in missingMembers)
        {
            var memberNodes = missingMemberFixer.CreateMemberFromSymbol(member, classDeclaration, fixContext.SourceFile, null /*body*/, PreserveOptionalFlags.All);
            foreach (var memberNode in memberNodes ?? [])
            {
                InsertInterfaceMemberNode(changeTracker, fixContext.SourceFile, classDeclaration, constructor!, memberNode);
            }
        }
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:136-142 — getChanges merges the
    // tracker's edits for the file with the import adder's edits.
    internal static List<global::Ts.LsProto.TextEdit>? GetChanges(global::Ts.Ls.Change.Tracker changeTracker, global::Ts.Ls.AutoImport.ImportAdder? importAdder, SourceFile sourceFile)
    {
        var fileChanges = changeTracker.GetChanges().GetValueOrDefault(sourceFile.FileName());
        if (importAdder != null && importAdder.HasFixes())
        {
            fileChanges ??= [];
            fileChanges.AddRange(importAdder.Edits() ?? []);
        }
        return fileChanges;
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:144-150 — insertInterfaceMemberNode
    // appends the member after the constructor, or at the start of the class body.
    internal static void InsertInterfaceMemberNode(global::Ts.Ls.Change.Tracker changeTracker, SourceFile sourceFile, Node classDeclaration, Node? constructor, Node member)
    {
        if (constructor == null)
        {
            changeTracker.InsertMemberAtStart(sourceFile, classDeclaration, member);
        }
        else
        {
            changeTracker.InsertNodeAfter(sourceFile, constructor, member);
        }
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:152-158 — getClass.
    internal static Node? GetClass(SourceFile sourceFile, TextRange span)
    {
        var token = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, span.Pos);
        if (token == null)
        {
            return null;
        }
        return AstUtilities.GetContainingClass(token);
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:160-170 — getConstructor.
    internal static Node? GetConstructor(Node classDeclaration)
    {
        if (classDeclaration == null || classDeclaration.MemberList() == null)
        {
            return null;
        }
        foreach (var member in classDeclaration.MemberList()!.Nodes)
        {
            if (member != null && NodeTests.IsConstructorDeclaration(member))
            {
                return member;
            }
        }
        return null;
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:172-201 — getMissingMembers.
    internal static List<global::Ts.Ast.Symbol> GetMissingMembers(global::Ts.Checker.Checker typeChecker, Node classDeclaration, global::Ts.Checker.Type[] implementedTypes)
    {
        var inheritedMembers = GetInheritedMembers(typeChecker, classDeclaration);
        var seenMembers = new Dictionary<string, global::Ts.Ast.Symbol>(StringComparer.Ordinal);

        Dictionary<string, global::Ts.Ast.Symbol>? classMembers = null;
        if (classDeclaration.Symbol() != null)
        {
            classMembers = classDeclaration.Symbol()!.Members;
        }

        var missingMembers = new List<global::Ts.Ast.Symbol>();
        foreach (var implementedType in implementedTypes)
        {
            foreach (var symbol in typeChecker.GetPropertiesOfType(implementedType))
            {
                if (symbol == null)
                {
                    continue;
                }
                if (classMembers != null && classMembers.TryGetValue(symbol.Name, out var classMember) && classMember != null)
                {
                    continue;
                }
                if ((inheritedMembers.TryGetValue(symbol.Name, out var inheritedMember) && inheritedMember != null) ||
                    (seenMembers.TryGetValue(symbol.Name, out var seenMember) && seenMember != null))
                {
                    continue;
                }
                var flags = global::Ts.Checker.Checker.GetDeclarationModifierFlagsFromSymbol(symbol);
                if ((flags & ModifierFlags.Private) == 0)
                {
                    seenMembers[symbol.Name] = symbol;
                    missingMembers.Add(symbol);
                }
            }
        }
        return missingMembers;
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:203-225 — getInheritedMembers.
    internal static Dictionary<string, global::Ts.Ast.Symbol> GetInheritedMembers(global::Ts.Checker.Checker typeChecker, Node classDeclaration)
    {
        var typeNode = AstUtilities.GetClassExtendsHeritageElement(classDeclaration);
        if (typeNode == null)
        {
            return new Dictionary<string, global::Ts.Ast.Symbol>();
        }

        var baseType = typeChecker.GetTypeAtLocation(typeNode.AsNode());
        if (baseType == null)
        {
            return new Dictionary<string, global::Ts.Ast.Symbol>();
        }

        var inheritedMembers = new Dictionary<string, global::Ts.Ast.Symbol>(StringComparer.Ordinal);
        foreach (var symbol in typeChecker.GetPropertiesOfType(baseType))
        {
            if (symbol == null)
            {
                continue;
            }
            var flags = global::Ts.Checker.Checker.GetDeclarationModifierFlagsFromSymbol(symbol);
            if ((flags & ModifierFlags.Private) == 0)
            {
                inheritedMembers[symbol.Name] = symbol;
            }
        }
        return inheritedMembers;
    }

    // codeactions_fixclassincorrectlyimplementsinterface.go:227-236 — createImportAdder.
    internal static global::Ts.Ls.AutoImport.ImportAdder? CreateImportAdder(CancellationToken context, CodeFixContext fixContext, global::Ts.Checker.Checker typeChecker)
    {
        var (view, err) = fixContext.LS.GetPreparedAutoImportView(fixContext.SourceFile);
        if (err != null)
        {
            throw err; // (View, error) → throw per plan/05 §4, matching GetPreparedAutoImportView callers
        }
        if (view == null)
        {
            return null;
        }
        return global::Ts.Ls.AutoImport.AutoImportApi.NewImportAdder(fixContext.Program, typeChecker, fixContext.SourceFile, view, fixContext.LS.FormatOptions(), fixContext.LS.Converters(), fixContext.LS.UserPreferences());
    }
}

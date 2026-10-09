// Ported from tsgo internal/project/untitled_test.go (whole file).
//
// PORT-NOTEs:
//  * gotest.tools assert → xUnit (argument order swapped: actual/expected); Go message
//    strings are kept in the Assert.True message where xUnit has no overload for them.
//  * Go's t.Logf diagnostics-only logging is dropped (MTP swallows console output; the
//    assertions it fed are kept verbatim).
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background; projecttestutil.WithRequestID →
//    SessionUtils.WithRequestID. The LanguageService-facing calls take a
//    CancellationToken in C# (TestContext.Current.CancellationToken).
//  * C# ProvideDefinition takes (fileName, position) where Go takes a DocumentUri
//    (Go converts via uri.FileName() internally); the Go "file:///Untitled-2.ts" URI
//    maps to the "/Untitled-2.ts" file name passed here.
//  * ProvideReferences returns the LocationsOrNull directly (Go's (resp, error) collapses
//    per the port's error conventions), and is internal — visible to this test project
//    via InternalsVisibleTo.

using System;
using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using Position = global::Ts.LsProto.Position;
using ReferenceContext = global::Ts.LsProto.ReferenceContext;
using ReferenceParams = global::Ts.LsProto.ReferenceParams;
using TextDocumentIdentifier = global::Ts.LsProto.TextDocumentIdentifier;

namespace Ts.Project.Tests;

public sealed class UntitledTests
{
    // untitled_test.go:15-98 — TestUntitledReferences.
    [Fact]
    public void TestUntitledReferences()
    {
        // First test the URI conversion functions to understand the issue
        var untitledURI = new DocumentUri("untitled:Untitled-2");
        string convertedFileName = global::Ts.LsProto.DocumentUriExtensions.FileName(untitledURI);

        var backToURI = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(convertedFileName);

        if (backToURI.Value != untitledURI.Value)
        {
            Assert.Fail($"Round-trip conversion failed: '{untitledURI.Value}' -> '{convertedFileName}' -> '{backToURI.Value}'");
        }

        // Create a test case that simulates how untitled files should work
        string testContent = "let x = 42;\n\nx\n\nx++;";

        // Use the converted filename that DocumentURIToFileName would produce
        // untitledFileName := convertedFileName // "^/untitled/ts-nul-authority/Untitled-2"

        // Set up the file system with an untitled file -
        // But use a regular file first to see the current behavior
        var files = new Dictionary<string, object?>
        {
            ["/Untitled-2.ts"] = testContent,
        };

        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(files);

        var ctx = SessionUtils.WithRequestID(RequestContext.Background);
        session.DidOpenFile(ctx, "file:///Untitled-2.ts", 1, testContent, LanguageKind.TypeScript.Value!);

        // Get language service
        (global::Ts.Ls.LanguageService? languageService, Exception? err) = session.GetLanguageService(ctx, "file:///Untitled-2.ts");
        Assert.Null(err);

        // Test the filename that the source file reports
        var program = languageService!.GetProgram();
        program.GetSourceFile("/Untitled-2.ts");

        // Call ProvideReferences using the LSP method
        DocumentUri uri = "file:///Untitled-2.ts";
        var lspPosition = new Position { Line = 2, Character = 0 }; // Line 3, character 1 (0-indexed)

        var refParams = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = lspPosition,
            Context = new ReferenceContext { IncludeDeclaration = true },
        };

        var resp = languageService.ProvideReferences(TestContext.Current.CancellationToken, refParams, null);

        var refs = resp.Locations!;

        // We expect to find 3 references
        Assert.True(refs.Length == 3, $"Expected 3 references, got {refs.Length}");

        // Also test definition using ProvideDefinition. Go passes the LSP position
        // {Line: 2, Character: 0}; C#'s worker entry takes the absolute offset, which for
        // "let x = 42;\n\nx\n\nx++;" line 2/char 0 is 13 ("x" on the third line).
        languageService.ProvideDefinition("/Untitled-2.ts", 13, TestContext.Current.CancellationToken);
    }

    // untitled_test.go:100-160 — TestUntitledFileInInferredProject.
    [Fact]
    public void TestUntitledFileInInferredProject()
    {
        // Test that untitled files are properly handled in inferred projects
        string testContent = "let x = 42;\n\nx\n\nx++;";

        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(new Dictionary<string, object?>());

        var ctx = SessionUtils.WithRequestID(RequestContext.Background);

        // Open untitled files - these should create an inferred project
        session.DidOpenFile(ctx, "untitled:Untitled-1", 1, "x\n\n", LanguageKind.TypeScript.Value!);
        session.DidOpenFile(ctx, "untitled:Untitled-2", 1, testContent, LanguageKind.TypeScript.Value!);

        var snapshot = session.Snapshot()!;

        // Should have an inferred project
        Assert.NotNull(snapshot.ProjectCollection.InferredProject());

        // Get language service for the untitled file
        (global::Ts.Ls.LanguageService? languageService, Exception? err) = session.GetLanguageService(ctx, "untitled:Untitled-2");
        Assert.Null(err);

        var program = languageService!.GetProgram();
        string untitledFileName = global::Ts.LsProto.DocumentUriExtensions.FileName(new DocumentUri("untitled:Untitled-2"));
        var sourceFile = program.GetSourceFile(untitledFileName);
        Assert.NotNull(sourceFile);
        Assert.Equal(testContent, sourceFile!.Text());

        // Test references on 'x' at position 13 (line 3, after "let x = 42;\n\n")
        DocumentUri uri = "untitled:Untitled-2";
        var lspPosition = new Position { Line = 2, Character = 0 }; // Line 3, character 1 (0-indexed)

        var refParams = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = lspPosition,
            Context = new ReferenceContext { IncludeDeclaration = true },
        };

        var resp = languageService.ProvideReferences(TestContext.Current.CancellationToken, refParams, null);

        var refs = resp.Locations!;
        foreach (var @ref in refs)
        {
            // All URIs should be untitled: URIs, not file: URIs
            Assert.True(@ref.Uri.Value.StartsWith("untitled:", StringComparison.Ordinal),
                $"Expected untitled: URI, got {@ref.Uri.Value}");
        }

        // We expect to find 4 references
        Assert.True(refs.Length == 4, $"Expected 4 references, got {refs.Length}");
    }

    // untitled_test.go:162-183 — TestImportsInUntitled.
    [Fact]
    public void TestImportsInUntitled()
    {
        var files = new Dictionary<string, object?>
        {
            // Make sure typings directory exists so it would actually try to fetch typings from this location
            [ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/somelib/index.d.ts"] = @"export const x: number;",
        };
        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(files);
        string content = "import \"https://deno.land/std@0.208.0/path/mod.ts\"\n\t\timport  \"./relative\"\n";
        DocumentUri uri1 = "untitled:Untitled-1";
        session.DidOpenFile(RequestContext.Background, uri1, 1, content, LanguageKind.TypeScript.Value!);

        // 2) Wait for ATA/background tasks to finish, then get a language service for the first file
        session.WaitForBackgroundTasks();
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, uri1);
        Assert.Null(err);
    }
}

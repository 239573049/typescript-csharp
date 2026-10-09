#!/usr/bin/env -S node --experimental-strip-types --no-warnings

// Fork of tsgo internal/fourslash/_scripts/convertFourslash.mts (plan/08-testing-strategy.md
// §7.2: "fork, not rewrite"). The parse half — parseFourslashStatement and its ~4,000 lines of
// command recognizers, the codeFix allow-lists, and the unparsed/manual handling — is kept as
// close to the Go original as the C# emission permits; only the emitting half
// (generateGoTest → generateCSharpTest and the per-command generate* helpers) is replaced, to
// emit C# xUnit v3 tests in the plan/08 §7.2 shape:
//   tests/Ts.Fourslash.Tests/Gen/<name>.g.cs, namespace Ts.Fourslash.Tests.Gen,
//   one class per file, test names keep the Go spelling (TestXxx), raw string literals for the
//   test content with quote-fence escalation.
//
// Usage: node --experimental-strip-types --no-warnings convert-fourslash-csharp.mts [submoduleRoot]
//   (or set TSCS_TYPESCRIPT_SUBMODULE). The default is <repo>/_submodules/TypeScript; the
//   TypeScript submodule checkout is READ-ONLY input (the strada tests/cases/fourslash tree).
//
// Supporting artifacts written by this script (so the corpus stays regenerable in one command):
//   tests/Ts.Fourslash.Tests/FourslashTestUtil.g.cs
//       — port of internal/fourslash/tests/util/util.go (the dot-imported helper package the
//         Go corpus references): completion constant lists + the Core/Ls constant shims the
//         emitted fragments reference (Core.TSTrue, Ls.SortText*, Ls.DeprecateSortText).
//   tests/Ts.Fourslash.Tests/Scripts/unparsedTests.txt
//       — the negative-space report, same format as Go's.
//
// Known emission deltas vs the Go original (each mechanical, driven by the C# target):
//   - `t *testing.T` receiver args and testutil.RecoverAndFail/t.Parallel are dropped; the
//     failing skip rides the existing Ts.Fourslash.Tests.SkipIfFailing mechanism instead.
//   - Go variadic spreads `X...` become C# collection spreads `[.. X]`.
//   - Go `ToAny(X)...` collapses to `[.. X]` (C# needs no any-widening shim).
//   - format.setOption's three Go statements (GetOptions/field assign/Configure) collapse to
//     one `f.ConfigureFormat(new FormatCodeSettings { ... })` call — the C# UserPreferences
//     does not carry a FormatCodeSettings field yet (the format wave owns it).
//   - `f.GetRangesByText().Get(k)` (Go collections.MultiMap.Get) becomes the C# Dictionary
//     indexer `f.GetRangesByText()[k]`.
//   - goTo.file path prefixes and server marking (MarkTestAsStradaServer) are preserved.

import * as fs from "fs";
import * as path from "path";
import * as ts from "typescript";
import * as url from "url";

const REPO_ROOT = path.resolve(import.meta.dirname, "..", "..");
const outputPath = (rel: string) => path.join(REPO_ROOT, rel);

// The TypeScript submodule root (READ-ONLY input). CLI arg wins, then TSCS_TYPESCRIPT_SUBMODULE,
// then this repo's default checkout. The strada fourslash cases live under tests/cases/fourslash.
const submoduleRoot = (): string =>
    process.argv[2] ??
    process.env["TSCS_TYPESCRIPT_SUBMODULE"] ??
    path.join(REPO_ROOT, "_submodules", "TypeScript");

const stradaFourslashPath = (): string => path.join(submoduleRoot(), "tests", "cases", "fourslash");

const goUtilGoPath = (): string =>
    process.env["TSCS_TSGO_ROOT"] ??
    path.join(REPO_ROOT, "..", "typescript-go-main");

let inputFileSet: Set<string> | undefined;

const manualTestsPath = path.join(import.meta.dirname, "manualTests.txt");

const outputDir = outputPath("tests/Ts.Fourslash.Tests/Gen");
const utilOutputPath = outputPath("tests/Ts.Fourslash.Tests/FourslashTestUtil.g.cs");
const scriptsDir = outputPath("tests/Ts.Fourslash.Tests/Scripts");
const unparsedFiles: { file: string; error: string; }[] = [];
const unparsedReportPath = path.join(scriptsDir, "unparsedTests.txt");
const crashingTestsPath = path.join(scriptsDir, "crashingTests.txt");

// Code fix IDs that have been implemented in the Go port (and thus in the emitted corpus).
// Tests for code fixes not in this set are rejected during conversion.
const allowedCodeFixIds = new Set([
    "fixMissingImport",
    "fixMissingTypeAnnotationOnExports",
    "fixClassIncorrectlyImplementsInterface",
]);

// File name prefixes for code fix tests that are allowed even without a fixId.
// These correspond to tests using verify.codeFix() or verify.codeFixAvailable()
// that don't include a fixId field.
const allowedCodeFixDescriptionPrefixes = [
    "Import ",
    "Add import from ",
    "Update import from ",
    "Implement interface '",
    "Change 'import' to 'import type'",
    "Add annotation of type",
    "Add return type",
    "Add satisfies and an inline type assertion",
    "Annotate types of properties expando function",
    "Extract default export to variable",
    "Extract base class to variable",
    "Extract binding expressions to variable",
    "Extract to variable and replace with",
    "Mark array literal as const",
];

function getManualTests(): Set<string> {
    if (!fs.existsSync(manualTestsPath)) {
        return new Set();
    }
    const manualTestsList = fs.readFileSync(manualTestsPath, "utf-8").split("\n").map(line => line.trim()).filter(line => line.length > 0);
    return new Set(manualTestsList);
}

// The crashing list: tests that crash the test process (stack overflow etc.) are emitted
// wrapped in `#if false` (plan/08 §7.4) — produced by fourslash-update-failing.mts.
function getCrashingTests(): Set<string> {
    if (!fs.existsSync(crashingTestsPath)) {
        return new Set();
    }
    return new Set(
        fs.readFileSync(crashingTestsPath, "utf-8")
            .split("\n").map(line => line.trim()).filter(line => line.length > 0),
    );
}

export async function main() {
    const args = process.argv.slice(2);
    const inputFilesPath = args[1];
    if (inputFilesPath) {
        const inputFiles = fs.readFileSync(inputFilesPath, "utf-8")
            .split("\n").map(line => line.trim())
            .filter(line => line.length > 0)
            .map(line => path.basename(line));
        inputFileSet = new Set(inputFiles);
    }

    fs.rmSync(outputDir, { recursive: true, force: true });
    fs.mkdirSync(outputDir, { recursive: true });
    fs.mkdirSync(scriptsDir, { recursive: true });

    emitFourslashTestUtil();

    const crashing = getCrashingTests();
    let emitted = 0;
    for (const name of parseTypeScriptFiles(getManualTests(), stradaFourslashPath(), crashing)) {
        emitted++;
        console.error(`emitted: ${name}`);
    }

    unparsedFiles.sort((a, b) => a.file.localeCompare(b.file, "en-US"));
    fs.writeFileSync(unparsedReportPath, unparsedFiles.map(({ file, error }) => `${file} parse error: ${JSON.stringify(error)}`).join("\n"), "utf-8");
    console.log(`Emitted ${emitted} tests to ${outputDir}`);
    console.log(`Failed to parse ${unparsedFiles.length} files. See ${unparsedReportPath} for details.`);
    console.log(`Wrote ${utilOutputPath}`);
}

function hasTSExtension(file: string): boolean {
    return file.endsWith(".ts") ||
        file.endsWith(".tsx");
}

function* parseTypeScriptFiles(manualTests: Set<string>, folder: string, crashingTests: Set<string>): Generator<string> {
    const files = fs.readdirSync(folder);

    for (const file of files) {
        const filePath = path.join(folder, file);
        const stat = fs.statSync(filePath);
        if (inputFileSet && !inputFileSet.has(file)) {
            continue;
        }

        if (stat.isDirectory()) {
            yield* parseTypeScriptFiles(manualTests, filePath, crashingTests);
        }
        else if (hasTSExtension(file) && !manualTests.has(file.slice(0, -3)) && file !== "fourslash.ts") {
            const content = fs.readFileSync(filePath, "utf-8");
            const isServer = filePath.split(path.sep).includes("server");
            try {
                const test = parseFileContent(file, content);
                if (test === NO_TEST) continue;
                const testContent = generateCSharpTest(test, isServer, crashingTests.has(`Test${testName(test.name)}`));
                const testPath = path.join(outputDir, `${test.name}.g.cs`);
                fs.writeFileSync(testPath, testContent, "utf-8");
                yield test.name;
            }
            catch (e) {
                const message = e instanceof Error ? e.message : String(e);
                console.error(`Error parsing file ${file}: ${message}`);
                unparsedFiles.push({ file, error: message });
            }
        }
    }
}

const NO_TEST: unique symbol = Symbol("NO_TEST");
type NoTest = typeof NO_TEST;

function parseFileContent(filename: string, content: string): GoTest | NoTest {
    const sourceFile = ts.createSourceFile("temp.ts", content, ts.ScriptTarget.Latest, true /*setParentNodes*/);
    const statements = sourceFile.statements;
    const goTest: GoTest = {
        name: filename.replace(".tsx", "").replace(".ts", "").replace(".", ""),
        content: getTestInput(content),
        commands: [],
    };
    for (const statement of statements) {
        const result = parseFourslashStatement(statement);
        goTest.commands.push(...result);
    }
    if (goTest.commands.length === 0) {
        console.error(`No commands parsed in file (skipping): ${filename}`);
        return NO_TEST;
    }
    validateCodeFixCommands(goTest.commands);
    return goTest;
}

function validateCodeFixCommands(commands: Cmd[]): void {
    const hasCodeFixCmd = commands.some(c => c.kind === "verifyCodeFix" || c.kind === "verifyCodeFixAvailable" || c.kind === "verifyCodeFixAll");
    if (!hasCodeFixCmd) {
        return;
    }
    // Every codeFixAll must use an allowed fixId.
    for (const cmd of commands) {
        if (cmd.kind === "verifyCodeFixAll" && !allowedCodeFixIds.has(cmd.fixId)) {
            throw new Error(`Unsupported code fix ID: ${cmd.fixId}`);
        }
    }
    // If there are codeFix/codeFixAvailable commands but no codeFixAll with an allowed ID,
    // the test is only accepted if its descriptions match allowed patterns.
    const hasAllowedCodeFixAll = commands.some(c => c.kind === "verifyCodeFixAll" && allowedCodeFixIds.has(c.fixId));
    const hasCodeFixOrAvailable = commands.some(c => c.kind === "verifyCodeFix" || c.kind === "verifyCodeFixAvailable");
    if (hasCodeFixOrAvailable && !hasAllowedCodeFixAll) {
        const allAllowed = commands.every(c => {
            if (c.kind === "verifyCodeFix") {
                return allowedCodeFixDescriptionPrefixes.some(p => c.description.startsWith(p));
            }
            if (c.kind === "verifyCodeFixAvailable") {
                // Empty descriptions means "assert no fixes available", which is always allowed.
                return c.descriptions.length === 0 || c.descriptions.every(d => allowedCodeFixDescriptionPrefixes.some(p => d.startsWith(p)));
            }
            return true;
        });
        if (!allAllowed) {
            throw new Error(`Code fix test has no allowed fixId and descriptions do not match any allowed prefix`);
        }
    }
}

function getTestInput(content: string): string {
    const lines = content.split("\n").map(line => line.endsWith("\r") ? line.slice(0, -1) : line);
    let testInput: string[] = [];
    for (const line of lines) {
        let newLine = "";
        if (line.startsWith("////")) {
            // C# raw string literals have no backtick-escaping problem; keep the stripped
            // line verbatim (the Go emitter had to splice `\` + "`" + \` around backticks).
            testInput.push(line.substring(4));
        }
        else if (line.startsWith("// @") || line.startsWith("//@")) {
            testInput.push(line);
        }
        // !!! preserve non-input comments?
    }

    // chomp leading spaces
    if (
        !testInput.some(line =>
            line.length != 0 &&
            !line.startsWith(" ") &&
            !line.startsWith("// ") &&
            !line.startsWith("//@")
        )
    ) {
        testInput = testInput.map(line => {
            if (line.startsWith(" ")) return line.substring(1);
            return line;
        });
    }
    return testInput.join("\n");
}

function getBadStatementText(statement: ts.Statement): string {
    if (ts.isExpressionStatement(statement) && ts.isCallExpression(statement.expression)) {
        return statement.expression.expression.getText() + "(...)";
    }
    return statement.getText();
}

interface VerifyAssertion {
    name: string;
    negated: boolean;
}

function parseVerifyAssertion(access: ts.PropertyAccessExpression): VerifyAssertion | undefined {
    if (ts.isIdentifier(access.expression) && access.expression.text === "verify") {
        return {
            name: access.name.text,
            negated: false,
        };
    }

    if (
        ts.isPropertyAccessExpression(access.expression) &&
        ts.isIdentifier(access.expression.expression) &&
        access.expression.expression.text === "verify" &&
        access.expression.name.text === "not"
    ) {
        return {
            name: access.name.text,
            negated: true,
        };
    }

    return undefined;
}

function isVerifyCompletionsCall(expression: ts.Expression): expression is ts.CallExpression {
    if (!ts.isCallExpression(expression) || !ts.isPropertyAccessExpression(expression.expression)) {
        return false;
    }
    const assertion = parseVerifyAssertion(expression.expression);
    return !!assertion && !assertion.negated && assertion.name === "completions";
}

function parseFourslashStatement(statement: ts.Statement): Cmd[] {
    if (ts.isVariableStatement(statement)) {
        // variable declarations (for ranges and markers), e.g. `const range = test.ranges()[0];`
        return [];
    }
    else if (ts.isEmptyStatement(statement)) {
        // Stray semicolons, e.g. `;;`
        return [];
    }
    else if (ts.isForOfStatement(statement)) {
        return parseForOfStatement(statement);
    }
    else if (ts.isExpressionStatement(statement) && ts.isCallExpression(statement.expression)) {
        const callExpression = statement.expression;
        if (!ts.isPropertyAccessExpression(callExpression.expression)) {
            throw new Error(`Expected property access expression, got ${callExpression.expression.getText()}`);
        }
        const accessExpression = callExpression.expression;
        const verifyAssertion = parseVerifyAssertion(accessExpression);

        if (verifyAssertion?.negated) {
            switch (verifyAssertion.name) {
                case "quickInfoExists":
                    return parseQuickInfoArgs("notQuickInfoExists", callExpression.arguments);
                case "codeFixAvailable":
                    return parseCodeFixAvailableArgs("notCodeFixAvailable", callExpression.arguments);
            }
            throw new Error(`Unrecognized fourslash statement: ${getBadStatementText(statement)}`);
        }

        const expression = accessExpression.expression;
        if (isVerifyCompletionsCall(expression) && accessExpression.name.text === "andApplyCodeAction") {
            return parseVerifyCompletionsArgs(expression.arguments, callExpression.arguments);
        }

        // `verify.(...)`
        if (verifyAssertion) {
            switch (verifyAssertion.name) {
                case "completions":
                    // `verify.completions(...)`
                    return parseVerifyCompletionsArgs(callExpression.arguments);
                case "applyCodeActionFromCompletion":
                    // `verify.applyCodeActionFromCompletion(...)`
                    return parseVerifyApplyCodeActionFromCompletionArgs(callExpression.arguments);
                case "importFixAtPosition":
                    // `verify.importFixAtPosition(...)`
                    return parseImportFixAtPositionArgs(callExpression.arguments);
                case "importFixModuleSpecifiers":
                    // `verify.importFixModuleSpecifiers(...)`
                    return parseImportFixModuleSpecifiersArgs(callExpression.arguments);
                case "currentLineContentIs":
                case "currentFileContentIs":
                case "indentationIs":
                case "indentationAtPositionIs":
                case "textAtCaretIs":
                    return parseCurrentContentIsArgs(verifyAssertion.name, callExpression.arguments);
                case "quickInfoAt":
                case "quickInfoExists":
                case "quickInfoIs":
                case "quickInfos":
                    // `verify.quickInfo...(...)`
                    return parseQuickInfoArgs(verifyAssertion.name, callExpression.arguments);
                case "organizeImports":
                    // `verify.organizeImports(...)`
                    return parseOrganizeImportsArgs(callExpression.arguments);
                case "baselineFindAllReferences":
                    // `verify.baselineFindAllReferences(...)`
                    return parseBaselineFindAllReferencesArgs(callExpression.arguments);
                case "baselineDocumentHighlights":
                    return parseBaselineDocumentHighlightsArgs(callExpression.arguments);
                case "baselineQuickInfo":
                    return parseBaselineQuickInfo(callExpression.arguments);
                case "baselineSignatureHelp":
                    return [parseBaselineSignatureHelp(callExpression.arguments)];
                case "signatureHelp":
                    return parseSignatureHelp(callExpression.arguments);
                case "noSignatureHelp":
                    return parseNoSignatureHelp(callExpression.arguments);
                case "signatureHelpPresentForTriggerReason":
                    return parseSignatureHelpPresentForTriggerReason(callExpression.arguments);
                case "noSignatureHelpForTriggerReason":
                    return parseNoSignatureHelpForTriggerReason(callExpression.arguments);
                case "baselineSmartSelection":
                    return [parseBaselineSmartSelection(callExpression.arguments)];
                case "baselineCallHierarchy":
                    return [parseBaselineCallHierarchy(callExpression.arguments)];
                case "baselineGoToDefinition":
                case "baselineGetDefinitionAtPosition":
                case "baselineGoToType":
                case "baselineGoToImplementation":
                case "baselineGoToSourceDefinition":
                    // Both `baselineGoToDefinition` and `baselineGetDefinitionAtPosition` take the same
                    // arguments, but differ in that...
                    //  - `verify.baselineGoToDefinition(...)` called getDefinitionAndBoundSpan
                    //  - `verify.baselineGetDefinitionAtPosition(...)` called getDefinitionAtPosition
                    // LSP doesn't have two separate commands though.
                    return parseBaselineGoToDefinitionArgs(verifyAssertion.name, callExpression.arguments);
                case "baselineRename":
                case "baselineRenameAtRangesWithText":
                    // `verify.baselineRename...(...)`
                    return parseBaselineRenameArgs(verifyAssertion.name, callExpression.arguments);
                case "baselineInlayHints":
                    return parseBaselineInlayHints(callExpression.arguments);
                case "baselineLinkedEditing":
                    return [{ kind: "verifyBaselineLinkedEditing" }];
                case "linkedEditing":
                    return parseVerifyLinkedEditing(callExpression.arguments);
                case "renameInfoSucceeded":
                case "renameInfoFailed":
                    return parseRenameInfo(verifyAssertion.name, callExpression.arguments);
                case "getEditsForFileRename":
                    return parseGetEditsForFileRename(callExpression.arguments);
                case "getSemanticDiagnostics":
                case "getSuggestionDiagnostics":
                case "getSyntacticDiagnostics":
                    return parseVerifyDiagnostics(verifyAssertion.name, callExpression.arguments);
                case "baselineSyntacticDiagnostics":
                case "baselineSyntacticAndSemanticDiagnostics":
                    return [{ kind: "verifyBaselineDiagnostics" }];
                case "navigateTo":
                    return parseVerifyNavigateTo(callExpression.arguments);
                case "outliningSpansInCurrentFile":
                case "outliningHintSpansInCurrentFile":
                    return parseOutliningSpansArgs(callExpression.arguments);
                case "navigationTree":
                    return parseVerifyNavTree(callExpression.arguments);
                case "navigationBar":
                    return []; // Deprecated.
                case "numberOfErrorsInCurrentFile":
                    return parseNumberOfErrorsInCurrentFile(callExpression.arguments);
                case "noErrors":
                    return [{ kind: "verifyNoErrors" }];
                case "errorExistsAtRange":
                    return parseErrorExistsAtRange(callExpression.arguments);
                case "currentLineContentIs":
                    return parseCurrentLineContentIs(callExpression.arguments);
                case "currentFileContentIs":
                    return parseCurrentFileContentIs(callExpression.arguments);
                case "errorExistsBetweenMarkers":
                    return parseErrorExistsBetweenMarkers(callExpression.arguments);
                case "errorExistsAfterMarker":
                    return parseErrorExistsAfterMarker(callExpression.arguments);
                case "errorExistsBeforeMarker":
                    return parseErrorExistsBeforeMarker(callExpression.arguments);
                case "codeFix":
                    return parseCodeFixArgs(callExpression.arguments);
                case "codeFixAvailable":
                    return parseCodeFixAvailableArgs(verifyAssertion.name, callExpression.arguments);
                case "rangeAfterCodeFix":
                    return parseRangeAfterCodeFixArgs(callExpression.arguments);
                case "codeFixAll":
                    return parseCodeFixAllArgs(callExpression.arguments);
                case "semanticClassificationsAre":
                    return parseSemanticClassificationsAre(callExpression.arguments);
                case "syntacticClassificationsAre":
                    return [];
                case "docCommentTemplateAt":
                    return [parseDocCommentTemplateAtArgs(callExpression.arguments)];
                case "noDocCommentTemplateAt":
                    return [parseNoDocCommentTemplateAtArgs(callExpression.arguments)];
            }
        }

        if (!ts.isIdentifier(expression)) {
            throw new Error(`Unrecognized fourslash statement: ${getBadStatementText(statement)}`);
        }

        // `goTo....`
        if (expression.text === "goTo") {
            return parseGoToArgs(callExpression.arguments, accessExpression.name.text);
        }
        // `edit....`
        if (expression.text === "edit") {
            const result = parseEditStatement(accessExpression.name.text, callExpression.arguments);
            return [result];
        }
        if (expression.text === "format") {
            return parseFormatStatement(accessExpression.name.text, callExpression.arguments);
        }
        // !!! other fourslash commands
    }
    throw new Error(`Unrecognized fourslash statement: ${getBadStatementText(statement)}`);
}

function parseForOfStatement(statement: ts.ForOfStatement): Cmd[] {
    const initializer = statement.initializer;
    let variableName: string | undefined;
    if (ts.isVariableDeclarationList(initializer) && initializer.declarations.length === 1 && ts.isIdentifier(initializer.declarations[0].name)) {
        variableName = initializer.declarations[0].name.text;
    }
    else if (ts.isIdentifier(initializer)) {
        variableName = initializer.text;
    }
    if (!variableName) {
        throw new Error(`Unrecognized fourslash statement: ${statement.getText()}`);
    }

    let rangeExpression: string;
    const exprText = statement.expression.getText();
    if (exprText === "test.markerNames()") {
        rangeExpression = "f.MarkerNames()";
    }
    else if (exprText === "test.markers()") {
        rangeExpression = "f.Markers()";
    }
    else if (ts.isArrayLiteralExpression(statement.expression)) {
        const elements = statement.expression.elements.map(element => {
            const text = getStringLiteralLike(element)?.text;
            if (text === undefined) {
                throw new Error(`Expected string literal in for-of array, got ${element.getText()}`);
            }
            return csStringLiteral(text);
        });
        rangeExpression = `new[]{${elements.join(", ")}}`;
    }
    else {
        throw new Error(`Unrecognized fourslash statement: ${statement.getText()}`);
    }

    const body = ts.isBlock(statement.statement) ? statement.statement.statements : [statement.statement];
    return [{
        kind: "forOf",
        variableName,
        rangeExpression,
        bodyCommands: body.flatMap(parseFourslashStatement),
    }];
}

function parseEditStatement(funcName: string, args: readonly ts.Expression[]): EditCmd {
    switch (funcName) {
        case "insert":
        case "paste":
        case "insertLine": {
            let arg0;
            if (args.length !== 1 || !(arg0 = getStringLiteralLike(args[0]))) {
                throw new Error(`Expected a single string literal argument in edit.${funcName}, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            return {
                kind: "edit",
                csStatement: `f.${funcName.charAt(0).toUpperCase() + funcName.slice(1)}(${csStringLiteral(arg0.text)})`,
            };
        }
        case "replaceLine": {
            let arg0, arg1;
            if (args.length !== 2 || !(arg0 = getNumericLiteral(args[0])) || !(arg1 = getStringLiteralLike(args[1]))) {
                throw new Error(`Expected a single string literal argument in edit.insert, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            return {
                kind: "edit",
                csStatement: `f.ReplaceLine(${arg0.text}, ${csStringLiteral(arg1.text)})`,
            };
        }
        case "backspace": {
            const arg = args[0];
            if (args[0]) {
                let arg0;
                if (!(arg0 = getNumericLiteral(arg))) {
                    throw new Error(`Expected numeric literal argument in edit.backspace, got ${arg.getText()}`);
                }
                return {
                    kind: "edit",
                    csStatement: `f.Backspace(${arg0.text})`,
                };
            }
            return {
                kind: "edit",
                csStatement: `f.Backspace(1)`,
            };
        }
        case "deleteAtCaret": {
            const arg = args[0];
            if (arg) {
                let arg0;
                if (arg0 = getNumericLiteral(arg)) {
                    return {
                        kind: "edit",
                        csStatement: `f.DeleteAtCaret(${arg0.text})`,
                    };
                }
                // Handle 'string'.length expressions
                const lengthValue = getStringLengthExpression(arg);
                if (lengthValue !== undefined) {
                    return {
                        kind: "edit",
                        csStatement: `f.DeleteAtCaret(${lengthValue})`,
                    };
                }
                throw new Error(`Expected numeric literal argument in edit.deleteAtCaret, got ${arg.getText()}`);
            }
            return {
                kind: "edit",
                csStatement: `f.DeleteAtCaret(1)`,
            };
        }
        default:
            throw new Error(`Unrecognized edit function: ${funcName}`);
    }
}

function parseFormatStatement(funcName: string, args: readonly ts.Expression[]): FormatCmd[] {
    switch (funcName) {
        case "document": {
            return [{
                kind: "format",
                csStatement: `f.FormatDocument("")`,
            }];
        }
        case "setOption":
            var optName = getStringLiteralLike(args[0])!.text;
            if (optName == "newline") {
                optName = "NewLineCharacter";
            }
            var optValue = args[1].getText();
            if (
                (args[1].kind == ts.SyntaxKind.TrueKeyword || args[1].kind == ts.SyntaxKind.FalseKeyword)
            ) {
                optValue = stringToTristate(args[1].getText());
            }
            // Go emits three statements (GetOptions / field assign / Configure). The C#
            // UserPreferences does not carry a FormatCodeSettings field yet, so the C#
            // emission is the equivalent single ConfigureFormat call (see file header).
            const formattedValue = optValue === "true" || optValue === "false" || optValue.startsWith("Core.TS")
                ? optValue
                : csStringLiteral(JSON.parse(optValue));
            return [{
                kind: "format",
                csStatement: `f.ConfigureFormat(new FormatCodeSettings { ${optName.charAt(0).toUpperCase() + optName.slice(1)} = ${formattedValue} })`,
            }];
        case "selection": {
            const startMarker = getStringLiteralLike(args[0])?.text;
            const endMarker = getStringLiteralLike(args[1])?.text;
            if (startMarker === undefined || endMarker === undefined) {
                throw new Error(`format.selection: expected two string literal marker names`);
            }
            return [{
                kind: "format",
                csStatement: `f.FormatSelection(${csStringLiteral(startMarker)}, ${csStringLiteral(endMarker)})`,
            }];
        }
        case "onType":
        case "copyFormatOptions":
        case "setFormatOptions":
        default:
            throw new Error(`Unrecognized format function: ${funcName}`);
    }
}

function parseCurrentContentIsArgs(funcName: string, args: readonly ts.Expression[]): VerifyContentCmd[] {
    switch (funcName) {
        case "currentFileContentIs":
            return [{
                kind: "verifyContent",
                csStatement: `f.VerifyCurrentFileContent(${csStringLiteralFromNode(args[0])!})`,
            }];
        case "currentLineContentIs":
            return [{
                kind: "verifyContent",
                csStatement: `f.VerifyCurrentLineContent(${csStringLiteralFromNode(args[0])!})`,
            }];
        case "indentationIs":
            // return [{
            //     kind: "verifyContent",
            //     csStatement: `f.VerifyIndentation(${getNumericLiteral(args[0])?.text})`,
            // }];
        case "indentationAtPositionIs":
        case "textAtCaretIs":
            // return [{
            //     kind: "verifyContent",
            //     csStatement: `f.VerifyTextAtCaret(${csStringLiteral(args[0].getText())})`,
            // }];
        default:
            throw new Error(`Unrecognized verify content function: ${funcName}`);
    }
}

function parseDocCommentTemplateAtArgs(args: readonly ts.Expression[]): VerifyDocCommentTemplateCmd {
    if (args.length < 3 || args.length > 4) {
        throw new Error(`Expected 3-4 arguments in verify.docCommentTemplateAt, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    return {
        kind: "verifyDocCommentTemplate",
        marker: parseMarkerInput(args[0]),
        expectedOffset: getNumericLiteral(args[1])?.text ?? "0",
        expectedText: csStringLiteralFromNode(args[2]),
        generateReturnInDocTemplate: args[3] ? parseGenerateReturnInDocTemplateOption(args[3]) : undefined,
    };
}

function parseNoDocCommentTemplateAtArgs(args: readonly ts.Expression[]): VerifyNoDocCommentTemplateCmd {
    if (args.length !== 1) {
        throw new Error(`Expected 1 argument in verify.noDocCommentTemplateAt, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    return {
        kind: "verifyNoDocCommentTemplate",
        marker: parseMarkerInput(args[0]),
    };
}

function parseMarkerInput(arg: ts.Expression): string {
    const literal = getStringLiteralLike(arg);
    if (literal) {
        return csStringLiteral(literal.text);
    }
    if (ts.isIdentifier(arg)) {
        return arg.text;
    }
    throw new Error(`Expected marker input, got ${arg.getText()}`);
}

function parseGenerateReturnInDocTemplateOption(arg: ts.Expression): string | undefined {
    const obj = getObjectLiteralExpression(arg);
    if (!obj) {
        throw new Error(`Expected object literal for doc comment template options, got ${arg.getText()}`);
    }
    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            continue;
        }
        if (prop.name.text === "generateReturnInDocTemplate") {
            if (prop.initializer.kind === ts.SyntaxKind.TrueKeyword) {
                return "true";
            }
            if (prop.initializer.kind === ts.SyntaxKind.FalseKeyword) {
                return "false";
            }
        }
    }
    return undefined;
}

// C# string literals: JSON escaping is valid C# for these strings, matching the Go emitter's
// use of JSON.stringify (getGoStringLiteral).
function csStringLiteral(text: string): string {
    return `${JSON.stringify(text)}`;
}

// Multi-line strings become C# 11 raw string literals. The literal is emitted with the
// content at column 0 (mirroring the Go backtick emission); the quote-fence width escalates
// past any run of quotes inside the content. Single-line strings stay escaped literals.
function csMultiLineStringLiteral(text: string): string {
    if (!text.includes("\n")) {
        return csStringLiteral(text);
    }
    const fenceCount = (() => {
        let longest = 0;
        let run = 0;
        for (const ch of text) {
            if (ch === '"') {
                run++;
                longest = Math.max(longest, run);
            }
            else {
                run = 0;
            }
        }
        return Math.max(3, longest + 1);
    })();
    const fence = "\"".repeat(fenceCount);
    // Content lines sit at column 0, so the closing delimiter may too (a shorter-indent line
    // under an indented delimiter would be CS8999/CS9003).
    if (text.includes("\n" + fence)) {
        // A line starting with exactly the fence would terminate the literal early; escalate.
        return csMultiLineStringLiteralEscalated(text, fenceCount + 1);
    }
    return `${fence}\n${text}\n${fence}`;
}

function csMultiLineStringLiteralEscalated(text: string, fenceCount: number): string {
    const fence = "\"".repeat(fenceCount);
    if (text.includes("\n" + fence)) {
        return csMultiLineStringLiteralEscalated(text, fenceCount + 1);
    }
    return `${fence}\n${text}\n${fence}`;
}

function csStringLiteralFromNode(node: ts.Node): string {
    const stringLiteralLike = getStringLiteralLike(node);
    if (stringLiteralLike) {
        return csMultiLineStringLiteral(stringLiteralLike.text);
    }
    switch (node.kind) {
        case ts.SyntaxKind.BinaryExpression: {
            const binaryExpr = node as ts.BinaryExpression;
            const left = csStringLiteralFromNode(binaryExpr.left);
            const right = csStringLiteralFromNode(binaryExpr.right);
            const op = binaryExpr.operatorToken.getText();
            return left + op + right;
        }
        default:
            throw new Error(`Unhandled case ${node.kind} in csStringLiteralFromNode: ${node.getText()}`);
    }
}

function parseGoToArgs(args: readonly ts.Expression[], funcName: string): GoToCmd[] {
    switch (funcName) {
        case "marker": {
            const arg = args[0];
            if (arg === undefined) {
                return [{
                    kind: "goTo",
                    funcName: "marker",
                    args: [`""`],
                }];
            }
            let strArg;
            if (!(strArg = getStringLiteralLike(arg))) {
                throw new Error(`Unrecognized argument in goTo.marker: ${arg.getText()}`);
            }
            return [{
                kind: "goTo",
                funcName: "marker",
                args: [csStringLiteral(strArg.text)],
            }];
        }
        case "file": {
            if (args.length !== 1) {
                throw new Error(`Expected a single argument in goTo.file, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            let arg0;
            if (arg0 = getStringLiteralLike(args[0])) {
                const text = arg0.text.replace("tests/cases/fourslash/server/", "").replace("tests/cases/fourslash/", "");
                return [{
                    kind: "goTo",
                    funcName: "file",
                    args: [csStringLiteral(text)],
                }];
            }
            else if (arg0 = getNumericLiteral(args[0])) {
                return [{
                    kind: "goTo",
                    funcName: "fileNumber",
                    args: [arg0.text],
                }];
            }
            throw new Error(`Expected string or number literal argument in goTo.file, got ${args[0].getText()}`);
        }
        case "position": {
            let arg0;
            if (args.length !== 1 || !(arg0 = getNumericLiteral(args[0]))) {
                throw new Error(`Expected a single numeric literal argument in goTo.position, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            return [{
                kind: "goTo",
                funcName: "position",
                args: [`${arg0.text}`],
            }];
        }
        case "eof":
            return [{
                kind: "goTo",
                funcName: "EOF",
                args: [],
            }];
        case "bof":
            return [{
                kind: "goTo",
                funcName: "BOF",
                args: [],
            }];
        case "select": {
            let arg0, arg1;
            if (args.length !== 2 || !(arg0 = getStringLiteralLike(args[0])) || !(arg1 = getStringLiteralLike(args[1]))) {
                throw new Error(`Expected two string literal arguments in goTo.select, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            return [{
                kind: "goTo",
                funcName: "select",
                args: [csStringLiteral(arg0.text), csStringLiteral(arg1.text)],
            }];
        }
        default:
            throw new Error(`Unrecognized goTo function: ${funcName}`);
    }
}

function parseVerifyCompletionsArgs(args: readonly ts.Expression[], codeActionArgs?: readonly ts.Expression[]): VerifyCompletionsCmd[] {
    const cmds = [];
    const codeAction = codeActionArgs?.[0] && parseAndApplyCodeActionArg(codeActionArgs[0]);
    for (const arg of args) {
        const result = parseVerifyCompletionArg(arg, codeAction);
        if (codeActionArgs?.length) {
            result.andApplyCodeActionArgs = parseAndApplyCodeActionArg(codeActionArgs[0]);
        }
        cmds.push(result);
    }
    return cmds;
}

function parseVerifyApplyCodeActionFromCompletionArgs(args: readonly ts.Expression[]): VerifyApplyCodeActionFromCompletionCmd[] {
    const cmds: VerifyApplyCodeActionFromCompletionCmd[] = [];
    if (args.length !== 2) {
        throw new Error(`Expected two arguments in verify.applyCodeActionFromCompletion, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    if (!ts.isStringLiteralLike(args[0]) && args[0].getText() !== "undefined") {
        throw new Error(`Expected string literal or "undefined" in verify.applyCodeActionFromCompletion, got ${args[0].getText()}`);
    }
    const markerName = getStringLiteralLike(args[0])?.text;
    const marker = markerName === undefined ? "null" : csStringLiteral(markerName);
    const options = parseVerifyApplyCodeActionArgs(args[1]);

    cmds.push({ kind: "verifyApplyCodeActionFromCompletion", marker, options });
    return cmds;
}

function parseVerifyApplyCodeActionArgs(arg: ts.Expression): string {
    const obj = getObjectLiteralExpression(arg);
    if (!obj) {
        throw new Error(`Expected object literal for verify.applyCodeActionFromCompletion options, got ${arg.getText()}`);
    }
    let nameInit, sourceInit, descInit, dataInit;
    const props: string[] = [];
    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            if (ts.isShorthandPropertyAssignment(prop) && prop.name.text === "preferences") {
                continue; // !!! parse once preferences are supported in fourslash
            }
            throw new Error(`Expected property assignment with identifier name in verify.applyCodeActionFromCompletion options, got ${prop.getText()}`);
        }
        const propName = prop.name.text;
        const init = prop.initializer;
        switch (propName) {
            case "name":
                nameInit = getStringLiteralLike(init);
                if (!nameInit) {
                    throw new Error(`Expected string literal for name in verify.applyCodeActionFromCompletion options, got ${init.getText()}`);
                }
                props.push(`Name = ${csStringLiteral(nameInit.text)},`);
                break;
            case "source":
                sourceInit = getStringLiteralLike(init);
                if (!sourceInit) {
                    throw new Error(`Expected string literal for source in verify.applyCodeActionFromCompletion options, got ${init.getText()}`);
                }
                props.push(`Source = ${csStringLiteral(sourceInit.text)},`);
                break;
            case "data":
                dataInit = getObjectLiteralExpression(init);
                if (!dataInit) {
                    throw new Error(`Expected object literal for data in verify.applyCodeActionFromCompletion options, got ${init.getText()}`);
                }
                const dataProps: string[] = [];
                for (const dataProp of dataInit.properties) {
                    if (!ts.isPropertyAssignment(dataProp) || !ts.isIdentifier(dataProp.name)) {
                        throw new Error(`Expected property assignment with identifier name in verify.applyCodeActionFromCompletion data, got ${dataProp.getText()}`);
                    }
                    const dataPropName = dataProp.name.text;
                    switch (dataPropName) {
                        case "moduleSpecifier":
                            const moduleSpecifierInit = getStringLiteralLike(dataProp.initializer);
                            if (!moduleSpecifierInit) {
                                throw new Error(`Expected string literal for moduleSpecifier in verify.applyCodeActionFromCompletion data, got ${dataProp.initializer.getText()}`);
                            }
                            dataProps.push(`ModuleSpecifier = ${csStringLiteral(moduleSpecifierInit.text)},`);
                            break;
                    }
                }
                props.push(`AutoImportFix = new LsProto.AutoImportFix\n{\n${dataProps.join("\n")}\n},`);
                break;
            case "description":
                descInit = getStringLiteralLike(init);
                if (!descInit) {
                    throw new Error(`Expected string literal for description in verify.applyCodeActionFromCompletion options, got ${init.getText()}`);
                }
                props.push(`Description = ${csStringLiteral(descInit.text)},`);
                break;
            case "newFileContent":
                const newFileContentInit = getStringLiteralLike(init);
                if (!newFileContentInit) {
                    throw new Error(`Expected string literal for newFileContent in verify.applyCodeActionFromCompletion options, got ${init.getText()}`);
                }
                props.push(`NewFileContent = ${csMultiLineStringLiteral(newFileContentInit.text)},`);
                break;
            case "newRangeContent":
                const newRangeContentInit = getStringLiteralLike(init);
                if (!newRangeContentInit) {
                    throw new Error(`Expected string literal for newRangeContent in verify.applyCodeActionFromCompletion options, got ${init.getText()}`);
                }
                props.push(`NewRangeContent = ${csMultiLineStringLiteral(newRangeContentInit.text)},`);
                break;
            case "preferences":
                // Few if any tests use non-default preferences
                break;
            default:
                throw new Error(`Unrecognized property in verify.applyCodeActionFromCompletion options: ${prop.getText()}`);
        }
    }
    if (!nameInit) {
        throw new Error(`Expected name property in verify.applyCodeActionFromCompletion options`);
    }
    if (!sourceInit && !dataInit) {
        throw new Error(`Expected source property in verify.applyCodeActionFromCompletion options`);
    }
    if (!descInit) {
        throw new Error(`Expected description property in verify.applyCodeActionFromCompletion options`);
    }
    return `new ApplyCodeActionFromCompletionOptions\n{\n${props.join("\n")}\n}`;
}

function parseImportFixAtPositionArgs(args: readonly ts.Expression[]): VerifyImportFixAtPositionCmd[] {
    if (args.length < 1 || args.length > 3) {
        throw new Error(`Expected 1-3 arguments in verify.importFixAtPosition, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    const arrayArg = getArrayLiteralExpression(args[0]);
    if (!arrayArg) {
        throw new Error(`Expected array literal for first argument in verify.importFixAtPosition, got ${args[0].getText()}`);
    }
    const expectedTexts: string[] = [];
    for (const elem of arrayArg.elements) {
        const strElem = getStringLiteralLike(elem);
        if (!strElem) {
            throw new Error(`Expected string literal in verify.importFixAtPosition array, got ${elem.getText()}`);
        }
        expectedTexts.push(csMultiLineStringLiteral(strElem.text));
    }

    // If the array is empty, we should still generate valid C# code
    if (expectedTexts.length === 0) {
        expectedTexts.push(""); // This will be handled specially in code generation
    }

    let preferences: string | undefined;
    if (args.length > 2 && ts.isObjectLiteralExpression(args[2])) {
        preferences = parseUserPreferences(args[2]);
    }
    return [{
        kind: "verifyImportFixAtPosition",
        expectedTexts,
        preferences: preferences || "null /*preferences*/",
    }];
}

function parseImportFixModuleSpecifiersArgs(args: readonly ts.Expression[]): [VerifyImportFixModuleSpecifiersCmd] {
    if (args.length < 2 || args.length > 3) {
        throw new Error(`Expected 2-3 arguments in verify.importFixModuleSpecifiers, got ${args.length}`);
    }

    const markerArg = getStringLiteralLike(args[0]);
    if (!markerArg) {
        throw new Error(`Expected string literal for marker in verify.importFixModuleSpecifiers, got ${args[0].getText()}`);
    }
    const markerName = csStringLiteral(markerArg.text);

    const arrayArg = getArrayLiteralExpression(args[1]);
    if (!arrayArg) {
        throw new Error(`Expected array literal for module specifiers in verify.importFixModuleSpecifiers, got ${args[1].getText()}`);
    }

    const moduleSpecifiers: string[] = [];
    for (const elem of arrayArg.elements) {
        const strElem = getStringLiteralLike(elem);
        if (!strElem) {
            throw new Error(`Expected string literal in module specifiers array, got ${elem.getText()}`);
        }
        moduleSpecifiers.push(csStringLiteral(strElem.text));
    }

    let preferences = "null /*preferences*/";
    if (args.length > 2 && ts.isObjectLiteralExpression(args[2])) {
        const parsedPrefs = parseUserPreferences(args[2]);
        preferences = parsedPrefs;
    }

    return [{
        kind: "verifyImportFixModuleSpecifiers",
        markerName,
        moduleSpecifiers,
        preferences,
    }];
}

const completionConstants = new Map([
    ["completion.globals", "CompletionGlobals"],
    ["completion.globalTypes", "CompletionGlobalTypes"],
    ["completion.classElementKeywords", "CompletionClassElementKeywords"],
    ["completion.classElementInJsKeywords", "CompletionClassElementInJSKeywords"],
    ["completion.constructorParameterKeywords", "CompletionConstructorParameterKeywords"],
    ["completion.functionMembersWithPrototype", "CompletionFunctionMembersWithPrototype"],
    ["completion.functionMembers", "CompletionFunctionMembers"],
    ["completion.typeKeywords", "CompletionTypeKeywords"],
    ["completion.undefinedVarEntry", "CompletionUndefinedVarItem"],
    ["completion.typeAssertionKeywords", "CompletionTypeAssertionKeywords"],
    ["completion.globalThisEntry", "CompletionGlobalThisItem"],
]);

const completionPlus = new Map([
    ["completion.globalsPlus", "CompletionGlobalsPlus"],
    ["completion.globalTypesPlus", "CompletionGlobalTypesPlus"],
    ["completion.functionMembersPlus", "CompletionFunctionMembersPlus"],
    ["completion.functionMembersWithPrototypePlus", "CompletionFunctionMembersWithPrototypePlus"],
    ["completion.globalsInJsPlus", "CompletionGlobalsInJSPlus"],
    ["completion.typeKeywordsPlus", "CompletionTypeKeywordsPlus"],
]);

function parseVerifyCompletionArg(arg: ts.Expression, codeActionArgs?: VerifyApplyCodeActionArgs): VerifyCompletionsCmd {
    let marker: string | undefined;
    let goArgs: VerifyCompletionsArgs | undefined;
    const defaultGoArgs: VerifyCompletionsArgs = { preferences: "null /*preferences*/" };
    const obj = getObjectLiteralExpression(arg);
    if (!obj) {
        throw new Error(`Expected object literal expression in verify.completions, got ${arg.getText()}`);
    }
    let isNewIdentifierLocation: true | undefined;
    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            if (ts.isShorthandPropertyAssignment(prop) && prop.name.text === "preferences") {
                continue; // !!! parse once preferences are supported in fourslash
            }
            throw new Error(`Expected property assignment with identifier name, got ${prop.getText()}`);
        }
        const propName = prop.name.text;
        const init = prop.initializer;
        switch (propName) {
            case "marker": {
                let markerInit;
                if (markerInit = getStringLiteralLike(init)) {
                    marker = csStringLiteral(markerInit.text);
                }
                else if (markerInit = getArrayLiteralExpression(init)) {
                    marker = "new[]{";
                    for (const elem of markerInit.elements) {
                        if (!ts.isStringLiteral(elem)) {
                            throw new Error(`Expected string literal in marker array, got ${elem.getText()}`); // !!! parse marker arrays?
                        }
                        marker += `${csStringLiteral(elem.text)}, `;
                    }
                    marker += "}";
                }
                else if (markerInit = getObjectLiteralExpression(init)) {
                    // !!! parse marker objects?
                    throw new Error(`Unrecognized marker initializer: ${markerInit.getText()}`);
                }
                else if (init.getText() === "test.markers()") {
                    marker = "f.Markers()";
                }
                else if (
                    ts.isCallExpression(init)
                    && init.expression.getText() === "test.marker"
                    && ts.isStringLiteralLike(init.arguments[0])
                ) {
                    marker = csStringLiteral(init.arguments[0].text);
                }
                else {
                    throw new Error(`Unrecognized marker initializer: ${init.getText()}`);
                }
                break;
            }
            case "exact":
            case "includes":
            case "unsorted": {
                if (init.getText() === "undefined") {
                    return {
                        kind: "verifyCompletions",
                        marker: marker ? marker : "null",
                        args: "null",
                    };
                }
                let expected: string;
                const initText = init.getText();
                if (completionConstants.has(initText)) {
                    expected = completionConstants.get(initText)!;
                }
                else if (completionPlus.keys().some(funcName => initText.startsWith(funcName))) {
                    const tsFunc = completionPlus.keys().find(funcName => initText.startsWith(funcName));
                    const funcName = completionPlus.get(tsFunc!)!;
                    const maybeItems = (init as ts.CallExpression).arguments[0];
                    const maybeOpts = (init as ts.CallExpression).arguments[1];
                    let items;
                    if (!(items = getArrayLiteralExpression(maybeItems))) {
                        throw new Error(`Expected array literal expression for completion.globalsPlus items, got ${maybeItems.getText()}`);
                    }
                    expected = `${funcName}(\n[`;
                    for (const elem of items.elements) {
                        const result = parseExpectedCompletionItem(elem, codeActionArgs);
                        expected += "\n" + result + ",";
                    }
                    expected += "\n]";
                    if (maybeOpts) {
                        let opts;
                        if (!(opts = getObjectLiteralExpression(maybeOpts))) {
                            throw new Error(`Expected object literal expression for completion.globalsPlus options, got ${maybeOpts.getText()}`);
                        }
                        const noLib = opts.properties[0];
                        if (noLib && ts.isPropertyAssignment(noLib) && noLib.name.getText() === "noLib") {
                            if (noLib.initializer.kind === ts.SyntaxKind.TrueKeyword) {
                                expected += ", true";
                            }
                            else if (noLib.initializer.kind === ts.SyntaxKind.FalseKeyword) {
                                expected += ", false";
                            }
                            else {
                                throw new Error(`Expected boolean literal for noLib, got ${noLib.initializer.getText()}`);
                            }
                        }
                        else {
                            throw new Error(`Expected noLib property in completion.globalsPlus options, got ${maybeOpts.getText()}`);
                        }
                    }
                    else if (tsFunc === "completion.globalsPlus" || tsFunc === "completion.globalsInJsPlus") {
                        expected += ", false"; // Default for noLib
                    }
                    expected += ")";
                }
                else {
                    expected = "[";
                    let items;
                    if (items = getArrayLiteralExpression(init)) {
                        for (const elem of items.elements) {
                            const result = parseExpectedCompletionItem(elem);
                            expected += "\n" + result + ",";
                        }
                    }
                    else {
                        const result = parseExpectedCompletionItem(init);
                        expected += "\n" + result + ",";
                    }
                    expected += "\n]";
                }
                if (propName === "includes") {
                    (goArgs ??= defaultGoArgs).includes = expected;
                }
                else if (propName === "exact") {
                    (goArgs ??= defaultGoArgs).exact = expected;
                }
                else {
                    (goArgs ??= defaultGoArgs).unsorted = expected;
                }
                break;
            }
            case "excludes": {
                let excludes = "[";
                let item;
                if (item = getStringLiteralLike(init)) {
                    excludes += `\n${csStringLiteral(item.text)},`;
                }
                else if (item = getArrayLiteralExpression(init)) {
                    for (const elem of item.elements) {
                        if (!ts.isStringLiteral(elem)) {
                            throw new Error(`Expected string literal in excludes array, got ${elem.getText()}`);
                        }
                        excludes += `\n${csStringLiteral(elem.text)},`;
                    }
                }
                excludes += "\n]";
                (goArgs ??= defaultGoArgs).excludes = excludes;
                break;
            }
            case "isNewIdentifierLocation":
                if (init.kind === ts.SyntaxKind.TrueKeyword) {
                    isNewIdentifierLocation = true;
                }
                break;
            case "preferences": {
                if (!ts.isObjectLiteralExpression(init)) {
                    throw new Error(`Expected object literal for user preferences, got ${init.getText()}`);
                }
                const preferences = parseUserPreferences(init);
                (goArgs ??= defaultGoArgs).preferences = preferences;
                break;
            }
            case "triggerCharacter":
                break; // !!! parse once they're supported in fourslash
            case "defaultCommitCharacters":
            case "optionalReplacementSpan": // the only two tests that use this will require manual conversion
            case "isGlobalCompletion":
                break; // Ignored, unused
            default:
                throw new Error(`Unrecognized expected completion item: ${init.parent.getText()}`);
        }
    }
    return {
        kind: "verifyCompletions",
        marker: marker ? marker : "null",
        args: goArgs,
        isNewIdentifierLocation: isNewIdentifierLocation,
    };
}

function parseExpectedCompletionItem(expr: ts.Expression, codeActionArgs?: VerifyApplyCodeActionArgs): string {
    if (completionConstants.has(expr.getText())) {
        return completionConstants.get(expr.getText())!;
    }
    let strExpr;
    if (strExpr = getStringLiteralLike(expr)) {
        return csStringLiteral(strExpr.text);
    }
    if (strExpr = getObjectLiteralExpression(expr)) {
        let isOptional = false;
        const completionItemTags = new Set<string>();
        let sourceInit: ts.StringLiteralLike | undefined;
        let extensions: string[] = []; // !!!
        let itemProps: string[] = [];
        let name: string | undefined;
        let insertText: string | undefined;
        let filterText: string | undefined;
        let replacementSpanIdx: string | undefined;
        for (const prop of strExpr.properties) {
            if (!(ts.isPropertyAssignment(prop) || ts.isShorthandPropertyAssignment(prop)) || !ts.isIdentifier(prop.name)) {
                throw new Error(`Expected property assignment with identifier name for completion item, got ${prop.getText()}`);
            }
            const propName = prop.name.text;
            const init = ts.isPropertyAssignment(prop) ? prop.initializer : prop.name;
            switch (propName) {
                case "name": {
                    let nameInit;
                    if (nameInit = getStringLiteralLike(init)) {
                        name = nameInit.text;
                    }
                    else {
                        throw new Error(`Expected string literal for completion item name, got ${init.getText()}`);
                    }
                    break;
                }
                case "sortText":
                    const sortText = parseSortText(init);
                    itemProps.push(`SortText = ${sortText.expression},`);
                    if (sortText.expression === "Ls.SortTextOptionalMember") {
                        isOptional = true;
                    }
                    if (sortText.deprecated) {
                        completionItemTags.add("LsProto.CompletionItemTag.Deprecated");
                    }
                    break;
                case "insertText": {
                    let insertTextInit;
                    if (insertTextInit = getStringLiteralLike(init)) {
                        insertText = insertTextInit.text;
                    }
                    else if (init.getText() === "undefined") {
                        // Ignore
                    }
                    else {
                        throw new Error(`Expected string literal for insertText, got ${init.getText()}`);
                    }
                    break;
                }
                case "filterText": {
                    let filterTextInit;
                    if (filterTextInit = getStringLiteralLike(init)) {
                        filterText = filterTextInit.text;
                    }
                    else {
                        throw new Error(`Expected string literal for filterText, got ${init.getText()}`);
                    }
                    break;
                }
                case "isRecommended":
                    if (init.kind === ts.SyntaxKind.TrueKeyword) {
                        itemProps.push(`Preselect = true,`);
                    }
                    break;
                case "kind":
                    const kind = parseKind(init);
                    itemProps.push(`Kind = ${kind},`);
                    break;
                case "kindModifiers":
                    const modifiers = parseKindModifiers(init);
                    ({ isOptional, extensions } = modifiers);
                    if (modifiers.isDeprecated) {
                        completionItemTags.add("LsProto.CompletionItemTag.Deprecated");
                    }
                    break;
                case "text": {
                    let textInit;
                    if (textInit = getStringLiteralLike(init)) {
                        itemProps.push(`Detail = ${csStringLiteral(textInit.text)},`);
                    }
                    else {
                        throw new Error(`Expected string literal for text, got ${init.getText()}`);
                    }
                    break;
                }
                case "documentation": {
                    let docInit;
                    if (docInit = getStringLiteralLike(init)) {
                        itemProps.push(`Documentation = LsProto.StringOrMarkupContent.FromMarkupContent(new LsProto.MarkupContent
						{
							Kind = LsProto.MarkupKind.Markdown,
							Value = ${csStringLiteral(docInit.text)},
						}),`);
                    }
                    else {
                        throw new Error(`Expected string literal for documentation, got ${init.getText()}`);
                    }
                    break;
                }
                case "isFromUncheckedFile":
                    break; // Ignored
                case "hasAction":
                    itemProps.push("AdditionalTextEdits = AnyTextEdits,");
                    break;
                case "source":
                case "sourceDisplay":
                    if (sourceInit !== undefined) {
                        break;
                    }
                    if (sourceInit = getStringLiteralLike(init)) {
                        if (propName === "source" && sourceInit.text.endsWith("/")) {
                            // source: "ClassMemberSnippet/"
                            itemProps.push(`Data = new LsProto.CompletionItemData
                                {
                                    Source = ${csStringLiteral(sourceInit.text)},
                                },`);
                            break;
                        }
                        itemProps.push(`Data = new LsProto.CompletionItemData
                            {
                                AutoImport = new LsProto.AutoImportFix
                                {
                                    ModuleSpecifier = ${csStringLiteral(sourceInit.text)},
                                },
                            },`);
                    }
                    else if (init.getText().startsWith("completion.CompletionSource.")) {
                        const source = init.getText().slice("completion.CompletionSource.".length);
                        switch (source) {
                            // Ignore switch snippet sources
                            case "SwitchCases": {
                                continue;
                            }
                            default:
                                throw new Error(`Unrecognized source in expected completion item: ${init.getText()}`);
                        }
                    }
                    else {
                        throw new Error(`Expected string literal for source/sourceDisplay, got ${init.getText()}`);
                    }
                    break;
                case "commitCharacters":
                    // !!! support these later
                    break;
                case "replacementSpan": {
                    let span;
                    if (ts.isIdentifier(init)) {
                        span = getNodeOfKind(init, (n: ts.Node): n is ts.Node => !ts.isIdentifier(n));
                    }
                    else {
                        span = init;
                    }
                    if (span?.getText().startsWith("test.ranges()[")) {
                        replacementSpanIdx = span.getText().match(/\d+/)?.[0];
                    }
                    break;
                }
                case "isSnippet":
                    if (init.kind === ts.SyntaxKind.TrueKeyword) {
                        itemProps.push(`InsertTextFormat = LsProto.InsertTextFormat.Snippet,`);
                    }
                    break;
                default:
                    throw new Error(`Unrecognized property in expected completion item: ${propName}`); // Unsupported property
            }
        }
        if (!name) {
            throw new Error(`Expected name property in expected completion item`);
        }
        if (codeActionArgs && codeActionArgs.name === name && codeActionArgs.source === sourceInit?.text) {
            itemProps.push(`LabelDetails = new LsProto.CompletionItemLabelDetails
            {
                Description = ${csStringLiteral(codeActionArgs.source)},
            },`);
        }
        if (replacementSpanIdx) {
            itemProps.push(`TextEdit = new LsProto.TextEditOrInsertReplaceEdit
            {
                TextEdit = new LsProto.TextEdit
                {
                    NewText = ${csStringLiteral(name)},
                    Range = f.Ranges()[${replacementSpanIdx}].LSRange,
                },
            },`);
        }
        if (isOptional) {
            insertText ??= name;
            filterText ??= name;
            name += "?";
        }
        if (filterText) itemProps.unshift(`FilterText = ${csStringLiteral(filterText)},`);
        if (insertText) itemProps.unshift(`InsertText = ${csStringLiteral(insertText)},`);
        const tags = formatCompletionItemTags(completionItemTags);
        if (tags) itemProps.push(tags);
        itemProps.unshift(`Label = ${csStringLiteral(name!)},`);
        return `new LsProto.CompletionItem\n{\n${itemProps.join("\n")}\n}`;
    }
    throw new Error(`Expected string literal or object literal for expected completion item, got ${expr.getText()}`); // Unsupported expression type
}

function parseAndApplyCodeActionArg(arg: ts.Expression): VerifyApplyCodeActionArgs {
    const obj = getObjectLiteralExpression(arg);
    if (!obj) {
        throw new Error(`Expected object literal for code action argument, got ${arg.getText()}`);
    }
    const nameProperty = obj.properties.find(prop =>
        ts.isPropertyAssignment(prop) &&
        ts.isIdentifier(prop.name) &&
        prop.name.text === "name" &&
        ts.isStringLiteralLike(prop.initializer)
    ) as ts.PropertyAssignment;
    if (!nameProperty) {
        throw new Error(`Expected name property in code action argument, got ${obj.getText()}`);
    }
    const sourceProperty = obj.properties.find(prop =>
        ts.isPropertyAssignment(prop) &&
        ts.isIdentifier(prop.name) &&
        prop.name.text === "source" &&
        ts.isStringLiteralLike(prop.initializer)
    ) as ts.PropertyAssignment;
    if (!sourceProperty) {
        throw new Error(`Expected source property in code action argument, got ${obj.getText()}`);
    }
    const descriptionProperty = obj.properties.find(prop =>
        ts.isPropertyAssignment(prop) &&
        ts.isIdentifier(prop.name) &&
        prop.name.text === "description" &&
        ts.isStringLiteralLike(prop.initializer)
    ) as ts.PropertyAssignment;
    if (!descriptionProperty) {
        throw new Error(`Expected description property in code action argument, got ${obj.getText()}`);
    }
    const newFileContentProperty = obj.properties.find(prop =>
        ts.isPropertyAssignment(prop) &&
        ts.isIdentifier(prop.name) &&
        prop.name.text === "newFileContent" &&
        ts.isStringLiteralLike(prop.initializer)
    ) as ts.PropertyAssignment;
    if (!newFileContentProperty) {
        throw new Error(`Expected newFileContent property in code action argument, got ${obj.getText()}`);
    }
    return {
        name: (nameProperty.initializer as ts.StringLiteralLike).text,
        source: (sourceProperty.initializer as ts.StringLiteralLike).text,
        description: (descriptionProperty.initializer as ts.StringLiteralLike).text,
        newFileContent: (newFileContentProperty.initializer as ts.StringLiteralLike).text,
    };
}

function parseBaselineFindAllReferencesArgs(args: readonly ts.Expression[]): [VerifyBaselineFindAllReferencesCmd] {
    const newArgs = [];
    for (const arg of args) {
        let strArg;
        if (strArg = getStringLiteralLike(arg)) {
            newArgs.push(csStringLiteral(strArg.text));
        }
        else if (arg.getText() === "...test.markerNames()") {
            newArgs.push("[.. f.MarkerNames()]");
        }
        else if (arg.getText() === "...test.ranges()") {
            return [{
                kind: "verifyBaselineFindAllReferences",
                markers: [],
                ranges: true,
            }];
        }
        else {
            throw new Error(`Unrecognized argument in verify.baselineFindAllReferences: ${arg.getText()}`);
        }
    }

    return [{
        kind: "verifyBaselineFindAllReferences",
        markers: newArgs,
    }];
}

function parseBaselineDocumentHighlightsArgs(args: readonly ts.Expression[]): [VerifyBaselineDocumentHighlightsCmd] {
    const newArgs: string[] = [];
    let preferences: string | undefined;
    let filesToSearch: string[] | undefined;
    for (const arg of args) {
        let strArg;
        if (strArg = getArrayLiteralExpression(arg)) {
            for (const elem of strArg.elements) {
                const newArg = parseBaselineMarkerOrRangeArg(elem);
                newArgs.push(newArg);
            }
        }
        else if (ts.isCallExpression(arg) && arg.getText().includes("test.ranges()")) {
            newArgs.push("[.. f.Ranges()]");
        }
        else if (ts.isObjectLiteralExpression(arg)) {
            for (const prop of arg.properties) {
                if (ts.isPropertyAssignment(prop) && ts.isIdentifier(prop.name) && prop.name.text === "filesToSearch" && ts.isArrayLiteralExpression(prop.initializer)) {
                    filesToSearch = [];
                    for (const e of prop.initializer.elements) {
                        if (ts.isStringLiteral(e)) {
                            filesToSearch.push(JSON.stringify(e.text));
                        }
                        else if (ts.isPropertyAccessExpression(e) && e.name.text === "fileName") {
                            // e.g. test.ranges()[0].fileName -> f.Ranges()[0].FileName()
                            const obj = e.expression;
                            if (ts.isElementAccessExpression(obj) && ts.isCallExpression(obj.expression) && obj.expression.getText().includes("ranges")) {
                                const index = obj.argumentExpression?.getText();
                                if (index !== undefined) {
                                    filesToSearch.push(`f.Ranges()[${index}].FileName()`);
                                    continue;
                                }
                            }
                            // e.g. range.fileName where `const range = test.ranges()[0]`
                            if (ts.isIdentifier(obj)) {
                                const resolved = parseRangeVariable(obj);
                                if (resolved) {
                                    filesToSearch.push(`${resolved}.FileName()`);
                                    continue;
                                }
                            }
                            // Fallback: skip filesToSearch entirely
                            filesToSearch = undefined;
                            break;
                        }
                        else {
                            // Unsupported expression; skip filesToSearch
                            filesToSearch = undefined;
                            break;
                        }
                    }
                }
            }
        }
        else {
            newArgs.push(parseBaselineMarkerOrRangeArg(arg));
        }
    }

    if (newArgs.length === 0) {
        newArgs.push("[.. f.Ranges()]");
    }

    return [{
        kind: "verifyBaselineDocumentHighlights",
        args: newArgs,
        preferences: preferences ? preferences : "null /*preferences*/",
        filesToSearch,
    }];
}

function parseBaselineGoToDefinitionArgs(
    funcName: "baselineGoToDefinition" | "baselineGoToType" | "baselineGetDefinitionAtPosition" | "baselineGoToImplementation" | "baselineGoToSourceDefinition",
    args: readonly ts.Expression[],
): [VerifyBaselineGoToDefinitionCmd] {
    let boundSpan: true | undefined;
    if (funcName === "baselineGoToDefinition") {
        boundSpan = true;
    }
    let kind: "verifyBaselineGoToDefinition" | "verifyBaselineGoToType" | "verifyBaselineGoToImplementation" | "verifyBaselineGoToSourceDefinition";
    switch (funcName) {
        case "baselineGoToDefinition":
        case "baselineGetDefinitionAtPosition":
            kind = "verifyBaselineGoToDefinition";
            break;
        case "baselineGoToType":
            kind = "verifyBaselineGoToType";
            break;
        case "baselineGoToImplementation":
            kind = "verifyBaselineGoToImplementation";
            break;
        case "baselineGoToSourceDefinition":
            kind = "verifyBaselineGoToSourceDefinition";
            break;
    }
    const newArgs = [];
    for (const arg of args) {
        let strArg;
        if (strArg = getStringLiteralLike(arg)) {
            newArgs.push(csStringLiteral(strArg.text));
        }
        else if (arg.getText() === "...test.markerNames()") {
            newArgs.push("[.. f.MarkerNames()]");
        }
        else if (arg.getText() === "...test.ranges()") {
            return [{
                kind,
                markers: [],
                ranges: true,
                boundSpan,
            }];
        }
        else {
            throw new Error(`Unrecognized argument in verify.${funcName}: ${arg.getText()}`);
        }
    }

    return [{
        kind,
        markers: newArgs,
        boundSpan,
    }];
}

function parseRenameInfo(funcName: "renameInfoSucceeded" | "renameInfoFailed", args: readonly ts.Expression[]): [VerifyRenameInfoCmd] {
    let preferences = "null /*preferences*/";
    let prefArg;
    switch (funcName) {
        case "renameInfoSucceeded":
            if (args[6]) {
                prefArg = args[6];
            }
            break;
        case "renameInfoFailed":
            if (args[1]) {
                prefArg = args[1];
            }
            break;
    }
    if (prefArg) {
        if (!ts.isObjectLiteralExpression(prefArg)) {
            throw new Error(`Expected object literal expression for preferences, got ${prefArg.getText()}`);
        }
        const parsedPreferences = parseUserPreferences(prefArg);
        preferences = parsedPreferences;
    }
    return [{ kind: funcName, preferences }];
}

function parseGetEditsForFileRename(args: readonly ts.Expression[]): [VerifyGetEditsForFileRenameCmd] {
    if (args.length !== 1 || !ts.isObjectLiteralExpression(args[0])) {
        throw new Error(`Expected a single object literal argument in verify.getEditsForFileRename, got ${args.map(arg => arg.getText()).join(", ")}`);
    }

    let oldPath: string | undefined;
    let newPath: string | undefined;
    let newFileContents = "new Dictionary<string, string>()";
    let preferences = "null /*preferences*/";

    for (const prop of args[0].properties) {
        if (!ts.isPropertyAssignment(prop)) {
            throw new Error(`Expected property assignment in verify.getEditsForFileRename argument, got ${prop.getText()}`);
        }
        const name = prop.name.getText();
        switch (name) {
            case "oldPath": {
                const value = getStringLiteralLike(prop.initializer);
                if (!value) {
                    throw new Error(`Expected string literal for oldPath, got ${prop.initializer.getText()}`);
                }
                oldPath = csStringLiteral(value.text);
                break;
            }
            case "newPath": {
                const value = getStringLiteralLike(prop.initializer);
                if (!value) {
                    throw new Error(`Expected string literal for newPath, got ${prop.initializer.getText()}`);
                }
                newPath = csStringLiteral(value.text);
                break;
            }
            case "newFileContents": {
                const obj = getObjectLiteralExpression(prop.initializer);
                if (!obj) {
                    throw new Error(`Expected object literal for newFileContents, got ${prop.initializer.getText()}`);
                }
                const entries: string[] = [];
                for (const entry of obj.properties) {
                    if (!ts.isPropertyAssignment(entry)) {
                        throw new Error(`Expected property assignment in verify.getEditsForFileRename argument, got ${prop.getText()}`);
                    }
                    const key = getStringLiteralLike(entry.name);
                    const value = getStringLiteralLike(entry.initializer);
                    if (!key || !value) {
                        throw new Error(`Expected string literal key/value in newFileContents, got ${entry.getText()}`);
                    }
                    entries.push(`[${csStringLiteral(key.text)}] = ${csMultiLineStringLiteral(value.text)}`);
                }
                newFileContents = entries.length === 0
                    ? "new Dictionary<string, string>()"
                    : `new Dictionary<string, string>\n{\n${entries.join(",\n")},\n}`;
                break;
            }
            case "preferences": {
                if (!ts.isObjectLiteralExpression(prop.initializer)) {
                    throw new Error(`Expected object literal for preferences, got ${prop.initializer.getText()}`);
                }
                preferences = parseUserPreferences(prop.initializer);
                break;
            }
        }
    }

    if (!oldPath || !newPath) {
        throw new Error(`Expected oldPath and newPath in verify.getEditsForFileRename`);
    }

    return [{
        kind: "verifyGetEditsForFileRename",
        oldPath,
        newPath,
        newFileContents,
        preferences,
    }];
}

function parseBaselineRenameArgs(funcName: string, args: readonly ts.Expression[]): [VerifyBaselineRenameCmd] {
    let newArgs: string[] = [];
    let preferences: string | undefined;
    for (const arg of args) {
        let typedArg;
        if ((typedArg = getArrayLiteralExpression(arg))) {
            for (const elem of typedArg.elements) {
                const newArg = parseBaselineMarkerOrRangeArg(elem);
                newArgs.push(newArg);
            }
        }
        else if (ts.isObjectLiteralExpression(arg)) {
            preferences = parseUserPreferences(arg);
            continue;
        }
        else {
            newArgs.push(parseBaselineMarkerOrRangeArg(arg));
        }
    }
    return [{
        kind: funcName === "baselineRenameAtRangesWithText" ? "verifyBaselineRenameAtRangesWithText" : "verifyBaselineRename",
        args: newArgs,
        preferences: preferences ? preferences : "null /*preferences*/",
    }];
}

function parseBaselineInlayHints(args: readonly ts.Expression[]): [VerifyBaselineInlayHintsCmd] {
    let preferences: string | undefined;
    // Parse span
    if (args.length > 0) {
        if (args[0].getText() !== "undefined") {
            throw new Error(`Unsupported span argument in verify.baselineInlayHints: ${args[0].getText()}`);
        }
    }
    // Parse preferences
    if (args.length > 1) {
        if (ts.isObjectLiteralExpression(args[1])) {
            preferences = parseUserPreferences(args[1]);
        }
    }
    return [{
        kind: "verifyBaselineInlayHints",
        span: "null /*span*/", // Only supporteed manually
        preferences: preferences ? preferences : "null /*preferences*/",
    }];
}

function parseVerifyLinkedEditing(args: readonly ts.Expression[]): [VerifyLinkedEditingCmd] {
    // The Go converter passed the TS text through (its only two call sites happened to be
    // Go-shaped after undefined→nil). For C# the literal is translated structurally.
    if (args.length !== 1 || !ts.isObjectLiteralExpression(args[0])) {
        throw new Error(`Expected a single object literal argument in verify.linkedEditing`);
    }
    const entries: string[] = [];
    for (const prop of args[0].properties) {
        if (!ts.isPropertyAssignment(prop) && !ts.isShorthandPropertyAssignment(prop)) {
            throw new Error(`Expected property assignment in verify.linkedEditing, got ${prop.getText()}`);
        }
        const key = getStringLiteralLike(prop.name);
        if (!key) {
            throw new Error(`Expected string literal key in verify.linkedEditing, got ${prop.name.getText()}`);
        }
        const valueExpr = ts.isPropertyAssignment(prop) ? prop.initializer : prop.name;
        if (valueExpr.getText() === "undefined") {
            entries.push(`[${csStringLiteral(key.text)}] = null`);
            continue;
        }
        let rangeArray;
        if (!(rangeArray = getArrayLiteralExpression(valueExpr))) {
            throw new Error(`Expected array literal or undefined in verify.linkedEditing value, got ${valueExpr.getText()}`);
        }
        const ranges: string[] = [];
        for (const elem of rangeArray.elements) {
            ranges.push(parseLsprotoRangeLiteral(elem));
        }
        entries.push(`[${csStringLiteral(key.text)}] = new[]{${ranges.join(", ")}}`);
    }
    const ranges = `new Dictionary<string, LsProto.Range[]?>\n{\n${entries.join(",\n")},\n}`;
    return [{
        kind: "verifyLinkedEditing",
        ranges,
    }];
}

// { start: { line: N, character: M }, end: {...} } → new LsProto.Range { ... }
function parseLsprotoRangeLiteral(elem: ts.Expression): string {
    const obj = getObjectLiteralExpression(elem);
    if (!obj) {
        throw new Error(`Expected range object literal in verify.linkedEditing, got ${elem.getText()}`);
    }
    let start: string | undefined;
    let end: string | undefined;
    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            throw new Error(`Expected property assignment in verify.linkedEditing range, got ${prop.getText()}`);
        }
        const position = parseLsprotoPositionLiteral(prop.initializer);
        if (prop.name.text === "start") start = position;
        else if (prop.name.text === "end") end = position;
        else throw new Error(`Unrecognized range property in verify.linkedEditing: ${prop.name.text}`);
    }
    if (!start || !end) {
        throw new Error(`Expected start/end in verify.linkedEditing range, got ${elem.getText()}`);
    }
    return `new LsProto.Range { Start = ${start}, End = ${end} }`;
}

function parseLsprotoPositionLiteral(expr: ts.Expression): string {
    const obj = getObjectLiteralExpression(expr);
    if (!obj) {
        throw new Error(`Expected position object literal in verify.linkedEditing, got ${expr.getText()}`);
    }
    let line: string | undefined;
    let character: string | undefined;
    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            throw new Error(`Expected property assignment in verify.linkedEditing position, got ${prop.getText()}`);
        }
        const num = getNumericLiteral(prop.initializer);
        if (!num) {
            throw new Error(`Expected numeric literal in verify.linkedEditing position, got ${prop.initializer.getText()}`);
        }
        if (prop.name.text === "line") line = num.text;
        else if (prop.name.text === "character") character = num.text;
        else throw new Error(`Unrecognized position property in verify.linkedEditing: ${prop.name.text}`);
    }
    if (line === undefined || character === undefined) {
        throw new Error(`Expected line/character in verify.linkedEditing position, got ${expr.getText()}`);
    }
    return `new LsProto.Position { Line = ${line}, Character = ${character} }`;
}

function parseVerifyDiagnostics(funcName: string, args: readonly ts.Expression[]): [VerifyDiagnosticsCmd] {
    if (!args[0] || !ts.isArrayLiteralExpression(args[0])) {
        throw new Error(`Expected an array literal argument in verify.${funcName}`);
    }
    const goArgs: string[] = [];
    for (const expr of args[0].elements) {
        const diag = parseExpectedDiagnostic(expr);
        goArgs.push(diag);
    }
    return [{
        kind: "verifyDiagnostics",
        arg: goArgs.length > 0 ? `[\n${goArgs.join(",\n")},\n]` : "[]",
        isSuggestion: funcName === "getSuggestionDiagnostics",
    }];
}

function parseExpectedDiagnostic(expr: ts.Expression): string {
    if (!ts.isObjectLiteralExpression(expr)) {
        throw new Error(`Expected object literal expression for expected diagnostic, got ${expr.getText()}`);
    }

    const diagnosticProps: string[] = [];

    for (const prop of expr.properties) {
        if (!ts.isPropertyAssignment(prop) || !(ts.isIdentifier(prop.name) || ts.isStringLiteral(prop.name))) {
            throw new Error(`Expected property assignment with identifier name for expected diagnostic, got ${prop.getText()}`);
        }

        const propName = prop.name.text;
        const init = prop.initializer;

        switch (propName) {
            case "message": {
                let messageInit;
                if (messageInit = getStringLiteralLike(init)) {
                    const messageText = messageInit.text.replace("/tests/cases/fourslash", "");
                    diagnosticProps.push(`Message = LsProto.StringOrMarkupContent.FromString(${csStringLiteral(messageText)}),`);
                }
                else {
                    throw new Error(`Expected string literal for diagnostic message, got ${init.getText()}`);
                }
                break;
            }
            case "code": {
                let codeInit;
                if (codeInit = getNumericLiteral(init)) {
                    diagnosticProps.push(`Code = LsProto.IntegerOrString.FromInteger(${parseInt(codeInit.text, 10)}),`);
                }
                else {
                    throw new Error(`Expected numeric literal for diagnostic code, got ${init.getText()}`);
                }
                break;
            }
            case "range": {
                // Handle range references like ranges[0]
                const rangeArg = parseBaselineMarkerOrRangeArg(init);
                diagnosticProps.push(`Range = ${rangeArg}.LSRange,`);
                break;
            }
            case "reportsDeprecated": {
                if (init.kind === ts.SyntaxKind.TrueKeyword) {
                    diagnosticProps.push(`Tags = [LsProto.DiagnosticTag.Deprecated],`);
                }
                break;
            }
            case "reportsUnnecessary": {
                if (init.kind === ts.SyntaxKind.TrueKeyword) {
                    diagnosticProps.push(`Tags = [LsProto.DiagnosticTag.Unnecessary],`);
                }
                break;
            }
            default:
                throw new Error(`Unrecognized property in expected diagnostic: ${propName}`);
        }
    }

    if (diagnosticProps.length === 0) {
        throw new Error(`No valid properties found in diagnostic object`);
    }

    return `new LsProto.Diagnostic\n{\n${diagnosticProps.join("\n")}\n}`;
}

function parseNumberOfErrorsInCurrentFile(args: readonly ts.Expression[]): [VerifyNumberOfErrorsInCurrentFileCmd] {
    let arg0;
    if (args.length !== 1 || !(arg0 = getNumericLiteral(args[0]))) {
        throw new Error(`Expected a single numeric literal argument in verify.numberOfErrorsInCurrentFile, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    return [{
        kind: "verifyNumberOfErrorsInCurrentFile",
        expectedCount: parseInt(arg0.text, 10),
    }];
}

function parseErrorExistsAtRange(args: readonly ts.Expression[]): [VerifyErrorExistsAtRangeCmd] {
    if (args.length < 2 || args.length > 3) {
        throw new Error(`Expected 2 or 3 arguments in verify.errorExistsAtRange, got ${args.length}`);
    }

    // First arg is a range
    const rangeArg = parseBaselineMarkerOrRangeArg(args[0]);

    // Second arg is error code
    let codeArg;
    if (!(codeArg = getNumericLiteral(args[1]))) {
        throw new Error(`Expected numeric literal for code in verify.errorExistsAtRange, got ${args[1].getText()}`);
    }

    // Third arg is optional message
    let message = "";
    if (args[2]) {
        const messageArg = getStringLiteralLike(args[2]);
        if (!messageArg) {
            throw new Error(`Expected string literal for message in verify.errorExistsAtRange, got ${args[2].getText()}`);
        }
        message = messageArg.text;
    }

    return [{
        kind: "verifyErrorExistsAtRange",
        range: rangeArg,
        code: parseInt(codeArg.text, 10),
        message: message,
    }];
}

function parseCurrentLineContentIs(args: readonly ts.Expression[]): [VerifyCurrentLineContentIsCmd] {
    let arg0;
    if (args.length !== 1 || !(arg0 = getStringLiteralLike(args[0]))) {
        throw new Error(`Expected a single string literal argument in verify.currentLineContentIs, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    return [{
        kind: "verifyCurrentLineContentIs",
        text: arg0.text,
    }];
}

function parseCurrentFileContentIs(args: readonly ts.Expression[]): [VerifyCurrentFileContentIsCmd] {
    let arg0;
    if (args.length !== 1 || !(arg0 = getStringLiteralLike(args[0]))) {
        throw new Error(`Expected a single string literal argument in verify.currentFileContentIs, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    return [{
        kind: "verifyCurrentFileContentIs",
        text: arg0.text,
    }];
}

function parseErrorExistsBetweenMarkers(args: readonly ts.Expression[]): [VerifyErrorExistsBetweenMarkersCmd] {
    if (args.length !== 2) {
        throw new Error(`Expected 2 arguments in verify.errorExistsBetweenMarkers, got ${args.length}`);
    }
    let startMarker, endMarker;
    if (!(startMarker = getStringLiteralLike(args[0])) || !(endMarker = getStringLiteralLike(args[1]))) {
        throw new Error(`Expected string literal arguments in verify.errorExistsBetweenMarkers, got ${args.map(arg => arg.getText()).join(", ")}`);
    }
    return [{
        kind: "verifyErrorExistsBetweenMarkers",
        startMarker: startMarker.text,
        endMarker: endMarker.text,
    }];
}

function parseErrorExistsAfterMarker(args: readonly ts.Expression[]): [VerifyErrorExistsAfterMarkerCmd] {
    let markerName = "";
    if (args.length > 0) {
        const arg0 = getStringLiteralLike(args[0]);
        if (!arg0) {
            throw new Error(`Expected string literal argument in verify.errorExistsAfterMarker, got ${args[0].getText()}`);
        }
        markerName = arg0.text;
    }
    return [{
        kind: "verifyErrorExistsAfterMarker",
        markerName: markerName,
    }];
}

function parseErrorExistsBeforeMarker(args: readonly ts.Expression[]): [VerifyErrorExistsBeforeMarkerCmd] {
    let markerName = "";
    if (args.length > 0) {
        const arg0 = getStringLiteralLike(args[0]);
        if (!arg0) {
            throw new Error(`Expected string literal argument in verify.errorExistsBeforeMarker, got ${args[0].getText()}`);
        }
        markerName = arg0.text;
    }
    return [{
        kind: "verifyErrorExistsBeforeMarker",
        markerName: markerName,
    }];
}

function parseCodeFixArgs(args: readonly ts.Expression[]): [VerifyCodeFixCmd] {
    if (args.length !== 1) {
        throw new Error(`Expected 1 argument in verify.codeFix, got ${args.length}`);
    }
    const obj = getObjectLiteralExpression(args[0]);
    if (!obj) {
        throw new Error(`Expected object literal in verify.codeFix, got ${args[0].getText()}`);
    }

    const sourceFile = args[0].getSourceFile();
    let description = "";
    let newFileContent: string | undefined;
    let newRangeContent: string | undefined;
    let index = 0;
    let applyChanges = false;
    let preferences = "null /*preferences*/";

    for (const prop of obj.properties) {
        const name = getPropertyName(prop);
        if (!name) continue;
        if (ts.isShorthandPropertyAssignment(prop)) {
            if (name === "description") {
                const resolved = resolveDescriptionExpression(prop.name, sourceFile);
                if (resolved) description = resolved;
            }
            continue;
        }
        if (!ts.isPropertyAssignment(prop)) continue;
        switch (name) {
            case "description": {
                const resolved = resolveDescriptionExpression(prop.initializer, sourceFile);
                if (resolved) description = resolved;
                break;
            }
            case "newFileContent": {
                const str = getStringLiteralLike(prop.initializer);
                if (str) newFileContent = str.text;
                break;
            }
            case "newRangeContent": {
                const str = getStringLiteralLike(prop.initializer);
                if (str) newRangeContent = str.text;
                break;
            }
            case "index": {
                const num = getNumericLiteral(prop.initializer);
                if (num) index = parseInt(num.text);
                break;
            }
            case "applyChanges": {
                if (prop.initializer.kind === ts.SyntaxKind.TrueKeyword) {
                    applyChanges = true;
                }
                break;
            }
            case "preferences": {
                const prefs = getObjectLiteralExpression(prop.initializer);
                if (!prefs) {
                    throw new Error(`Expected object literal for preferences in verify.codeFix, got ${prop.initializer.getText()}`);
                }
                preferences = parseUserPreferences(prefs);
                break;
            }
        }
    }

    return [{
        kind: "verifyCodeFix",
        description,
        newFileContent,
        newRangeContent,
        index,
        applyChanges,
        preferences,
    }];
}

function parseCodeFixAvailableArgs(funcName: string, args: readonly ts.Expression[]): [VerifyCodeFixAvailableCmd] {
    switch (funcName) {
        case "codeFixAvailable": {
            const descriptions: string[] = [];
            let expectNone = false;

            if (args.length === 1) {
                const sourceFile = args[0].getSourceFile();
                const arrayArg = getArrayLiteralExpression(args[0]);
                if (arrayArg) {
                    if (arrayArg.elements.length === 0) {
                        expectNone = true;
                    }
                    for (const elem of arrayArg.elements) {
                        const obj = getObjectLiteralExpression(elem);
                        if (obj) {
                            for (const prop of obj.properties) {
                                if (getPropertyName(prop) === "description") {
                                    let resolved: string | undefined;
                                    if (ts.isPropertyAssignment(prop)) {
                                        resolved = resolveDescriptionExpression(prop.initializer, sourceFile);
                                    }
                                    else if (ts.isShorthandPropertyAssignment(prop)) {
                                        resolved = resolveDescriptionExpression(prop.name, sourceFile);
                                    }
                                    if (resolved) {
                                        descriptions.push(resolved);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return [{
                kind: "verifyCodeFixAvailable",
                descriptions,
                unavailableDescriptions: [],
                expectNone,
            }];
        }
        case "notCodeFixAvailable":
            if (args.length === 0) {
                return [{
                    kind: "verifyCodeFixAvailable",
                    descriptions: [],
                    unavailableDescriptions: [],
                    expectNone: true,
                }];
            }
            if (args.length === 1) {
                const [descriptionExpression] = args;
                const description = resolveDescriptionExpression(descriptionExpression, descriptionExpression.getSourceFile());
                if (!description) {
                    throw new Error(`Unsupported argument in verify.not.codeFixAvailable: ${descriptionExpression.getText()}`);
                }
                return [{
                    kind: "verifyCodeFixAvailable",
                    descriptions: [],
                    unavailableDescriptions: [description],
                    expectNone: false,
                }];
            }
            throw new Error(`Expected 0 or 1 arguments in verify.not.codeFixAvailable, got ${args.map(arg => arg.getText()).join(", ")}`);
        default:
            throw new Error(`Unrecognized codeFixAvailable function: ${funcName}`);
    }
}

function parseRangeAfterCodeFixArgs(args: readonly ts.Expression[]): [VerifyRangeAfterCodeFixCmd] {
    const [expectedTextArg, includeWhiteSpaceArg, errorCodeArg, indexArg] = args;
    const expectedText = expectedTextArg && getStringLiteralLike(expectedTextArg);
    if (!expectedText) {
        throw new Error(`Expected string literal argument in verify.rangeAfterCodeFix, got ${expectedTextArg?.getText()}`);
    }

    let includeWhiteSpace = false;
    if (includeWhiteSpaceArg !== undefined && includeWhiteSpaceArg.kind !== ts.SyntaxKind.UndefinedKeyword) {
        includeWhiteSpace = includeWhiteSpaceArg.kind === ts.SyntaxKind.TrueKeyword;
    }

    let errorCode = 0;
    if (errorCodeArg !== undefined && errorCodeArg.kind !== ts.SyntaxKind.UndefinedKeyword) {
        const parsedErrorCode = getNumericLiteral(errorCodeArg);
        if (!parsedErrorCode) {
            throw new Error(`Expected numeric literal errorCode in verify.rangeAfterCodeFix, got ${errorCodeArg.getText()}`);
        }
        errorCode = parseInt(parsedErrorCode.text);
    }

    let index = 0;
    if (indexArg !== undefined && indexArg.kind !== ts.SyntaxKind.UndefinedKeyword) {
        const parsedIndex = getNumericLiteral(indexArg);
        if (!parsedIndex) {
            throw new Error(`Expected numeric literal index in verify.rangeAfterCodeFix, got ${indexArg.getText()}`);
        }
        index = parseInt(parsedIndex.text);
    }

    return [{
        kind: "verifyRangeAfterCodeFix",
        expectedText: expectedText.text,
        includeWhiteSpace,
        errorCode,
        index,
    }];
}

function parseCodeFixAllArgs(args: readonly ts.Expression[]): [VerifyCodeFixAllCmd] {
    if (args.length !== 1) {
        throw new Error(`Expected 1 argument in verify.codeFixAll, got ${args.length}`);
    }
    const obj = getObjectLiteralExpression(args[0]);
    if (!obj) {
        throw new Error(`Expected object literal in verify.codeFixAll, got ${args[0].getText()}`);
    }

    let fixId = "";
    let newFileContent = "";

    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) continue;
        switch (prop.name.text) {
            case "fixId": {
                const str = getStringLiteralLike(prop.initializer);
                if (str) fixId = str.text;
                break;
            }
            case "newFileContent": {
                const str = getStringLiteralLike(prop.initializer);
                if (str) newFileContent = str.text;
                break;
            }
        }
    }

    return [{
        kind: "verifyCodeFixAll",
        fixId,
        newFileContent,
    }];
}

function stringToTristate(s: string): string {
    switch (s) {
        case "true":
            return "Core.TSTrue";
        case "false":
            return "Core.TSFalse";
        default:
            return "Core.TSUnknown";
    }
}

function parseUserPreferences(arg: ts.ObjectLiteralExpression): string {
    const inlayHintPreferences: string[] = [];
    const moduleSpecifierPreferences: string[] = [];
    const preferences: string[] = [];
    for (const prop of arg.properties) {
        if (ts.isPropertyAssignment(prop)) {
            switch (prop.name.getText()) {
                // !!! other preferences
                case "providePrefixAndSuffixTextForRename":
                    preferences.push(`UseAliasesForRename = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "quotePreference":
                    if (!ts.isStringLiteralLike(prop.initializer)) {
                        throw new Error(`Expected string literal for quotePreference, got ${prop.initializer.getText()}`);
                    }
                    preferences.push(`QuotePreference = ${csStringLiteral(prop.initializer.text)}`);
                    break;
                case "autoImportSpecifierExcludeRegexes":
                    const regexArrayArg = getArrayLiteralExpression(prop.initializer);
                    if (!regexArrayArg) {
                        throw new Error(`Expected array literal for autoImportSpecifierExcludeRegexes, got ${prop.initializer.getText()}`);
                    }
                    const regexes: string[] = [];
                    for (const elem of regexArrayArg.elements) {
                        const strElem = getStringLiteralLike(elem);
                        if (!strElem) {
                            throw new Error(`Expected string literal in autoImportSpecifierExcludeRegexes array, got ${elem.getText()}`);
                        }
                        regexes.push(csStringLiteral(strElem.text));
                    }
                    moduleSpecifierPreferences.push(`AutoImportSpecifierExcludeRegexes = [${regexes.join(", ")}]`);
                    break;
                case "importModuleSpecifierPreference":
                    if (!ts.isStringLiteralLike(prop.initializer)) {
                        throw new Error(`Expected string literal for importModuleSpecifierPreference, got ${prop.initializer.getText()}`);
                    }
                    moduleSpecifierPreferences.push(`ImportModuleSpecifierPreference = new ImportModuleSpecifierPreference(${csStringLiteral(prop.initializer.text)})`);
                    break;
                case "importModuleSpecifierEnding":
                    if (!ts.isStringLiteralLike(prop.initializer)) {
                        throw new Error(`Expected string literal for importModuleSpecifierEnding, got ${prop.initializer.getText()}`);
                    }
                    moduleSpecifierPreferences.push(`ImportModuleSpecifierEnding = new ImportModuleSpecifierEndingPreference(${csStringLiteral(prop.initializer.text)})`);
                    break;
                case "allowRenameOfImportPath":
                    preferences.push(`AllowRenameOfImportPath = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "preferTypeOnlyAutoImports":
                    preferences.push(`PreferTypeOnlyAutoImports = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "organizeImportsTypeOrder":
                    if (!ts.isStringLiteralLike(prop.initializer)) {
                        throw new Error(`Expected string literal for organizeImportsTypeOrder, got ${prop.initializer.getText()}`);
                    }
                    switch (prop.initializer.text) {
                        case "last":
                            preferences.push(`OrganizeImportsTypeOrder = LsUtil.OrganizeImportsTypeOrder.Last`);
                            break;
                        case "inline":
                            preferences.push(`OrganizeImportsTypeOrder = LsUtil.OrganizeImportsTypeOrder.Inline`);
                            break;
                        case "first":
                            preferences.push(`OrganizeImportsTypeOrder = LsUtil.OrganizeImportsTypeOrder.First`);
                            break;
                        default:
                            throw new Error(`Unsupported organizeImportsTypeOrder value: ${prop.initializer.text}`);
                    }
                    break;
                case "autoImportFileExcludePatterns":
                    const arrayArg = getArrayLiteralExpression(prop.initializer);
                    if (!arrayArg) {
                        throw new Error(`Expected array literal for autoImportFileExcludePatterns, got ${prop.initializer.getText()}`);
                    }
                    const patterns: string[] = [];
                    for (const elem of arrayArg.elements) {
                        const strElem = getStringLiteralLike(elem);
                        if (!strElem) {
                            throw new Error(`Expected string literal in autoImportFileExcludePatterns array, got ${elem.getText()}`);
                        }
                        patterns.push(csStringLiteral(strElem.text));
                    }
                    preferences.push(`AutoImportFileExcludePatterns = [${patterns.join(", ")}]`);
                    break;
                case "includeInlayParameterNameHints":
                    let paramHint;
                    if (!ts.isStringLiteralLike(prop.initializer)) {
                        throw new Error(`Expected string literal for includeInlayParameterNameHints, got ${prop.initializer.getText()}`);
                    }
                    switch (prop.initializer.text) {
                        case "none":
                            paramHint = "IncludeInlayParameterNameHints.None";
                            break;
                        case "literals":
                            paramHint = "IncludeInlayParameterNameHints.Literals";
                            break;
                        case "all":
                            paramHint = "IncludeInlayParameterNameHints.All";
                            break;
                    }
                    inlayHintPreferences.push(`IncludeInlayParameterNameHints = ${paramHint}`);
                    break;
                case "includeInlayParameterNameHintsWhenArgumentMatchesName":
                    inlayHintPreferences.push(`IncludeInlayParameterNameHintsWhenArgumentMatchesName = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "includeInlayFunctionParameterTypeHints":
                    inlayHintPreferences.push(`IncludeInlayFunctionParameterTypeHints = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "includeInlayVariableTypeHints":
                    inlayHintPreferences.push(`IncludeInlayVariableTypeHints = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "includeInlayVariableTypeHintsWhenTypeMatchesName":
                    inlayHintPreferences.push(`IncludeInlayVariableTypeHintsWhenTypeMatchesName = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "includeInlayPropertyDeclarationTypeHints":
                    inlayHintPreferences.push(`IncludeInlayPropertyDeclarationTypeHints = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "includeInlayFunctionLikeReturnTypeHints":
                    inlayHintPreferences.push(`IncludeInlayFunctionLikeReturnTypeHints = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "includeInlayEnumMemberValueHints":
                    inlayHintPreferences.push(`IncludeInlayEnumMemberValueHints = ${stringToTristate(prop.initializer.getText())}`);
                    break;
                case "interactiveInlayHints":
                    // Ignore, deprecated
                    break;
            }
        }
        else {
            throw new Error(`Expected property assignment in user preferences object, got ${prop.getText()}`);
        }
    }

    if (inlayHintPreferences.length > 0) {
        preferences.push(`InlayHints = new InlayHintsPreferences\n{\n${inlayHintPreferences.join(",\n")},\n}`);
    }
    if (moduleSpecifierPreferences.length > 0) {
        preferences.push(...moduleSpecifierPreferences);
    }
    if (preferences.length === 0) {
        return "null /*preferences*/";
    }
    return `new LsUtil.UserPreferences\n{\n${preferences.join(",\n")},\n}`;
}

function parseBaselineMarkerOrRangeArg(arg: ts.Expression): string {
    if (ts.isStringLiteral(arg)) {
        return csStringLiteral(arg.text);
    }
    else if (ts.isIdentifier(arg) || (ts.isElementAccessExpression(arg) && ts.isIdentifier(arg.expression))) {
        const result = parseRangeVariable(arg);
        if (result) {
            return result;
        }
        const init = getNodeOfKind(arg, ts.isCallExpression);
        if (init) {
            const result = getRangesByTextArg(init);
            if (result) {
                return result;
            }
        }
    }
    else if (ts.isElementAccessExpression(arg) && ts.isCallExpression(arg.expression) && arg.expression.getText().includes("ranges")) {
        // `test.ranges()[n]`
        const index = arg.argumentExpression?.getText();
        if (index !== undefined) {
            return `f.Ranges()[${index}]`;
        }
    }
    else if (ts.isCallExpression(arg)) {
        const result = getRangesByTextArg(arg);
        if (result) {
            return result;
        }
        // Handle `.filter(r => !(r.marker && r.marker.data.KEY))` patterns
        const filterResult = parseFilterExpression(arg);
        if (filterResult) {
            return filterResult;
        }
    }
    if (arg.getText() === "test.markers()") {
        return "[.. f.Markers()]";
    }
    throw new Error(`Unrecognized marker or range argument: ${arg.getText()}`);
}

function parseRangeVariable(arg: ts.Identifier | ts.ElementAccessExpression): string | undefined {
    const argName = ts.isIdentifier(arg) ? arg.text : (arg.expression as ts.Identifier).text;
    const file = arg.getSourceFile();
    const varStmts = file.statements.filter(ts.isVariableStatement);
    for (const varStmt of varStmts) {
        for (const decl of varStmt.declarationList.declarations) {
            if (ts.isArrayBindingPattern(decl.name) && decl.initializer) {
                // Resolve the initializer to a C# expression for the source array
                const sourceExpr = resolveRangesExpression(decl.initializer, varStmts);
                if (!sourceExpr) continue;
                for (let i = 0; i < decl.name.elements.length; i++) {
                    const elem = decl.name.elements[i];
                    if (ts.isBindingElement(elem) && ts.isIdentifier(elem.name) && elem.name.text === argName) {
                        if (elem.dotDotDotToken === undefined) {
                            return `${sourceExpr}[${i}]`;
                        }
                        if (ts.isElementAccessExpression(arg)) {
                            return `${sourceExpr}[${i + parseInt(arg.argumentExpression!.getText())}]`;
                        }
                        return `[.. ${sourceExpr}[${i}..]]`;
                    }
                }
            }
            // `const ranges = test.ranges();` and arg is `ranges[n]`
            if (ts.isIdentifier(decl.name) && decl.name.text === argName && decl.initializer?.getText().includes("ranges")) {
                if (ts.isElementAccessExpression(arg)) {
                    return `f.Ranges()[${arg.argumentExpression!.getText()}]`;
                }
                // `const range = test.ranges()[0]` used directly as `range`
                if (ts.isIdentifier(arg) && ts.isElementAccessExpression(decl.initializer) && ts.isCallExpression(decl.initializer.expression) && decl.initializer.argumentExpression) {
                    return `f.Ranges()[${decl.initializer.argumentExpression.getText()}]`;
                }
            }
            // `const cRanges = ranges.get("C")` or `const cRanges = test.rangesByText().get("C")`
            if (ts.isIdentifier(decl.name) && decl.name.text === argName && decl.initializer && ts.isCallExpression(decl.initializer)) {
                const initText = decl.initializer.getText();
                if (initText.includes("rangesByText") || (ts.isPropertyAccessExpression(decl.initializer.expression) && decl.initializer.expression.name.text === "get")) {
                    // Try to find the .get("text") argument
                    const getCall = decl.initializer;
                    if (getCall.arguments.length === 1 && ts.isStringLiteralLike(getCall.arguments[0])) {
                        return `[.. f.GetRangesByText()[${csStringLiteral(getCall.arguments[0].text)}]]`;
                    }
                }
            }
        }
    }
    return undefined;
}

/**
 * Resolves a range initializer expression to a C# expression.
 * Handles `test.ranges()`, `test.rangesByText().get("X")`, and variable references to those.
 */
function resolveRangesExpression(expr: ts.Expression, varStmts: ts.VariableStatement[]): string | undefined {
    const text = expr.getText();
    if (text.includes("test.ranges()")) {
        return "f.Ranges()";
    }
    if (text.includes("rangesByText()") && ts.isCallExpression(expr) && expr.arguments.length === 1 && ts.isStringLiteralLike(expr.arguments[0])) {
        return `f.GetRangesByText()[${csStringLiteral(expr.arguments[0].text)}]`;
    }
    // Handle `someVar.get("text")` where someVar resolves to rangesByText()
    if (ts.isCallExpression(expr) && ts.isPropertyAccessExpression(expr.expression) && expr.expression.name.text === "get" && expr.arguments.length === 1 && ts.isStringLiteralLike(expr.arguments[0])) {
        const obj = expr.expression.expression;
        if (ts.isIdentifier(obj)) {
            const resolved = resolveIdentifier(obj, varStmts);
            if (resolved?.includes("rangesByText")) {
                return `f.GetRangesByText()[${csStringLiteral(expr.arguments[0].text)}]`;
            }
        }
    }
    // It might be a variable reference like `const [d0, d1] = dRanges;`
    if (ts.isIdentifier(expr)) {
        for (const varStmt of varStmts) {
            for (const decl of varStmt.declarationList.declarations) {
                if (ts.isIdentifier(decl.name) && decl.name.text === expr.text && decl.initializer) {
                    return resolveRangesExpression(decl.initializer, varStmts);
                }
            }
        }
    }
    return undefined;
}

function resolveIdentifier(id: ts.Identifier, varStmts: ts.VariableStatement[]): string | undefined {
    for (const varStmt of varStmts) {
        for (const decl of varStmt.declarationList.declarations) {
            if (ts.isIdentifier(decl.name) && decl.name.text === id.text && decl.initializer) {
                return decl.initializer.getText();
            }
        }
    }
    return undefined;
}

function getRangesByTextArg(arg: ts.CallExpression): string | undefined {
    if (arg.getText().startsWith("test.rangesByText()")) {
        if (ts.isStringLiteralLike(arg.arguments[0])) {
            return `[.. f.GetRangesByText()[${csStringLiteral(arg.arguments[0].text)}]]`;
        }
    }
    return undefined;
}

/**
 * Handles `.filter(r => !(r.marker && r.marker.data.KEY))` patterns on range arrays.
 * Converts to `[.. Core.Filter(source, (RangeMarker r) => ...)]`
 */
function parseFilterExpression(arg: ts.CallExpression): string | undefined {
    if (!ts.isPropertyAccessExpression(arg.expression) || arg.expression.name.text !== "filter") {
        return undefined;
    }
    if (arg.arguments.length !== 1) {
        return undefined;
    }
    const filterArg = arg.arguments[0];
    if (!ts.isArrowFunction(filterArg)) {
        return undefined;
    }

    // Resolve the source (the thing being filtered)
    const sourceExpr = arg.expression.expression;
    let sourceGo: string | undefined;

    if (ts.isIdentifier(sourceExpr)) {
        const file = sourceExpr.getSourceFile();
        const varStmts = file.statements.filter(ts.isVariableStatement);
        sourceGo = resolveRangesExpression(sourceExpr, varStmts);
    }
    else if (ts.isCallExpression(sourceExpr)) {
        // e.g. test.ranges().filter(...)
        if (sourceExpr.getText().includes("test.ranges()")) {
            sourceGo = "f.Ranges()";
        }
    }

    if (!sourceGo) {
        return undefined;
    }

    // Parse the filter body to generate a C# predicate
    const predicate = parseFilterPredicate(filterArg);
    if (!predicate) {
        return undefined;
    }

    return `[.. Core.Filter(${sourceGo}, ${predicate})]`;
}

function parseFilterPredicate(arrow: ts.ArrowFunction): string | undefined {
    // Handle `r => !(r.marker && r.marker.data.KEY)` → filter OUT ranges with marker.data.KEY
    const body = arrow.body;
    if (!ts.isExpression(body)) {
        return undefined;
    }

    const paramName = arrow.parameters[0]?.name.getText();
    if (!paramName) {
        return undefined;
    }

    // Check for `!(r.marker && r.marker.data.KEY)` pattern
    if (ts.isPrefixUnaryExpression(body) && body.operator === ts.SyntaxKind.ExclamationToken) {
        const inner = ts.isParenthesizedExpression(body.operand) ? body.operand.expression : body.operand;
        if (ts.isBinaryExpression(inner) && inner.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken) {
            // Right side should be `r.marker.data.KEY`
            const right = inner.right;
            if (ts.isPropertyAccessExpression(right) && right.expression.getText() === `${paramName}.marker.data`) {
                const key = right.name.text;
                return `static (RangeMarker r) => r.Marker == null || !r.Marker.Data.ContainsKey(${csStringLiteral(key)})`;
            }
        }
    }

    return undefined;
}

function parseBaselineQuickInfo(args: ts.NodeArray<ts.Expression>): VerifyBaselineQuickInfoCmd[] {
    if (args.length === 0) {
        return [{
            kind: "verifyBaselineQuickInfo",
        }];
    }
    // First arg is verbosityLevels: { markerName: number | number[] }
    const verbosityArg = args[0];
    if (!ts.isObjectLiteralExpression(verbosityArg)) {
        throw new Error(`Expected object literal expression for verify.baselineQuickInfo argument, got ${verbosityArg.getText()}`);
    }
    const verbosityLevels: Record<string, number[]> = {};
    for (const prop of verbosityArg.properties) {
        if (!ts.isPropertyAssignment(prop)) {
            throw new Error(`Expected property assignment in baselineQuickInfo verbosity levels, got ${prop.getText()}`);
        }
        let name: string;
        if (ts.isIdentifier(prop.name) || ts.isStringLiteral(prop.name)) {
            name = prop.name.text;
        }
        else if (ts.isNumericLiteral(prop.name)) {
            name = prop.name.text;
        }
        else {
            throw new Error(`Expected identifier, string, or numeric literal for property name in baselineQuickInfo verbosity levels, got ${prop.name.getText()}`);
        }
        if (ts.isArrayLiteralExpression(prop.initializer)) {
            const levels: number[] = [];
            for (const elem of prop.initializer.elements) {
                if (!ts.isNumericLiteral(elem)) {
                    throw new Error(`Expected numeric literal in baselineQuickInfo verbosity levels array, got ${elem.getText()}`);
                }
                levels.push(Number(elem.text));
            }
            verbosityLevels[name] = levels;
        }
        else if (ts.isNumericLiteral(prop.initializer)) {
            verbosityLevels[name] = [Number(prop.initializer.text)];
        }
        else {
            throw new Error(`Expected numeric literal or array literal for baselineQuickInfo verbosity level, got ${prop.initializer.getText()}`);
        }
    }
    return [{
        kind: "verifyBaselineQuickInfo",
        verbosityLevels,
    }];
}

function parseQuickInfoArgs(funcName: string, args: readonly ts.Expression[]): VerifyQuickInfoCmd[] {
    // We currently don't support 'expectedTags'.
    switch (funcName) {
        case "quickInfoAt": {
            if (args.length < 1 || args.length > 3) {
                throw new Error(`Expected 1 or 2 arguments in quickInfoIs, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            let arg0;
            if (!(arg0 = getStringLiteralLike(args[0]))) {
                throw new Error(`Expected string literal for first argument in quickInfoAt, got ${args[0].getText()}`);
            }
            const marker = csStringLiteral(arg0.text);
            let text: string | undefined;
            let arg1;
            if (args[1]) {
                if (!(arg1 = getStringLiteralLike(args[1]))) {
                    throw new Error(`Expected string literal for second argument in quickInfoAt, got ${args[1].getText()}`);
                }
                text = csStringLiteral(arg1.text);
            }
            let docs: string | undefined;
            let arg2;
            if (args[2]) {
                if (!(arg2 = getStringLiteralLike(args[2])) && args[2].getText() !== "undefined") {
                    throw new Error(`Expected string literal or undefined for third argument in quickInfoAt, got ${args[2].getText()}`);
                }
                if (arg2) {
                    docs = csStringLiteral(arg2.text);
                }
            }
            return [{
                kind: "quickInfoAt",
                marker,
                text,
                docs,
            }];
        }
        case "quickInfos": {
            const cmds: VerifyQuickInfoCmd[] = [];
            let arg0;
            if (args.length !== 1 || !(arg0 = getObjectLiteralExpression(args[0]))) {
                throw new Error(`Expected a single object literal argument in quickInfos, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            for (const prop of arg0.properties) {
                if (!ts.isPropertyAssignment(prop)) {
                    throw new Error(`Expected property assignment in quickInfos, got ${prop.getText()}`);
                }
                if (!(ts.isIdentifier(prop.name) || ts.isStringLiteralLike(prop.name) || ts.isNumericLiteral(prop.name))) {
                    throw new Error(`Expected identifier or literal for property name in quickInfos, got ${prop.name.getText()}`);
                }
                const marker = csStringLiteral(prop.name.text);
                let text: string;
                let docs: string | undefined;
                let init;
                if (init = getArrayLiteralExpression(prop.initializer)) {
                    if (init.elements.length !== 2) {
                        throw new Error(`Expected two elements in array literal for quickInfos property, got ${init.getText()}`);
                    }
                    let textExp, docsExp;
                    if (!(textExp = getStringLiteralLike(init.elements[0])) || !(docsExp = getStringLiteralLike(init.elements[1]))) {
                        throw new Error(`Expected string literals in array literal for quickInfos property, got ${init.getText()}`);
                    }
                    text = csStringLiteral(textExp.text);
                    docs = csStringLiteral(docsExp.text);
                }
                else if (init = getStringLiteralLike(prop.initializer)) {
                    text = csStringLiteral(init.text);
                }
                else {
                    throw new Error(`Expected string literal or array literal for quickInfos property, got ${prop.initializer.getText()}`);
                }
                cmds.push({
                    kind: "quickInfoAt",
                    marker,
                    text,
                    docs,
                });
            }
            return cmds;
        }
        case "quickInfoExists":
            return [{
                kind: "quickInfoExists",
            }];
        case "notQuickInfoExists":
            return [{
                kind: "notQuickInfoExists",
            }];
        case "quickInfoIs": {
            if (args.length < 1 || args.length > 2) {
                throw new Error(`Expected 1 or 2 arguments in quickInfoIs, got ${args.map(arg => arg.getText()).join(", ")}`);
            }
            let arg0;
            if (!(arg0 = getStringLiteralLike(args[0]))) {
                throw new Error(`Expected string literal for first argument in quickInfoIs, got ${args[0].getText()}`);
            }
            const text = csStringLiteral(arg0.text);
            let docs: string | undefined;
            if (args[1]) {
                let arg1;
                if (!(arg1 = getStringLiteralLike(args[1]))) {
                    throw new Error(`Expected string literal for second argument in quickInfoIs, got ${args[1].getText()}`);
                }
                docs = csStringLiteral(arg1.text);
            }
            return [{
                kind: "quickInfoIs",
                text,
                docs,
            }];
        }
    }
    throw new Error(`Unrecognized quick info function: ${funcName}`);
}

function parseOrganizeImportsArgs(args: readonly ts.Expression[]): [VerifyOrganizeImportsCmd] {
    if (args.length < 1 || args.length > 3) {
        throw new Error(`Expected 1-3 arguments in verify.organizeImports, got ${args.length}`);
    }

    const expectedContent = getStringLiteralLike(args[0]);
    if (!expectedContent) {
        throw new Error(`Expected string literal as first argument in verify.organizeImports, got ${args[0].getText()}`);
    }

    let mode = "LsProto.CodeActionKind.SourceOrganizeImports";
    if (args.length >= 2 && args[1].getText() !== "undefined") {
        const modeExpr = args[1];
        if (
            ts.isPropertyAccessExpression(modeExpr) &&
            modeExpr.expression.getText() === "ts.OrganizeImportsMode"
        ) {
            const modeName = modeExpr.name.text;
            switch (modeName) {
                case "RemoveUnused":
                    mode = "LsProto.CodeActionKind.SourceRemoveUnusedImports";
                    break;
                case "SortAndCombine":
                    mode = "LsProto.CodeActionKind.SourceSortImports";
                    break;
                case "All":
                    mode = "LsProto.CodeActionKind.SourceOrganizeImports";
                    break;
                default:
                    throw new Error(`Unsupported organize imports mode: ${modeName}`);
            }
        }
        else {
            throw new Error(`Unsupported organize imports mode: ${modeExpr.getText()}`);
        }
    }

    let preferences = "null";
    if (args.length >= 3 && args[2].getText() !== "undefined") {
        const prefsObj = getObjectLiteralExpression(args[2]);
        if (!prefsObj) {
            throw new Error(`Expected object literal for preferences in verify.organizeImports, got ${args[2].getText()}`);
        }

        const prefsFields: string[] = [];
        for (const prop of prefsObj.properties) {
            if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
                continue;
            }
            const propName = prop.name.text;
            const propValue = prop.initializer;

            const goFieldName = propName.charAt(0).toUpperCase() + propName.slice(1);

            if (propName === "organizeImportsIgnoreCase") {
                if (ts.isStringLiteral(propValue) && propValue.text === "auto") {
                    prefsFields.push(`${goFieldName} = Core.TSUnknown`);
                }
                else if (propValue.kind === ts.SyntaxKind.TrueKeyword) {
                    prefsFields.push(`${goFieldName} = Core.TSTrue`);
                }
                else if (propValue.kind === ts.SyntaxKind.FalseKeyword) {
                    prefsFields.push(`${goFieldName} = Core.TSFalse`);
                }
                else {
                    throw new Error(`Unsupported value for organizeImportsIgnoreCase: ${propValue.getText()}`);
                }
            }
            else if (propName === "organizeImportsSort") {
                if (ts.isStringLiteral(propValue)) {
                    switch (propValue.text) {
                        case "auto":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsSort.Auto`);
                            break;
                        case "ordinal":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsSort.Ordinal`);
                            break;
                        case "ordinalIgnoreCase":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsSort.OrdinalIgnoreCase`);
                            break;
                        case "natural":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsSort.Natural`);
                            break;
                        case "naturalIgnoreCase":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsSort.NaturalIgnoreCase`);
                            break;
                        default:
                            throw new Error(`Unsupported value for organizeImportsSort: ${propValue.text}`);
                    }
                }
                else {
                    throw new Error(`Expected string literal for organizeImportsSort, got ${propValue.getText()}`);
                }
            }
            else if (propName === "organizeImportsCollation") {
                if (ts.isStringLiteral(propValue)) {
                    if (propValue.text === "unicode") {
                        prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsCollation.Unicode`);
                    }
                    else if (propValue.text === "ordinal") {
                        prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsCollation.Ordinal`);
                    }
                    else {
                        throw new Error(`Unsupported value for organizeImportsCollation: ${propValue.text}`);
                    }
                }
                else {
                    throw new Error(`Expected string literal for organizeImportsCollation, got ${propValue.getText()}`);
                }
            }
            else if (propName === "organizeImportsCaseFirst") {
                if (ts.isStringLiteral(propValue)) {
                    if (propValue.text === "upper") {
                        prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsCaseFirst.Upper`);
                    }
                    else if (propValue.text === "lower") {
                        prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsCaseFirst.Lower`);
                    }
                    else {
                        throw new Error(`Unsupported value for organizeImportsCaseFirst: ${propValue.text}`);
                    }
                }
                else if (propValue.kind === ts.SyntaxKind.FalseKeyword) {
                    prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsCaseFirst.False`);
                }
                else {
                    throw new Error(`Expected string literal or false for organizeImportsCaseFirst, got ${propValue.getText()}`);
                }
            }
            else if (propName === "organizeImportsTypeOrder") {
                if (ts.isStringLiteral(propValue)) {
                    const typeOrderValue = propValue.text;
                    switch (typeOrderValue) {
                        case "last":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsTypeOrder.Last`);
                            break;
                        case "inline":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsTypeOrder.Inline`);
                            break;
                        case "first":
                            prefsFields.push(`${goFieldName} = LsUtil.OrganizeImportsTypeOrder.First`);
                            break;
                        default:
                            throw new Error(`Unsupported value for organizeImportsTypeOrder: ${typeOrderValue}`);
                    }
                }
                else {
                    throw new Error(`Expected string literal for organizeImportsTypeOrder, got ${propValue.getText()}`);
                }
            }
            // Boolean fields that are now Tristate
            else if (propName === "organizeImportsNumericCollation" || propName === "organizeImportsAccentCollation") {
                if (propValue.kind === ts.SyntaxKind.TrueKeyword) {
                    prefsFields.push(`${goFieldName} = Core.TSTrue`);
                }
                else if (propValue.kind === ts.SyntaxKind.FalseKeyword) {
                    prefsFields.push(`${goFieldName} = Core.TSFalse`);
                }
                else {
                    throw new Error(`Expected boolean for ${propName}, got ${propValue.getText()}`);
                }
            }
            // organizeImportsLocale is a plain string, not a pointer
            else if (propName === "organizeImportsLocale") {
                if (ts.isStringLiteral(propValue)) {
                    prefsFields.push(`${goFieldName} = ${csStringLiteral(propValue.text)}`);
                }
                else {
                    throw new Error(`Expected string literal for organizeImportsLocale, got ${propValue.getText()}`);
                }
            }
            // Default handling for other string properties
            else if (ts.isStringLiteral(propValue)) {
                prefsFields.push(`${goFieldName} = ${csStringLiteral(propValue.text)}`);
            }
            else if (propValue.kind === ts.SyntaxKind.TrueKeyword) {
                prefsFields.push(`${goFieldName} = Core.TSTrue`);
            }
            else if (propValue.kind === ts.SyntaxKind.FalseKeyword) {
                prefsFields.push(`${goFieldName} = Core.TSFalse`);
            }
            else {
                prefsFields.push(`${goFieldName} = ${propValue.getText()}`);
            }
        }

        if (prefsFields.length > 0) {
            preferences = `new LsUtil.UserPreferences\n{\n${prefsFields.join(",\n")},\n}`;
        }
    }

    return [{
        kind: "verifyOrganizeImports",
        expectedContent: expectedContent.text,
        mode,
        preferences,
    }];
}

function parseBaselineSignatureHelp(args: ts.NodeArray<ts.Expression>): Cmd {
    if (args.length !== 0) {
        // All calls are currently empty!
        throw new Error("Expected no arguments in verify.baselineSignatureHelp");
    }
    return {
        kind: "verifyBaselineSignatureHelp",
    };
}

function parseSignatureHelpOptions(obj: ts.ObjectLiteralExpression): VerifySignatureHelpOptions {
    const options: VerifySignatureHelpOptions = {};

    for (const prop of obj.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            console.error(`Unexpected property in signatureHelp options: ${prop.getText()}`);
            continue;
        }
        const name = prop.name.text;
        const value = prop.initializer;

        switch (name) {
            case "marker": {
                if (ts.isStringLiteral(value)) {
                    options.marker = value.text;
                }
                else if (ts.isArrayLiteralExpression(value)) {
                    const markers: string[] = [];
                    for (const elem of value.elements) {
                        if (ts.isStringLiteral(elem)) {
                            markers.push(elem.text);
                        }
                        else {
                            throw new Error(`Expected string literal in marker array, got ${elem.getText()}`);
                        }
                    }
                    options.marker = markers;
                }
                else {
                    throw new Error(`Expected string or array for marker, got ${value.getText()}`);
                }
                break;
            }
            case "text": {
                const str = getStringLiteralLike(value);
                if (!str) {
                    throw new Error(`Expected string for text, got ${value.getText()}`);
                }
                options.text = str.text;
                break;
            }
            case "docComment": {
                const str = getStringLiteralLike(value);
                if (!str) {
                    throw new Error(`Expected string for docComment, got ${value.getText()}`);
                }
                options.docComment = str.text;
                break;
            }
            case "parameterCount": {
                const num = getNumericLiteral(value);
                if (!num) {
                    throw new Error(`Expected number for parameterCount, got ${value.getText()}`);
                }
                options.parameterCount = parseInt(num.text, 10);
                break;
            }
            case "parameterName": {
                const str = getStringLiteralLike(value);
                if (!str) {
                    throw new Error(`Expected string for parameterName, got ${value.getText()}`);
                }
                options.parameterName = str.text;
                break;
            }
            case "parameterSpan": {
                const str = getStringLiteralLike(value);
                if (!str) {
                    throw new Error(`Expected string for parameterSpan, got ${value.getText()}`);
                }
                options.parameterSpan = str.text;
                break;
            }
            case "parameterDocComment": {
                const str = getStringLiteralLike(value);
                if (!str) {
                    throw new Error(`Expected string for parameterDocComment, got ${value.getText()}`);
                }
                options.parameterDocComment = str.text;
                break;
            }
            case "overloadsCount": {
                const num = getNumericLiteral(value);
                if (!num) {
                    throw new Error(`Expected number for overloadsCount, got ${value.getText()}`);
                }
                options.overloadsCount = parseInt(num.text, 10);
                break;
            }
            case "overrideSelectedItemIndex": {
                const num = getNumericLiteral(value);
                if (!num) {
                    throw new Error(`Expected number for overrideSelectedItemIndex, got ${value.getText()}`);
                }
                options.overrideSelectedItemIndex = parseInt(num.text, 10);
                break;
            }
            case "triggerReason": {
                // triggerReason is an object like { kind: "invoked" } or { kind: "characterTyped", triggerCharacter: "(" }
                // For now, just pass it through as a string representation
                options.triggerReason = value.getText();
                break;
            }
            case "argumentCount":
                // ignore
                break;
            case "isVariadic": {
                if (value.kind === ts.SyntaxKind.TrueKeyword) {
                    options.isVariadic = true;
                }
                else if (value.kind === ts.SyntaxKind.FalseKeyword) {
                    options.isVariadic = false;
                }
                else {
                    throw new Error(`Expected boolean for isVariadic, got ${value.getText()}`);
                }
                break;
            }
            case "tags":
                // ignore
                break;
            default:
                throw new Error(`Unknown signatureHelp option: ${name}`);
        }
    }
    return options;
}

function parseSignatureHelp(args: ts.NodeArray<ts.Expression>): Cmd[] {
    const allOptions: VerifySignatureHelpOptions[] = [];

    for (const arg of args) {
        if (ts.isObjectLiteralExpression(arg)) {
            const opts = parseSignatureHelpOptions(arg);
            allOptions.push(opts);
        }
        else if (ts.isIdentifier(arg)) {
            // Could be a variable reference like `help2` - skip for now
            throw new Error(`signatureHelp with variable reference not supported: ${arg.getText()}`);
        }
        else {
            throw new Error(`Unexpected argument type in signatureHelp: ${arg.getText()}`);
        }
    }

    if (allOptions.length === 0) {
        throw new Error("signatureHelp requires at least one options object");
    }

    return [{
        kind: "verifySignatureHelp",
        options: allOptions,
    }];
}

function parseNoSignatureHelp(args: ts.NodeArray<ts.Expression>): Cmd[] {
    const markers: string[] = [];

    for (const arg of args) {
        if (ts.isStringLiteral(arg)) {
            markers.push(arg.text);
        }
        else if (ts.isSpreadElement(arg)) {
            // Handle ...test.markerNames()
            const expr = arg.expression;
            if (
                ts.isCallExpression(expr) &&
                ts.isPropertyAccessExpression(expr.expression) &&
                ts.isIdentifier(expr.expression.expression) &&
                expr.expression.expression.text === "test" &&
                ts.isIdentifier(expr.expression.name) &&
                expr.expression.name.text === "markerNames"
            ) {
                // This means "all markers" - we'll handle this specially in the generator
                return [{
                    kind: "verifyNoSignatureHelp",
                    markers: ["...test.markerNames()"],
                }];
            }
            throw new Error(`Unsupported spread in noSignatureHelp: ${arg.getText()}`);
        }
        else {
            throw new Error(`Unexpected argument in noSignatureHelp: ${arg.getText()}`);
        }
    }

    return [{
        kind: "verifyNoSignatureHelp",
        markers,
    }];
}

interface SignatureHelpTriggerReason {
    kind: "invoked" | "characterTyped" | "retrigger";
    triggerCharacter?: string;
}

function parseTriggerReason(arg: ts.Expression): SignatureHelpTriggerReason | "undefined" {
    // Handle undefined literal
    if (ts.isIdentifier(arg) && arg.text === "undefined") {
        return "undefined";
    }

    if (!ts.isObjectLiteralExpression(arg)) {
        throw new Error(`Expected object literal for trigger reason, got ${arg.getText()}`);
    }

    let kind: "invoked" | "characterTyped" | "retrigger" | undefined;
    let triggerCharacter: string | undefined;

    for (const prop of arg.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            throw new Error(`Unexpected property in trigger reason: ${prop.getText()}`);
        }
        const name = prop.name.text;
        if (name === "kind") {
            if (!ts.isStringLiteral(prop.initializer)) {
                throw new Error(`Expected string literal for kind, got ${prop.initializer.getText()}`);
            }
            const k = prop.initializer.text;
            if (k === "invoked" || k === "characterTyped" || k === "retrigger") {
                kind = k;
            }
            else {
                throw new Error(`Unknown trigger reason kind: ${k}`);
            }
        }
        else if (name === "triggerCharacter") {
            if (!ts.isStringLiteral(prop.initializer)) {
                throw new Error(`Expected string literal for triggerCharacter, got ${prop.initializer.getText()}`);
            }
            triggerCharacter = prop.initializer.text;
        }
    }

    if (!kind) {
        throw new Error(`Missing kind in trigger reason`);
    }

    return { kind, triggerCharacter };
}

function parseSignatureHelpPresentForTriggerReason(args: ts.NodeArray<ts.Expression>): Cmd[] {
    if (args.length === 0) {
        throw new Error("signatureHelpPresentForTriggerReason requires at least one argument");
    }

    const triggerReason = parseTriggerReason(args[0]);

    const markers: string[] = [];
    for (let i = 1; i < args.length; i++) {
        const arg = args[i];
        if (ts.isStringLiteral(arg)) {
            markers.push(arg.text);
        }
        else {
            throw new Error(`Unexpected argument in signatureHelpPresentForTriggerReason: ${arg.getText()}`);
        }
    }

    return [{
        kind: "verifySignatureHelpPresent",
        triggerReason: triggerReason === "undefined" ? undefined : triggerReason,
        markers,
    }];
}

function parseNoSignatureHelpForTriggerReason(args: ts.NodeArray<ts.Expression>): Cmd[] {
    if (args.length === 0) {
        throw new Error("noSignatureHelpForTriggerReason requires at least one argument");
    }

    const triggerReason = parseTriggerReason(args[0]);

    const markers: string[] = [];
    for (let i = 1; i < args.length; i++) {
        const arg = args[i];
        if (ts.isStringLiteral(arg)) {
            markers.push(arg.text);
        }
        else {
            throw new Error(`Unexpected argument in noSignatureHelpForTriggerReason: ${arg.getText()}`);
        }
    }

    return [{
        kind: "verifyNoSignatureHelpForTriggerReason",
        triggerReason: triggerReason === "undefined" ? undefined : triggerReason,
        markers,
    }];
}

function parseBaselineSmartSelection(args: ts.NodeArray<ts.Expression>): Cmd {
    if (args.length !== 0) {
        // All calls are currently empty!
        throw new Error("Expected no arguments in verify.baselineSmartSelection");
    }
    return {
        kind: "verifyBaselineSmartSelection",
    };
}

function parseBaselineCallHierarchy(args: ts.NodeArray<ts.Expression>): Cmd {
    if (args.length !== 0) {
        throw new Error("Expected no arguments in verify.baselineCallHierarchy");
    }
    return {
        kind: "verifyBaselineCallHierarchy",
    };
}

function parseOutliningSpansArgs(args: readonly ts.Expression[]): [VerifyOutliningSpansCmd] {
    if (args.length === 0) {
        throw new Error("Expected at least one argument in verify.outliningSpansInCurrentFile");
    }

    let spans: string = "";
    // Optional second argument for kind filter
    let foldingRangeKind: string | undefined;
    if (args.length > 1) {
        const kindArg = getStringLiteralLike(args[1]);
        if (!kindArg) {
            throw new Error(`Expected string literal for outlining kind, got ${args[1].getText()}`);
        }
        switch (kindArg.text) {
            case "comment":
                foldingRangeKind = "LsProto.FoldingRangeKind.Comment";
                break;
            case "region":
                foldingRangeKind = "LsProto.FoldingRangeKind.Region";
                break;
            case "imports":
                foldingRangeKind = "LsProto.FoldingRangeKind.Imports";
                break;
            case "code":
                break;
            default:
                throw new Error(`Unknown folding range kind: ${kindArg.text}`);
        }
    }

    return [{
        kind: "verifyOutliningSpans",
        spans,
        foldingRangeKind,
    }];
}

function parseSemanticClassificationsAre(args: readonly ts.Expression[]): [VerifySemanticClassificationsCmd] | [] {
    if (args.length < 1) {
        throw new Error("semanticClassificationsAre requires at least a format argument");
    }

    const formatArg = args[0];
    if (!ts.isStringLiteralLike(formatArg)) {
        throw new Error("semanticClassificationsAre first argument must be a string literal");
    }

    const format = formatArg.text;

    // Only handle "2020" format for semantic tokens
    if (format !== "2020") {
        // Skip other formats like "original"
        return [];
    }

    const tokens: Array<{ type: string; text: string; }> = [];

    // Parse the classification tokens (c2.semanticToken("type", "text"))
    for (let i = 1; i < args.length; i++) {
        const arg = args[i];
        if (!ts.isCallExpression(arg)) {
            throw new Error(`Expected call expression for token at index ${i}`);
        }

        if (!ts.isPropertyAccessExpression(arg.expression) || arg.expression.name.text !== "semanticToken") {
            throw new Error(`Expected semanticToken call at index ${i}`);
        }

        if (arg.arguments.length < 2) {
            throw new Error(`semanticToken requires 2 arguments at index ${i}`);
        }

        const typeArg = arg.arguments[0];
        const textArg = arg.arguments[1];

        if (!ts.isStringLiteralLike(typeArg) || !ts.isStringLiteralLike(textArg)) {
            throw new Error(`semanticToken arguments must be string literals at index ${i}`);
        }

        // Map TypeScript's internal "member" type to LSP's "method" type
        let tokenType = typeArg.text;
        tokenType = tokenType.replace(/\bmember\b/g, "method");

        tokens.push({
            type: tokenType,
            text: textArg.text,
        });
    }

    return [{
        kind: "verifySemanticClassifications",
        format,
        tokens,
    }];
}

function parseKind(expr: ts.Expression): string {
    if (!ts.isStringLiteral(expr)) {
        throw new Error(`Expected string literal for kind, got ${expr.getText()}`);
    }
    switch (expr.text) {
        case "primitive type":
        case "keyword":
            return "LsProto.CompletionItemKind.Keyword";
        case "const":
        case "let":
        case "var":
        case "local var":
        case "alias":
        case "parameter":
            return "LsProto.CompletionItemKind.Variable";
        case "property":
        case "getter":
        case "setter":
            return "LsProto.CompletionItemKind.Field";
        case "function":
        case "local function":
            return "LsProto.CompletionItemKind.Function";
        case "method":
        case "construct":
        case "call":
        case "index":
            return "LsProto.CompletionItemKind.Method";
        case "enum":
            return "LsProto.CompletionItemKind.Enum";
        case "enum member":
            return "LsProto.CompletionItemKind.EnumMember";
        case "module":
        case "external module name":
            return "LsProto.CompletionItemKind.Module";
        case "class":
        case "type":
            return "LsProto.CompletionItemKind.Class";
        case "interface":
            return "LsProto.CompletionItemKind.Interface";
        case "warning":
            return "LsProto.CompletionItemKind.Text";
        case "script":
            return "LsProto.CompletionItemKind.File";
        case "directory":
            return "LsProto.CompletionItemKind.Folder";
        case "string":
            return "LsProto.CompletionItemKind.Constant";
        default:
            return "LsProto.CompletionItemKind.Property";
    }
}

const fileKindModifiers = new Set([".d.ts", ".ts", ".tsx", ".js", ".jsx", ".json"]);

function parseKindModifiers(expr: ts.Expression): { isOptional: boolean; isDeprecated: boolean; extensions: string[]; } {
    if (!ts.isStringLiteral(expr)) {
        throw new Error(`Expected string literal for kind modifiers, got ${expr.getText()}`);
    }
    let isOptional = false;
    let isDeprecated = false;
    const extensions: string[] = [];
    const modifiers = expr.text.split(",");
    for (const modifier of modifiers) {
        switch (modifier) {
            case "optional":
                isOptional = true;
                break;
            case "deprecated":
                isDeprecated = true;
                break;
            default:
                if (fileKindModifiers.has(modifier)) {
                    extensions.push(modifier);
                }
        }
    }
    return {
        isOptional,
        isDeprecated,
        extensions,
    };
}

interface ParsedSortText {
    expression: string;
    deprecated: boolean;
}

function parseSortText(expr: ts.Expression): ParsedSortText {
    if (ts.isCallExpression(expr) && expr.expression.getText() === "completion.SortText.Deprecated") {
        const inner = parseSortText(expr.arguments[0]);
        return {
            expression: `Ls.DeprecateSortText(${inner.expression})`,
            deprecated: true,
        };
    }

    return { expression: parseSortTextExpression(expr.getText()), deprecated: false };
}

function parseSortTextExpression(text: string): string {
    switch (text) {
        case "completion.SortText.LocalDeclarationPriority":
            return "Ls.SortTextLocalDeclarationPriority";
        case "completion.SortText.LocationPriority":
            return "Ls.SortTextLocationPriority";
        case "completion.SortText.OptionalMember":
            return "Ls.SortTextOptionalMember";
        case "completion.SortText.MemberDeclaredBySpreadAssignment":
            return "Ls.SortTextMemberDeclaredBySpreadAssignment";
        case "completion.SortText.SuggestedClassMembers":
            return "Ls.SortTextSuggestedClassMembers";
        case "completion.SortText.GlobalsOrKeywords":
            return "Ls.SortTextGlobalsOrKeywords";
        case "completion.SortText.AutoImportSuggestions":
            return "Ls.SortTextAutoImportSuggestions";
        case "completion.SortText.ClassMemberSnippets":
            return "Ls.SortTextClassMemberSnippets";
        case "completion.SortText.JavascriptIdentifiers":
            return "Ls.SortTextJavascriptIdentifiers";
        default:
            throw new Error(`Unrecognized sort text: ${text}`); // !!! support deprecated/obj literal prop/etc
    }
}

function formatCompletionItemTags(tags: Set<string>): string | undefined {
    if (tags.size === 0) {
        return undefined;
    }
    return `Tags = [${[...tags].join(", ")}],`;
}

function parseVerifyNavigateTo(args: ts.NodeArray<ts.Expression>): [VerifyNavToCmd] {
    const goArgs = [];
    for (const arg of args) {
        const result = parseVerifyNavigateToArg(arg);
        goArgs.push(result);
    }
    return [{
        kind: "verifyNavigateTo",
        args: goArgs,
    }];
}

function parseVerifyNavigateToArg(arg: ts.Expression): string {
    if (!ts.isObjectLiteralExpression(arg)) {
        throw new Error(`Expected object literal expression for verify.navigateTo argument, got ${arg.getText()}`);
    }
    let prefs;
    const items = [];
    let pattern: string | undefined;
    for (const prop of arg.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            throw new Error(`Expected property assignment with identifier name for verify.navigateTo argument, got ${prop.getText()}`);
        }
        const propName = prop.name.text;
        switch (propName) {
            case "pattern": {
                let patternInit = getStringLiteralLike(prop.initializer);
                if (!patternInit) {
                    throw new Error(`Expected string literal for pattern in verify.navigateTo argument, got ${prop.initializer.getText()}`);
                }
                pattern = csStringLiteral(patternInit.text);
                break;
            }
            case "fileName":
                // no longer supported
                continue;
            case "expected": {
                const init = prop.initializer;
                if (!ts.isArrayLiteralExpression(init)) {
                    throw new Error(`Expected array literal expression for expected property in verify.navigateTo argument, got ${init.getText()}`);
                }
                for (const elem of init.elements) {
                    const result = parseNavToItem(elem);
                    items.push(result);
                }
                break;
            }
            case "excludeLibFiles": {
                if (prop.initializer.kind === ts.SyntaxKind.FalseKeyword) {
                    prefs = `new LsUtil.UserPreferences\n{\nExcludeLibrarySymbolsInNavTo = Core.TSFalse,\n}`;
                }
            }
        }
    }
    if (!prefs) {
        prefs = "null";
    }
    return `new VerifyWorkspaceSymbolCase
    {
        Pattern = ${pattern ? pattern : `""`},
        Preferences = ${prefs},
        Exact = new[]{${items.length ? items.join(",\n") + "," : ""}},
    }`;
}

function parseVerifyNavTree(args: readonly ts.Expression[]): [VerifyNavTreeCmd] {
    // Ignore arguments and use baseline tests intead.
    return [{
        kind: "verifyNavigationTree",
    }];
}

function parseNavToItem(arg: ts.Expression): string {
    let item = getNodeOfKind(arg, ts.isObjectLiteralExpression);
    if (!item) {
        throw new Error(`Expected object literal expression for navigateTo item, got ${arg.getText()}`);
    }
    const itemProps: string[] = [];
    for (const prop of item.properties) {
        if (!ts.isPropertyAssignment(prop) || !ts.isIdentifier(prop.name)) {
            throw new Error(`Expected property assignment with identifier name for navigateTo item, got ${prop.getText()}`);
        }
        const propName = prop.name.text;
        const init = prop.initializer;
        switch (propName) {
            case "name": {
                let nameInit;
                if (!(nameInit = getStringLiteralLike(init))) {
                    throw new Error(`Expected string literal for name in navigateTo item, got ${init.getText()}`);
                }
                itemProps.push(`Name = ${csStringLiteral(nameInit.text)}`);
                break;
            }
            case "kind": {
                const goKind = getSymbolKind(init);
                itemProps.push(`Kind = LsProto.${goKind}`);
                break;
            }
            case "kindModifiers": {
                if (init.getText().includes("deprecated")) {
                    itemProps.push(`Tags = [LsProto.SymbolTag.Deprecated]`);
                }
                break;
            }
            case "range": {
                if (ts.isIdentifier(init) || (ts.isElementAccessExpression(init) && ts.isIdentifier(init.expression))) {
                    let parsedRange = parseRangeVariable(init);
                    if (parsedRange) {
                        itemProps.push(`Location = ${parsedRange}.LSLocation()`);
                        continue;
                    }
                }
                if (ts.isElementAccessExpression(init) && init.expression.getText() === "test.ranges()") {
                    itemProps.push(`Location = f.Ranges()[${parseInt(init.argumentExpression.getText())}].LSLocation()`);
                    continue;
                }
                throw new Error(`Expected range variable for range in navigateTo item, got ${init.getText()}`);
            }
            case "containerName": {
                let nameInit;
                if (!(nameInit = getStringLiteralLike(init))) {
                    throw new Error(`Expected string literal for container name in navigateTo item, got ${init.getText()}`);
                }
                itemProps.push(`ContainerName = ${csStringLiteral(nameInit.text)}`);
                break;
            }
            default:
                // ignore other properties
        }
    }
    return `new LsProto.SymbolInformation\n{\n${itemProps.join(",\n")},\n}`;
}

function getSymbolKind(kind: ts.Expression): string {
    let result;
    if (!(result = getStringLiteralLike(kind))) {
        throw new Error(`Expected string literal for symbol kind, got ${kind.getText()}`);
    }
    return getSymbolKindWorker(result.text);
}

function getSymbolKindWorker(kind: string): string {
    switch (kind) {
        case "script":
            return "SymbolKind.File";
        case "module":
            return "SymbolKind.Namespace";
        case "class":
        case "local class":
            return "SymbolKind.Class";
        case "interface":
            return "SymbolKind.Interface";
        case "type":
            return "SymbolKind.Class";
        case "enum":
            return "SymbolKind.Enum";
        case "enum member":
            return "SymbolKind.EnumMember";
        case "var":
        case "local var":
        case "using":
        case "await using":
            return "SymbolKind.Variable";
        case "function":
        case "local function":
            return "SymbolKind.Function";
        case "method":
            return "SymbolKind.Method";
        case "getter":
        case "setter":
        case "property":
        case "accessor":
            return "SymbolKind.Property";
        case "constructor":
        case "construct":
            return "SymbolKind.Constructor";
        case "call":
        case "index":
            return "SymbolKind.Function";
        case "parameter":
            return "SymbolKind.Variable";
        case "type parameter":
            return "SymbolKind.TypeParameter";
        case "primitive type":
            return "SymbolKind.Object";
        case "const":
        case "let":
            return "SymbolKind.Variable";
        case "directory":
            return "SymbolKind.Package";
        case "external module name":
            return "SymbolKind.Module";
        case "string":
            return "SymbolKind.String";
        case "type":
            return "SymbolKind.Class";
        default:
            return "SymbolKind.Variable";
    }
}

interface VerifyCompletionsCmd {
    kind: "verifyCompletions";
    marker: string;
    isNewIdentifierLocation?: true;
    args?: VerifyCompletionsArgs | "null";
    andApplyCodeActionArgs?: VerifyApplyCodeActionArgs;
}

interface VerifyCompletionsArgs {
    includes?: string;
    excludes?: string;
    exact?: string;
    unsorted?: string;
    preferences: string;
}

interface VerifyApplyCodeActionArgs {
    name: string;
    source: string;
    description: string;
    newFileContent: string;
}

interface VerifyApplyCodeActionFromCompletionCmd {
    kind: "verifyApplyCodeActionFromCompletion";
    marker: string;
    options: string;
}

interface VerifyBaselineFindAllReferencesCmd {
    kind: "verifyBaselineFindAllReferences";
    markers: string[];
    ranges?: boolean;
}

interface VerifyBaselineGoToDefinitionCmd {
    kind: "verifyBaselineGoToDefinition" | "verifyBaselineGoToType" | "verifyBaselineGoToImplementation" | "verifyBaselineGoToSourceDefinition";
    markers: string[];
    boundSpan?: true;
    ranges?: boolean;
}

interface VerifyBaselineQuickInfoCmd {
    kind: "verifyBaselineQuickInfo";
    verbosityLevels?: Record<string, number[]>;
}

interface VerifyBaselineSignatureHelpCmd {
    kind: "verifyBaselineSignatureHelp";
}

interface VerifyBaselineSmartSelection {
    kind: "verifyBaselineSmartSelection";
}

interface VerifyBaselineCallHierarchy {
    kind: "verifyBaselineCallHierarchy";
}

interface VerifyBaselineRenameCmd {
    kind: "verifyBaselineRename" | "verifyBaselineRenameAtRangesWithText";
    args: string[];
    preferences: string;
}

interface VerifyBaselineDocumentHighlightsCmd {
    kind: "verifyBaselineDocumentHighlights";
    args: string[];
    preferences: string;
    filesToSearch?: string[];
}

interface VerifyBaselineInlayHintsCmd {
    kind: "verifyBaselineInlayHints";
    span: string;
    preferences: string;
}

interface VerifyImportFixAtPositionCmd {
    kind: "verifyImportFixAtPosition";
    expectedTexts: string[];
    preferences: string;
}

interface VerifyImportFixModuleSpecifiersCmd {
    kind: "verifyImportFixModuleSpecifiers";
    markerName: string;
    moduleSpecifiers: string[];
    preferences: string;
}

interface GoToCmd {
    kind: "goTo";
    // !!! `selectRange` and `rangeStart` require parsing variables and `test.ranges()[n]`
    funcName: "marker" | "file" | "fileNumber" | "EOF" | "BOF" | "position" | "select";
    args: string[];
}

interface EditCmd {
    kind: "edit";
    csStatement: string;
}

interface FormatCmd {
    kind: "format";
    csStatement: string;
}

interface VerifyContentCmd {
    kind: "verifyContent";
    csStatement: string;
}

interface VerifyQuickInfoCmd {
    kind: "quickInfoIs" | "quickInfoAt" | "quickInfoExists" | "notQuickInfoExists";
    marker?: string;
    text?: string;
    docs?: string;
}

interface VerifyOrganizeImportsCmd {
    kind: "verifyOrganizeImports";
    expectedContent: string;
    mode: string;
    preferences: string;
}

interface VerifyRenameInfoCmd {
    kind: "renameInfoSucceeded" | "renameInfoFailed";
    preferences: string;
}

interface VerifyGetEditsForFileRenameCmd {
    kind: "verifyGetEditsForFileRename";
    oldPath: string;
    newPath: string;
    newFileContents: string;
    preferences: string;
}

interface VerifyBaselineLinkedEditingCmd {
    kind: "verifyBaselineLinkedEditing";
}
interface VerifyLinkedEditingCmd {
    kind: "verifyLinkedEditing";
    ranges: string;
}

interface VerifyDiagnosticsCmd {
    kind: "verifyDiagnostics";
    arg: string;
    isSuggestion: boolean;
}

interface VerifyBaselineDiagnosticsCmd {
    kind: "verifyBaselineDiagnostics";
}

interface VerifyNavToCmd {
    kind: "verifyNavigateTo";
    args: string[];
}

interface VerifySignatureHelpOptions {
    marker?: string | string[];
    text?: string;
    docComment?: string;
    parameterCount?: number;
    parameterName?: string;
    parameterSpan?: string;
    parameterDocComment?: string;
    overloadsCount?: number;
    overrideSelectedItemIndex?: number;
    triggerReason?: string;
    isVariadic?: boolean;
}

interface VerifySignatureHelpCmd {
    kind: "verifySignatureHelp";
    options: VerifySignatureHelpOptions[];
}

interface VerifyNoSignatureHelpCmd {
    kind: "verifyNoSignatureHelp";
    markers: string[];
}

interface VerifySignatureHelpPresentCmd {
    kind: "verifySignatureHelpPresent";
    triggerReason?: SignatureHelpTriggerReason;
    markers: string[];
}

interface VerifyNoSignatureHelpForTriggerReasonCmd {
    kind: "verifyNoSignatureHelpForTriggerReason";
    triggerReason?: SignatureHelpTriggerReason;
    markers: string[];
}

interface VerifyOutliningSpansCmd {
    kind: "verifyOutliningSpans";
    spans: string;
    foldingRangeKind?: string;
}

interface VerifyNavTreeCmd {
    kind: "verifyNavigationTree";
}

interface VerifyNumberOfErrorsInCurrentFileCmd {
    kind: "verifyNumberOfErrorsInCurrentFile";
    expectedCount: number;
}

interface VerifyNoErrorsCmd {
    kind: "verifyNoErrors";
}

interface VerifyErrorExistsAtRangeCmd {
    kind: "verifyErrorExistsAtRange";
    range: string;
    code: number;
    message: string;
}

interface VerifyCurrentLineContentIsCmd {
    kind: "verifyCurrentLineContentIs";
    text: string;
}

interface VerifyCurrentFileContentIsCmd {
    kind: "verifyCurrentFileContentIs";
    text: string;
}

interface VerifyErrorExistsBetweenMarkersCmd {
    kind: "verifyErrorExistsBetweenMarkers";
    startMarker: string;
    endMarker: string;
}

interface VerifyErrorExistsAfterMarkerCmd {
    kind: "verifyErrorExistsAfterMarker";
    markerName: string;
}

interface VerifyErrorExistsBeforeMarkerCmd {
    kind: "verifyErrorExistsBeforeMarker";
    markerName: string;
}

interface VerifyCodeFixCmd {
    kind: "verifyCodeFix";
    description: string;
    newFileContent?: string;
    newRangeContent?: string;
    index: number;
    applyChanges: boolean;
    preferences: string;
}

interface VerifyCodeFixAvailableCmd {
    kind: "verifyCodeFixAvailable";
    descriptions: string[];
    unavailableDescriptions: string[];
    expectNone: boolean;
}

interface VerifyRangeAfterCodeFixCmd {
    kind: "verifyRangeAfterCodeFix";
    expectedText: string;
    includeWhiteSpace: boolean;
    errorCode: number;
    index: number;
}

interface VerifyCodeFixAllCmd {
    kind: "verifyCodeFixAll";
    fixId: string;
    newFileContent: string;
}

interface VerifyDocCommentTemplateCmd {
    kind: "verifyDocCommentTemplate";
    marker: string;
    expectedOffset: string;
    expectedText: string;
    generateReturnInDocTemplate?: string;
}

interface VerifyNoDocCommentTemplateCmd {
    kind: "verifyNoDocCommentTemplate";
    marker: string;
}

interface ForOfCmd {
    kind: "forOf";
    variableName: string;
    rangeExpression: string;
    bodyCommands: Cmd[];
}

interface VerifySemanticClassificationsCmd {
    kind: "verifySemanticClassifications";
    format: string;
    tokens: Array<{ type: string; text: string; }>;
}

type Cmd =
    | VerifyCompletionsCmd
    | VerifyApplyCodeActionFromCompletionCmd
    | VerifyBaselineFindAllReferencesCmd
    | VerifyBaselineDocumentHighlightsCmd
    | VerifyBaselineGoToDefinitionCmd
    | VerifyBaselineQuickInfoCmd
    | VerifyBaselineSignatureHelpCmd
    | VerifyBaselineSmartSelection
    | VerifySignatureHelpCmd
    | VerifyNoSignatureHelpCmd
    | VerifySignatureHelpPresentCmd
    | VerifyNoSignatureHelpForTriggerReasonCmd
    | VerifyBaselineCallHierarchy
    | GoToCmd
    | FormatCmd
    | EditCmd
    | VerifyContentCmd
    | VerifyQuickInfoCmd
    | VerifyOrganizeImportsCmd
    | VerifyBaselineRenameCmd
    | VerifyRenameInfoCmd
    | VerifyGetEditsForFileRenameCmd
    | VerifyBaselineLinkedEditingCmd
    | VerifyLinkedEditingCmd
    | VerifyNavToCmd
    | VerifyNavTreeCmd
    | VerifyBaselineInlayHintsCmd
    | VerifyImportFixAtPositionCmd
    | VerifyImportFixModuleSpecifiersCmd
    | VerifyDiagnosticsCmd
    | VerifyBaselineDiagnosticsCmd
    | VerifySemanticClassificationsCmd
    | VerifyOutliningSpansCmd
    | VerifyNumberOfErrorsInCurrentFileCmd
    | VerifyNoErrorsCmd
    | VerifyErrorExistsAtRangeCmd
    | VerifyCurrentLineContentIsCmd
    | VerifyCurrentFileContentIsCmd
    | VerifyErrorExistsBetweenMarkersCmd
    | VerifyErrorExistsAfterMarkerCmd
    | VerifyErrorExistsBeforeMarkerCmd
    | VerifyCodeFixCmd
    | VerifyCodeFixAvailableCmd
    | VerifyRangeAfterCodeFixCmd
    | VerifyCodeFixAllCmd
    | VerifyDocCommentTemplateCmd
    | VerifyNoDocCommentTemplateCmd
    | ForOfCmd;

function generateVerifyOutliningSpans({ foldingRangeKind }: VerifyOutliningSpansCmd): string {
    if (foldingRangeKind) {
        return `f.VerifyOutliningSpans(${foldingRangeKind})`;
    }
    return `f.VerifyOutliningSpans()`;
}

function generateVerifyCompletions({ marker, args, isNewIdentifierLocation, andApplyCodeActionArgs }: VerifyCompletionsCmd): string {
    let expectedList: string;
    if (args === "null") {
        expectedList = "null";
    }
    else {
        const expected = [];
        if (args?.includes) expected.push(`Includes = ${args.includes},`);
        if (args?.excludes) expected.push(`Excludes = ${args.excludes},`);
        if (args?.exact) expected.push(`Exact = ${args.exact},`);
        if (args?.unsorted) expected.push(`Unsorted = ${args.unsorted},`);
        // !!! isIncomplete
        const commitCharacters = isNewIdentifierLocation ? "[]" : "DefaultCommitCharacters";
        expectedList = `new CompletionsExpectedList
{
    IsIncomplete = false,
    ItemDefaults = new CompletionsExpectedItemDefaults
    {
        CommitCharacters = ${commitCharacters},
        EditRange = Ignored,
    },
    Items = new CompletionsExpectedItems
    {
        ${expected.join("\n")}
    },
    ${args?.preferences && !args.preferences.startsWith("null") ? `UserPreferences = ${args.preferences},` : ""}
}`;
    }

    const call = `f.VerifyCompletions(${marker}, ${expectedList})`;
    if (andApplyCodeActionArgs) {
        return `${call}.AndApplyCodeAction(new CompletionsExpectedCodeAction
{
    Name = ${csStringLiteral(andApplyCodeActionArgs.name)},
    Source = ${csStringLiteral(andApplyCodeActionArgs.source)},
    Description = ${csStringLiteral(andApplyCodeActionArgs.description)},
    NewFileContent = ${csMultiLineStringLiteral(andApplyCodeActionArgs.newFileContent)},
})`;
    }
    return call;
}

function generateVerifyApplyCodeActionFromCompletion({ marker, options }: VerifyApplyCodeActionFromCompletionCmd): string {
    return `f.VerifyApplyCodeActionFromCompletion(${marker}, ${options})`;
}

function generateBaselineFindAllReferences({ markers, ranges }: VerifyBaselineFindAllReferencesCmd): string {
    if (ranges || markers.length === 0) {
        return `f.VerifyBaselineFindAllReferences()`;
    }
    return `f.VerifyBaselineFindAllReferences(${markers.join(", ")})`;
}

function generateBaselineDocumentHighlights({ args, preferences, filesToSearch }: VerifyBaselineDocumentHighlightsCmd): string {
    if (filesToSearch) {
        const filesGo = `new[]{${filesToSearch.join(", ")}}`;
        return `f.VerifyBaselineDocumentHighlightsWithOptions(${preferences}, ${filesGo}, ${args.join(", ")})`;
    }
    return `f.VerifyBaselineDocumentHighlights(${preferences}, ${args.join(", ")})`;
}

function generateBaselineGoToDefinition({ markers, ranges, kind, boundSpan }: VerifyBaselineGoToDefinitionCmd): string {
    const originalSelectionRange = boundSpan ? "true" : "false";
    switch (kind) {
        case "verifyBaselineGoToDefinition":
            if (ranges || markers.length === 0) {
                return `f.VerifyBaselineGoToDefinition(${originalSelectionRange})`;
            }
            return `f.VerifyBaselineGoToDefinition(${originalSelectionRange}, ${markers.join(", ")})`;
        case "verifyBaselineGoToType":
            if (ranges || markers.length === 0) {
                return `f.VerifyBaselineGoToTypeDefinition()`;
            }
            return `f.VerifyBaselineGoToTypeDefinition(${markers.join(", ")})`;
        case "verifyBaselineGoToImplementation":
            if (ranges || markers.length === 0) {
                return `f.VerifyBaselineGoToImplementation()`;
            }
            return `f.VerifyBaselineGoToImplementation(${markers.join(", ")})`;
        case "verifyBaselineGoToSourceDefinition":
            if (ranges || markers.length === 0) {
                return `f.VerifyBaselineGoToSourceDefinition()`;
            }
            return `f.VerifyBaselineGoToSourceDefinition(${markers.join(", ")})`;
    }
}

function generateGoToCommand({ funcName, args }: GoToCmd): string {
    const funcNameCapitalized = funcName.charAt(0).toUpperCase() + funcName.slice(1);
    return `f.GoTo${funcNameCapitalized}(${args.join(", ")})`;
}

function generateQuickInfoCommand({ kind, marker, text, docs }: VerifyQuickInfoCmd): string {
    switch (kind) {
        case "quickInfoIs":
            return `f.VerifyQuickInfoIs(${text!}, ${docs ? docs : `""`})`;
        case "quickInfoAt":
            return `f.VerifyQuickInfoAt(${marker!}, ${text ? text : `""`}, ${docs ? docs : `""`})`;
        case "quickInfoExists":
            return `f.VerifyQuickInfoExists()`;
        case "notQuickInfoExists":
            return `f.VerifyNotQuickInfoExists()`;
    }
}

function generateOrganizeImports({ expectedContent, mode, preferences }: VerifyOrganizeImportsCmd): string {
    return `f.VerifyOrganizeImports(
        ${csMultiLineStringLiteral(expectedContent)},
        ${mode},
        ${preferences},
    )`;
}

function generateBaselineRename({ kind, args, preferences }: VerifyBaselineRenameCmd): string {
    switch (kind) {
        case "verifyBaselineRename":
            return `f.VerifyBaselineRename(${preferences}, ${args.join(", ")})`;
        case "verifyBaselineRenameAtRangesWithText":
            return `f.VerifyBaselineRenameAtRangesWithText(${preferences}, ${args.join(", ")})`;
    }
}

function generateBaselineInlayHints({ span, preferences }: VerifyBaselineInlayHintsCmd): string {
    return `f.VerifyBaselineInlayHints(${span}, ${preferences})`;
}

function generateImportFixAtPosition({ expectedTexts, preferences }: VerifyImportFixAtPositionCmd): string {
    // Handle empty array case
    if (expectedTexts.length === 1 && expectedTexts[0] === "") {
        return `f.VerifyImportFixAtPosition([], ${preferences})`;
    }
    return `f.VerifyImportFixAtPosition([\n${expectedTexts.join(",\n")},\n], ${preferences})`;
}

function generateImportFixModuleSpecifiers({ markerName, moduleSpecifiers, preferences }: VerifyImportFixModuleSpecifiersCmd): string {
    const specifiersArray = moduleSpecifiers.length === 0
        ? "[]"
        : `new[]{${moduleSpecifiers.join(", ")}}`;
    return `f.VerifyImportFixModuleSpecifiers(${markerName}, ${specifiersArray}, ${preferences})`;
}

function generateSignatureHelpExpected(opts: VerifySignatureHelpOptions): string {
    const fields: string[] = [];

    if (opts.text !== undefined) {
        fields.push(`Text = ${csStringLiteral(opts.text)}`);
    }
    if (opts.docComment !== undefined) {
        fields.push(`DocComment = ${csStringLiteral(opts.docComment)}`);
    }
    if (opts.parameterCount !== undefined) {
        fields.push(`ParameterCount = ${opts.parameterCount}`);
    }
    if (opts.parameterName !== undefined) {
        fields.push(`ParameterName = ${csStringLiteral(opts.parameterName)}`);
    }
    if (opts.parameterSpan !== undefined) {
        fields.push(`ParameterSpan = ${csStringLiteral(opts.parameterSpan)}`);
    }
    if (opts.parameterDocComment !== undefined) {
        fields.push(`ParameterDocComment = ${csStringLiteral(opts.parameterDocComment)}`);
    }
    if (opts.overloadsCount !== undefined) {
        fields.push(`OverloadsCount = ${opts.overloadsCount}`);
    }
    if (opts.overrideSelectedItemIndex !== undefined) {
        fields.push(`OverrideSelectedItemIndex = ${opts.overrideSelectedItemIndex}`);
    }
    if (opts.isVariadic !== undefined) {
        fields.push(`IsVariadic = ${opts.isVariadic}`);
        fields.push(`IsVariadicSet = true`);
    }

    return `new VerifySignatureHelpOptions { ${fields.join(", ")} }`;
}

function generateSignatureHelp({ options }: VerifySignatureHelpCmd): string {
    const lines: string[] = [];

    for (const opts of options) {
        const expected = generateSignatureHelpExpected(opts);

        // Add comments for unsupported options
        const unsupportedComments: string[] = [];

        if (opts.marker !== undefined) {
            const markers = Array.isArray(opts.marker) ? opts.marker : [opts.marker];
            for (const marker of markers) {
                lines.push(`f.GoToMarker(${csStringLiteral(marker)})`);
                for (const comment of unsupportedComments) {
                    lines.push(comment);
                }
                lines.push(`f.VerifySignatureHelp(${expected})`);
            }
        }
        else {
            // No marker specified, use current position
            for (const comment of unsupportedComments) {
                lines.push(comment);
            }
            lines.push(`f.VerifySignatureHelp(${expected})`);
        }
    }

    return lines.join("\n");
}

function generateNoSignatureHelp({ markers }: VerifyNoSignatureHelpCmd): string {
    if (markers.length === 1 && markers[0] === "...test.markerNames()") {
        // All markers
        return `f.VerifyNoSignatureHelpForMarkers([.. f.MarkerNames()])`;
    }
    if (markers.length === 0) {
        // Current position
        return `f.VerifyNoSignatureHelp()`;
    }
    // Specific markers
    const markerArgs = markers.map(m => csStringLiteral(m)).join(", ");
    return `f.VerifyNoSignatureHelpForMarkers(${markerArgs})`;
}

function generateTriggerContext(triggerReason: SignatureHelpTriggerReason | undefined): string {
    if (!triggerReason) {
        return "null";
    }
    switch (triggerReason.kind) {
        case "invoked":
            return `new LsProto.SignatureHelpContext { TriggerKind = LsProto.SignatureHelpTriggerKind.Invoked }`;
        case "characterTyped":
            return `new LsProto.SignatureHelpContext { TriggerKind = LsProto.SignatureHelpTriggerKind.TriggerCharacter, TriggerCharacter = ${csStringLiteral(triggerReason.triggerCharacter ?? "")}, IsRetrigger = false }`;
        case "retrigger":
            return `new LsProto.SignatureHelpContext { TriggerKind = LsProto.SignatureHelpTriggerKind.TriggerCharacter, TriggerCharacter = ${csStringLiteral(triggerReason.triggerCharacter ?? "")}, IsRetrigger = true }`;
        default:
            throw new Error(`Unknown trigger reason kind: ${triggerReason}`);
    }
}

function generateSignatureHelpPresent({ triggerReason, markers }: VerifySignatureHelpPresentCmd): string {
    const context = generateTriggerContext(triggerReason);
    if (markers.length === 0) {
        // Current position
        return `f.VerifySignatureHelpPresent(${context})`;
    }
    // Specific markers
    const markerArgs = markers.map(m => csStringLiteral(m)).join(", ");
    return `f.VerifySignatureHelpPresentForMarkers(${context}, ${markerArgs})`;
}

function generateNoSignatureHelpForTriggerReason({ triggerReason, markers }: VerifyNoSignatureHelpForTriggerReasonCmd): string {
    const context = generateTriggerContext(triggerReason);
    if (markers.length === 0) {
        // Current position
        return `f.VerifyNoSignatureHelpWithContext(${context})`;
    }
    // Specific markers
    const markerArgs = markers.map(m => csStringLiteral(m)).join(", ");
    return `f.VerifyNoSignatureHelpForMarkersWithContext(${context}, ${markerArgs})`;
}

function generateDocCommentTemplate({ marker, expectedOffset, expectedText, generateReturnInDocTemplate }: VerifyDocCommentTemplateCmd): string {
    return `f.VerifyJSDocCompletion(${marker}, ${expectedOffset}, ${expectedText}, ${generateReturnInDocTemplate ?? "null"})`;
}

function generateNoDocCommentTemplate({ marker }: VerifyNoDocCommentTemplateCmd): string {
    return `f.VerifyNoJSDocCompletion(${marker})`;
}

function generateNavigateTo({ args }: VerifyNavToCmd): string {
    return `f.VerifyWorkspaceSymbol(new[]{\n${args.join(",\n")}})`;
}

function generateSemanticClassifications({ format, tokens }: VerifySemanticClassificationsCmd): string {
    const tokensStr = tokens.map(t => `new SemanticToken { Type = ${csStringLiteral(t.type)}, Text = ${csStringLiteral(t.text)} }`).join(",\n        ");
    const maybeComma = tokens.length > 0 ? "," : "";
    return `f.VerifySemanticTokens(new[]{
        ${tokensStr}${maybeComma}
    })`;
}

function generateCSharpCmd(cmd: Cmd): string {
    switch (cmd.kind) {
        case "verifyCompletions":
            return generateVerifyCompletions(cmd);
        case "verifyApplyCodeActionFromCompletion":
            return generateVerifyApplyCodeActionFromCompletion(cmd);
        case "verifyBaselineFindAllReferences":
            return generateBaselineFindAllReferences(cmd);
        case "verifyBaselineDocumentHighlights":
            return generateBaselineDocumentHighlights(cmd);
        case "verifyBaselineGoToDefinition":
        case "verifyBaselineGoToType":
        case "verifyBaselineGoToImplementation":
        case "verifyBaselineGoToSourceDefinition":
            return generateBaselineGoToDefinition(cmd);
        case "verifyBaselineQuickInfo":
            // Quick Info -> Hover
            if (cmd.verbosityLevels && Object.keys(cmd.verbosityLevels).length > 0) {
                const entries = Object.entries(cmd.verbosityLevels).map(
                    ([marker, levels]) => `[${csStringLiteral(marker)}] = new[]{${levels.join(", ")}}`,
                ).join(", ");
                return `f.VerifyBaselineHoverWithVerbosity(new Dictionary<string, int[]>{${entries}})`;
            }
            return `f.VerifyBaselineHover()`;
        case "verifyBaselineSignatureHelp":
            return `f.VerifyBaselineSignatureHelp()`;
        case "verifyBaselineSmartSelection":
            return `f.VerifyBaselineSelectionRanges()`;
        case "verifyBaselineCallHierarchy":
            return `f.VerifyBaselineCallHierarchy()`;
        case "verifyLinkedEditing":
            return `f.VerifyLinkedEditing(${cmd.ranges})`;
        case "goTo":
            return generateGoToCommand(cmd);
        case "edit":
        case "format":
        case "verifyContent":
            return cmd.csStatement;
        case "quickInfoAt":
        case "quickInfoIs":
        case "quickInfoExists":
        case "notQuickInfoExists":
            return generateQuickInfoCommand(cmd);
        case "verifyOrganizeImports":
            return generateOrganizeImports(cmd);
        case "verifyBaselineRename":
        case "verifyBaselineRenameAtRangesWithText":
            return generateBaselineRename(cmd);
        case "renameInfoSucceeded":
            return `f.VerifyRenameSucceeded(${cmd.preferences})`;
        case "renameInfoFailed":
            return `f.VerifyRenameFailed(${cmd.preferences})`;
        case "verifyGetEditsForFileRename":
            return `f.VerifyWillRenameFilesEdits(${cmd.oldPath}, ${cmd.newPath}, ${cmd.newFileContents}, ${cmd.preferences})`;
        case "verifyBaselineInlayHints":
            return generateBaselineInlayHints(cmd);
        case "verifyBaselineLinkedEditing":
            return `f.VerifyBaselineLinkedEditing()`;
        case "verifyImportFixAtPosition":
            return generateImportFixAtPosition(cmd);
        case "verifyImportFixModuleSpecifiers":
            return generateImportFixModuleSpecifiers(cmd);
        case "verifyDiagnostics":
            const funcName = cmd.isSuggestion ? "VerifySuggestionDiagnostics" : "VerifyNonSuggestionDiagnostics";
            return `f.${funcName}(${cmd.arg})`;
        case "verifyBaselineDiagnostics":
            return `f.VerifyBaselineNonSuggestionDiagnostics()`;
        case "verifyNavigateTo":
            return generateNavigateTo(cmd);
        case "verifySignatureHelp":
            return generateSignatureHelp(cmd);
        case "verifyNoSignatureHelp":
            return generateNoSignatureHelp(cmd);
        case "verifySignatureHelpPresent":
            return generateSignatureHelpPresent(cmd);
        case "verifyNoSignatureHelpForTriggerReason":
            return generateNoSignatureHelpForTriggerReason(cmd);
        case "verifyOutliningSpans":
            return generateVerifyOutliningSpans(cmd);
        case "verifyNavigationTree":
            return `f.VerifyBaselineDocumentSymbol()`;
        case "verifyNumberOfErrorsInCurrentFile":
            return `f.VerifyNumberOfErrorsInCurrentFile(${cmd.expectedCount})`;
        case "verifyNoErrors":
            return `f.VerifyNoErrors()`;
        case "verifyErrorExistsAtRange":
            return `f.VerifyErrorExistsAtRange(${cmd.range}, ${cmd.code}, ${csStringLiteral(cmd.message)})`;
        case "verifyCurrentLineContentIs":
            return `f.VerifyCurrentLineContent(${csStringLiteral(cmd.text)})`;
        case "verifyCurrentFileContentIs":
            return `f.VerifyCurrentFileContent(${csStringLiteral(cmd.text)})`;
        case "verifyErrorExistsBetweenMarkers":
            return `f.VerifyErrorExistsBetweenMarkers(${csStringLiteral(cmd.startMarker)}, ${csStringLiteral(cmd.endMarker)})`;
        case "verifyErrorExistsAfterMarker":
            return `f.VerifyErrorExistsAfterMarker(${csStringLiteral(cmd.markerName)})`;
        case "verifyErrorExistsBeforeMarker":
            return `f.VerifyErrorExistsBeforeMarker(${csStringLiteral(cmd.markerName)})`;
        case "verifyCodeFix":
            return `f.VerifyCodeFix(new VerifyCodeFixOptions
{
    Description = ${csStringLiteral(cmd.description)},
    ${
                cmd.newRangeContent !== undefined
                    ? `NewRangeContent = ${csMultiLineStringLiteral(cmd.newRangeContent)},`
                    : `NewFileContent = ${csMultiLineStringLiteral(cmd.newFileContent ?? "")},`
            }
    Index = ${cmd.index},${
                cmd.applyChanges ? `
    ApplyChanges = true,` : ``
            }${
                cmd.preferences !== "null /*preferences*/" ? `
    UserPreferences = ${cmd.preferences},` : ``
            }
})`;
        case "verifyCodeFixAvailable":
            if (cmd.unavailableDescriptions.length > 0) {
                return `f.VerifyCodeFixNotAvailable(${cmd.unavailableDescriptions.map(d => csStringLiteral(d)).join(", ")})`;
            }
            if (cmd.expectNone) {
                return `f.VerifyCodeFixNotAvailable()`;
            }
            if (cmd.descriptions.length === 0) {
                return `f.VerifyCodeFixAvailable(null)`;
            }
            return `f.VerifyCodeFixAvailable(new[]{${cmd.descriptions.map(d => csStringLiteral(d)).join(", ")}})`;
        case "verifyRangeAfterCodeFix":
            return `f.VerifyRangeAfterCodeFix(${csMultiLineStringLiteral(cmd.expectedText)}, ${cmd.includeWhiteSpace}, ${cmd.errorCode}, ${cmd.index})`;
        case "verifyCodeFixAll":
            return `f.VerifyCodeFixAll(new VerifyCodeFixAllOptions
{
    FixID = ${csStringLiteral(cmd.fixId)},
    NewFileContent = ${csMultiLineStringLiteral(cmd.newFileContent)},
})`;
        case "verifySemanticClassifications":
            return generateSemanticClassifications(cmd);
        case "verifyDocCommentTemplate":
            return generateDocCommentTemplate(cmd);
        case "verifyNoDocCommentTemplate":
            return generateNoDocCommentTemplate(cmd);
        case "forOf":
            return `foreach (var ${cmd.variableName} in ${cmd.rangeExpression})
{
${indentBlock(cmd.bodyCommands.map(c => generateCSharpCmd(c)).join("\n\n"))}
}`;
        default:
            let neverCommand: never = cmd;
            throw new Error(`Unknown command kind: ${neverCommand as Cmd["kind"]}`);
    }
}

// Indents an emitted statement block to method-body level (8 spaces).
function indentBlock(block: string): string {
    return block.split("\n").map(line => line.length > 0 ? "    " + line : line).join("\n");
}

interface GoTest {
    name: string;
    content: string;
    commands: Cmd[];
}

// Mirrors the Go converter's testName computation (generateGoTest).
function testName(name: string): string {
    return (name[0].toUpperCase() + name.substring(1)).replaceAll("-", "_").replaceAll(/[^a-zA-Z0-9_]/g, "");
}

const fileHeader = (testName: string, goName: string): string => `// <auto-generated>convert-fourslash-csharp; DO NOT EDIT.
// Fork of tsgo internal/fourslash/tests/gen/${goName}_test.go (convertFourslash); C# emission
// per plan/08-testing-strategy.md §7.2. To modify this test, edit
// _submodules/TypeScript/tests/cases/fourslash/${goName}.ts (upstream) and re-run
// "npm run convert:fourslash" in tools/generator.</auto-generated>

using System;
using System.Collections.Generic;
using Ts.Core;
using Ts.Ls;
using Ts.ModuleSpecifiers;
using Ts.TestUtil.Fourslash;
using Xunit;
using Core = Ts.Fourslash.Tests.FourslashTestUtil;
using Ls = Ts.Fourslash.Tests.FourslashTestUtil;
using LsProto = Ts.LsProto;
using LsUtil = Ts.Ls;
using static Ts.Fourslash.Tests.FourslashTestUtil;

namespace Ts.Fourslash.Tests.Gen;

public class ${testName}Tests
{
    [Fact]
    public void Test${testName}()
    {
`;

const fileFooter = `    }
}
`;

// The test content is a C# raw string literal with the content at column 0 (exactly like the
// Go backtick emission); the fence width escalates past any run of quotes in the content.
function contentLiteral(content: string): string {
    let fenceCount = 3;
    for (;;) {
        const fence = "\"".repeat(fenceCount);
        if (!content.includes(fence)) {
            break;
        }
        fenceCount++;
    }
    const fence = "\"".repeat(fenceCount);
    // Content lines sit at column 0; the raw literal's value is exactly `content` (the newline
    // before the closing delimiter terminates the last line, it is not part of the value).
    return `${fence}\n${content}\n${fence}`;
}

function generateCSharpTest(test: GoTest, isServer: boolean, isCrashing: boolean): string {
    const name = testName(test.name);
    const hasDocCommentTemplateCommands = test.commands.some(hasDocCommentTemplateCommand);
    const content = hasDocCommentTemplateCommands && /@Filename: .*\.jsx?/i.test(test.content)
        ? prependLineToRawStringLiteral(test.content, "// @allowJs: true")
        : test.content;
    const commands = test.commands.map(cmd => generateCSharpCmd(cmd)).join("\n\n");
    const newStatement = hasDocCommentTemplateCommands
        ? `        var capabilities = FourslashTest.GetDefaultCapabilities();
        capabilities.TextDocument!.Completion!.CompletionItem!.SnippetSupport = false;
        using var f = FourslashTest.New(content, fileName: ${JSON.stringify(test.name)}, capabilities: capabilities, cancellationToken: TestContext.Current.CancellationToken);`
        : `        using var f = FourslashTest.New(content, fileName: ${JSON.stringify(test.name)}, cancellationToken: TestContext.Current.CancellationToken);`;
    const body = `        SkipIfFailing.Check();
        const string content = ${contentLiteral(content)};
${newStatement}
${isServer ? `        f.MarkTestAsStradaServer();\n` : ""}${indentBlock(commands)}`;

    const testClass = fileHeader(name, test.name) + body + "\n" + fileFooter;
    if (isCrashing) {
        // plan/08 §7.4: crashing tests are not even compiled (they kill the process).
        return `#if false // crashing test (tests/Ts.Fourslash.Tests/Scripts/crashingTests.txt); re-run convert:fourslash to regenerate\n\n` + testClass + `#endif\n`;
    }
    return testClass;
}

function prependLineToRawStringLiteral(literal: string, line: string): string {
    // The content arrives as the bare text (no literal framing yet).
    return line + "\n" + literal;
}

function hasDocCommentTemplateCommand(cmd: Cmd): boolean {
    if (cmd.kind === "verifyDocCommentTemplate" || cmd.kind === "verifyNoDocCommentTemplate") {
        return true;
    }
    if (cmd.kind === "forOf") {
        return cmd.bodyCommands.some(hasDocCommentTemplateCommand);
    }
    return false;
}

function getNodeOfKind<T extends ts.Node>(node: ts.Node, hasKind: (n: ts.Node) => n is T): T | undefined {
    if (hasKind(node)) {
        return node;
    }
    if (ts.isIdentifier(node)) {
        const init = getInitializer(node);
        if (init && hasKind(init)) {
            return init;
        }
    }
    return undefined;
}

function getObjectLiteralExpression(node: ts.Node): ts.ObjectLiteralExpression | undefined {
    return getNodeOfKind(node, ts.isObjectLiteralExpression);
}

function getStringLiteralLike(node: ts.Node): ts.StringLiteralLike | undefined {
    return getNodeOfKind(node, ts.isStringLiteralLike);
}

// Build a map from diagnostic property names (e.g. "Extract_base_class_to_variable")
// to their message text, by loading diagnosticMessages.json and applying the same
// key-generation algorithm used by TypeScript's processDiagnosticMessages script.
const diagnosticMessagesByPropName: Map<string, string> = (() => {
    const messagesPath = path.join(submoduleRoot(), "src", "compiler", "diagnosticMessages.json");
    if (!fs.existsSync(messagesPath)) {
        return new Map();
    }
    const raw = JSON.parse(fs.readFileSync(messagesPath, "utf-8"));
    const map = new Map<string, string>();
    for (const messageText of Object.keys(raw)) {
        const propName = messageText.split("").map((ch: string) => {
            if (ch === "*") return "_Asterisk";
            if (ch === "/") return "_Slash";
            if (ch === ":") return "_Colon";
            return /\w/.test(ch) ? ch : "_";
        }).join("")
            .replace(/_+/g, "_")
            .replace(/^_(\D)/, "$1")
            .replace(/_$/, "");
        map.set(propName, messageText);
    }
    return map;
})();

// Resolve a description value from various expression forms:
// - String literal: "Add return type 'void'"
// - ts.Diagnostics.X.message property access
// - Variable identifier referencing a const string in the same file
function resolveDescriptionExpression(expr: ts.Expression, sourceFile: ts.SourceFile): string | undefined {
    // String literal
    const str = getStringLiteralLike(expr);
    if (str) return str.text;

    // [ts.Diagnostics.Foo.message, "arg0", "arg1", ...]
    if (ts.isArrayLiteralExpression(expr) && expr.elements.length > 0) {
        const [diagnostic] = expr.elements;
        const template = resolveDescriptionExpression(diagnostic, sourceFile);
        if (template) {
            let message = template;
            for (let i = 1; i < expr.elements.length; i++) {
                const arg = resolveDescriptionExpression(expr.elements[i], sourceFile);
                if (arg === undefined) {
                    return undefined;
                }
                message = message.replaceAll(`{${i - 1}}`, arg);
            }
            return message;
        }
        return undefined;
    }

    // ts.Diagnostics.Foo_bar.message
    if (ts.isPropertyAccessExpression(expr) && expr.name.text === "message") {
        const inner = expr.expression;
        if (
            ts.isPropertyAccessExpression(inner) && ts.isPropertyAccessExpression(inner.expression)
            && ts.isIdentifier(inner.expression.name) && inner.expression.name.text === "Diagnostics"
            && ts.isIdentifier(inner.name)
        ) {
            const diagKey = inner.name.text;
            const message = diagnosticMessagesByPropName.get(diagKey);
            if (message) return message;
        }
    }

    // Variable reference: look for a const string declaration in the same file
    if (ts.isIdentifier(expr)) {
        const varName = expr.text;
        for (const stmt of sourceFile.statements) {
            if (ts.isVariableStatement(stmt)) {
                for (const decl of stmt.declarationList.declarations) {
                    if (ts.isIdentifier(decl.name) && decl.name.text === varName && decl.initializer) {
                        const initStr = getStringLiteralLike(decl.initializer);
                        if (initStr) return initStr.text;
                    }
                }
            }
        }
    }

    return undefined;
}

// Get the name of a property in an object literal, whether it's an identifier or string literal.
function getPropertyName(prop: ts.ObjectLiteralElementLike): string | undefined {
    if (ts.isPropertyAssignment(prop) || ts.isShorthandPropertyAssignment(prop)) {
        if (ts.isIdentifier(prop.name)) return prop.name.text;
        if (ts.isStringLiteral(prop.name)) return prop.name.text;
    }
    return undefined;
}

function getNumericLiteral(node: ts.Node): ts.NumericLiteral | undefined {
    return getNodeOfKind(node, ts.isNumericLiteral);
}

function getArrayLiteralExpression(node: ts.Node): ts.ArrayLiteralExpression | undefined {
    return getNodeOfKind(node, ts.isArrayLiteralExpression);
}

// Parses expressions like 'string'.length or "string".length and returns the length value
function getStringLengthExpression(node: ts.Node): number | undefined {
    if (ts.isPropertyAccessExpression(node) && node.name.text === "length") {
        const stringLiteral = getStringLiteralLike(node.expression);
        if (stringLiteral) {
            return stringLiteral.text.length;
        }
    }
    return undefined;
}

function getInitializer(name: ts.Identifier): ts.Expression | undefined {
    const file = name.getSourceFile();
    const varStmts = file.statements.filter(ts.isVariableStatement);
    for (const varStmt of varStmts) {
        const decls = varStmt.declarationList.declarations.filter(varDecl => {
            if (ts.isIdentifier(varDecl.name)) {
                return varDecl.name.text === name.text;
            }
            return false;
        });
        if (decls[0]) {
            return decls[0].initializer;
        }
    }
    return undefined;
}

// ---------------------------------------------------------------------------
// FourslashTestUtil.g.cs — port of internal/fourslash/tests/util/util.go (the package the
// Go corpus dot-imports), plus the Core/Ls constant shims the emitted fragments reference.
// The big data lists are translated from util.go so regeneration stays 1:1 with upstream.
// ---------------------------------------------------------------------------

function emitFourslashTestUtil(): void {
    const goRoot = goUtilGoPath();
    const utilGoPath = path.join(goRoot, "internal", "fourslash", "tests", "util", "util.go");
    const utilSource = fs.readFileSync(utilGoPath, "utf-8");

    const data = translateUtilGo(utilSource);

    const out = `// <auto-generated>convert-fourslash-csharp (emitFourslashTestUtil); DO NOT EDIT.
// Port of tsgo internal/fourslash/tests/util/util.go (the helper package the Go corpus
// dot-imports), regenerated by: npm run convert:fourslash
// plus the Core/Ls constant shims the emitted test fragments reference (Core.TSTrue,
// Ls.SortText*, Ls.DeprecateSortText) — Go typed constants that have no public C# surface yet
// (the Ts.Ls values are internal; see Fourslash.Stubs.cs for the runtime stubs).

// ReSharper disable InconsistentNaming
namespace Ts.Fourslash.Tests;

public static class FourslashTestUtil
{
    // ---- Core shim (Go: core.TSTrue/TSFalse/TSUnknown as used in the Go corpus emission). ----
    public const global::Ts.Core.Tristate TSTrue = global::Ts.Core.Tristate.True;
    public const global::Ts.Core.Tristate TSFalse = global::Ts.Core.Tristate.False;
    public const global::Ts.Core.Tristate TSUnknown = global::Ts.Core.Tristate.Unknown;

    // ---- Ls shim (Go: ls.SortText* constants, completions.go:202-213). ----
    public const string SortTextLocalDeclarationPriority = "10";
    public const string SortTextLocationPriority = "11";
    public const string SortTextOptionalMember = "12";
    public const string SortTextMemberDeclaredBySpreadAssignment = "13";
    public const string SortTextSuggestedClassMembers = "14";
    public const string SortTextGlobalsOrKeywords = "15";
    public const string SortTextAutoImportSuggestions = "16";
    public const string SortTextClassMemberSnippets = "17";
    public const string SortTextJavascriptIdentifiers = "18";

    // completions.go:213-215 — DeprecateSortText.
    public static string DeprecateSortText(string original) => "z" + original;

    // util.go:14-16.
    public static readonly object Ignored = new();

    public static readonly string[] DefaultCommitCharacters = [".", ",", ";"];

${data.singleItems}${data.lists}

    // util.go:1354-1398 — sortCompletionItems.
    private static object[] SortCompletionItems(object[] items)
    {
        var clone = (object[])items.Clone();
        Array.Sort(clone, (a, b) =>
        {
            const string defaultSortText = SortTextLocationPriority;
            string? aSortText = null;
            string? bSortText = null;
            if (a is LsProto.CompletionItem aItem && aItem.SortText != null)
            {
                aSortText = aItem.SortText;
            }
            if (b is LsProto.CompletionItem bItem && bItem.SortText != null)
            {
                bSortText = bItem.SortText;
            }
            var bySortText = CompareStringsCaseInsensitiveThenSensitive(aSortText ?? defaultSortText, bSortText ?? defaultSortText);
            if (bySortText != 0)
            {
                return bySortText;
            }
            return CompareStringsCaseInsensitiveThenSensitive(GetLabel(a), GetLabel(b));
        });
        return clone;
    }

    private static string GetLabel(object item) => item switch
    {
        LsProto.CompletionItem completionItem => completionItem.Label,
        string label => label,
        _ => throw new InvalidOperationException("unexpected completion item type: " + item.GetType().Name),
    };

    private static int CompareStringsCaseInsensitiveThenSensitive(string a, string b) =>
        global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(a, b);

    private static object[] Concat(params object[][] lists) => lists.SelectMany(static l => l).ToArray();

    // util.go:1345-1352 — CompletionGlobals.
    public static readonly object[] CompletionGlobals = SortCompletionItems(Concat(
        CompletionGlobalVars,
        CompletionGlobalKeywords,
        [CompletionGlobalThisItem, CompletionUndefinedVarItem]));

    // util.go:1400-1412 — CompletionGlobalsPlus.
    public static object[] CompletionGlobalsPlus(object[] items, bool noLib)
    {
        object[] all = noLib
            ? Concat(items, [CompletionGlobalThisItem, CompletionUndefinedVarItem], CompletionGlobalKeywords)
            : Concat(items, CompletionGlobals);
        return SortCompletionItems(all);
    }

    // util.go:1414-1421 — CompletionGlobalTypesPlus.
    public static object[] CompletionGlobalTypesPlus(object[] items) => SortCompletionItems(Concat(
        CompletionGlobalTypeDecls,
        [CompletionGlobalThisItem],
        CompletionTypeKeywords,
        items));

    public static readonly object[] CompletionGlobalTypes = CompletionGlobalTypesPlus([]);

    // util.go:1425-1448 — getInJSKeywords.
    private static object[] GetInJSKeywords(object[] keywords) => keywords.Where(static item =>
    {
        switch (GetLabel(item))
        {
            case "enum":
            case "interface":
            case "implements":
            case "private":
            case "protected":
            case "public":
            case "abstract":
            case "any":
            case "boolean":
            case "declare":
            case "infer":
            case "is":
            case "keyof":
            case "module":
            case "namespace":
            case "never":
            case "readonly":
            case "number":
            case "object":
            case "string":
            case "symbol":
            case "type":
            case "unique":
            case "override":
            case "unknown":
            case "global":
            case "bigint":
                return false;
            default:
                return true;
        }
    }).ToArray();

    public static readonly object[] CompletionGlobalInJSKeywords = GetInJSKeywords(CompletionGlobalKeywords);

    // util.go:1452-1462 — CompletionGlobalsInJSPlus.
    public static object[] CompletionGlobalsInJSPlus(object[] items, bool noLib)
    {
        var all = Concat(
            items,
            [CompletionGlobalThisItem, CompletionUndefinedVarItem],
            CompletionGlobalInJSKeywords).ToList();
        if (!noLib)
        {
            all.AddRange(CompletionGlobalVars);
        }
        return SortCompletionItems(all.ToArray());
    }

    // util.go:1523-1530 — CompletionFunctionMembersPlus.
    public static object[] CompletionFunctionMembersPlus(object[] items) => SortCompletionItems(Concat(
        CompletionFunctionMembers,
        items));

    // util.go:1542-1549 — CompletionFunctionMembersWithPrototypePlus.
    public static object[] CompletionFunctionMembersWithPrototypePlus(object[] items) => SortCompletionItems(Concat(
        CompletionFunctionMembersWithPrototype,
        items));

    // util.go:1551-1558 — CompletionTypeKeywordsPlus.
    public static object[] CompletionTypeKeywordsPlus(object[] items) => SortCompletionItems(Concat(
        CompletionTypeKeywords,
        items));

    // util.go:1532-1540 — CompletionFunctionMembersWithPrototype.
    public static readonly object[] CompletionFunctionMembersWithPrototype = SortCompletionItems(Concat(
        CompletionFunctionMembers,
        [
            new LsProto.CompletionItem { Label = "constructor", Kind = LsProto.CompletionItemKind.Keyword, SortText = SortTextGlobalsOrKeywords },
        ]));

    // util.go:1560-1566 — CompletionTypeAssertionKeywords.
    public static readonly object[] CompletionTypeAssertionKeywords = CompletionGlobalTypesPlus(
    [
        new LsProto.CompletionItem { Label = "keyof", Kind = LsProto.CompletionItemKind.Keyword, SortText = SortTextGlobalsOrKeywords },
        new LsProto.CompletionItem { Label = "readonly", Kind = LsProto.CompletionItemKind.Keyword, SortText = SortTextGlobalsOrKeywords },
    ]);
}
`;

    fs.writeFileSync(utilOutputPath, out, "utf-8");
}

// Translates the item data out of Go's util.go. Handles exactly the shapes util.go uses:
//   &lsproto.CompletionItem{ Label: "...", Kind: new(lsproto.CompletionItemKindX),
//                             SortText: new(string(ls.Y)), Tags: &[]lsproto.CompletionItemTag{...} }
//   var X = []fourslash.CompletionsExpectedItem{ <items>, }
// Anything else fails loudly (so a util.go change surfaces as a converter error).
function translateUtilGo(source: string): { singleItems: string; lists: string; } {
    const itemRe = /&lsproto\.CompletionItem\{([^}]*)\}/g;
    function translateItem(itemBody: string): string {
        const props: string[] = [];
        const propRe = /([A-Za-z]+):\s*(.+?),\s*(?=\/\/|$|[A-Za-z]+:|$)/gs;
        // Simpler: split on the known property names in order.
        const fieldRe = /(Label|Kind|SortText|Tags|Data|Detail|InsertText|FilterText):\s*([\s\S]*?)(?=\n\s*(?:Label|Kind|SortText|Tags|Data|Detail|InsertText|FilterText):|$)/g;
        let m: RegExpExecArray | null;
        while ((m = fieldRe.exec(itemBody)) !== null) {
            const field = m[1];
            // gofmt-aligned fields carry a trailing comma (and the capture may include it).
            const rawValue = m[2].trim().replace(/,\s*$/, "").trim();
            switch (field) {
                case "Label":
                    props.push(`Label = ${JSON.stringify(JSON.parse(rawValue))}`);
                    break;
                case "Kind": {
                    const kindMatch = rawValue.match(/^new\(lsproto\.(CompletionItemKind[A-Za-z]+)\)$/);
                    if (!kindMatch) throw new Error(`util.go: unrecognized Kind: ${rawValue}`);
                    props.push(`Kind = LsProto.CompletionItemKind.${goEnumSuffix(kindMatch[1])}`);
                    break;
                }
                case "SortText": {
                    const sortMatch = rawValue.match(/^new\(string\(ls\.([A-Za-z]+)\)\)$/);
                    if (sortMatch) {
                        props.push(`SortText = ${sortMatch[1]}`);
                        break;
                    }
                    const depMatch = rawValue.match(/^new\(string\(ls\.DeprecateSortText\(ls\.([A-Za-z]+)\)\)\)$/);
                    if (depMatch) {
                        props.push(`SortText = DeprecateSortText(${depMatch[1]})`);
                        break;
                    }
                    throw new Error(`util.go: unrecognized SortText: ${rawValue}`);
                }
                case "Tags": {
                    const tagMatch = rawValue.match(/^&\[\]lsproto\.CompletionItemTag\{(lsproto\.[A-Za-z]+)\}$/);
                    if (!tagMatch) throw new Error(`util.go: unrecognized Tags: ${rawValue}`);
                    props.push(`Tags = [LsProto.${goEnumSuffix(tagMatch[1])}]`);
                    break;
                }
                default:
                    throw new Error(`util.go: unrecognized completion item field: ${field}`);
            }
        }
        return `        new LsProto.CompletionItem { ${props.join(", ")} },`;
    }

    const goEnumSuffix = (goConst: string) => goConst.replace(/^CompletionItemKind|^CompletionItemTag/, "");

    const singleItems: string[] = [];
    const lists: string[] = [];

    // Single items: var X = &lsproto.CompletionItem{...}
    const singleRe = /var ([A-Za-z]+) = &lsproto\.CompletionItem\{([\s\S]*?)\n\}/g;
    let m: RegExpExecArray | null;
    while ((m = singleRe.exec(source)) !== null) {
        singleItems.push(`    public static readonly object ${m[1]} =\n${translateItem(m[2])}\n`);
    }

    // Lists: var X = []fourslash.CompletionsExpectedItem{ ... }
    const listRe = /var ([A-Za-z]+) = \[\]fourslash\.CompletionsExpectedItem\{([\s\S]*?)\n\}/g;
    while ((m = listRe.exec(source)) !== null) {
        const name = m[1];
        const body = m[2];
        const items: string[] = [];
        const itemBodyRe = /&lsproto\.CompletionItem\{([\s\S]*?)\n\t\t?\}/g;
        let im: RegExpExecArray | null;
        let consumed = 0;
        while ((im = itemBodyRe.exec(body)) !== null) {
            items.push(translateItem(im[1]));
            consumed += im[0].length;
        }
        if (consumed < body.replace(/[\s,]/g, "").length) {
            throw new Error(`util.go: list ${name} contains untranslated items (converter must be extended)`);
        }
        lists.push(`    // util.go — ${name}.\n    public static readonly object[] ${name} =\n    [\n${items.join("\n")}\n    ];\n`);
    }

    return { singleItems: singleItems.join("\n"), lists: lists.join("\n") };
}

if (url.fileURLToPath(import.meta.url) == process.argv[1]) {
    main().catch(e => {
        console.error(e);
        process.exit(1);
    });
}

using Ts.TestUtil.Fourslash;
using Xunit;

namespace Ts.Fourslash.Tests.Runner;

public sealed class FourslashRunnerTests
{
    [Fact(Skip = "LS: checker.GetSymbolAtLocation/GetTypeOfSymbolAtLocation not landed (GetQuickInfoAtPosition stub returns null)")]
    public void QuickInfo_ConstLiteralShowsLiteralType()
    {
        const string source = """
            ////const /*quickInfo*/x = 1;
            """;

        FourslashSession session = CreateSession(source, "quickInfo.ts");

        session.VerifyQuickInfoAt("quickInfo", "const x: 1");
    }

    [Fact(Skip = "LS: GetSymbolAtLocation not landed")]
    public void GoToDefinition_VariableUseResolvesToDeclaration()
    {
        const string source = """
            ////const /*declaration*/x = 1;
            ////const y = /*use*/x;
            """;

        FourslashSession session = CreateSession(source, "definition.ts");

        session.VerifyGoToDefinition("use", "declaration");
    }

    [Fact(Skip = "LS: getTypeScriptMemberSymbols not landed")]
    public void Completions_MemberListContainsDeclaredProperties()
    {
        const string source = """
            ////const value = { first: 1, second: "two" };
            ////value./*completion*/
            """;

        FourslashSession session = CreateSession(source, "completions.ts");
        session.GoToMarker("completion");

        session.VerifyCompletionsContain(["first", "second"]);
    }

    [Fact(Skip = "LS: GetSymbolAtLocation not landed")]
    public void GoToDefinition_ResolvesAcrossFilenameFiles()
    {
        const string source = """
            // @Filename: declarations.ts
            ////export const /*declaration*/shared = 1;
            // @Filename: usage.ts
            ////import { shared } from "./declarations";
            ////const result = /*use*/shared;
            """;

        FourslashSession session = CreateSession(source, "multiFile.ts");

        session.VerifyGoToDefinition("use", "declaration");
    }

    [Fact]
    public void Completions_GlobalListContainsLanguageKeywords()
    {
        const string source = """
            /////*completion*/
            """;

        FourslashSession session = CreateSession(source, "globalCompletions.ts");
        session.GoToMarker("completion");

        session.VerifyCompletionsContain(["const", "function"]);
    }

    [Fact]
    public void GoToMarker_SwitchesTheActiveFileUsedByTheNextVerb()
    {
        const string source = """
            // @Filename: first.ts
            ////export const value = { first: 1 }; value./*inFirstFile*/
            // @Filename: second.ts
            ////export const value = { first: 1 }; value;/*inSecondFile*/
            """;

        FourslashSession session = CreateSession(source, "markerNavigation.ts");
        session.GoToMarker("inFirstFile");
        session.GoToMarker("inSecondFile");

        session.VerifyCompletionsContain(["class", "function"]);
    }

    private static FourslashSession CreateSession(string source, string fileName)
    {
        FourslashProject model = FourslashParser.Parse(source, fileName);
        return new FourslashSession(model);
    }
}

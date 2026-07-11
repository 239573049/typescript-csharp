// Ported from tsgo internal/tsoptions/diagnostics.go

using Message = Ts.Diagnostics.Message;
namespace Ts.TsOptions;

// PORT-NOTE: Go structs used via pointer → reference classes. Lowercase (unexported)
// Go fields keep their spelling as internal (same-assembly cross-file access).
public sealed class DidYouMeanOptionsDiagnostics
{
    internal AlternateModeDiagnostics? alternateMode;
    public CommandLineOption[] OptionDeclarations = System.Array.Empty<CommandLineOption>();
    public Message? UnknownOptionDiagnostic;
    public Message? UnknownDidYouMeanDiagnostic;
}

public sealed class AlternateModeDiagnostics
{
    internal Message? diagnostic;
    internal NameMap? optionsNameMap;
}

public sealed class ParseCommandLineWorkerDiagnostics
{
    internal DidYouMeanOptionsDiagnostics didYouMean = new();
    // PORT-NOTE: Go's `optionsNameMap *NameMap` + `optionsNameMapOnce sync.Once` on this
    // struct are dead (never read anywhere in the package); dropped to avoid an unused
    // private field. Only AlternateModeDiagnostics.optionsNameMap is consumed (errors.go).
    public Message? OptionTypeMismatchDiagnostic;

    // this will only return the correct diagnostics for `compiler` mode, and is factored into a function for testing reasons.
    internal static ParseCommandLineWorkerDiagnostics getParseCommandLineWorkerDiagnostics(CommandLineOption[] decls)
    {
        return new ParseCommandLineWorkerDiagnostics
        {
            didYouMean = new DidYouMeanOptionsDiagnostics
            {
                alternateMode = new AlternateModeDiagnostics
                {
                    diagnostic = global::Ts.Diagnostics.Diagnostics.Compiler_option_0_may_only_be_used_with_build,
                    optionsNameMap = NameMaps.BuildNameMap,
                },
                OptionDeclarations = decls,
                UnknownOptionDiagnostic = global::Ts.Diagnostics.Diagnostics.Unknown_compiler_option_0,
                UnknownDidYouMeanDiagnostic = global::Ts.Diagnostics.Diagnostics.Unknown_compiler_option_0_Did_you_mean_1,
            },
            OptionTypeMismatchDiagnostic = global::Ts.Diagnostics.Diagnostics.Compiler_option_0_expects_an_argument,
        };
    }

    public static readonly ParseCommandLineWorkerDiagnostics CompilerOptionsDidYouMeanDiagnostics =
        getParseCommandLineWorkerDiagnostics(DeclsCompiler.OptionsDeclarations);

    internal static readonly ParseCommandLineWorkerDiagnostics watchOptionsDidYouMeanDiagnostics = new()
    {
        didYouMean = new DidYouMeanOptionsDiagnostics
        {
            // no alternateMode
            OptionDeclarations = DeclsWatch.OptionsForWatch,
            UnknownOptionDiagnostic = global::Ts.Diagnostics.Diagnostics.Unknown_watch_option_0,
            UnknownDidYouMeanDiagnostic = global::Ts.Diagnostics.Diagnostics.Unknown_watch_option_0_Did_you_mean_1,
        },
        OptionTypeMismatchDiagnostic = global::Ts.Diagnostics.Diagnostics.Watch_option_0_requires_a_value_of_type_1,
    };

    internal static readonly ParseCommandLineWorkerDiagnostics buildOptionsDidYouMeanDiagnostics = new()
    {
        didYouMean = new DidYouMeanOptionsDiagnostics
        {
            alternateMode = new AlternateModeDiagnostics
            {
                diagnostic = global::Ts.Diagnostics.Diagnostics.Compiler_option_0_may_not_be_used_with_build,
                optionsNameMap = NameMaps.CompilerNameMap,
            },
            OptionDeclarations = DeclsBuild.BuildOpts,
            UnknownOptionDiagnostic = global::Ts.Diagnostics.Diagnostics.Unknown_build_option_0,
            UnknownDidYouMeanDiagnostic = global::Ts.Diagnostics.Diagnostics.Unknown_build_option_0_Did_you_mean_1,
        },
        OptionTypeMismatchDiagnostic = global::Ts.Diagnostics.Diagnostics.Build_option_0_requires_a_value_of_type_1,
    };
}

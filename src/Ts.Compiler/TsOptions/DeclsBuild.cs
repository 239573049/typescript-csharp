// Ported from tsgo internal/tsoptions/declsbuild.go

using System.Linq;
using static Ts.TsOptions.CommandLineOptionKind;

namespace Ts.TsOptions;

public static class DeclsBuild
{
    public static readonly CommandLineOption TscBuildOption = new()
    {
        Name = "build",
        Kind = "boolean",
        ShortName = "b",
        ShowInSimplifiedHelpView = true,
        Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
        Description = global::Ts.Diagnostics.Diagnostics.Build_one_or_more_projects_and_their_dependencies_if_out_of_date,
        DefaultValueDescription = false,
    };

    public static readonly CommandLineOption[] OptionsForBuild = new CommandLineOption[]
    {
        TscBuildOption,
        new CommandLineOption
        {
            Name = "verbose",
            ShortName = "v",
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_verbose_logging,
            Kind = "boolean",
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "dry",
            ShortName = "d",
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Show_what_would_be_built_or_deleted_if_specified_with_clean,
            Kind = "boolean",
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "force",
            ShortName = "f",
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Build_all_projects_including_those_that_appear_to_be_up_to_date,
            Kind = "boolean",
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "clean",
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Delete_the_outputs_of_all_projects,
            Kind = "boolean",
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "builders",
            Kind = CommandLineOptionTypeNumber,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Set_the_number_of_projects_to_build_concurrently,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_4_unless_singleThreaded_is_passed,
            minValue = 1,
        },
        new CommandLineOption
        {
            Name = "stopBuildOnErrors",
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Skip_building_downstream_projects_on_error_in_upstream_project,
            Kind = "boolean",
            DefaultValueDescription = false,
        },
    };

    public static readonly CommandLineOption[] BuildOpts =
        DeclsCompiler.commonOptionsWithBuild.Concat(OptionsForBuild).ToArray();
}

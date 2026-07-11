// Ported from tsgo internal/tsoptions/declscompiler.go

using System;
using System.Linq;
using Ts.Core;
using static Ts.TsOptions.CommandLineOptionKind;

namespace Ts.TsOptions;

public static class DeclsCompiler
{
    // PORT-NOTE: C# static field initializers run in textual order, so the two source
    // arrays are declared before OptionsDeclarations, which concatenates them.
    internal static readonly CommandLineOption[] commonOptionsWithBuild = new CommandLineOption[]
    {
        //******* commonOptionsWithBuild *******
        new CommandLineOption
        {
            Name = "help",
            ShortName = "h",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            IsCommandLineOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Print_this_message,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "help",
            ShortName = "?",
            Kind = CommandLineOptionTypeBoolean,
            IsCommandLineOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "watch",
            ShortName = "w",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            IsCommandLineOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Watch_input_files,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "preserveWatchOutput",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = false,
            Category = global::Ts.Diagnostics.Diagnostics.Output_Formatting,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_wiping_the_console_in_watch_mode,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "listFiles",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Print_all_of_the_files_read_during_the_compilation,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "explainFiles",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Print_files_read_during_the_compilation_including_why_it_was_included,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "listEmittedFiles",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Print_the_names_of_emitted_files_after_a_compilation,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "pretty",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Output_Formatting,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_color_and_formatting_in_TypeScript_s_output_to_make_compiler_errors_easier_to_read,
            DefaultValueDescription = true,
        },
        new CommandLineOption
        {
            Name = "traceResolution",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Log_paths_used_during_the_moduleResolution_process,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "diagnostics",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Output_compiler_performance_information_after_building,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "extendedDiagnostics",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Output_more_detailed_compiler_performance_information_after_building,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "generateCpuProfile",
            Kind = CommandLineOptionTypeString,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Emit_a_v8_CPU_profile_of_the_compiler_run_for_debugging,
            DefaultValueDescription = "profile.cpuprofile",
        },
        new CommandLineOption
        {
            Name = "generateTrace",
            Kind = CommandLineOptionTypeString,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Generates_an_event_trace_and_a_list_of_types,
        },
        new CommandLineOption
        {
            Name = "incremental",
            ShortName = "i",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Projects,
            Description = global::Ts.Diagnostics.Diagnostics.Save_tsbuildinfo_files_to_allow_for_incremental_compilation_of_projects,
            transpileOptionValue = Tristate.Unknown,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_false_unless_composite_is_set,
        },
        new CommandLineOption
        {
            Name = "declaration",
            ShortName = "d",
            Kind = CommandLineOptionTypeBoolean,
            // Not setting affectsEmit because we calculate this flag might not affect full emit
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            transpileOptionValue = Tristate.Unknown,
            Description = global::Ts.Diagnostics.Diagnostics.Generate_d_ts_files_from_TypeScript_and_JavaScript_files_in_your_project,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_false_unless_composite_is_set,
        },
        new CommandLineOption
        {
            Name = "declarationMap",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            DefaultValueDescription = false,
            Description = global::Ts.Diagnostics.Diagnostics.Create_sourcemaps_for_d_ts_files,
        },
        new CommandLineOption
        {
            Name = "emitDeclarationOnly",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Only_output_d_ts_files_and_not_JavaScript_files,
            transpileOptionValue = Tristate.Unknown,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "sourceMap",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            DefaultValueDescription = false,
            Description = global::Ts.Diagnostics.Diagnostics.Create_source_map_files_for_emitted_JavaScript_files,
        },
        new CommandLineOption
        {
            Name = "inlineSourceMap",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Include_sourcemap_files_inside_the_emitted_JavaScript,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noCheck",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = false,
            Category = global::Ts.Diagnostics.Diagnostics.Compiler_Diagnostics,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_full_type_checking_only_critical_parse_and_emit_errors_will_be_reported,
            transpileOptionValue = Tristate.True,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "deduplicatePackages",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Deduplicate_packages_with_the_same_name_and_version,
            DefaultValueDescription = true,
            AffectsProgramStructure = true,
        },
        new CommandLineOption
        {
            Name = "noEmit",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_emitting_files_from_a_compilation,
            transpileOptionValue = Tristate.Unknown,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "assumeChangesOnlyAffectDirectDependencies",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Have_recompiles_in_projects_that_use_incremental_and_watch_mode_assume_that_changes_within_a_file_will_only_affect_files_directly_depending_on_it,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "locale",
            Kind = CommandLineOptionTypeString,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            IsCommandLineOnly = true,
            Description = global::Ts.Diagnostics.Diagnostics.Set_the_language_of_the_messaging_from_TypeScript_This_does_not_affect_emit,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.Platform_specific,
            extraValidation = ExtraValidation.extraValidationLocale,
        },
        new CommandLineOption
        {
            Name = "quiet",
            ShortName = "q",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Do_not_print_diagnostics,
        },
        new CommandLineOption
        {
            Name = "singleThreaded",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Run_in_single_threaded_mode,
        },
        new CommandLineOption
        {
            Name = "pprofDir",
            Kind = CommandLineOptionTypeString,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Generate_pprof_CPU_Slashmemory_profiles_to_the_given_directory,
        },
        new CommandLineOption
        {
            Name = "checkers",
            Kind = CommandLineOptionTypeNumber,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Set_the_number_of_checkers_per_project,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_4_unless_singleThreaded_is_passed,
            minValue = 1,
        },
    };

    internal static readonly CommandLineOption[] optionsForCompiler = new CommandLineOption[]
    {
        //******* compilerOptions not common with --build *******

        // CommandLine only options
        new CommandLineOption
        {
            Name = "all",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Show_all_compiler_options,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "version",
            ShortName = "v",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Print_the_compiler_s_version,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "init",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Initializes_a_TypeScript_project_and_creates_a_tsconfig_json_file,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "project",
            ShortName = "p",
            Kind = CommandLineOptionTypeString,
            IsFilePath = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            Description = global::Ts.Diagnostics.Diagnostics.Compile_the_project_given_the_path_to_its_configuration_file_or_to_a_folder_with_a_tsconfig_json,
        },
        new CommandLineOption
        {
            Name = "showConfig",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            IsCommandLineOnly = true,
            Description = global::Ts.Diagnostics.Diagnostics.Print_the_final_configuration_instead_of_building,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "listFilesOnly",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            IsCommandLineOnly = true,
            Description = global::Ts.Diagnostics.Diagnostics.Print_names_of_files_that_are_part_of_the_compilation_and_then_stop_processing,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "ignoreConfig",
            Kind = CommandLineOptionTypeBoolean,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Command_line_Options,
            IsCommandLineOnly = true,
            Description = global::Ts.Diagnostics.Diagnostics.Ignore_the_tsconfig_found_and_build_with_commandline_options_and_files,
            DefaultValueDescription = false,
        },

        // Basic
        new CommandLineOption
        {
            Name = "target",
            ShortName = "t",
            Kind = CommandLineOptionTypeEnum, // targetOptionMap
            AffectsSourceFile = true,
            AffectsModuleResolution = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Set_the_JavaScript_language_version_for_emitted_JavaScript_and_include_compatible_library_declarations,
            DefaultValueDescription = ScriptTarget.LatestStandard,
        },
        new CommandLineOption
        {
            Name = "module",
            ShortName = "m",
            Kind = CommandLineOptionTypeEnum, // moduleOptionMap
            AffectsModuleResolution = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_what_module_code_is_generated,
            DefaultValueDescription = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "lib",
            Kind = CommandLineOptionTypeList,
            AffectsProgramStructure = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_a_set_of_bundled_library_declaration_files_that_describe_the_target_runtime_environment,
            transpileOptionValue = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "allowJs",
            Kind = CommandLineOptionTypeBoolean,
            allowJsFlag = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.JavaScript_Support,
            Description = global::Ts.Diagnostics.Diagnostics.Allow_JavaScript_files_to_be_a_part_of_your_program_Use_the_checkJs_option_to_get_errors_from_these_files,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_false_unless_checkJs_is_set,
        },
        new CommandLineOption
        {
            Name = "checkJs",
            Kind = CommandLineOptionTypeBoolean,
            AffectsModuleResolution = true,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.JavaScript_Support,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_error_reporting_in_type_checked_JavaScript_files,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "jsx",
            Kind = CommandLineOptionTypeEnum, // jsxOptionMap,
            AffectsSourceFile = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsModuleResolution = true,
            AffectsSemanticDiagnostics = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_what_JSX_code_is_generated,
            DefaultValueDescription = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "outFile",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsDeclarationPath = true,
            IsFilePath = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_a_file_that_bundles_all_outputs_into_one_JavaScript_file_If_declaration_is_true_also_designates_a_file_that_bundles_all_d_ts_output,
            transpileOptionValue = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "outDir",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsDeclarationPath = true,
            IsFilePath = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_an_output_folder_for_all_emitted_files,
        },
        new CommandLineOption
        {
            Name = "rootDir",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsDeclarationPath = true,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_root_folder_within_your_source_files,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.Computed_from_the_list_of_input_files,
        },
        new CommandLineOption
        {
            Name = "composite",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            IsTSConfigOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Projects,
            transpileOptionValue = Tristate.Unknown,
            DefaultValueDescription = false,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_constraints_that_allow_a_TypeScript_project_to_be_used_with_project_references,
        },
        new CommandLineOption
        {
            Name = "tsBuildInfoFile",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Projects,
            transpileOptionValue = Tristate.Unknown,
            DefaultValueDescription = ".tsbuildinfo",
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_path_to_tsbuildinfo_incremental_compilation_file,
        },
        new CommandLineOption
        {
            Name = "removeComments",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            DefaultValueDescription = false,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_emitting_comments,
        },
        new CommandLineOption
        {
            Name = "importHelpers",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsSourceFile = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Allow_importing_helper_functions_from_tslib_once_per_project_instead_of_including_them_per_file,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "downlevelIteration",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Emit_more_compliant_but_verbose_and_less_performant_JavaScript_for_iteration,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "isolatedModules",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Ensure_that_each_file_can_be_safely_transpiled_without_relying_on_other_imports,
            transpileOptionValue = Tristate.True,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "verbatimModuleSyntax",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Do_not_transform_or_elide_any_imports_or_exports_not_marked_as_type_only_ensuring_they_are_written_in_the_output_file_s_format_based_on_the_module_setting,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "isolatedDeclarations",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Require_sufficient_annotation_on_exports_so_other_tools_can_trivially_generate_declaration_files,
            DefaultValueDescription = false,
            AffectsBuildInfo = true,
            AffectsSemanticDiagnostics = true,
        },
        new CommandLineOption
        {
            Name = "erasableSyntaxOnly",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Do_not_allow_runtime_constructs_that_are_not_part_of_ECMAScript,
            DefaultValueDescription = false,
            AffectsBuildInfo = true,
            AffectsSemanticDiagnostics = true,
        },
        new CommandLineOption
        {
            Name = "libReplacement",
            Kind = CommandLineOptionTypeBoolean,
            AffectsProgramStructure = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_lib_replacement,
            DefaultValueDescription = false,
        },

        // Strict Type Checks
        new CommandLineOption
        {
            Name = "strict",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_all_strict_type_checking_options,
            DefaultValueDescription = true,
        },
        new CommandLineOption
        {
            Name = "noImplicitAny",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_error_reporting_for_expressions_and_declarations_with_an_implied_any_type,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "strictNullChecks",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.When_type_checking_take_into_account_null_and_undefined,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "strictFunctionTypes",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.When_assigning_functions_check_to_ensure_parameters_and_the_return_values_are_subtype_compatible,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "strictBindCallApply",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Check_that_the_arguments_for_bind_call_and_apply_methods_match_the_original_function,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "strictPropertyInitialization",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Check_for_class_properties_that_are_declared_but_not_set_in_the_constructor,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "strictBuiltinIteratorReturn",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Built_in_iterators_are_instantiated_with_a_TReturn_type_of_undefined_instead_of_any,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "noImplicitThis",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_error_reporting_when_this_is_given_the_type_any,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "useUnknownInCatchVariables",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            strictFlag = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Default_catch_clause_variables_as_unknown_instead_of_any,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_unless_strict_is_false,
        },
        new CommandLineOption
        {
            Name = "alwaysStrict",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSourceFile = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Ensure_use_strict_is_always_emitted,
            DefaultValueDescription = true,
        },
        new CommandLineOption
        {
            Name = "stableTypeOrdering",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Ensure_types_are_ordered_stably_and_deterministically_across_compilations,
            DefaultValueDescription = true,
        },

        // Additional Checks
        new CommandLineOption
        {
            Name = "noUnusedLocals",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_error_reporting_when_local_variables_aren_t_read,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noUnusedParameters",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Raise_an_error_when_a_function_parameter_isn_t_read,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "exactOptionalPropertyTypes",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Interpret_optional_property_types_as_written_rather_than_adding_undefined,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noImplicitReturns",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_error_reporting_for_codepaths_that_do_not_explicitly_return_in_a_function,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noFallthroughCasesInSwitch",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBindDiagnostics = true,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_error_reporting_for_fallthrough_cases_in_switch_statements,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noUncheckedIndexedAccess",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Add_undefined_to_a_type_when_accessed_using_an_index,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noImplicitOverride",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Ensure_overriding_members_in_derived_classes_are_marked_with_an_override_modifier,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noPropertyAccessFromIndexSignature",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = false,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Enforces_using_indexed_accessors_for_keys_declared_using_an_indexed_type,
            DefaultValueDescription = false,
        },

        // Module Resolution
        new CommandLineOption
        {
            Name = "moduleResolution",
            Kind = CommandLineOptionTypeEnum,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_how_TypeScript_looks_up_a_file_from_a_given_module_specifier,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_nodenext_if_module_is_nodenext_node16_if_module_is_node16_or_node18_otherwise_bundler,
        },
        new CommandLineOption
        {
            Name = "baseUrl",
            Kind = CommandLineOptionTypeString,
            AffectsModuleResolution = true,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_base_directory_to_resolve_non_relative_module_names,
        },
        new CommandLineOption
        {
            // this option can only be specified in tsconfig.json
            // use type = object to copy the value as-is
            Name = "paths",
            Kind = CommandLineOptionTypeObject,
            AffectsModuleResolution = true,
            allowConfigDirTemplateSubstitution = true,
            IsTSConfigOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_a_set_of_entries_that_re_map_imports_to_additional_lookup_locations,
            transpileOptionValue = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "rootDirs",
            Kind = CommandLineOptionTypeList,
            IsTSConfigOnly = true,
            AffectsModuleResolution = true,
            allowConfigDirTemplateSubstitution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Allow_multiple_folders_to_be_treated_as_one_when_resolving_modules,
            transpileOptionValue = Tristate.Unknown,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.Computed_from_the_list_of_input_files,
        },
        new CommandLineOption
        {
            Name = "typeRoots",
            Kind = CommandLineOptionTypeList,
            AffectsModuleResolution = true,
            allowConfigDirTemplateSubstitution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_multiple_folders_that_act_like_Slashnode_modules_Slash_types,
        },
        new CommandLineOption
        {
            Name = "types",
            Kind = CommandLineOptionTypeList,
            AffectsProgramStructure = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_type_package_names_to_be_included_without_being_referenced_in_a_source_file,
            transpileOptionValue = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "allowSyntheticDefaultImports",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Allow_import_x_from_y_when_a_module_doesn_t_have_a_default_export,
            DefaultValueDescription = true,
        },
        new CommandLineOption
        {
            Name = "esModuleInterop",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            ShowInSimplifiedHelpView = true,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Emit_additional_JavaScript_to_ease_support_for_importing_CommonJS_modules_This_enables_allowSyntheticDefaultImports_for_type_compatibility,
            DefaultValueDescription = true,
        },
        new CommandLineOption
        {
            Name = "preserveSymlinks",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_resolving_symlinks_to_their_realpath_This_correlates_to_the_same_flag_in_node,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "allowUmdGlobalAccess",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Allow_accessing_UMD_globals_from_modules,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "moduleSuffixes",
            Kind = CommandLineOptionTypeList,
            listPreserveFalsyValues = true,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.List_of_file_name_suffixes_to_search_when_resolving_a_module,
        },
        new CommandLineOption
        {
            Name = "allowImportingTsExtensions",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Allow_imports_to_include_TypeScript_file_extensions_Requires_moduleResolution_bundler_and_either_noEmit_or_emitDeclarationOnly_to_be_set,
            DefaultValueDescription = false,
            transpileOptionValue = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "rewriteRelativeImportExtensions",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Rewrite_ts_tsx_mts_and_cts_file_extensions_in_relative_import_paths_to_their_JavaScript_equivalent_in_output_files,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "resolvePackageJsonExports",
            Kind = CommandLineOptionTypeBoolean,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Use_the_package_json_exports_field_when_resolving_package_imports,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_when_moduleResolution_is_node16_nodenext_or_bundler_otherwise_false,
        },
        new CommandLineOption
        {
            Name = "resolvePackageJsonImports",
            Kind = CommandLineOptionTypeBoolean,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Use_the_package_json_imports_field_when_resolving_imports,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_when_moduleResolution_is_node16_nodenext_or_bundler_otherwise_false,
        },
        new CommandLineOption
        {
            Name = "customConditions",
            Kind = CommandLineOptionTypeList,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Conditions_to_set_in_addition_to_the_resolver_specific_defaults_when_resolving_imports,
        },
        new CommandLineOption
        {
            Name = "noUncheckedSideEffectImports",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Check_side_effect_imports,
            DefaultValueDescription = true,
        },

        // Source Maps
        new CommandLineOption
        {
            Name = "sourceRoot",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_root_path_for_debuggers_to_find_the_reference_source_code,
        },
        new CommandLineOption
        {
            Name = "mapRoot",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_location_where_debugger_should_locate_map_files_instead_of_generated_locations,
        },
        new CommandLineOption
        {
            Name = "inlineSources",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Include_source_code_in_the_sourcemaps_inside_the_emitted_JavaScript,
            DefaultValueDescription = false,
        },

        // Experimental
        new CommandLineOption
        {
            Name = "experimentalDecorators",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_experimental_support_for_legacy_experimental_decorators,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "emitDecoratorMetadata",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Emit_design_type_metadata_for_decorated_declarations_in_source_files,
            DefaultValueDescription = false,
        },

        // Advanced
        new CommandLineOption
        {
            Name = "jsxFactory",
            Kind = CommandLineOptionTypeString,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_JSX_factory_function_used_when_targeting_React_JSX_emit_e_g_React_createElement_or_h,
            DefaultValueDescription = "`React.createElement`",
        },
        new CommandLineOption
        {
            Name = "jsxFragmentFactory",
            Kind = CommandLineOptionTypeString,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_JSX_Fragment_reference_used_for_fragments_when_targeting_React_JSX_emit_e_g_React_Fragment_or_Fragment,
            DefaultValueDescription = "React.Fragment",
        },
        new CommandLineOption
        {
            Name = "jsxImportSource",
            Kind = CommandLineOptionTypeString,
            AffectsSemanticDiagnostics = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsModuleResolution = true,
            AffectsSourceFile = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_module_specifier_used_to_import_the_JSX_factory_functions_when_using_jsx_Colon_react_jsx_Asterisk,
            DefaultValueDescription = "react",
        },
        new CommandLineOption
        {
            Name = "resolveJsonModule",
            Kind = CommandLineOptionTypeBoolean,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_importing_json_files,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "allowArbitraryExtensions",
            Kind = CommandLineOptionTypeBoolean,
            AffectsProgramStructure = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Enable_importing_files_with_any_extension_provided_a_declaration_file_is_present,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "reactNamespace",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_object_invoked_for_createElement_This_only_applies_when_targeting_react_JSX_emit,
            DefaultValueDescription = "`React`",
        },
        new CommandLineOption
        {
            Name = "skipDefaultLibCheck",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Completeness,
            Description = global::Ts.Diagnostics.Diagnostics.Skip_type_checking_d_ts_files_that_are_included_with_TypeScript,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "emitBOM",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Emit_a_UTF_8_Byte_Order_Mark_BOM_in_the_beginning_of_output_files,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "newLine",
            Kind = CommandLineOptionTypeEnum, // newLineOptionMap,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Set_the_newline_character_for_emitting_files,
            DefaultValueDescription = "lf",
        },
        new CommandLineOption
        {
            Name = "noErrorTruncation",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Output_Formatting,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_truncating_types_in_error_messages,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noLib",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            AffectsProgramStructure = true,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_including_any_library_files_including_the_default_lib_d_ts,
            transpileOptionValue = Tristate.True,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noResolve",
            Kind = CommandLineOptionTypeBoolean,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Modules,
            Description = global::Ts.Diagnostics.Diagnostics.Disallow_import_s_require_s_or_reference_s_from_expanding_the_number_of_files_TypeScript_should_add_to_a_project,
            transpileOptionValue = Tristate.True,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "stripInternal",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_emitting_declarations_that_have_internal_in_their_JSDoc_comments,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "disableSizeLimit",
            Kind = CommandLineOptionTypeBoolean,
            AffectsProgramStructure = true,
            Category = global::Ts.Diagnostics.Diagnostics.Editor_Support,
            Description = global::Ts.Diagnostics.Diagnostics.Remove_the_20mb_cap_on_total_source_code_size_for_JavaScript_files_in_the_TypeScript_language_server,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "disableSourceOfProjectReferenceRedirect",
            Kind = CommandLineOptionTypeBoolean,
            IsTSConfigOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Projects,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_preferring_source_files_instead_of_declaration_files_when_referencing_composite_projects,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "disableSolutionSearching",
            Kind = CommandLineOptionTypeBoolean,
            IsTSConfigOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Projects,
            Description = global::Ts.Diagnostics.Diagnostics.Opt_a_project_out_of_multi_project_reference_checking_when_editing,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "disableReferencedProjectLoad",
            Kind = CommandLineOptionTypeBoolean,
            IsTSConfigOnly = true,
            Category = global::Ts.Diagnostics.Diagnostics.Projects,
            Description = global::Ts.Diagnostics.Diagnostics.Reduce_the_number_of_projects_loaded_automatically_by_TypeScript,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noEmitHelpers",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_generating_custom_helper_functions_like_extends_in_compiled_output,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "noEmitOnError",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            transpileOptionValue = Tristate.Unknown,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_emitting_files_if_any_type_checking_errors_are_reported,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "preserveConstEnums",
            Kind = CommandLineOptionTypeBoolean,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_erasing_const_enum_declarations_in_generated_code,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "declarationDir",
            Kind = CommandLineOptionTypeString,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            AffectsDeclarationPath = true,
            IsFilePath = true,
            Category = global::Ts.Diagnostics.Diagnostics.Emit,
            transpileOptionValue = Tristate.Unknown,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_output_directory_for_generated_declaration_files,
        },
        new CommandLineOption
        {
            Name = "skipLibCheck",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Completeness,
            Description = global::Ts.Diagnostics.Diagnostics.Skip_type_checking_all_d_ts_files,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "allowUnusedLabels",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBindDiagnostics = true,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_error_reporting_for_unused_labels,
            DefaultValueDescription = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "allowUnreachableCode",
            Kind = CommandLineOptionTypeBoolean,
            AffectsBindDiagnostics = true,
            AffectsSemanticDiagnostics = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Type_Checking,
            Description = global::Ts.Diagnostics.Diagnostics.Disable_error_reporting_for_unreachable_code,
            DefaultValueDescription = Tristate.Unknown,
        },
        new CommandLineOption
        {
            Name = "forceConsistentCasingInFileNames",
            Kind = CommandLineOptionTypeBoolean,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Interop_Constraints,
            Description = global::Ts.Diagnostics.Diagnostics.Ensure_that_casing_is_correct_in_imports,
            DefaultValueDescription = true,
        },
        new CommandLineOption
        {
            Name = "maxNodeModuleJsDepth",
            Kind = CommandLineOptionTypeNumber,
            AffectsModuleResolution = true,
            Category = global::Ts.Diagnostics.Diagnostics.JavaScript_Support,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_the_maximum_folder_depth_used_for_checking_JavaScript_files_from_node_modules_Only_applicable_with_allowJs,
            DefaultValueDescription = 0,
        },
        new CommandLineOption
        {
            Name = "useDefineForClassFields",
            Kind = CommandLineOptionTypeBoolean,
            AffectsSemanticDiagnostics = true,
            AffectsEmit = true,
            AffectsBuildInfo = true,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            Description = global::Ts.Diagnostics.Diagnostics.Emit_ECMAScript_standard_compliant_class_fields,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_true_for_ES2022_and_above_including_ESNext,
        },
        new CommandLineOption
        {
            // A list of plugins to load in the language service
            Name = "plugins",
            Kind = CommandLineOptionTypeList,
            IsTSConfigOnly = true,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_a_list_of_language_service_plugins_to_include,
            Category = global::Ts.Diagnostics.Diagnostics.Editor_Support,
        },
        new CommandLineOption
        {
            Name = "moduleDetection",
            Kind = CommandLineOptionTypeEnum,
            AffectsSourceFile = true,
            AffectsModuleResolution = true,
            Description = global::Ts.Diagnostics.Diagnostics.Control_what_method_is_used_to_detect_module_format_JS_files,
            Category = global::Ts.Diagnostics.Diagnostics.Language_and_Environment,
            DefaultValueDescription = global::Ts.Diagnostics.Diagnostics.X_auto_Colon_Treat_files_with_imports_exports_import_meta_jsx_with_jsx_Colon_react_jsx_or_esm_format_with_module_Colon_node16_as_modules,
        },
        new CommandLineOption
        {
            Name = "ignoreDeprecations",
            Kind = CommandLineOptionTypeString,
            DefaultValueDescription = Tristate.Unknown,
        },
    };

    public static readonly CommandLineOption[] OptionsDeclarations =
        commonOptionsWithBuild.Concat(optionsForCompiler).ToArray();

    // PORT-NOTE: Go's optionsHaveChanges/ForEachCompilerOptionValue use reflection over
    // core.CompilerOptions (declscompiler.go:1203-1240). Reflection is AOT-banned
    // (plan/07 §1.1), so field enumeration goes through the hand-written per-field table
    // CompilerOptionsFields (the "single generator 统一供给" equivalent). CompilerOptionsMap.Get
    // matches by the Go/C# field name (case-insensitive), exactly as reflect.Field(i).Name does.
    internal static bool optionsHaveChanges(CompilerOptions? oldOptions, CompilerOptions? newOptions, Func<CommandLineOption, bool> declFilter)
    {
        if (ReferenceEquals(oldOptions, newOptions))
        {
            return false;
        }
        if (oldOptions == null || newOptions == null)
        {
            return true;
        }
        var old = oldOptions;
        return ForEachCompilerOptionValue(newOptions, declFilter, (option, value, i) =>
        {
            var newValue = value;
            var oldValue = CompilerOptionsFields.Fields[i].Get(old);
            if (option.strictFlag)
            {
                return old.GetStrictOptionValue((Tristate)oldValue!) != newOptions.GetStrictOptionValue((Tristate)newValue!);
            }
            if (option.allowJsFlag)
            {
                return old.GetAllowJS() != newOptions.GetAllowJS();
            }
            return !CompilerOptionsFields.DeepEqual(newValue, oldValue);
        });
    }

    public static bool ForEachCompilerOptionValue(CompilerOptions options, Func<CommandLineOption, bool> declFilter, Func<CommandLineOption, object?, int, bool> fn)
    {
        var fields = CompilerOptionsFields.Fields;
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var optionDeclaration = TsConfigParsing.CommandLineCompilerOptionsMap.Get(field.Name);
            if (optionDeclaration != null && declFilter(optionDeclaration))
            {
                if (fn(optionDeclaration, field.Get(options), i))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool CompilerOptionsAffectSemanticDiagnostics(CompilerOptions? oldOptions, CompilerOptions? newOptions)
    {
        return optionsHaveChanges(oldOptions, newOptions, static option => option.AffectsSemanticDiagnostics);
    }

    public static bool CompilerOptionsAffectDeclarationPath(CompilerOptions? oldOptions, CompilerOptions? newOptions)
    {
        return optionsHaveChanges(oldOptions, newOptions, static option => option.AffectsDeclarationPath);
    }

    public static bool CompilerOptionsAffectEmit(CompilerOptions? oldOptions, CompilerOptions? newOptions)
    {
        return optionsHaveChanges(oldOptions, newOptions, static option => option.AffectsEmit);
    }
}

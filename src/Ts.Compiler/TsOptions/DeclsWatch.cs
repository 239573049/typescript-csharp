// Ported from tsgo internal/tsoptions/declswatch.go

using Ts.Core;
using static Ts.TsOptions.CommandLineOptionKind;

namespace Ts.TsOptions;

public static class DeclsWatch
{
    public static readonly CommandLineOption[] OptionsForWatch = new CommandLineOption[]
    {
        new CommandLineOption
        {
            Name = "watchInterval",
            Kind = CommandLineOptionTypeNumber,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
        },
        new CommandLineOption
        {
            Name = "watchFile",
            Kind = CommandLineOptionTypeEnum,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_how_the_TypeScript_watch_mode_works,
            DefaultValueDescription = WatchFileKind.UseFsEvents,
        },
        new CommandLineOption
        {
            Name = "watchDirectory",
            Kind = CommandLineOptionTypeEnum,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_how_directories_are_watched_on_systems_that_lack_recursive_file_watching_functionality,
            DefaultValueDescription = WatchDirectoryKind.UseFsEvents,
        },
        new CommandLineOption
        {
            Name = "fallbackPolling",
            Kind = CommandLineOptionTypeEnum,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Specify_what_approach_the_watcher_should_use_if_the_system_runs_out_of_native_file_watchers,
            DefaultValueDescription = PollingKind.PriorityInterval,
        },
        new CommandLineOption
        {
            Name = "synchronousWatchDirectory",
            Kind = CommandLineOptionTypeBoolean,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Synchronously_call_callbacks_and_update_the_state_of_directory_watchers_on_platforms_that_don_t_support_recursive_watching_natively,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "excludeDirectories",
            Kind = CommandLineOptionTypeList,
            allowConfigDirTemplateSubstitution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Remove_a_list_of_directories_from_the_watch_process,
        },
        new CommandLineOption
        {
            Name = "excludeFiles",
            Kind = CommandLineOptionTypeList,
            allowConfigDirTemplateSubstitution = true,
            Category = global::Ts.Diagnostics.Diagnostics.Watch_and_Build_Modes,
            Description = global::Ts.Diagnostics.Diagnostics.Remove_a_list_of_files_from_the_watch_mode_s_processing,
        },
    };
}

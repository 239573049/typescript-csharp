// Ported from tsgo internal/execute/build/uptodatestatus.go.

using System;

// PORT-NOTE: status/data fields are populated by buildtask.go (next construction batch);
// CS0649 is suppressed until it lands.
#pragma warning disable CS0649

namespace Ts.Execute.Build;

// uptodatestatus.go:5-49
internal enum UpToDateStatusType : ushort
{
    // Errors:

    // config file was not found
    ConfigFileNotFound = 0,
    // found errors during build
    BuildErrors,
    // did not build because upstream project has errors - and we have option to stop build on upstream errors
    UpstreamErrors,

    // Its all good, no work to do
    UpToDate,

    // Pseudo-builds - touch timestamps, no actual build:

    // The project appears out of date because its upstream inputs are newer than its outputs,
    // but all of its outputs are actually newer than the previous identical outputs of its (.d.ts) inputs.
    // This means we can Pseudo-build (just touch timestamps), as if we had actually built this project.
    UpToDateWithUpstreamTypes,
    // The project appears up to date and even though input file changed, its text didnt so just need to update timestamps
    UpToDateWithInputFileText,

    // Needs build:

    // input file is missing
    InputFileMissing,
    // output file is missing
    OutputMissing,
    // input file is newer than output file
    InputFileNewer,
    // build info is out of date as we need to emit some files
    OutOfDateBuildInfoWithPendingEmit,
    // build info indicates that project has errors and they need to be reported
    OutOfDateBuildInfoWithErrors,
    // build info options indicate there is work to do based on changes in options
    OutOfDateOptions,
    // file was root when built but not any more
    OutOfDateRoots,
    // buildInfo.version mismatch with current ts version
    TsVersionOutputOfDate,
    // build because --force was specified
    ForceBuild,

    // solution file
    Solution,
}

// uptodatestatus.go:51-54
internal sealed class InputOutputName
{
    internal string input = "";
    internal string output = "";
}

// uptodatestatus.go:56-59
internal sealed class FileAndTime
{
    internal string file = "";
    internal DateTime time;
}

// uptodatestatus.go:61-65
internal sealed class InputOutputFileAndTime
{
    internal FileAndTime input = new();
    internal FileAndTime output = new();
    internal string buildInfo = "";
}

// uptodatestatus.go:67-70
internal sealed class UpstreamErrorsData
{
    internal string reference = "";
    internal bool refHasUpstreamErrors;
}

// uptodatestatus.go:72-75
internal sealed class UpToDateStatus
{
    internal UpToDateStatusType kind;
    internal object? data;

    // uptodatestatus.go:77-86
    internal bool IsError()
    {
        switch (kind)
        {
            case UpToDateStatusType.ConfigFileNotFound:
            case UpToDateStatusType.BuildErrors:
            case UpToDateStatusType.UpstreamErrors:
                return true;
            default:
                return false;
        }
    }

    // uptodatestatus.go:88-96
    internal bool IsPseudoBuild()
    {
        switch (kind)
        {
            case UpToDateStatusType.UpToDateWithUpstreamTypes:
            case UpToDateStatusType.UpToDateWithInputFileText:
                return true;
            default:
                return false;
        }
    }

    // uptodatestatus.go:98-104
    internal InputOutputFileAndTime? InputOutputFileAndTime() => data as InputOutputFileAndTime;

    // uptodatestatus.go:106-112
    internal InputOutputName? InputOutputName() => data as InputOutputName;

    // uptodatestatus.go:114-126
    internal string OldestOutputFileName()
    {
        if (!IsPseudoBuild() && kind != UpToDateStatusType.UpToDate)
        {
            throw new InvalidOperationException("only valid for up to date status of pseudo-build or up to date");
        }

        if (InputOutputFileAndTime() is { } inputOutputFileAndTime)
        {
            return inputOutputFileAndTime.output.file;
        }
        if (InputOutputName() is { } inputOutputName)
        {
            return inputOutputName.output;
        }
        return (string)data!;
    }

    // uptodatestatus.go:128-130
    internal UpstreamErrorsData UpstreamErrors() => (UpstreamErrorsData)data!;
}

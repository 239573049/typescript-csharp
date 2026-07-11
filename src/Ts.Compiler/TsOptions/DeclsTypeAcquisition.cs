// Ported from tsgo internal/tsoptions/declstypeacquisition.go

using static Ts.TsOptions.CommandLineOptionKind;

namespace Ts.TsOptions;

public static class DeclsTypeAcquisition
{
    // PORT-NOTE: Go initializes package vars by dependency order; C# static field
    // initializers run in textual order, so typeAcquisitionDecls must be declared
    // before typeAcquisitionDeclaration (which reads it).

    // Do not delete this without updating the website's tsconfig generation.
    public static readonly CommandLineOption[] typeAcquisitionDecls = new CommandLineOption[]
    {
        new CommandLineOption
        {
            Name = "enable",
            Kind = CommandLineOptionTypeBoolean,
            DefaultValueDescription = false,
        },
        new CommandLineOption
        {
            Name = "include",
            Kind = CommandLineOptionTypeList,
        },
        new CommandLineOption
        {
            Name = "exclude",
            Kind = CommandLineOptionTypeList,
        },
        new CommandLineOption
        {
            Name = "disableFilenameBasedTypeAcquisition",
            Kind = CommandLineOptionTypeBoolean,
            DefaultValueDescription = false,
        },
    };

    public static readonly CommandLineOption typeAcquisitionDeclaration = new()
    {
        Name = "typeAcquisition",
        Kind = CommandLineOptionTypeObject,
        ElementOptions = TsConfigParsing.commandLineOptionsToMap(typeAcquisitionDecls),
    };
}

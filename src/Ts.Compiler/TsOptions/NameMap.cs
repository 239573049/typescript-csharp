// Ported from tsgo internal/tsoptions/namemap.go

using System.Collections.Generic;
using Ts.Collections;

namespace Ts.TsOptions;

// Package-level vars/funcs of namemap.go collect onto the file-named static class
// (05-conventions.md §2.1).
public static class NameMaps
{
    public static readonly NameMap CompilerNameMap = GetNameMapFromList(DeclsCompiler.OptionsDeclarations);
    public static readonly NameMap BuildNameMap = GetNameMapFromList(DeclsBuild.BuildOpts);
    public static readonly NameMap WatchNameMap = GetNameMapFromList(DeclsWatch.OptionsForWatch);

    public static NameMap GetNameMapFromList(CommandLineOption[] optDecls)
    {
        var optionsNames = new OrderedMap<string, CommandLineOption>(optDecls.Length);
        var shortOptionNames = new Dictionary<string, string>();
        foreach (var option in optDecls)
        {
            optionsNames.Set(OptionsUtil.ToLower(option.Name), option);
            if (option.ShortName != "")
            {
                shortOptionNames[option.ShortName] = option.Name;
            }
        }
        return new NameMap(optionsNames, shortOptionNames);
    }
}

public sealed class NameMap
{
    private readonly OrderedMap<string, CommandLineOption> optionsNames;
    private readonly Dictionary<string, string> shortOptionNames;

    internal NameMap(OrderedMap<string, CommandLineOption> optionsNames, Dictionary<string, string> shortOptionNames)
    {
        this.optionsNames = optionsNames;
        this.shortOptionNames = shortOptionNames;
    }

    public CommandLineOption? Get(string name)
    {
        return optionsNames.GetOrZero(OptionsUtil.ToLower(name));
    }

    public CommandLineOption? GetFromShort(string shortName)
    {
        // returns option only if shortName is a valid short option
        if (!shortOptionNames.TryGetValue(shortName, out var name))
        {
            return null;
        }
        return Get(name);
    }

    public CommandLineOption? GetOptionDeclarationFromName(string optionName, bool allowShort)
    {
        optionName = OptionsUtil.ToLower(optionName);
        // Try to translate short option names to their full equivalents.
        if (allowShort)
        {
            if (shortOptionNames.TryGetValue(optionName, out var shortName) && shortName != "")
            {
                optionName = shortName;
            }
        }
        return Get(optionName);
    }
}

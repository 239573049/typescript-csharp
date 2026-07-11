// Ported from tsgo internal/tsoptions/enummaps.go

using System.Collections.Generic;
using System.Linq;
using Ts.Collections;
using Ts.Core;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.TsOptions;

// Package-level vars/funcs of enummaps.go collect onto the file-named static class
// (05-conventions.md §2.1). Go `any` map values → `object?` (boxed enum/string).
public static class EnumMaps
{
    public static readonly OrderedMap<string, object?> LibMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        // JavaScript only
        new("es5", "lib.es5.d.ts"),
        new("es6", "lib.es2015.d.ts"),
        new("es2015", "lib.es2015.d.ts"),
        new("es7", "lib.es2016.d.ts"),
        new("es2016", "lib.es2016.d.ts"),
        new("es2017", "lib.es2017.d.ts"),
        new("es2018", "lib.es2018.d.ts"),
        new("es2019", "lib.es2019.d.ts"),
        new("es2020", "lib.es2020.d.ts"),
        new("es2021", "lib.es2021.d.ts"),
        new("es2022", "lib.es2022.d.ts"),
        new("es2023", "lib.es2023.d.ts"),
        new("es2024", "lib.es2024.d.ts"),
        new("es2025", "lib.es2025.d.ts"),
        new("esnext", "lib.esnext.d.ts"),
        // Host only
        new("dom", "lib.dom.d.ts"),
        new("dom.iterable", "lib.dom.iterable.d.ts"),
        new("dom.asynciterable", "lib.dom.asynciterable.d.ts"),
        new("webworker", "lib.webworker.d.ts"),
        new("webworker.importscripts", "lib.webworker.importscripts.d.ts"),
        new("webworker.iterable", "lib.webworker.iterable.d.ts"),
        new("webworker.asynciterable", "lib.webworker.asynciterable.d.ts"),
        new("scripthost", "lib.scripthost.d.ts"),
        // ES2015 and later By-feature options
        new("es2015.core", "lib.es2015.core.d.ts"),
        new("es2015.collection", "lib.es2015.collection.d.ts"),
        new("es2015.generator", "lib.es2015.generator.d.ts"),
        new("es2015.iterable", "lib.es2015.iterable.d.ts"),
        new("es2015.promise", "lib.es2015.promise.d.ts"),
        new("es2015.proxy", "lib.es2015.proxy.d.ts"),
        new("es2015.reflect", "lib.es2015.reflect.d.ts"),
        new("es2015.symbol", "lib.es2015.symbol.d.ts"),
        new("es2015.symbol.wellknown", "lib.es2015.symbol.wellknown.d.ts"),
        new("es2016.array.include", "lib.es2016.array.include.d.ts"),
        new("es2016.intl", "lib.es2016.intl.d.ts"),
        new("es2017.arraybuffer", "lib.es2017.arraybuffer.d.ts"),
        new("es2017.date", "lib.es2017.date.d.ts"),
        new("es2017.object", "lib.es2017.object.d.ts"),
        new("es2017.sharedmemory", "lib.es2017.sharedmemory.d.ts"),
        new("es2017.string", "lib.es2017.string.d.ts"),
        new("es2017.intl", "lib.es2017.intl.d.ts"),
        new("es2017.typedarrays", "lib.es2017.typedarrays.d.ts"),
        new("es2018.asyncgenerator", "lib.es2018.asyncgenerator.d.ts"),
        new("es2018.asynciterable", "lib.es2018.asynciterable.d.ts"),
        new("es2018.intl", "lib.es2018.intl.d.ts"),
        new("es2018.promise", "lib.es2018.promise.d.ts"),
        new("es2018.regexp", "lib.es2018.regexp.d.ts"),
        new("es2019.array", "lib.es2019.array.d.ts"),
        new("es2019.object", "lib.es2019.object.d.ts"),
        new("es2019.string", "lib.es2019.string.d.ts"),
        new("es2019.symbol", "lib.es2019.symbol.d.ts"),
        new("es2019.intl", "lib.es2019.intl.d.ts"),
        new("es2020.bigint", "lib.es2020.bigint.d.ts"),
        new("es2020.date", "lib.es2020.date.d.ts"),
        new("es2020.promise", "lib.es2020.promise.d.ts"),
        new("es2020.sharedmemory", "lib.es2020.sharedmemory.d.ts"),
        new("es2020.string", "lib.es2020.string.d.ts"),
        new("es2020.symbol.wellknown", "lib.es2020.symbol.wellknown.d.ts"),
        new("es2020.intl", "lib.es2020.intl.d.ts"),
        new("es2020.number", "lib.es2020.number.d.ts"),
        new("es2021.promise", "lib.es2021.promise.d.ts"),
        new("es2021.string", "lib.es2021.string.d.ts"),
        new("es2021.weakref", "lib.es2021.weakref.d.ts"),
        new("es2021.intl", "lib.es2021.intl.d.ts"),
        new("es2022.array", "lib.es2022.array.d.ts"),
        new("es2022.error", "lib.es2022.error.d.ts"),
        new("es2022.intl", "lib.es2022.intl.d.ts"),
        new("es2022.object", "lib.es2022.object.d.ts"),
        new("es2022.string", "lib.es2022.string.d.ts"),
        new("es2022.regexp", "lib.es2022.regexp.d.ts"),
        new("es2023.array", "lib.es2023.array.d.ts"),
        new("es2023.collection", "lib.es2023.collection.d.ts"),
        new("es2023.intl", "lib.es2023.intl.d.ts"),
        new("es2024.arraybuffer", "lib.es2024.arraybuffer.d.ts"),
        new("es2024.collection", "lib.es2024.collection.d.ts"),
        new("es2024.object", "lib.es2024.object.d.ts"),
        new("es2024.promise", "lib.es2024.promise.d.ts"),
        new("es2024.regexp", "lib.es2024.regexp.d.ts"),
        new("es2024.sharedmemory", "lib.es2024.sharedmemory.d.ts"),
        new("es2024.string", "lib.es2024.string.d.ts"),
        new("es2025.collection", "lib.es2025.collection.d.ts"),
        new("es2025.float16", "lib.es2025.float16.d.ts"),
        new("es2025.intl", "lib.es2025.intl.d.ts"),
        new("es2025.iterator", "lib.es2025.iterator.d.ts"),
        new("es2025.promise", "lib.es2025.promise.d.ts"),
        new("es2025.regexp", "lib.es2025.regexp.d.ts"),
        // Fallback for backward compatibility
        new("esnext.asynciterable", "lib.es2018.asynciterable.d.ts"),
        new("esnext.symbol", "lib.es2019.symbol.d.ts"),
        new("esnext.bigint", "lib.es2020.bigint.d.ts"),
        new("esnext.weakref", "lib.es2021.weakref.d.ts"),
        new("esnext.object", "lib.es2024.object.d.ts"),
        new("esnext.regexp", "lib.es2024.regexp.d.ts"),
        new("esnext.string", "lib.es2024.string.d.ts"),
        new("esnext.float16", "lib.es2025.float16.d.ts"),
        new("esnext.iterator", "lib.es2025.iterator.d.ts"),
        new("esnext.promise", "lib.es2025.promise.d.ts"),
        // ESNext By-feature options
        new("esnext.array", "lib.esnext.array.d.ts"),
        new("esnext.collection", "lib.esnext.collection.d.ts"),
        new("esnext.date", "lib.esnext.date.d.ts"),
        new("esnext.decorators", "lib.esnext.decorators.d.ts"),
        new("esnext.disposable", "lib.esnext.disposable.d.ts"),
        new("esnext.error", "lib.esnext.error.d.ts"),
        new("esnext.intl", "lib.esnext.intl.d.ts"),
        new("esnext.sharedmemory", "lib.esnext.sharedmemory.d.ts"),
        new("esnext.temporal", "lib.esnext.temporal.d.ts"),
        new("esnext.typedarrays", "lib.esnext.typedarrays.d.ts"),
        // Decorators
        new("decorators", "lib.decorators.d.ts"),
        new("decorators.legacy", "lib.decorators.legacy.d.ts"),
    });

    public static readonly string[] Libs = LibMap.Keys().ToArray();

    public static readonly Set<string> LibFilesSet =
        Set.FromItems(LibMap.Values().Select(static s => (string)s!).ToArray());

    // Go: func GetLibFileName(libName string) (string, bool). (T, ok) → Try (05 §4.1).
    public static bool TryGetLibFileName(string libName, out string result)
    {
        // checks if the libName is a valid lib name or file name and converts the lib name to the filename if needed
        libName = TsPathApi.ToFileNameLowerCase(libName);
        if (LibFilesSet.Has(libName))
        {
            result = libName;
            return true;
        }
        if (!LibMap.TryGet(libName, out var lib))
        {
            result = "";
            return false;
        }
        result = (string)lib!;
        return true;
    }

    public static readonly OrderedMap<string, object?> ModuleResolutionOptionMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("node16", ModuleResolutionKind.Node16),
        new("nodenext", ModuleResolutionKind.NodeNext),
        new("bundler", ModuleResolutionKind.Bundler),
        new("classic", ModuleResolutionKind.Classic),
        new("node", ModuleResolutionKind.Node10),
        new("node10", ModuleResolutionKind.Node10),
    });

    public static readonly OrderedMap<string, object?> TargetOptionMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("es5", ScriptTarget.ES5),
        new("es6", ScriptTarget.ES2015),
        new("es2015", ScriptTarget.ES2015),
        new("es2016", ScriptTarget.ES2016),
        new("es2017", ScriptTarget.ES2017),
        new("es2018", ScriptTarget.ES2018),
        new("es2019", ScriptTarget.ES2019),
        new("es2020", ScriptTarget.ES2020),
        new("es2021", ScriptTarget.ES2021),
        new("es2022", ScriptTarget.ES2022),
        new("es2023", ScriptTarget.ES2023),
        new("es2024", ScriptTarget.ES2024),
        new("es2025", ScriptTarget.ES2025),
        new("esnext", ScriptTarget.ESNext),
    });

    public static readonly OrderedMap<string, object?> ModuleOptionMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("commonjs", ModuleKind.CommonJS),
        new("amd", ModuleKind.AMD),
        new("system", ModuleKind.System),
        new("umd", ModuleKind.UMD),
        new("es6", ModuleKind.ES2015),
        new("es2015", ModuleKind.ES2015),
        new("es2020", ModuleKind.ES2020),
        new("es2022", ModuleKind.ES2022),
        new("esnext", ModuleKind.ESNext),
        new("node16", ModuleKind.Node16),
        new("node18", ModuleKind.Node18),
        new("node20", ModuleKind.Node20),
        new("nodenext", ModuleKind.NodeNext),
        new("preserve", ModuleKind.Preserve),
    });

    public static readonly OrderedMap<string, object?> ModuleDetectionOptionMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("auto", ModuleDetectionKind.Auto),
        new("legacy", ModuleDetectionKind.Legacy),
        new("force", ModuleDetectionKind.Force),
    });

    public static readonly OrderedMap<string, object?> JsxOptionMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("preserve", JsxEmit.Preserve),
        new("react-native", JsxEmit.ReactNative),
        new("react-jsx", JsxEmit.ReactJSX),
        new("react-jsxdev", JsxEmit.ReactJSXDev),
        new("react", JsxEmit.React),
    });

    public static readonly OrderedMap<string, object?> NewLineOptionMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("crlf", NewLineKind.CRLF),
        new("lf", NewLineKind.LF),
    });

    private static readonly Dictionary<ScriptTarget, string> targetToLibMap = new()
    {
        [ScriptTarget.ESNext] = "lib.esnext.full.d.ts",
        [ScriptTarget.ES2025] = "lib.es2025.full.d.ts",
        [ScriptTarget.ES2024] = "lib.es2024.full.d.ts",
        [ScriptTarget.ES2023] = "lib.es2023.full.d.ts",
        [ScriptTarget.ES2022] = "lib.es2022.full.d.ts",
        [ScriptTarget.ES2021] = "lib.es2021.full.d.ts",
        [ScriptTarget.ES2020] = "lib.es2020.full.d.ts",
        [ScriptTarget.ES2019] = "lib.es2019.full.d.ts",
        [ScriptTarget.ES2018] = "lib.es2018.full.d.ts",
        [ScriptTarget.ES2017] = "lib.es2017.full.d.ts",
        [ScriptTarget.ES2016] = "lib.es2016.full.d.ts",
        [ScriptTarget.ES2015] = "lib.es6.d.ts", // We don't use lib.es2015.full.d.ts due to breaking change.
    };

    public static Dictionary<ScriptTarget, string> TargetToLibMap()
    {
        return targetToLibMap;
    }

    public static string GetDefaultLibFileName(CompilerOptions options)
    {
        if (!targetToLibMap.TryGetValue(options.GetEmitScriptTarget(), out var name))
        {
            return "lib.d.ts";
        }
        return name;
    }

    public static readonly OrderedMap<string, object?> WatchFileEnumMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("fixedpollinginterval", WatchFileKind.FixedPollingInterval),
        new("prioritypollinginterval", WatchFileKind.PriorityPollingInterval),
        new("dynamicprioritypolling", WatchFileKind.DynamicPriorityPolling),
        new("fixedchunksizepolling", WatchFileKind.FixedChunkSizePolling),
        new("usefsevents", WatchFileKind.UseFsEvents),
        new("usefseventsonparentdirectory", WatchFileKind.UseFsEventsOnParentDirectory),
    });

    public static readonly OrderedMap<string, object?> WatchDirectoryEnumMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("usefsevents", WatchDirectoryKind.UseFsEvents),
        new("fixedpollinginterval", WatchDirectoryKind.FixedPollingInterval),
        new("dynamicprioritypolling", WatchDirectoryKind.DynamicPriorityPolling),
        new("fixedchunksizepolling", WatchDirectoryKind.FixedChunkSizePolling),
    });

    public static readonly OrderedMap<string, object?> FallbackEnumMap = OrderedMap.FromList(new MapEntry<string, object?>[]
    {
        new("fixedinterval", PollingKind.FixedInterval),
        new("priorityinterval", PollingKind.PriorityInterval),
        new("dynamicpriority", PollingKind.DynamicPriority),
        new("fixedchunksize", PollingKind.FixedChunkSize),
    });
}

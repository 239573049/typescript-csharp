// Ported from tsgo internal/checker/utilities.go (FeatureMapEntry + getFeatureMap,
// lines 1263-1529): the static es-lib feature index behind the "Do you need to change your
// target library?" suggestions (getSuggestedLibForNonExistentName / ...NonExistentProperty).

namespace Ts.Checker;

// utilities.go:1263-1266
internal readonly struct FeatureMapEntry
{
    internal readonly string Lib;
    internal readonly string[] Props;

    internal FeatureMapEntry(string lib, string[] props)
    {
        Lib = lib;
        Props = props;
    }
}

public partial class Checker
{
    // utilities.go:1269-1529 — Go wraps the table in sync.OnceValue; the C# static readonly field
    // initializer is the thread-safe once analogue. The table is only ever looked up by key
    // (TryGetValue) and its entry slices scanned in order — it is never map-iterated.
    private static readonly Dictionary<string, FeatureMapEntry[]> featureMap = new()
    {
        ["Array"] =
        [
            new("es2015", ["find", "findIndex", "fill", "copyWithin", "entries", "keys", "values"]),
            new("es2016", ["includes"]),
            new("es2019", ["flat", "flatMap"]),
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Iterator"] =
        [
            new("es2015", []),
        ],
        ["AsyncIterator"] =
        [
            new("es2015", []),
        ],
        ["ArrayBuffer"] =
        [
            new("es2024",
            [
                "maxByteLength",
                "resizable",
                "resize",
                "detached",
                "transfer",
                "transferToFixedLength",
            ]),
        ],
        ["Atomics"] =
        [
            new("es2017",
            [
                "add",
                "and",
                "compareExchange",
                "exchange",
                "isLockFree",
                "load",
                "or",
                "store",
                "sub",
                "wait",
                "notify",
                "xor",
            ]),
            new("es2024",
            [
                "waitAsync",
            ]),
        ],
        ["SharedArrayBuffer"] =
        [
            new("es2017",
            [
                "byteLength",
                "slice",
            ]),
            new("es2024",
            [
                "growable",
                "maxByteLength",
                "grow",
            ]),
        ],
        ["AsyncIterable"] =
        [
            new("es2018", []),
        ],
        ["AsyncIterableIterator"] =
        [
            new("es2018", []),
        ],
        ["AsyncGenerator"] =
        [
            new("es2018", []),
        ],
        ["AsyncGeneratorFunction"] =
        [
            new("es2018", []),
        ],
        ["RegExp"] =
        [
            new("es2015", ["flags", "sticky", "unicode"]),
            new("es2018", ["dotAll"]),
            new("es2024", ["unicodeSets"]),
        ],
        ["RegExpConstructor"] =
        [
            new("es2025", ["escape"]),
        ],
        ["Reflect"] =
        [
            new("es2015", ["apply", "construct", "defineProperty", "deleteProperty", "get", "getOwnPropertyDescriptor", "getPrototypeOf", "has", "isExtensible", "ownKeys", "preventExtensions", "set", "setPrototypeOf"]),
        ],
        ["ArrayConstructor"] =
        [
            new("es2015", ["from", "of"]),
            new("esnext", ["fromAsync"]),
        ],
        ["ObjectConstructor"] =
        [
            new("es2015", ["assign", "getOwnPropertySymbols", "keys", "is", "setPrototypeOf"]),
            new("es2017", ["values", "entries", "getOwnPropertyDescriptors"]),
            new("es2019", ["fromEntries"]),
            new("es2022", ["hasOwn"]),
            new("es2024", ["groupBy"]),
        ],
        ["NumberConstructor"] =
        [
            new("es2015", ["isFinite", "isInteger", "isNaN", "isSafeInteger", "parseFloat", "parseInt"]),
        ],
        ["Math"] =
        [
            new("es2015", ["clz32", "imul", "sign", "log10", "log2", "log1p", "expm1", "cosh", "sinh", "tanh", "acosh", "asinh", "atanh", "hypot", "trunc", "fround", "cbrt"]),
            new("es2025", ["f16round"]),
        ],
        ["Map"] =
        [
            new("es2015", ["entries", "keys", "values"]),
            new("esnext",
            [
                "getOrInsert",
                "getOrInsertComputed",
            ]),
        ],
        ["MapConstructor"] =
        [
            new("es2024", ["groupBy"]),
        ],
        ["Set"] =
        [
            new("es2015", ["entries", "keys", "values"]),
            new("es2025",
            [
                "union",
                "intersection",
                "difference",
                "symmetricDifference",
                "isSubsetOf",
                "isSupersetOf",
                "isDisjointFrom",
            ]),
        ],
        ["PromiseConstructor"] =
        [
            new("es2015", ["all", "race", "reject", "resolve"]),
            new("es2020", ["allSettled"]),
            new("es2021", ["any"]),
            new("es2024", ["withResolvers"]),
            new("es2025", ["try"]),
        ],
        ["Symbol"] =
        [
            new("es2015", ["for", "keyFor"]),
            new("es2019", ["description"]),
        ],
        ["WeakMap"] =
        [
            new("es2015", []),
            new("esnext",
            [
                "getOrInsert",
                "getOrInsertComputed",
            ]),
        ],
        ["WeakSet"] =
        [
            new("es2015", []),
        ],
        ["String"] =
        [
            new("es2015", ["codePointAt", "includes", "endsWith", "normalize", "repeat", "startsWith", "anchor", "big", "blink", "bold", "fixed", "fontcolor", "fontsize", "italics", "link", "small", "strike", "sub", "sup"]),
            new("es2017", ["padStart", "padEnd"]),
            new("es2019", ["trimStart", "trimEnd", "trimLeft", "trimRight"]),
            new("es2020", ["matchAll"]),
            new("es2021", ["replaceAll"]),
            new("es2022", ["at"]),
            new("es2024", ["isWellFormed", "toWellFormed"]),
        ],
        ["StringConstructor"] =
        [
            new("es2015", ["fromCodePoint", "raw"]),
        ],
        ["DateTimeFormat"] =
        [
            new("es2017", ["formatToParts"]),
        ],
        ["Promise"] =
        [
            new("es2015", []),
            new("es2018", ["finally"]),
        ],
        ["RegExpMatchArray"] =
        [
            new("es2018", ["groups"]),
        ],
        ["RegExpExecArray"] =
        [
            new("es2018", ["groups"]),
        ],
        ["Intl"] =
        [
            new("es2018", ["PluralRules"]),
            new("es2020", ["RelativeTimeFormat", "Locale", "DisplayNames"]),
            new("es2021", ["ListFormat", "DateTimeFormat"]),
            new("es2022", ["Segmenter"]),
            new("es2025", ["DurationFormat"]),
        ],
        ["NumberFormat"] =
        [
            new("es2018", ["formatToParts"]),
        ],
        ["SymbolConstructor"] =
        [
            new("es2020", ["matchAll"]),
            new("esnext",
            [
                "metadata",
                "dispose",
                "asyncDispose",
            ]),
        ],
        ["DataView"] =
        [
            new("es2020", ["setBigInt64", "setBigUint64", "getBigInt64", "getBigUint64"]),
            new("es2025", ["setFloat16", "getFloat16"]),
        ],
        ["BigInt"] =
        [
            new("es2020", []),
        ],
        ["RelativeTimeFormat"] =
        [
            new("es2020", ["format", "formatToParts", "resolvedOptions"]),
        ],
        ["Int8Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Uint8Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Uint8ClampedArray"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Int16Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Uint16Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Int32Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Uint32Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Float16Array"] =
        [
            new("es2025", []),
        ],
        ["Float32Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Float64Array"] =
        [
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["BigInt64Array"] =
        [
            new("es2020", []),
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["BigUint64Array"] =
        [
            new("es2020", []),
            new("es2022", ["at"]),
            new("es2023", ["findLastIndex", "findLast", "toReversed", "toSorted", "toSpliced", "with"]),
        ],
        ["Error"] =
        [
            new("es2022", ["cause"]),
        ],
        ["ErrorConstructor"] =
        [
            new("esnext", ["isError"]),
        ],
        ["Uint8ArrayConstructor"] =
        [
            new("esnext", ["fromBase64", "fromHex"]),
        ],
        ["DisposableStack"] =
        [
            new("esnext", []),
        ],
        ["AsyncDisposableStack"] =
        [
            new("esnext", []),
        ],
        ["Date"] =
        [
            new("esnext", ["toTemporalInstant"]),
        ],
    };

    // utilities.go:1269
    internal static Dictionary<string, FeatureMapEntry[]> GetFeatureMap() => featureMap;
}

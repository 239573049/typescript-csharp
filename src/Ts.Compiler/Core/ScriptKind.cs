// Ported from tsgo internal/core/scriptkind.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

namespace Ts.Core;

// Go: type ScriptKind int32. Member mapping (05 §1.1 enum prefix drop, §1.4:
// two-letter acronyms stay upper-case, three-letter+ are Pascal-cased):
// ScriptKindUnknown→Unknown, ScriptKindJS→JS, ScriptKindJSX→Jsx, ScriptKindTS→TS,
// ScriptKindTSX→Tsx, ScriptKindExternal→External, ScriptKindJSON→Json,
// ScriptKindDeferred→Deferred.
public enum ScriptKind
{
    Unknown = 0,
    JS = 1,
    Jsx = 2,
    TS = 3,
    Tsx = 4,
    External = 5,
    Json = 6,
    /**
     * Used on extensions that doesn't define the ScriptKind but the content defines it.
     * Deferred extensions are going to be included in all project contexts.
     */
    Deferred = 7,
}

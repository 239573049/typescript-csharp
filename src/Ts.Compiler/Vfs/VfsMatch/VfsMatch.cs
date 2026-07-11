// =============================================================================
// MATCHING_ALGORITHM.md (reproduced verbatim from tsgo internal/vfs/vfsmatch,
// per plan/06 §2.6).
// =============================================================================
// # Glob Matching Algorithm Specification
//
// This document is a formal algorithmic specification of the file-path glob matching
// logic. An implementation conforming to this specification must produce identical
// results for all inputs. All subroutine errors are propagated as errors of the
// calling routine unless stated otherwise.
//
// ---
//
// ## 1. Definitions
//
// **Path** — A normalized, `/`-separated absolute file path (e.g., `/project/src/index.ts`).
//
// **Path component** — A single segment between `/` delimiters (e.g., `src`, `index.ts`). The leading `/` produces the root component, which is the empty string `""`.
//
// **Spec** — A user-provided glob string (e.g., `src/**/*.ts`), before compilation.
//
// **Base path** — The absolute directory against which relative specs are resolved.
//
// **Usage mode** — One of three modes that alter matching semantics:
// - **Files** — Matches complete file paths.
// - **Directories** — Matches directory prefixes for traversal pruning.
// - **Exclude** — Matches paths to be excluded.
//
// **Component kind** — One of:
// - **Literal** — Contains no `*` or `?` characters.
// - **Wildcard** — Contains at least one `*` or `?` character.
// - **DoubleAsterisk** — The exact string `**`.
//
// **Character** — A single Unicode scalar value (codepoint). See Section 9 for the precise character-boundary requirements that apply during segment matching.
//
// **Segment kind** — One of:
// - **SegLiteral** — An exact literal substring.
// - **SegStar** — Matches zero or more characters excluding `/`.
// - **SegQuestion** — Matches exactly one character excluding `/`.
//
// **Pattern** — A compiled spec consisting of a component list, a usage mode, and a case-sensitivity flag.
//
// ---
//
// ## 2. Helper Predicates
//
// **IS_HIDDEN_PATH**(_component_)
//
// > 1. If the length of _component_ is 0, return **false**.
// > 2. If the first character of _component_ is `"."`, return **true**.
// > 3. Return **false**.
//
// **IS_PACKAGE_FOLDER**(_component_)
//
// > 1. If _component_ equals `"node_modules"` (case-insensitive), return **true**.
// > 2. If _component_ equals `"bower_components"` (case-insensitive), return **true**.
// > 3. If _component_ equals `"jspm_packages"` (case-insensitive), return **true**.
// > 4. Return **false**.
//
// **ENSURE_TRAILING_SLASH**(_s_)
//
// > 1. If the length of _s_ is 0, return _s_.
// > 2. If the last character of _s_ is `"/"`, return _s_.
// > 3. Return _s_ concatenated with `"/"`.
//
// **STRINGS_EQUAL**(_a_, _b_, _caseSensitive_)
//
// > 1. If _caseSensitive_ is **true**, return whether _a_ and _b_ are byte-for-byte identical.
// > 2. Return whether _a_ and _b_ are equal under Unicode case folding.
//
// **IS_IMPLICIT_GLOB**(_component_)
//
// > 1. If _component_ contains any of the characters `"."`, `"*"`, or `"?"`, return **false**.
// > 2. Return **true**.
//
// ---
//
// ## 3. Spec Normalization
//
// **NORMALIZE_SPEC**(_spec_, _basePath_)
//
// > 1. Let _components_ be the result of resolving _spec_ against _basePath_ into an ordered list of normalized path components. The first element is the absolute root prefix (e.g., `"/home"`). The resolution uses `/` as the path separator, resolves `.` and `..` segments, and collapses consecutive separators.
// > 2. If the last character of _components_\[0\] is `"/"`, remove it.
// > 3. Return _components_.
//
// ---
//
// ## 4. Segment Parsing
//
// **PARSE_SEGMENTS**(_string_)
//
// > 1. Let _segments_ be an empty list.
// > 2. Let _start_ be 0.
// > 3. For each index _i_ from 0 to the length of _string_ − 1:
// >    1. If _string_\[_i_\] is `"*"` or `"?"`, then:
// >       1. If _i_ > _start_, append a **SegLiteral** segment with value _string_\[_start_.._i_\] to _segments_.
// >       2. If _string_\[_i_\] is `"*"`, append a **SegStar** segment to _segments_.
// >       3. Otherwise, append a **SegQuestion** segment to _segments_.
// >       4. Set _start_ to _i_ + 1.
// > 4. If _start_ < length of _string_, append a **SegLiteral** segment with value _string_\[_start_..\] to _segments_.
// > 5. Return _segments_.
//
// ---
//
// ## 5. Pattern Compilation
//
// **COMPILE_PATTERN**(_spec_, _basePath_, _usage_, _caseSensitive_)
//
// > 1. Let _components_ be the result of NORMALIZE_SPEC(_spec_, _basePath_).
// > 2. If the last element of _components_ is `"**"` and _usage_ is not **Exclude**, return **failure**. (The pattern compiles to nothing.)
// > 3. If IS_IMPLICIT_GLOB(last element of _components_) is **true** (note: this check is applied to the _normalized_ component, not the raw spec string), then:
// >    1. Append `"**"` to _components_.
// >    2. Append `"*"` to _components_.
// > 4. Let _compiledComponents_ be an empty list.
// > 5. For each _part_ in _components_:
// >    1. If _part_ is `"**"`, append a **DoubleAsterisk** component to _compiledComponents_.
// >    2. Otherwise, if _part_ contains no `"*"` or `"?"` characters, append a **Literal** component with value _part_ to _compiledComponents_.
// >    3. Otherwise, append a **Wildcard** component with segments PARSE_SEGMENTS(_part_) to _compiledComponents_.
// > 6. Return a pattern with component list _compiledComponents_, usage mode _usage_, and case-sensitivity flag _caseSensitive_.
//
// ---
//
// ## 6. Path Component Extraction
//
// **NEXT_PATH_COMPONENT**(_path_, _offset_)
//
// > 1. If _offset_ ≥ length of _path_, return (**none**, _offset_, **false**).
// > 2. If _offset_ is 0 and _path_\[0\] is `"/"`, return (`""`, 1, **true**).
// > 3. While _offset_ < length of _path_ and _path_\[_offset_\] is `"/"`, increment _offset_.
// > 4. If _offset_ ≥ length of _path_, return (**none**, _offset_, **false**).
// > 5. Let _start_ be _offset_.
// > 6. While _offset_ < length of _path_ and _path_\[_offset_\] is not `"/"`, increment _offset_.
// > 7. Return (_path_\[_start_.._offset_\], _offset_, **true**).
//
// ---
//
// ## 7. Full-Path Matching
//
// **MATCH_PATH**(_pattern_, _path_)
//
// > 1. Return the result of MATCH_PATH_INNER(_pattern_, _path_, 0, 0, **false**).
//
// **MATCH_PATH_PREFIX**(_pattern_, _path_)
//
// > 1. Return the result of MATCH_PATH_INNER(_pattern_, _path_, 0, 0, **true**).
//
// **MATCH_PATH_INNER**(_pattern_, _path_, _pathOffset_, _compIdx_, _prefixOnly_)
//
// > 1. Let _components_ be the component list of _pattern_.
// > 2. Let _usage_ be the usage mode of _pattern_.
// > 3. Let _caseSensitive_ be the case-sensitivity flag of _pattern_.
// > 4. Loop:
// >    1. Let (_part_, _nextOffset_, _ok_) be the result of NEXT_PATH_COMPONENT(_path_, _pathOffset_).
// >    2. If _ok_ is **false**, then:
// >       1. If _prefixOnly_ is **true**, return **true**.
// >       2. Return the result of PATTERN_SATISFIED(_components_, _compIdx_).
// >    3. If _compIdx_ ≥ length of _components_, then:
// >       1. If _usage_ is **Exclude** and _prefixOnly_ is **false**, return **true**.
// >       2. Return **false**.
// >    4. Let _comp_ be _components_\[_compIdx_\].
// >    5. If the kind of _comp_ is **DoubleAsterisk**, then:
// >       1. Let _skipResult_ be the result of MATCH_PATH_INNER(_pattern_, _path_, _pathOffset_, _compIdx_ + 1, _prefixOnly_).
// >       2. If _skipResult_ is **true**, return **true**.
// >       3. If _usage_ is not **Exclude**, then:
// >          1. If IS_HIDDEN_PATH(_part_) is **true**, return **false**.
// >          2. If IS_PACKAGE_FOLDER(_part_) is **true**, return **false**.
// >       4. Set _pathOffset_ to _nextOffset_.
// >       5. Continue the loop.
// >    6. If the kind of _comp_ is **Literal**, then:
// >       1. If STRINGS_EQUAL(_comp_.value, _part_, _caseSensitive_) is **false**, return **false**.
// >    7. If the kind of _comp_ is **Wildcard**, then:
// >       1. If _usage_ is not **Exclude** and IS_PACKAGE_FOLDER(_part_) is **true**, return **false**.
// >       2. If the result of MATCH_WILDCARD(_pattern_, _comp_.segments, _part_) is **false**, return **false**.
// >    8. Set _pathOffset_ to _nextOffset_.
// >    9. Increment _compIdx_.
//
// **PATTERN_SATISFIED**(_components_, _compIdx_)
//
// > 1. For each index _i_ from _compIdx_ to length of _components_ − 1:
// >    1. If the kind of _components_\[_i_\] is not **DoubleAsterisk**, return **false**.
// > 2. Return **true**.
//
// ---
//
// ## 8. Wildcard Component Matching
//
// **MATCH_WILDCARD**(_pattern_, _segments_, _string_)
//
// > 1. Let _usage_ be the usage mode of _pattern_.
// > 2. Let _caseSensitive_ be the case-sensitivity flag of _pattern_.
// > 3. If _usage_ is not **Exclude**, then:
// >    1. If the length of _segments_ > 0, then:
// >       1. Let _firstKind_ be the kind of _segments_\[0\].
// >       2. If (_firstKind_ is **SegStar** or _firstKind_ is **SegQuestion**) and IS_HIDDEN_PATH(_string_) is **true**, return **false**.
// > 4. Let _matched_ be the result of MATCH_SEGMENTS(_segments_, _string_, _caseSensitive_).
// > 5. If _matched_ is **false**, return **false**.
// > 6. Let _accepted_ be the result of SHOULD_ACCEPT_MIN_JS(_pattern_, _segments_, _string_).
// > 7. Return _accepted_.
//
// ---
//
// ## 9. Segment Matching
//
// In this section, all string positions refer to **character** (codepoint) boundaries.
// Implementations must advance by full codepoints, not by encoding units (e.g., not
// by individual bytes in UTF-8, nor by individual code units in UTF-16). "Increment
// _sIdx_" means advance _sIdx_ past the next character (one codepoint). Likewise,
// "length of _s_" is the number of characters, and _s_\[_sIdx_\] is the character
// at position _sIdx_.
//
// The original TypeScript implementation uses ECMAScript regexes without the `u` flag,
// which operate on UTF-16 code units; a conforming implementation may match on
// codepoints instead, as the difference is only observable for supplementary-plane
// characters (U+10000 and above) in filenames.
//
// **MATCH_SEGMENTS**(_segments_, _s_, _caseSensitive_)
//
// > 1. Let _segIdx_ be 0.
// > 2. Let _sIdx_ be 0.
// > 3. Let _starSegIdx_ be −1.
// > 4. Let _starSIdx_ be 0.
// > 5. While _sIdx_ < length of _s_:
// >    1. If _segIdx_ < length of _segments_, then:
// >       1. Let _seg_ be _segments_\[_segIdx_\].
// >       2. If the kind of _seg_ is **SegLiteral**, then:
// >          1. Let _lit_ be the value of _seg_.
// >          2. If _sIdx_ + length of _lit_ ≤ length of _s_ and STRINGS_EQUAL(_lit_, _s_\[_sIdx_.._sIdx_+len(_lit_)\], _caseSensitive_) is **true**, then:
// >             1. Set _sIdx_ to _sIdx_ + length of _lit_.
// >             2. Increment _segIdx_.
// >             3. Continue the loop.
// >       3. If the kind of _seg_ is **SegQuestion**, then:
// >          1. If _s_\[_sIdx_\] is not `"/"`, then:
// >             1. Increment _sIdx_.
// >             2. Increment _segIdx_.
// >             3. Continue the loop.
// >       4. If the kind of _seg_ is **SegStar**, then:
// >          1. Set _starSegIdx_ to _segIdx_.
// >          2. Set _starSIdx_ to _sIdx_.
// >          3. Increment _segIdx_.
// >          4. Continue the loop.
// >    2. If _starSegIdx_ ≥ 0 and _starSIdx_ < length of _s_ and _s_\[_starSIdx_\] is not `"/"`, then:
// >       1. Increment _starSIdx_.
// >       2. Set _sIdx_ to _starSIdx_.
// >       3. Set _segIdx_ to _starSegIdx_ + 1.
// >       4. Continue the loop.
// >    3. Return **false**.
// > 6. While _segIdx_ < length of _segments_ and the kind of _segments_\[_segIdx_\] is **SegStar**:
// >    1. Increment _segIdx_.
// > 7. Return _segIdx_ ≥ length of _segments_.
//
// ---
//
// ## 10. `.min.js` Default Exclusion
//
// **SHOULD_ACCEPT_MIN_JS**(_pattern_, _segments_, _filename_)
//
// > 1. Let _usage_ be the usage mode of _pattern_.
// > 2. If _usage_ is not **Files**, return **true**.
// > 3. If the result of HAS_MIN_JS_SUFFIX(_filename_, _pattern_.caseSensitive) is **false**, return **true**.
// > 4. If the result of PATTERN_MENTIONS_MIN_SUFFIX(_segments_, _pattern_.caseSensitive) is **true**, return **true**.
// > 5. Return **false**.
//
// **HAS_MIN_JS_SUFFIX**(_filename_, _caseSensitive_)
//
// > 1. Let _suffix_ be `".min.js"`.
// > 2. If length of _filename_ < length of _suffix_, return **false**.
// > 3. Let _tail_ be the last 7 characters of _filename_.
// > 4. If _caseSensitive_ is **true**, return whether _tail_ equals `".min.js"`.
// > 5. Return whether _tail_ equals `".min.js"` under Unicode case folding.
//
// **PATTERN_MENTIONS_MIN_SUFFIX**(_segments_, _caseSensitive_)
//
// > 1. For each _seg_ in _segments_:
// >    1. If the kind of _seg_ is not **SegLiteral**, continue.
// >    2. Let _lit_ be the value of _seg_.
// >    3. If _caseSensitive_ is **false**, let _lit_ be the lowercase form of _lit_.
// >    4. If _lit_ contains the substring `".min.js"` or `".min."`, return **true**.
// > 2. Return **false**.
//
// ---
//
// ## 11. Composite Matchers
//
// **MATCH_FILE**(_path_, _includePatterns_, _excludePatterns_, _hadIncludes_)
//
// > 1. For each _pattern_ in _excludePatterns_:
// >    1. If the result of MATCH_PATH(_pattern_, _path_) is **true**, return (0, **false**).
// > 2. If length of _includePatterns_ is 0, then:
// >    1. If _hadIncludes_ is **true**, return (0, **false**).
// >    2. Return (0, **true**).
// > 3. For each index _i_ from 0 to length of _includePatterns_ − 1:
// >    1. If the result of MATCH_PATH(_includePatterns_\[_i_\], _path_) is **true**, return (_i_, **true**).
// > 4. Return (0, **false**).
//
// **MATCH_DIRECTORY**(_path_, _includePatterns_, _excludePatterns_, _hadIncludes_)
//
// > 1. For each _pattern_ in _excludePatterns_:
// >    1. If the result of MATCH_PATH(_pattern_, _path_) is **true**, return **false**.
// > 2. If length of _includePatterns_ is 0, then:
// >    1. If _hadIncludes_ is **true**, return **false**.
// >    2. Return **true**.
// > 3. For each _pattern_ in _includePatterns_:
// >    1. If the result of MATCH_PATH_PREFIX(_pattern_, _path_) is **true**, return **true**.
// > 4. Return **false**.
//
// **MATCH_SPEC**(_patterns_, _path_)
//
// > 1. For each _pattern_ in _patterns_:
// >    1. If the result of MATCH_PATH(_pattern_, _path_) is **true**, return **true**.
// > 2. Return **false**.
//
// **MATCH_SPEC_INDEX**(_patterns_, _path_)
//
// > 1. For each index _i_ from 0 to length of _patterns_ − 1:
// >    1. If the result of MATCH_PATH(_patterns_\[_i_\], _path_) is **true**, return _i_.
// > 2. Return −1.
//
// ---
//
// ## 12. Pattern Set Compilation
//
// **COMPILE_PATTERNS**(_specs_, _basePath_, _usage_, _caseSensitive_)
//
// > 1. Let _patterns_ be an empty list.
// > 2. For each _spec_ in _specs_:
// >    1. Let _result_ be the result of COMPILE_PATTERN(_spec_, _basePath_, _usage_, _caseSensitive_).
// >    2. If _result_ is not **failure**, append _result_ to _patterns_.
// > 3. Return _patterns_.
//
// **COMPILE_FILE_MATCHER**(_includeSpecs_, _excludeSpecs_, _basePath_, _caseSensitive_)
//
// > 1. Let _includePatterns_ be the result of COMPILE_PATTERNS(_includeSpecs_, _basePath_, **Files**, _caseSensitive_).
// > 2. Let _excludePatterns_ be the result of COMPILE_PATTERNS(_excludeSpecs_, _basePath_, **Exclude**, _caseSensitive_).
// > 3. Let _hadIncludes_ be whether length of _includeSpecs_ > 0.
// > 4. Return (_includePatterns_, _excludePatterns_, _hadIncludes_).
//
// **COMPILE_DIRECTORY_MATCHER**(_includeSpecs_, _excludeSpecs_, _basePath_, _caseSensitive_)
//
// > 1. Let _includePatterns_ be the result of COMPILE_PATTERNS(_includeSpecs_, _basePath_, **Directories**, _caseSensitive_).
// > 2. Let _excludePatterns_ be the result of COMPILE_PATTERNS(_excludeSpecs_, _basePath_, **Exclude**, _caseSensitive_).
// > 3. Let _hadIncludes_ be whether length of _includeSpecs_ > 0.
// > 4. Return (_includePatterns_, _excludePatterns_, _hadIncludes_).
//
// ---
//
// ## 13. Base Path Computation
//
// **GET_BASE_PATHS**(_rootPath_, _includeSpecs_, _caseSensitive_)
//
// > 1. Let _basePaths_ be a list containing _rootPath_.
// > 2. If _includeSpecs_ is empty, return _basePaths_.
// > 3. Let _includeBasePaths_ be an empty list.
// > 4. For each _spec_ in _includeSpecs_:
// >    1. Let _absolute_ be the result of resolving _spec_ to an absolute normalized path against _rootPath_.
// >    2. Let _basePath_ be GET_INCLUDE_BASE_PATH(_absolute_).
// >    3. Append _basePath_ to _includeBasePaths_.
// > 5. Sort _includeBasePaths_ using a string comparator that is case-insensitive if _caseSensitive_ is **false**.
// > 6. For each _candidate_ in _includeBasePaths_:
// >    1. If no element of _basePaths_ is a path-prefix of _candidate_ (respecting _caseSensitive_), append _candidate_ to _basePaths_.
// > 7. Return _basePaths_.
//
// **GET_INCLUDE_BASE_PATH**(_absoluteSpec_)
//
// > 1. Let _wildcardOffset_ be the index of the first `"*"` or `"?"` character in _absoluteSpec_.
// > 2. If _wildcardOffset_ < 0, then:
// >    1. If _absoluteSpec_ has a file extension (contains `"."`), return the parent directory of _absoluteSpec_.
// >    2. Return _absoluteSpec_.
// > 3. Return the substring of _absoluteSpec_ up to and including the last `"/"` before _wildcardOffset_.
//
// ---
//
// ## 14. Directory Traversal
//
// **READ_DIRECTORY**(_host_, _currentDir_, _path_, _extensions_, _excludeSpecs_, _includeSpecs_, _caseSensitive_, _depth_)
//
// The _host_ must provide the following operations:
// - **Realpath**(_path_) — Resolves symlinks and returns the canonical absolute path.
// - **GetAccessibleEntries**(_path_) — Returns the sorted lists of files and subdirectories in the directory at _path_.
//
// > 1. Let _path_ be the normalized form of _path_.
// > 2. Let _currentDir_ be the normalized form of _currentDir_.
// > 3. Let _absolutePath_ be the concatenation of _currentDir_, `"/"`, and _path_ (normalized).
// > 4. Let (_fileIncludes_, _fileExcludes_, _fileHadIncludes_) be the result of COMPILE_FILE_MATCHER(_includeSpecs_, _excludeSpecs_, _absolutePath_, _caseSensitive_).
// > 5. Let (_dirIncludes_, _dirExcludes_, _dirHadIncludes_) be the result of COMPILE_DIRECTORY_MATCHER(_includeSpecs_, _excludeSpecs_, _absolutePath_, _caseSensitive_).
// > 6. Let _resultBuckets_ be a list of empty lists, with length equal to max(length of _fileIncludes_, 1).
// > 7. Let _visited_ be an empty set of strings.
// > 8. Let _basePaths_ be the result of GET_BASE_PATHS(_path_, _includeSpecs_, _caseSensitive_).
// > 9. For each _basePath_ in _basePaths_:
// >    1. Let _baseAbsolute_ be the concatenation of _currentDir_, `"/"`, and _basePath_ (normalized).
// >    2. Perform VISIT(_host_, _basePath_, _baseAbsolute_, _depth_, _extensions_, _fileIncludes_, _fileExcludes_, _fileHadIncludes_, _dirIncludes_, _dirExcludes_, _dirHadIncludes_, _caseSensitive_, _visited_, _resultBuckets_).
// > 10. Return the concatenation of all lists in _resultBuckets_, in order.
//
// **VISIT**(_host_, _path_, _absolutePath_, _depth_, _extensions_, _fileIncludes_, _fileExcludes_, _fileHadIncludes_, _dirIncludes_, _dirExcludes_, _dirHadIncludes_, _caseSensitive_, _visited_, _resultBuckets_)
//
// > 1. Let _realPath_ be the result of _host_.Realpath(_absolutePath_).
// > 2. Let _canonicalPath_ be the canonical form of _realPath_ under the file system's case-sensitivity rules.
// > 3. If _visited_ contains _canonicalPath_, return.
// > 4. Add _canonicalPath_ to _visited_.
// > 5. Let _entries_ be the result of _host_.GetAccessibleEntries(_absolutePath_).
// > 6. Let _absPrefix_ be ENSURE_TRAILING_SLASH(_absolutePath_).
// > 7. Let _pathPrefix_ be ENSURE_TRAILING_SLASH(_path_).
// > 8. For each _file_ in _entries_.files:
// >    1. If _extensions_ is non-empty and the file extension of _file_ is not in _extensions_, continue.
// >    2. Let _absFile_ be _absPrefix_ concatenated with _file_.
// >    3. Let (_index_, _matched_) be the result of MATCH_FILE(_absFile_, _fileIncludes_, _fileExcludes_, _fileHadIncludes_).
// >    4. If _matched_ is **true**, append _pathPrefix_ concatenated with _file_ to _resultBuckets_\[_index_\].
// > 9. If _depth_ is finite (i.e., not the sentinel value representing unlimited depth), then:
// >    1. Decrement _depth_.
// >    2. If _depth_ is 0, return.
// > 10. For each _dir_ in _entries_.directories:
// >     1. Let _absDir_ be _absPrefix_ concatenated with _dir_.
// >     2. If the result of MATCH_DIRECTORY(_absDir_, _dirIncludes_, _dirExcludes_, _dirHadIncludes_) is **false**, continue.
// >     3. Perform VISIT(_host_, _pathPrefix_ concatenated with _dir_, _absDir_, _depth_, _extensions_, _fileIncludes_, _fileExcludes_, _fileHadIncludes_, _dirIncludes_, _dirExcludes_, _dirHadIncludes_, _caseSensitive_, _visited_, _resultBuckets_).
//
// ---
//
// ## 15. Invariants
//
// The following properties hold for all conforming implementations:
//
// 1. COMPILE_PATTERN returns **failure** for any include or directory spec whose last component is `"**"`.
// 2. When the pattern is exhausted but path components remain, exclude patterns return **true** and all other patterns return **false**.
// 3. MATCH_SEGMENTS is guaranteed **O(n·m)** where _n_ is the string length and _m_ is the segment count.
// 4. The `.min.js` default exclusion applies only under **Files** usage mode and only to wildcard components.
// 5. Symlink cycles are detected through real-path canonicalization in VISIT and cause the directory to be skipped.
// 6. Excludes are always evaluated before includes in MATCH_FILE and MATCH_DIRECTORY.
// 7. For include patterns, wildcard components reject package folders; literal components do not.
// 8. For include patterns, `**` does not descend into hidden paths or package folders.
// 9. For include patterns, a wildcard component whose first segment is **SegStar** or **SegQuestion** does not match hidden path components.
// =============================================================================

// Ported from tsgo internal/vfs/vfsmatch/vfsmatch.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
// This file implements the glob matching algorithm specified in MATCHING_ALGORITHM.md
// (reproduced verbatim above, per plan/06 §2.6). It is the production include/exclude
// wildcard engine that tsconfig include/exclude relies on.
//
// PORT-NOTES:
//  - core.Every / core.LastOrNil / core.Flatten are ported as local helpers below;
//    internal/core/core.go is a separate phase-1 work item (owned elsewhere), so these
//    live here until it lands. Their behavior mirrors the Go originals exactly.
//  - Positions are UTF-16 code units (repo-wide decision, plan/01). Go advanced ? and *
//    over runes via utf8.DecodeRuneInString; here StringUtil.DecodeRuneAt advances the same
//    rune boundary in UTF-16 units, so supplementary-plane filenames behave identically.
//  - slices.SortStableFunc → StableSort (index tie-break) to preserve stability.

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Vfs;
using ComparePathsOptions = Ts.TsPath.ComparePathsOptions;
using StrUtil = Ts.StringUtil.StringUtil; // Go: stringutil.* / strings.EqualFold etc.
using TsPathApi = Ts.TsPath.TsPath;       // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.Vfs.VfsMatch;

// vfsmatch.go:20-26 — Usage int8.
public enum Usage : sbyte
{
    Files = 0,
    Directories = 1,
    Exclude = 2,
}

// stringer_generated.go — stringer -type=Usage -trimprefix=Usage. Ported to a method
// rather than regenerated (plan/04 stringer 族); output is byte-identical to the generator.
public static class UsageExtensions
{
    private const string UsageName = "FilesDirectoriesExclude";
    private static readonly byte[] UsageIndex = { 0, 5, 16, 23 };

    public static string ToDisplayString(this Usage i)
    {
        int idx = (int)i;
        if (i < 0 || idx >= UsageIndex.Length - 1)
        {
            return "Usage(" + ((sbyte)i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
        return UsageName[UsageIndex[idx]..UsageIndex[idx + 1]];
    }
}

// vfsmatch.go:116-137 — componentKind / segmentKind.
internal enum ComponentKind
{
    Literal = 0,        // exact match (e.g., "src")
    Wildcard = 1,       // contains * or ? (e.g., "*.ts")
    DoubleAsterisk = 2, // ** matches zero or more directories
}

internal enum SegmentKind
{
    Literal = 0,  // exact text
    Star = 1,     // * matches any chars except /
    Question = 2, // ? matches single char except /
}

// vfsmatch.go:124-129 — segment is a piece of a wildcard component ("*.ts" -> [Star, Literal(".ts")]).
internal readonly struct Segment
{
    public readonly SegmentKind Kind;
    public readonly string Literal; // only for SegmentKind.Literal

    public Segment(SegmentKind kind, string literal)
    {
        Kind = kind;
        Literal = literal;
    }
}

// vfsmatch.go:106-114 — a single path segment in a glob pattern.
internal sealed class Component
{
    public readonly ComponentKind Kind;
    public readonly string Literal;             // for Literal: the exact string to match
    public readonly List<Segment>? Segments;    // for Wildcard: parsed wildcard pattern
    public readonly bool SkipPackageFolders;    // include-wildcard components skip node_modules, etc.

    public Component(ComponentKind kind, string literal, List<Segment>? segments, bool skipPackageFolders)
    {
        Kind = kind;
        Literal = literal;
        Segments = segments;
        SkipPackageFolders = skipPackageFolders;
    }
}

// vfsmatch.go:98-104 — a compiled glob pattern for matching file paths without regex.
internal sealed class GlobPattern
{
    private readonly List<Component> components;
    private readonly bool isExclude;
    private readonly bool caseSensitive;
    private readonly bool excludeMinJs;

    public GlobPattern(List<Component> components, bool isExclude, bool caseSensitive, bool excludeMinJs)
    {
        this.components = components;
        this.isExclude = isExclude;
        this.caseSensitive = caseSensitive;
        this.excludeMinJs = excludeMinJs;
    }

    // vfsmatch.go:218-221 — matches returns true if path matches this pattern.
    public bool Matches(string path)
    {
        return MatchPathParts(path, "", 0, 0, false);
    }

    // vfsmatch.go:223-227 — matchesParts returns true if prefix+suffix matches this pattern.
    public bool MatchesParts(string prefix, string suffix)
    {
        return MatchPathParts(prefix, suffix, 0, 0, false);
    }

    // vfsmatch.go:229-232 — matchesPrefixParts returns true if files under prefix+suffix could match.
    public bool MatchesPrefixParts(string prefix, string suffix)
    {
        return MatchPathParts(prefix, suffix, 0, 0, true);
    }

    // vfsmatch.go:234-280 — matchPathParts operates on a virtual path formed by prefix+suffix.
    private bool MatchPathParts(string prefix, string suffix, int pathOffset, int compIdx, bool prefixOnly)
    {
        while (true)
        {
            if (!VfsMatch.NextPathPartParts(prefix, suffix, pathOffset, out string pathPart, out int nextOffset))
            {
                if (prefixOnly)
                {
                    return true;
                }
                return PatternSatisfied(compIdx);
            }

            if (compIdx >= components.Count)
            {
                return isExclude && !prefixOnly;
            }

            var comp = components[compIdx];
            switch (comp.Kind)
            {
                case ComponentKind.DoubleAsterisk:
                    if (MatchPathParts(prefix, suffix, pathOffset, compIdx + 1, prefixOnly))
                    {
                        return true;
                    }
                    if (!isExclude && (VfsMatch.IsHiddenPath(pathPart) || VfsMatch.IsPackageFolder(pathPart)))
                    {
                        return false;
                    }
                    pathOffset = nextOffset;
                    continue;
                case ComponentKind.Literal:
                    if (comp.SkipPackageFolders && VfsMatch.IsPackageFolder(pathPart))
                    {
                        throw new InvalidOperationException("unreachable: literal components never have skipPackageFolders");
                    }
                    if (!StringsEqual(comp.Literal, pathPart))
                    {
                        return false;
                    }
                    break;
                case ComponentKind.Wildcard:
                    if (comp.SkipPackageFolders && VfsMatch.IsPackageFolder(pathPart))
                    {
                        return false;
                    }
                    if (!MatchWildcard(comp.Segments!, pathPart))
                    {
                        return false;
                    }
                    break;
            }

            pathOffset = nextOffset;
            compIdx++;
        }
    }

    // vfsmatch.go:282-292 — patternSatisfied checks if remaining pattern components can match empty input.
    private bool PatternSatisfied(int compIdx)
    {
        for (int i = compIdx; i < components.Count; i++)
        {
            if (components[i].Kind != ComponentKind.DoubleAsterisk)
            {
                return false;
            }
        }
        return true;
    }

    // vfsmatch.go:360-377 — matchWildcard matches a path component against wildcard segments.
    private bool MatchWildcard(List<Segment> segs, string s)
    {
        // Include patterns: wildcards at start cannot match hidden files
        if (!isExclude && segs.Count > 0 && VfsMatch.IsHiddenPath(s) && (segs[0].Kind == SegmentKind.Star || segs[0].Kind == SegmentKind.Question))
        {
            return false;
        }

        // Fast path: single * followed by literal suffix (e.g., "*.ts")
        if (segs.Count == 2 && segs[0].Kind == SegmentKind.Star && segs[1].Kind == SegmentKind.Literal)
        {
            string suffix = segs[1].Literal;
            if (s.Length < suffix.Length || !StringsEqual(suffix, s[(s.Length - suffix.Length)..]))
            {
                return false;
            }
            return ShouldIncludeMinJs(s, segs);
        }

        return MatchSegments(segs, s) && ShouldIncludeMinJs(s, segs);
    }

    // vfsmatch.go:379-431 — matchSegments matches segments against string s iteratively
    // (O(n*m), no exponential backtracking; only the last star position is tracked).
    private bool MatchSegments(List<Segment> segs, string s)
    {
        int segIdx = 0, sIdx = 0;
        int starSegIdx = -1, starSIdx = 0;

        while (sIdx < s.Length)
        {
            if (segIdx < segs.Count)
            {
                var seg = segs[segIdx];
                switch (seg.Kind)
                {
                    case SegmentKind.Literal:
                        int end = sIdx + seg.Literal.Length;
                        if (end <= s.Length && StringsEqual(seg.Literal, s[sIdx..end]))
                        {
                            sIdx = end;
                            segIdx++;
                            continue;
                        }
                        break;
                    case SegmentKind.Question:
                        if (s[sIdx] != '/')
                        {
                            int size = StrUtil.DecodeRuneAt(s, sIdx).Size;
                            sIdx += size;
                            segIdx++;
                            continue;
                        }
                        break;
                    case SegmentKind.Star:
                        // Record star position for backtracking, then try matching zero chars.
                        starSegIdx = segIdx;
                        starSIdx = sIdx;
                        segIdx++;
                        continue;
                }
            }

            // Current segment didn't match. Backtrack to last star if possible.
            if (starSegIdx >= 0 && starSIdx < s.Length && s[starSIdx] != '/')
            {
                // Star consumes one more character (rune), retry from segment after star.
                int size = StrUtil.DecodeRuneAt(s, starSIdx).Size;
                starSIdx += size;
                sIdx = starSIdx;
                segIdx = starSegIdx + 1;
                continue;
            }

            return false;
        }

        // Consume any trailing stars.
        while (segIdx < segs.Count && segs[segIdx].Kind == SegmentKind.Star)
        {
            segIdx++;
        }
        return segIdx >= segs.Count;
    }

    // vfsmatch.go:433-449 — shouldIncludeMinJs.
    private bool ShouldIncludeMinJs(string filename, List<Segment> segs)
    {
        if (!excludeMinJs)
        {
            return true;
        }

        // Preserve legacy behavior:
        // - When matching is case-sensitive, only the exact ".min.js" suffix is excluded by default.
        // - When matching is case-insensitive, any casing variant is excluded by default.
        if (!HasMinJsSuffix(filename))
        {
            return true;
        }
        // Allow when the user's pattern explicitly references the .min. suffix.
        if (PatternMentionsMinSuffix(segs))
        {
            return true;
        }
        return false;
    }

    // vfsmatch.go:451-461 — hasMinJsSuffix.
    private bool HasMinJsSuffix(string filename)
    {
        if (caseSensitive)
        {
            return filename.EndsWith(".min.js", StringComparison.Ordinal);
        }
        const string minJs = ".min.js";
        if (filename.Length < minJs.Length)
        {
            return false;
        }
        // Avoid allocating via strings.ToLower; compare suffix case-insensitively.
        return StrUtil.EqualFold(filename[(filename.Length - minJs.Length)..], minJs);
    }

    // vfsmatch.go:463-477 — patternMentionsMinSuffix.
    private bool PatternMentionsMinSuffix(List<Segment> segs)
    {
        foreach (var seg in segs)
        {
            if (seg.Kind != SegmentKind.Literal)
            {
                continue;
            }
            string lit = seg.Literal;
            if (!caseSensitive)
            {
                lit = VfsMatch.ToLower(lit);
            }
            if (lit.Contains(".min.js", StringComparison.Ordinal) || lit.Contains(".min.", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // vfsmatch.go:479-485 — stringsEqual compares strings with appropriate case sensitivity.
    private bool StringsEqual(string a, string b)
    {
        if (caseSensitive)
        {
            return a == b;
        }
        return StrUtil.EqualFold(a, b);
    }
}

// vfsmatch.go:512-577 — globMatcher combines include and exclude patterns for file matching.
internal sealed class GlobMatcher
{
    public readonly List<GlobPattern> Includes;
    public readonly List<GlobPattern> Excludes;
    public readonly bool HadIncludes; // true if include specs were provided (even if none compiled)

    // vfsmatch.go:519-537 — newGlobMatcher.
    public GlobMatcher(string[] includeSpecs, string[] excludeSpecs, string basePath, bool caseSensitive, Usage usage)
    {
        HadIncludes = includeSpecs.Length > 0;
        Includes = new List<GlobPattern>(includeSpecs.Length);
        Excludes = new List<GlobPattern>(excludeSpecs.Length);

        foreach (var spec in includeSpecs)
        {
            var p = VfsMatch.CompileGlobPattern(spec, basePath, usage, caseSensitive);
            if (p != null)
            {
                Includes.Add(p);
            }
        }
        foreach (var spec in excludeSpecs)
        {
            var p = VfsMatch.CompileGlobPattern(spec, basePath, Usage.Exclude, caseSensitive);
            if (p != null)
            {
                Excludes.Add(p);
            }
        }
    }

    // vfsmatch.go:539-559 — matchesFileParts. Returns the matching include index and true, or (0, false).
    public bool MatchesFileParts(string prefix, string suffix, out int index)
    {
        foreach (var e in Excludes)
        {
            if (e.MatchesParts(prefix, suffix))
            {
                index = 0;
                return false;
            }
        }
        if (Includes.Count == 0)
        {
            index = 0;
            return !HadIncludes;
        }
        for (int i = 0; i < Includes.Count; i++)
        {
            if (Includes[i].MatchesParts(prefix, suffix))
            {
                index = i;
                return true;
            }
        }
        index = 0;
        return false;
    }

    // vfsmatch.go:561-577 — matchesDirectoryParts.
    public bool MatchesDirectoryParts(string prefix, string suffix)
    {
        foreach (var e in Excludes)
        {
            if (e.MatchesParts(prefix, suffix))
            {
                return false;
            }
        }
        if (Includes.Count == 0)
        {
            return !HadIncludes;
        }
        foreach (var inc in Includes)
        {
            if (inc.MatchesPrefixParts(prefix, suffix))
            {
                return true;
            }
        }
        return false;
    }
}

// vfsmatch.go:579-646 — globVisitor traverses directories matching files against glob patterns.
internal sealed class GlobVisitor
{
    public required IFileSystem Host { get; init; }
    public required GlobMatcher FileMatcher { get; init; }
    public required GlobMatcher DirectoryMatcher { get; init; }
    public required string[] Extensions { get; init; }
    public required bool UseCaseSensitiveFileNames { get; init; }
    public required List<string>[] Results { get; init; }

    private readonly Set<string> visited = new();

    // vfsmatch.go:594-646 — visit walks a directory tree, collecting matching files.
    // resolvedRealPath, when non-empty, is the already-resolved real path for this directory
    // (computed incrementally from the parent). When empty, Realpath is called.
    public void Visit(string path, string absolutePath, int depth, string resolvedRealPath)
    {
        // Detect symlink cycles
        string realPath;
        if (resolvedRealPath != "")
        {
            realPath = resolvedRealPath;
        }
        else
        {
            realPath = Host.Realpath(absolutePath);
        }
        string canonicalPath = TsPathApi.GetCanonicalFileName(realPath, UseCaseSensitiveFileNames);
        if (visited.Has(canonicalPath))
        {
            return;
        }
        visited.Add(canonicalPath);

        var entries = Host.GetAccessibleEntries(absolutePath);

        string pathPrefix = VfsMatch.EnsureTrailingSlash(path);
        string absPrefix = VfsMatch.EnsureTrailingSlash(absolutePath);

        if (entries.Files != null)
        {
            foreach (var file in entries.Files)
            {
                if (Extensions.Length > 0 && !TsPathApi.FileExtensionIsOneOf(file, Extensions))
                {
                    continue;
                }
                if (FileMatcher.MatchesFileParts(absPrefix, file, out int idx))
                {
                    Results[idx].Add(pathPrefix + file);
                }
            }
        }

        if (depth != VfsMatch.UnlimitedDepth)
        {
            depth--;
            if (depth == 0)
            {
                return;
            }
        }

        if (entries.Directories != null)
        {
            foreach (var dir in entries.Directories)
            {
                if (!DirectoryMatcher.MatchesDirectoryParts(absPrefix, dir))
                {
                    continue;
                }
                string absDir = absPrefix + dir;
                string childRealPath = "";
                if (entries.Symlinks != null)
                {
                    if (!entries.Symlinks.Contains(dir))
                    {
                        // Non-symlink directory: compute realpath incrementally.
                        childRealPath = TsPathApi.CombinePaths(realPath, dir);
                    }
                    // else: symlink directory; leave childRealPath empty to force Realpath call.
                }
                // If Symlinks is null, the FS doesn't track symlinks; leave childRealPath empty
                // to call Realpath (preserving old behavior).
                Visit(pathPrefix + dir, absDir, depth, childRealPath);
            }
        }
    }
}

// vfsmatch.go:676-717 — SpecMatcher wraps multiple glob patterns for matching paths.
public sealed class SpecMatcher
{
    private readonly List<GlobPattern> patterns;

    internal SpecMatcher(List<GlobPattern> patterns)
    {
        this.patterns = patterns;
    }

    // vfsmatch.go:681-689 — MatchString returns true if any pattern matches the path.
    public bool MatchString(string path)
    {
        foreach (var p in patterns)
        {
            if (p.Matches(path))
            {
                return true;
            }
        }
        return false;
    }

    // vfsmatch.go:691-699 — MatchIndex returns the index of the first matching pattern, or -1.
    public int MatchIndex(string path)
    {
        for (int i = 0; i < patterns.Count; i++)
        {
            if (patterns[i].Matches(path))
            {
                return i;
            }
        }
        return -1;
    }
}

// Package-level funcs of vfsmatch.go (naming per the VfsUtil/IoVfsUtil precedent).
public static class VfsMatch
{
    // vfsmatch.go:28-29 — UnlimitedDepth. PORT-NOTE: Go's math.MaxInt is the platform int max;
    // in the 32-bit C# int world int.MaxValue is the equivalent unreachable-depth sentinel.
    public const int UnlimitedDepth = int.MaxValue;

    // vfsmatch.go:42 — wildcardCharCodes.
    private static readonly char[] WildcardChars = { '*', '?' };

    // vfsmatch.go:38-40 — IsImplicitGlob checks the ".*?" set.
    private static readonly char[] ImplicitGlobChars = { '.', '*', '?' };

    // vfsmatch.go:31-33 — ReadDirectory.
    public static List<string> ReadDirectory(IFileSystem host, string currentDir, string path, string[]? extensions, string[]? excludes, string[]? includes, int depth)
    {
        return MatchFiles(path, extensions ?? [], excludes ?? [], includes ?? [], host.UseCaseSensitiveFileNames(), currentDir, depth, host);
    }

    // vfsmatch.go:35-40 — IsImplicitGlob. An "includes" path "foo" is implicitly "foo/**/*"
    // if its last component has no extension and no glob characters.
    public static bool IsImplicitGlob(string lastPathComponent)
    {
        return lastPathComponent.IndexOfAny(ImplicitGlobChars) < 0;
    }

    // vfsmatch.go:701-717 — NewSpecMatcher creates a matcher for one or more glob specs.
    public static SpecMatcher? NewSpecMatcher(string[]? specs, string basePath, Usage usage, bool useCaseSensitiveFileNames)
    {
        if (specs == null || specs.Length == 0)
        {
            return null;
        }
        var patterns = new List<GlobPattern>(specs.Length);
        foreach (var spec in specs)
        {
            var p = CompileGlobPattern(spec, basePath, usage, useCaseSensitiveFileNames);
            if (p != null)
            {
                patterns.Add(p);
            }
        }
        if (patterns.Count == 0)
        {
            return null;
        }
        return new SpecMatcher(patterns);
    }

    // vfsmatch.go:44-55 — getIncludeBasePath.
    private static string GetIncludeBasePath(string absolute)
    {
        int wildcardOffset = absolute.IndexOfAny(WildcardChars);
        if (wildcardOffset < 0)
        {
            // No "*" or "?" in the path
            if (!TsPathApi.HasExtension(absolute))
            {
                return absolute;
            }
            return TsPathApi.RemoveTrailingDirectorySeparator(TsPathApi.GetDirectoryPath(absolute));
        }
        return absolute[..Math.Max(absolute[..wildcardOffset].LastIndexOf('/'), 0)];
    }

    // vfsmatch.go:57-96 — getBasePaths computes the unique non-wildcard base paths.
    // (internal for white-box tests, which call it directly.)
    internal static List<string> GetBasePaths(string path, string[] includes, bool useCaseSensitiveFileNames)
    {
        // Storage for our results in the form of literal paths (e.g. the paths as written by the user).
        var basePaths = new List<string> { path };

        if (includes.Length > 0)
        {
            var comparePathsOptions = new ComparePathsOptions { CurrentDirectory = path, UseCaseSensitiveFileNames = useCaseSensitiveFileNames };
            var stringComparer = comparePathsOptions.GetComparer();

            // Storage for literal base paths amongst the include patterns.
            var includeBasePaths = new List<string>();
            foreach (var include in includes)
            {
                // We also need to check the relative paths by converting them to absolute and
                // normalizing in case they escape the base path (e.g "..\somedirectory").
                string absolute;
                if (TsPathApi.IsRootedDiskPath(include))
                {
                    absolute = include;
                }
                else
                {
                    absolute = TsPathApi.NormalizePath(TsPathApi.CombinePaths(path, include));
                }
                // Append the literal and canonical candidate base paths.
                includeBasePaths.Add(GetIncludeBasePath(absolute));
            }

            // Sort the offsets array using either the literal or canonical path representations.
            StableSort(includeBasePaths, stringComparer);

            // Iterate over each include base path and include unique base paths that are not a
            // subpath of an existing base path
            foreach (var includeBasePath in includeBasePaths)
            {
                if (Every(basePaths, basepath => !TsPathApi.ContainsPath(basepath, includeBasePath, comparePathsOptions)))
                {
                    basePaths.Add(includeBasePath);
                }
            }
        }

        return basePaths;
    }

    // vfsmatch.go:139-169 — compileGlobPattern. Returns null if the pattern would match nothing.
    internal static GlobPattern? CompileGlobPattern(string spec, string basePath, Usage usage, bool caseSensitive)
    {
        var parts = TsPathApi.GetNormalizedPathComponents(spec, basePath);

        // "src/**" without a filename matches nothing (for include patterns)
        if (usage != Usage.Exclude && LastOrNil(parts) == "**")
        {
            return null;
        }

        // Normalize root: "/home/" -> "/home"
        parts[0] = TsPathApi.RemoveTrailingDirectorySeparator(parts[0]);

        // Directories implicitly match all files: "src" -> "src/**/*"
        if (IsImplicitGlob(LastOrNil(parts)))
        {
            parts.Add("**");
            parts.Add("*");
        }

        // Avoid slice growth during compilation.
        var components = new List<Component>(parts.Count);
        foreach (var part in parts)
        {
            components.Add(ParseComponent(part, usage != Usage.Exclude));
        }
        return new GlobPattern(components, usage == Usage.Exclude, caseSensitive, usage == Usage.Files);
    }

    // vfsmatch.go:171-184 — parseComponent converts a path segment string into a component.
    private static Component ParseComponent(string s, bool isInclude)
    {
        if (s == "**")
        {
            return new Component(ComponentKind.DoubleAsterisk, "", null, false);
        }
        if (s.IndexOfAny(WildcardChars) < 0)
        {
            return new Component(ComponentKind.Literal, s, null, false);
        }
        return new Component(ComponentKind.Wildcard, "", ParseSegments(s), isInclude);
    }

    // vfsmatch.go:186-216 — parseSegments breaks "*.ts" into [Star, Literal(".ts")].
    private static List<Segment> ParseSegments(string s)
    {
        // Preallocate based on wildcard count.
        int wildcards = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '*' || s[i] == '?')
            {
                wildcards++;
            }
        }
        var result = new List<Segment>(2 * wildcards + 1);
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '*' || ch == '?')
            {
                if (i > start)
                {
                    result.Add(new Segment(SegmentKind.Literal, s[start..i]));
                }
                if (ch == '*')
                {
                    result.Add(new Segment(SegmentKind.Star, ""));
                }
                else
                {
                    result.Add(new Segment(SegmentKind.Question, ""));
                }
                start = i + 1;
            }
        }
        if (start < s.Length)
        {
            result.Add(new Segment(SegmentKind.Literal, s[start..]));
        }
        return result;
    }

    // vfsmatch.go:294-313 — nextPathPartSingle extracts the next path component from s at offset.
    internal static bool NextPathPartSingle(string s, int offset, out string part, out int nextOffset)
    {
        if (offset >= s.Length)
        {
            part = "";
            nextOffset = offset;
            return false;
        }
        if (offset == 0 && s.Length > 0 && s[0] == '/')
        {
            part = "";
            nextOffset = 1;
            return true;
        }
        while (offset < s.Length && s[offset] == '/')
        {
            offset++;
        }
        if (offset >= s.Length)
        {
            part = "";
            nextOffset = offset;
            return false;
        }
        string rest = s[offset..];
        int idx = rest.IndexOf('/');
        if (idx >= 0)
        {
            part = rest[..idx];
            nextOffset = offset + idx;
            return true;
        }
        part = rest;
        nextOffset = s.Length;
        return true;
    }

    // vfsmatch.go:315-358 — nextPathPartParts scans a virtual prefix+suffix.
    internal static bool NextPathPartParts(string prefix, string suffix, int offset, out string part, out int nextOffset)
    {
        // Fast paths: keep the hot single-string scan tight.
        if (suffix.Length == 0)
        {
            return NextPathPartSingle(prefix, offset, out part, out nextOffset);
        }
        if (prefix.Length == 0)
        {
            return NextPathPartSingle(suffix, offset, out part, out nextOffset);
        }

        // For matchFilesNoRegex call sites, prefix is a directory path ending in '/',
        // and suffix is a single entry name (no '/').
        int totalLen = prefix.Length + suffix.Length;
        if (offset >= totalLen)
        {
            part = "";
            nextOffset = offset;
            return false;
        }

        // Handle leading slash (root of absolute path)
        if (offset == 0 && prefix[0] == '/')
        {
            part = "";
            nextOffset = 1;
            return true;
        }

        // Scan within prefix.
        if (offset < prefix.Length)
        {
            while (offset < prefix.Length && prefix[offset] == '/')
            {
                offset++;
            }
            if (offset < prefix.Length)
            {
                string rest = prefix[offset..];
                int idx = rest.IndexOf('/');
                // idx is guaranteed >= 0 for the call sites we care about because prefix ends in '/'.
                part = rest[..idx];
                nextOffset = offset + idx;
                return true;
            }
            // Fall through into suffix region.
        }

        // Scan suffix: it's a single component.
        int sOff = offset - prefix.Length;
        if (sOff >= suffix.Length)
        {
            part = "";
            nextOffset = offset;
            return false;
        }
        part = suffix[sOff..];
        nextOffset = totalLen;
        return true;
    }

    // vfsmatch.go:487-490 — isHiddenPath checks if a path component is hidden (starts with dot).
    internal static bool IsHiddenPath(string name)
    {
        return name.Length > 0 && name[0] == '.';
    }

    // vfsmatch.go:492-503 — isPackageFolder checks for a common package folder (node_modules, etc.).
    internal static bool IsPackageFolder(string name)
    {
        switch (name.Length)
        {
            case 12: // len("node_modules")
                return StrUtil.EqualFold(name, "node_modules");
            case 13: // len("jspm_packages")
                return StrUtil.EqualFold(name, "jspm_packages");
            case 16: // len("bower_components")
                return StrUtil.EqualFold(name, "bower_components");
        }
        return false;
    }

    // vfsmatch.go:505-510 — ensureTrailingSlash.
    internal static string EnsureTrailingSlash(string s)
    {
        if (s.Length > 0 && s[^1] != '/')
        {
            return s + "/";
        }
        return s;
    }

    // vfsmatch.go:648-674 — matchFiles. (internal for white-box tests, which call it directly.)
    internal static List<string> MatchFiles(string path, string[]? extensions, string[]? excludes, string[]? includes, bool useCaseSensitiveFileNames, string currentDirectory, int depth, IFileSystem host)
    {
        extensions ??= [];
        excludes ??= [];
        includes ??= [];
        path = TsPathApi.NormalizePath(path);
        currentDirectory = TsPathApi.NormalizePath(currentDirectory);
        string absolutePath = TsPathApi.CombinePaths(currentDirectory, path);

        var fileMatcher = new GlobMatcher(includes, excludes, absolutePath, useCaseSensitiveFileNames, Usage.Files);
        var directoryMatcher = new GlobMatcher(includes, excludes, absolutePath, useCaseSensitiveFileNames, Usage.Directories);

        int bucketCount = Math.Max(fileMatcher.Includes.Count, 1);
        var results = new List<string>[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            results[i] = new List<string>();
        }

        var v = new GlobVisitor
        {
            Host = host,
            FileMatcher = fileMatcher,
            DirectoryMatcher = directoryMatcher,
            Extensions = extensions,
            UseCaseSensitiveFileNames = useCaseSensitiveFileNames,
            Results = results,
        };

        foreach (var basePath in GetBasePaths(path, includes, useCaseSensitiveFileNames))
        {
            v.Visit(basePath, TsPathApi.CombinePaths(currentDirectory, basePath), depth, "");
        }

        // Fast path: a single include bucket (or no includes) doesn't need flattening.
        if (results.Length == 1)
        {
            return results[0];
        }
        return Flatten(results);
    }

    // Go: strings.ToLower — Unicode lowercase mapping (used only for the .min. detection).
    internal static string ToLower(string s)
    {
        return StrUtil.MapRunes(static r => StrUtil.ToLowerRune(r), s);
    }

    // Go: slices.SortStableFunc — stable sort with an index tie-break.
    private static void StableSort(List<string> list, Func<string, string, int> cmp)
    {
        var indexed = new (string Val, int Idx)[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            indexed[i] = (list[i], i);
        }
        Array.Sort(indexed, (a, b) =>
        {
            int c = cmp(a.Val, b.Val);
            return c != 0 ? c : a.Idx.CompareTo(b.Idx);
        });
        for (int i = 0; i < list.Count; i++)
        {
            list[i] = indexed[i].Val;
        }
    }

    // PORT-NOTE: local ports of core.Every / core.LastOrNil / core.Flatten
    // (internal/core/core.go:199-207, 264-269, 502-508). core.go is owned by another
    // phase-1 work item; these mirror the Go originals exactly and should be redirected to
    // Ts.Core.CoreUtil once that port lands.
    private static bool Every<T>(List<T> slice, Func<T, bool> f)
    {
        foreach (var value in slice)
        {
            if (!f(value))
            {
                return false;
            }
        }
        return true;
    }

    private static string LastOrNil(List<string> slice)
    {
        return slice.Count != 0 ? slice[^1] : "";
    }

    private static List<string> Flatten(List<string>[] array)
    {
        var result = new List<string>();
        foreach (var subArray in array)
        {
            result.AddRange(subArray);
        }
        return result;
    }
}

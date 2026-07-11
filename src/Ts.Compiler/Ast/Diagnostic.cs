using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;

namespace Ts.Ast;

// Go: internal/ast/diagnostic.go — the ast-level Diagnostic type shared by the scanner
// error callback and the parser (plan/06 §4.1). `code` is Go int32 → C# int; Go
// `diagnostics.Key` is `type Key string` → C# string.
//
// SCOPE: Diagnostic.Localize / Diagnostic.String (diagnostic.go:101-108) depend on the
// `locale` package and diagnostics.Localize, both deferred to phase 9 (see
// Diagnostics/Diagnostics.cs). They are omitted here until that surface lands.

// diagnostic.go:14-20 — RepopulateDiagnosticKind indicates the kind of repopulation for a
// diagnostic chain entry.
public enum RepopulateDiagnosticKind
{
    RepopulateModeMismatch = 1,
    RepopulateModuleNotFound = 2,
}

// diagnostic.go:22-29 — RepopulateDiagnosticInfo stores information needed to recompute a
// diagnostic chain entry during incremental builds when the program state may have changed.
public sealed class RepopulateDiagnosticInfo
{
    public RepopulateDiagnosticKind Kind;
    public string ModuleReference = "";
    public ResolutionMode Mode;
    public string PackageName = "";
}

// diagnostic.go:33-48
public sealed class Diagnostic
{
    private SourceFile? file;
    private TextRange loc;
    private int code;
    private Category category;
    // Original message; may be nil. (internal: read by the deferred Localize/String
    // surface once locale lands, phase 9.)
    internal Message? message;
    private string messageKey = "";
    private string[]? messageArgs;
    private Diagnostic[]? messageChain;
    private Diagnostic[]? relatedInformation;
    private bool reportsUnnecessary;
    private bool reportsDeprecated;
    private bool skippedOnNoEmit;
    private RepopulateDiagnosticInfo? repopulateInfo;

    public SourceFile? File() => file; // diagnostic.go:50
    public int Pos() => loc.Pos; // diagnostic.go:51
    public int End() => loc.End; // diagnostic.go:52
    public int Len() => loc.Len; // diagnostic.go:53
    public TextRange Loc() => loc; // diagnostic.go:54
    public int Code() => code; // diagnostic.go:55
    public Category Category() => category; // diagnostic.go:56
    public string MessageKey() => messageKey; // diagnostic.go:57
    public string[]? MessageArgs() => messageArgs; // diagnostic.go:58
    public Diagnostic[]? MessageChain() => messageChain; // diagnostic.go:59
    public Diagnostic[]? RelatedInformation() => relatedInformation; // diagnostic.go:60
    public bool ReportsUnnecessary() => reportsUnnecessary; // diagnostic.go:61
    public bool ReportsDeprecated() => reportsDeprecated; // diagnostic.go:62
    public bool SkippedOnNoEmit() => skippedOnNoEmit; // diagnostic.go:63
    public RepopulateDiagnosticInfo? RepopulateInfo() => repopulateInfo; // diagnostic.go:64

    public void SetFile(SourceFile? file) => this.file = file; // diagnostic.go:66
    public void SetLocation(TextRange loc) => this.loc = loc; // diagnostic.go:67
    public void SetCategory(Category category) => this.category = category; // diagnostic.go:68
    public void SetSkippedOnNoEmit() => skippedOnNoEmit = true; // diagnostic.go:69
    public void SetRepopulateInfo(RepopulateDiagnosticInfo? info) => repopulateInfo = info; // diagnostic.go:70

    // diagnostic.go:72-75
    public Diagnostic SetMessageChain(Diagnostic[]? messageChain)
    {
        this.messageChain = messageChain;
        return this;
    }

    // diagnostic.go:77-82
    public Diagnostic AddMessageChain(Diagnostic? messageChain)
    {
        if (messageChain != null)
        {
            this.messageChain = [.. this.messageChain ?? [], messageChain];
        }
        return this;
    }

    // diagnostic.go:84-87
    public Diagnostic SetRelatedInfo(Diagnostic[]? relatedInformation)
    {
        this.relatedInformation = relatedInformation;
        return this;
    }

    // diagnostic.go:89-94
    public Diagnostic AddRelatedInfo(Diagnostic? relatedInformation)
    {
        if (relatedInformation != null)
        {
            this.relatedInformation = [.. this.relatedInformation ?? [], relatedInformation];
        }
        return this;
    }

    // diagnostic.go:96-99 — shallow copy (`result := *d`).
    public Diagnostic Clone()
    {
        return (Diagnostic)MemberwiseClone();
    }

    // diagnostic.go:110-136
    public static Diagnostic NewDiagnosticFromSerialized(
        SourceFile? file,
        TextRange loc,
        int code,
        Category category,
        string messageKey,
        string[]? messageArgs,
        Diagnostic[]? messageChain,
        Diagnostic[]? relatedInformation,
        bool reportsUnnecessary,
        bool reportsDeprecated,
        bool skippedOnNoEmit)
    {
        return new Diagnostic
        {
            file = file,
            loc = loc,
            code = code,
            category = category,
            messageKey = messageKey,
            messageArgs = messageArgs,
            messageChain = messageChain,
            relatedInformation = relatedInformation,
            reportsUnnecessary = reportsUnnecessary,
            reportsDeprecated = reportsDeprecated,
            skippedOnNoEmit = skippedOnNoEmit,
        };
    }

    // diagnostic.go:138-150
    public static Diagnostic NewDiagnostic(SourceFile? file, TextRange loc, Message message, params object?[] args)
    {
        return new Diagnostic
        {
            file = file,
            loc = loc,
            code = message.Code,
            category = message.Category,
            message = message,
            messageKey = message.Key,
            messageArgs = StringifyArgs(args),
            reportsUnnecessary = message.ReportsUnnecessary,
            reportsDeprecated = message.ReportsDeprecated,
        };
    }

    // diagnostic.go:152-157
    public static Diagnostic NewDiagnosticChain(Diagnostic? chain, Message message, params object?[] args)
    {
        if (chain != null)
        {
            return NewDiagnostic(chain.file, chain.loc, message, args).AddMessageChain(chain).SetRelatedInfo(chain.relatedInformation);
        }
        return NewDiagnostic(null, new TextRange(), message, args);
    }

    // diagnostic.go:159-161
    public static Diagnostic NewCompilerDiagnostic(Message message, params object?[] args)
    {
        return NewDiagnostic(null, TextRange.Undefined, message, args);
    }

    // PORT-NOTE: local mirror of diagnostics.StringifyArgs (diagnostics.go:136-150), which
    // is deferred to phase 9 in Ts.Diagnostics. Go's `fmt.Sprintf("%v", arg)` fallback
    // differs from C# ToString for a few types (e.g. bool casing); AST/parser diagnostic
    // args are strings/numbers in practice. Switches to Ts.Diagnostics.StringifyArgs when
    // that lands. (plan/06 §2.2, AGENTS.md §6 deviation registration.)
    private static string[]? StringifyArgs(object?[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }
        var result = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is string s)
            {
                result[i] = s;
            }
            else
            {
                result[i] = args[i]?.ToString() ?? "";
            }
        }
        return result;
    }
}

// diagnostic.go:163-170
public sealed class DiagnosticsCollection
{
    private readonly Lock mu = new();
    private int count;
    private Dictionary<string, List<Diagnostic>>? fileDiagnostics;
    private readonly Set<string> fileDiagnosticsSorted = new();
    private List<Diagnostic>? nonFileDiagnostics;
    private bool nonFileDiagnosticsSorted;

    // diagnostic.go:172-189
    public void Add(Diagnostic diagnostic)
    {
        lock (mu)
        {
            count++;

            if (diagnostic.File() != null)
            {
                var fileName = diagnostic.File()!.FileName();
                fileDiagnostics ??= new Dictionary<string, List<Diagnostic>>();
                if (!fileDiagnostics.TryGetValue(fileName, out var list))
                {
                    list = new List<Diagnostic>();
                    fileDiagnostics[fileName] = list;
                }
                list.Add(diagnostic);
                fileDiagnosticsSorted.Delete(fileName);
            }
            else
            {
                nonFileDiagnostics ??= new List<Diagnostic>();
                nonFileDiagnostics.Add(diagnostic);
                nonFileDiagnosticsSorted = false;
            }
        }
    }

    // diagnostic.go:191-205
    public Diagnostic? Lookup(Diagnostic diagnostic)
    {
        lock (mu)
        {
            Diagnostic[] diagnostics;
            if (diagnostic.File() != null)
            {
                diagnostics = GetDiagnosticsForFileLocked(diagnostic.File()!.FileName());
            }
            else
            {
                diagnostics = GetGlobalDiagnosticsLocked();
            }
            var (i, ok) = BinarySearchFunc(diagnostics, diagnostic, CompareDiagnostics);
            if (ok)
            {
                return diagnostics[i];
            }
            return null;
        }
    }

    // diagnostic.go:207-212
    public Diagnostic[] GetGlobalDiagnostics()
    {
        lock (mu)
        {
            return GetGlobalDiagnosticsLocked();
        }
    }

    // diagnostic.go:214-220
    private Diagnostic[] GetGlobalDiagnosticsLocked()
    {
        if (!nonFileDiagnosticsSorted)
        {
            SortStableFunc(ref nonFileDiagnostics, CompareDiagnostics);
            nonFileDiagnosticsSorted = true;
        }
        return Clone(nonFileDiagnostics);
    }

    // diagnostic.go:222-227
    public Diagnostic[] GetDiagnosticsForFile(string fileName)
    {
        lock (mu)
        {
            return GetDiagnosticsForFileLocked(fileName);
        }
    }

    // diagnostic.go:229-235
    private Diagnostic[] GetDiagnosticsForFileLocked(string fileName)
    {
        if (!fileDiagnosticsSorted.Has(fileName))
        {
            List<Diagnostic>? list = fileDiagnostics != null ? fileDiagnostics.GetValueOrDefault(fileName) : null;
            SortStableFunc(ref list, CompareDiagnostics);
            fileDiagnosticsSorted.Add(fileName);
        }
        return Clone(fileDiagnostics != null ? fileDiagnostics.GetValueOrDefault(fileName) : null);
    }

    // diagnostic.go:237-248
    public Diagnostic[] GetDiagnostics()
    {
        lock (mu)
        {
            var diagnostics = new List<Diagnostic>(count);
            if (nonFileDiagnostics != null)
            {
                diagnostics.AddRange(nonFileDiagnostics);
            }
            if (fileDiagnostics != null)
            {
                // map-iter: 序无关 —— all file diagnostic lists are concatenated then re-sorted below.
                foreach (var diags in fileDiagnostics.Values)
                {
                    diagnostics.AddRange(diags);
                }
            }
            diagnostics.Sort((a, b) => CompareDiagnostics(a, b));
            return diagnostics.ToArray();
        }
    }

    // diagnostic.go:250-255
    private static string GetDiagnosticPath(Diagnostic d)
    {
        if (d.File() != null)
        {
            return d.File()!.FileName();
        }
        return "";
    }

    // diagnostic.go:257-263
    public static bool EqualDiagnostics(Diagnostic d1, Diagnostic d2)
    {
        if (d1 == d2)
        {
            return true;
        }
        return EqualDiagnosticsNoRelatedInfo(d1, d2) &&
            SlicesEqualFunc(d1.RelatedInformation(), d2.RelatedInformation(), EqualDiagnostics);
    }

    // diagnostic.go:265-274
    public static bool EqualDiagnosticsNoRelatedInfo(Diagnostic d1, Diagnostic d2)
    {
        if (d1 == d2)
        {
            return true;
        }
        return GetDiagnosticPath(d1) == GetDiagnosticPath(d2) &&
            d1.Loc() == d2.Loc() &&
            d1.Code() == d2.Code() &&
            SlicesEqual(d1.MessageArgs(), d2.MessageArgs()) &&
            SlicesEqualFunc(d1.MessageChain(), d2.MessageChain(), EqualMessageChain);
    }

    // diagnostic.go:276-283
    private static bool EqualMessageChain(Diagnostic c1, Diagnostic c2)
    {
        if (c1 == c2)
        {
            return true;
        }
        return c1.Code() == c2.Code() &&
            SlicesEqual(c1.MessageArgs(), c2.MessageArgs()) &&
            SlicesEqualFunc(c1.MessageChain(), c2.MessageChain(), EqualMessageChain);
    }

    // diagnostic.go:285-297
    private static int CompareMessageChainSize(Diagnostic[]? c1, Diagnostic[]? c2)
    {
        int c = (c2?.Length ?? 0) - (c1?.Length ?? 0);
        if (c != 0)
        {
            return c;
        }
        for (int i = 0; i < (c1?.Length ?? 0); i++)
        {
            c = CompareMessageChainSize(c1![i].MessageChain(), c2![i].MessageChain());
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    // diagnostic.go:299-313
    private static int CompareMessageChainContent(Diagnostic[]? c1, Diagnostic[]? c2)
    {
        for (int i = 0; i < (c1?.Length ?? 0); i++)
        {
            int c = CompareStringSlices(c1![i].MessageArgs(), c2![i].MessageArgs());
            if (c != 0)
            {
                return c;
            }
            if (c1[i].MessageChain() != null)
            {
                c = CompareMessageChainContent(c1[i].MessageChain(), c2![i].MessageChain());
                if (c != 0)
                {
                    return c;
                }
            }
        }
        return 0;
    }

    // diagnostic.go:315-327
    private static int CompareRelatedInfo(Diagnostic[]? r1, Diagnostic[]? r2)
    {
        int c = (r2?.Length ?? 0) - (r1?.Length ?? 0);
        if (c != 0)
        {
            return c;
        }
        for (int i = 0; i < (r1?.Length ?? 0); i++)
        {
            c = CompareDiagnostics(r1![i], r2![i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    // diagnostic.go:329-362
    public static int CompareDiagnostics(Diagnostic d1, Diagnostic d2)
    {
        if (d1 == d2)
        {
            return 0;
        }
        int c = string.CompareOrdinal(GetDiagnosticPath(d1), GetDiagnosticPath(d2));
        if (c != 0)
        {
            return c;
        }
        c = d1.Loc().Pos - d2.Loc().Pos;
        if (c != 0)
        {
            return c;
        }
        c = d1.Loc().End - d2.Loc().End;
        if (c != 0)
        {
            return c;
        }
        c = d1.Code() - d2.Code();
        if (c != 0)
        {
            return c;
        }
        c = CompareStringSlices(d1.MessageArgs(), d2.MessageArgs());
        if (c != 0)
        {
            return c;
        }
        c = CompareMessageChainSize(d1.MessageChain(), d2.MessageChain());
        if (c != 0)
        {
            return c;
        }
        c = CompareMessageChainContent(d1.MessageChain(), d2.MessageChain());
        if (c != 0)
        {
            return c;
        }
        return CompareRelatedInfo(d1.RelatedInformation(), d2.RelatedInformation());
    }

    // ── slices helpers (Go stdlib `slices` equivalents used above) ──

    // slices.SortStableFunc — stable sort in place. Stable per plan/05 §6.4 / R22
    // (diagnostic ordering is baseline-load-bearing); LINQ OrderBy is a stable sort.
    private static void SortStableFunc(ref List<Diagnostic>? list, Comparison<Diagnostic> cmp)
    {
        if (list == null || list.Count < 2)
        {
            return;
        }
        var sorted = new List<Diagnostic>(list);
        sorted.Sort(StableComparison(cmp, list));
        list.Clear();
        list.AddRange(sorted);
    }

    // Decorate the comparison with original index to make List.Sort stable.
    private static Comparison<Diagnostic> StableComparison(Comparison<Diagnostic> cmp, List<Diagnostic> original)
    {
        // Diagnostic does not override Equals/GetHashCode, so the default comparer is
        // reference identity (plan/05 §6.2).
        var index = new Dictionary<Diagnostic, int>();
        for (int i = 0; i < original.Count; i++)
        {
            index[original[i]] = i;
        }
        return (a, b) =>
        {
            int c = cmp(a, b);
            if (c != 0)
            {
                return c;
            }
            return index[a] - index[b];
        };
    }

    private static Diagnostic[] Clone(List<Diagnostic>? list)
    {
        // Go slices.Clone(nil) → nil; callers treat nil as empty. Return empty array.
        return list != null ? list.ToArray() : System.Array.Empty<Diagnostic>();
    }

    private static (int Index, bool Found) BinarySearchFunc(Diagnostic[] slice, Diagnostic target, Comparison<Diagnostic> cmp)
    {
        int lo = 0;
        int hi = slice.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (cmp(slice[mid], target) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        bool found = lo < slice.Length && cmp(slice[lo], target) == 0;
        return (lo, found);
    }

    private static bool SlicesEqual(string[]? a, string[]? b)
    {
        int la = a?.Length ?? 0;
        int lb = b?.Length ?? 0;
        if (la != lb)
        {
            return false;
        }
        for (int i = 0; i < la; i++)
        {
            if (a![i] != b![i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool SlicesEqualFunc(Diagnostic[]? a, Diagnostic[]? b, Func<Diagnostic, Diagnostic, bool> eq)
    {
        int la = a?.Length ?? 0;
        int lb = b?.Length ?? 0;
        if (la != lb)
        {
            return false;
        }
        for (int i = 0; i < la; i++)
        {
            if (!eq(a![i], b![i]))
            {
                return false;
            }
        }
        return true;
    }

    private static int CompareStringSlices(string[]? a, string[]? b)
    {
        int la = a?.Length ?? 0;
        int lb = b?.Length ?? 0;
        int n = System.Math.Min(la, lb);
        for (int i = 0; i < n; i++)
        {
            int c = string.CompareOrdinal(a![i], b![i]);
            if (c != 0)
            {
                return c < 0 ? -1 : 1;
            }
        }
        return la.CompareTo(lb);
    }
}

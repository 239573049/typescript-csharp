// Package diagnostics contains generated localizable diagnostic messages.
//
// Ported from typescript-go internal/diagnostics/diagnostics.go — minimal real
// subset: the Category enum + Name() (diagnostics.go:20-41) and the Message
// struct with its getters (diagnostics.go:44-65). Go's `type Key string` maps to
// plain string (the generated KeyToMessage takes string, plan/04-codegen.md §3.4).
//
// NOT YET PORTED (localization/formatting surface, later phase per plan/04 §3.5 and
// plan/06 §2.2 「运行时接线归阶段 9」): Localize, Format, StringifyArgs,
// NewAdHocMessage, getLocalizedMessages and the locale matcher/loader plumbing
// (loc_generated.go). The generated Messages.g.cs half of this package is emitted
// by tools/generator/generate-diagnostics.ts.

using System;

namespace Ts.Diagnostics;

public enum Category
{
    Warning,
    Error,
    Suggestion,
    Message,
}

public static class CategoryExtensions
{
    // Hand-written Name() (diagnostics.go:29-41) — this is the errors.txt
    // load-bearing lowercase form; the stringer family does not generate for
    // Category (plan/04 §7.2).
    public static string Name(this Category category)
    {
        switch (category)
        {
            case Category.Warning:
                return "warning";
            case Category.Error:
                return "error";
            case Category.Suggestion:
                return "suggestion";
            case Category.Message:
                return "message";
            default:
                throw new InvalidOperationException("Unhandled diagnostic category");
        }
    }
}

public sealed class Message
{
    private readonly int code;
    private readonly Category category;
    private readonly string key;
    private readonly string text;
    private readonly bool reportsUnnecessary;
    private readonly bool elidedInCompatibilityPyramid;
    private readonly bool reportsDeprecated;

    public Message(
        int code,
        Category category,
        string key,
        string text,
        bool reportsUnnecessary = false,
        bool elidedInCompatibilityPyramid = false,
        bool reportsDeprecated = false)
    {
        this.code = code;
        this.category = category;
        this.key = key;
        this.text = text;
        this.reportsUnnecessary = reportsUnnecessary;
        this.elidedInCompatibilityPyramid = elidedInCompatibilityPyramid;
        this.reportsDeprecated = reportsDeprecated;
    }

    public int Code => code;
    public Category Category => category;
    public string Key => key;
    // Go exposes text only package-internally (plus the debug String()); the
    // formatting/localization surface that consumes it arrives in a later phase.
    public string Text => text;
    public bool ReportsUnnecessary => reportsUnnecessary;
    public bool ElidedInCompatibilityPyramid => elidedInCompatibilityPyramid;
    public bool ReportsDeprecated => reportsDeprecated;

    // For debugging only.
    public override string ToString() => text;
}

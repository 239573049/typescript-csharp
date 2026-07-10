using System;
using System.Text;

namespace Ts.Core;

// Go: internal/debug/debug.go — absorbed into Ts.Core per plan/01 §2.3 (ruling R3);
// does not count against the 1:1 package roster. Go panics → DebugFailureException.
public sealed class DebugFailureException : Exception
{
    public DebugFailureException(string message)
        : base(message)
    {
    }
}

// Implemented by values that can describe their syntax kind (Go used an anonymous
// interface constraint `interface{ KindString() string }`).
public interface IKindString
{
    public string KindString();
}

public static class Debug
{
    public static void Fail(string reason = "")
    {
        reason = reason.Length == 0 ? "Debug failure." : "Debug failure. " + reason;
        throw new DebugFailureException(reason);
    }

    public static void FailBadSyntaxKind(IKindString node, params object?[] message)
    {
        string msg = message.Length == 0 ? "Unexpected node." : Sprint(message);
        Fail($"{msg}\nNode {node.KindString()} was unexpected.");
    }

    public static void AssertNever(object? member, params object?[] message)
    {
        string msg = message.Length == 0 ? "Illegal value:" : Sprint(message);
        string detail = member switch
        {
            IKindString m => m.KindString(),
            _ => member?.ToString() ?? "<null>",
        };
        Fail($"{msg} {detail}");
    }

    public static void Assert(bool value, params object?[] message)
    {
        if (value)
        {
            return;
        }
        AssertSlow(message);
    }

    private static void AssertSlow(object?[] message)
    {
        string msg = message.Length > 0 ? "False expression: " + Sprint(message) : "False expression.";
        Fail(msg);
    }

    // Approximates fmt.Sprint concatenation (assertion text is not baseline-load-bearing).
    private static string Sprint(object?[] parts)
    {
        var b = new StringBuilder();
        foreach (object? part in parts)
        {
            b.Append(part);
        }
        return b.ToString();
    }
}

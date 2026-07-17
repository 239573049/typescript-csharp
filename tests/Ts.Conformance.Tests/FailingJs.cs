// Opt-in .js emit gate bookkeeping (phase 6 currency). Separate from failingCases.txt and
// failingTypes.txt: TSCS_CONFORMANCE_JS=1 adds the emit-output currency only for cases whose
// errors baseline matched.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using global::Ts.TestUtil;

namespace Ts.Conformance.Tests;

public static class FailingJs
{
    public static string FilePath { get; } = Path.Combine(
        global::Ts.TestUtil.RepoPaths.RootPath,
        "tests",
        "Ts.Conformance.Tests",
        "failingJs.txt");

    private static readonly Lazy<HashSet<string>> s_names = new(
        () =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(FilePath))
            {
                return names;
            }
            foreach (string rawLine in File.ReadAllLines(FilePath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                names.Add(line);
            }
            return names;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static HashSet<string> Names => s_names.Value;

    private const string Header =
        """
        # Conformance .js emit failing-cases allowlist (emit pipeline WIP).
        # One case name per line (= baseline name without .errors.txt). '#' and blank lines are ignored.
        # Only errors.txt-matching cases reach this gate. A listed case skips on mismatch; an unlisted
        # mismatch fails. A listed case whose .js artifact matches passes normally.
        # Regenerate the current deterministic corpus wave (run from repo root):
        #   TSCS_CONFORMANCE_JS=1 TSCS_CONFORMANCE_UPDATE_JS=1 \
        #     dotnet test tests/Ts.Conformance.Tests/Ts.Conformance.Tests.csproj /nodeReuse:false
        """;

    public static void Save(IEnumerable<string> names)
    {
        var builder = new StringBuilder();
        builder.Append(Header.ReplaceLineEndings("\n"));
        builder.Append('\n');
        foreach (string name in names.OrderBy(name => name, StringComparer.Ordinal))
        {
            builder.Append(name);
            builder.Append('\n');
        }
        File.WriteAllText(FilePath, builder.ToString());
    }
}

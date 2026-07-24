// Ported from tsgo internal/ls/autoimport/view.go (whole file): the per-request view over the
// registry — construction, bucket search (project + ancestor node_modules with package.json
// dependency filtering), and grouped auto-import completions. The View methods declared in
// fix.go live in Fix.cs; GetModuleSpecifier (specifiers.go) lives in Specifiers.cs.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.PackageJson;

namespace Ts.Ls.AutoImport;

// view.go:21-33 — View. (The fix.go-facing fields are declared in Fix.cs's partial.)
public sealed partial class View
{
    internal Registry registry = null!;
    internal global::Ts.TsPath.Path projectKey;

    internal global::Ts.ModuleSpecifiers.ModuleSpecifierEnding[]? allowedEndings;
    internal Set<string>? conditions;

    // view.go:35-49 — NewView.
    public static View NewView(Registry registry, SourceFile importingFile, global::Ts.TsPath.Path projectKey, global::Ts.Compiler.Program program, global::Ts.ModuleSpecifiers.UserPreferences preferences)
    {
        return new View
        {
            registry = registry,
            importingFile = importingFile,
            program = program,
            projectKey = projectKey,
            preferences = preferences,
            conditions = Set.FromItems(
                global::Ts.Module.Module.GetConditions(program.Options(),
                    program.GetDefaultResolutionModeForFile(importingFile))),
            shouldUseUriStyleNodeCoreModules = LsDeps.ShouldUseUriStyleNodeCoreModules(importingFile, program),
        };
    }

    // view.go:51-63 — getAllowedEndings.
    internal global::Ts.ModuleSpecifiers.ModuleSpecifierEnding[] GetAllowedEndings()
    {
        if (allowedEndings == null)
        {
            var resolutionMode = program.GetDefaultResolutionModeForFile(importingFile);
            allowedEndings = global::Ts.ModuleSpecifiers.Specifiers.GetAllowedEndingsInPreferredOrder(
                preferences,
                program,
                program.Options(),
                importingFile,
                "",
                resolutionMode);
        }
        return allowedEndings;
    }

    // view.go:72-87 — Search.
    public List<Export> Search(string query, QueryKind kind)
    {
        Func<RegistryBucket, List<Export>> searchFn = bucket =>
        {
            switch (kind)
            {
                case QueryKind.WordPrefix:
                    return bucket.Index!.SearchWordPrefix(query);
                case QueryKind.ExactMatch:
                    return bucket.Index!.Find(query, true);
                case QueryKind.CaseInsensitiveMatch:
                    return bucket.Index!.Find(query, false);
                default:
                    throw new InvalidOperationException("unreachable");
            }
        };

        return SearchWorker(searchFn);
    }

    // view.go:89-97 — SearchByExportID.
    public Export[] SearchByExportID(ExportID id)
    {
        Func<RegistryBucket, List<Export>> search = bucket =>
        {
            var matched = new List<Export>();
            foreach (var e in bucket.Index!.entries)
            {
                if (e.ExportID == id)
                {
                    matched.Add(e);
                }
            }
            return matched;
        };

        return SearchWorker(search).ToArray();
    }

    // view.go:98-162 — search.
    private List<Export> SearchWorker(Func<RegistryBucket, List<Export>> searchFn)
    {
        var results = new List<Export>();

        if (registry.projects != null && registry.projects.TryGetValue(projectKey, out var bucket))
        {
            var exports = searchFn(bucket);
            foreach (var e in exports)
            {
                if (e.ModuleID.Value == importingFile.Path().Value)
                {
                    // Don't auto-import from the importing file itself
                    continue;
                }
                results.Add(e);
            }
        }

        // Compute the set of packages accessible to the importing file.
        // This includes packages from package.json dependencies (aggregated from ancestor directories)
        // plus packages that are directly imported by the project's program files.
        // If no package.json is found, allowedPackages remains nil and all packages are allowed.
        Set<string>? allowedPackages = null;
        global::Ts.TsPath.TsPath.ForEachAncestorDirectoryPath<object?>(importingFile.Path().GetDirectoryPath(), dirPath =>
        {
            if (registry.directories.TryGetValue(dirPath, out var dir))
            {
                var pj = dir.packageJson;
                if (pj.Exists() && pj!.Contents!.Parseable)
                {
                    // Initialize to empty set if this is the first package.json we've seen
                    allowedPackages ??= new Set<string>();
                    var deps = new HashSet<string>(StringComparer.Ordinal);
                    Util.AddPackageJsonDependencies(pj.Contents!, deps);
                    foreach (var dep in deps)
                    {
                        allowedPackages.Add(dep);
                    }
                }
            }
            return (null, false);
        });
        // If we found at least one package.json, also include packages directly imported by the project
        if (allowedPackages != null)
        {
            if (registry.projects != null && registry.projects.TryGetValue(projectKey, out var projectBucket))
            {
                allowedPackages = allowedPackages.UnionedWith(projectBucket.ResolvedPackageNames);
            }
        }

        var excludePackages = new Set<string>();
        global::Ts.TsPath.TsPath.ForEachAncestorDirectoryPath<object?>(importingFile.Path().GetDirectoryPath(), dirPath =>
        {
            if (registry.nodeModules != null && registry.nodeModules.TryGetValue(dirPath, out var nodeModulesBucket))
            {
                var exports = searchFn(nodeModulesBucket);
                foreach (var e in exports)
                {
                    // Exclude packages found in lower node_modules (shadowing)
                    if (excludePackages.Has(e.PackageName))
                    {
                        continue;
                    }
                    // If allowedPackages is nil, no package.json was found, so include all packages.
                    // Otherwise, only include packages that are dependencies or directly imported.
                    if (allowedPackages != null && !allowedPackages.Has(e.PackageName))
                    {
                        continue;
                    }
                    results.Add(e);
                }

                // As we go up the directory tree, exclude packages found in lower node_modules
                // map-iter: 序无关 (集合聚合)
                foreach (var pkgName in (nodeModulesBucket.PackageFiles ?? new Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>()).Keys)
                {
                    excludePackages.Add(pkgName);
                }
            }
            return (null, false);
        });
        return results;
    }

    // view.go:173-256 — GetCompletions.
    public List<FixAndExport> GetCompletions(string prefix, global::Ts.LsProto.Position position, bool forJSX, bool isTypeOnlyLocation)
    {
        var results = Search(prefix, QueryKind.WordPrefix);

        // view.go:175-179 — exportGroupKey.
        var grouped = new Dictionary<(ExportID Target, string Name, string AmbientModuleOrPackageName), List<Export>>(results.Count);
        foreach (var e in results)
        {
            var name = e.Name();
            if (!global::Ts.Scanner.Scanner.IsIdentifierText(name, LanguageVariant.Standard))
            {
                continue;
            }
            if (forJSX && !(System.Text.Rune.IsUpper(new System.Text.Rune(name[0])) || e.IsRenameable()))
            {
                continue;
            }
            var target = e.ExportID;
            if (e.Target != default(ExportID))
            {
                target = e.Target;
            }
            var key = (target, name, CoreUtil.FirstNonZero(e.AmbientModuleName(), e.PackageName) ?? "");
            if (e.PackageName == "@types/node" || e.Path.Value.Contains("/node_modules/@types/node/", StringComparison.Ordinal))
            {
                if (global::Ts.Core.Core.UnprefixedNodeCoreModules.Contains(key.Item3))
                {
                    // Group URI-style and non-URI style node core modules together so the ranking logic
                    // is allowed to drop one if an explicit preference is detected.
                    key = (key.Item1, key.Item2, "node:" + key.Item3);
                }
            }
            if (grouped.TryGetValue(key, out var existing))
            {
                var replaced = false;
                for (var i = 0; i < existing.Count; i++)
                {
                    var ex = existing[i];
                    if (e.ExportID == ex.ExportID)
                    {
                        existing[i] = new Export
                        {
                            ModuleID = e.ExportID.ModuleID,
                            ExportName = e.ExportID.ExportName,
                            ModuleFileName = e.ModuleFileName,
                            PackageName = e.PackageName,
                            IsTypeOnly = e.IsTypeOnly || ex.IsTypeOnly,
                            Syntax = (ExportSyntax)Math.Min((int)e.Syntax, (int)ex.Syntax),
                            Flags = e.Flags | ex.Flags,
                            ScriptElementKind = (ScriptElementKind)Math.Min((int)e.ScriptElementKind, (int)ex.ScriptElementKind),
                            ScriptElementKindModifiers = e.ScriptElementKindModifiers | ex.ScriptElementKindModifiers,
                            localName = e.localName,
                            Target = e.Target,
                            Path = e.Path,
                        };
                        replaced = true;
                        break;
                    }
                }
                if (replaced)
                {
                    continue;
                }
            }
            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<Export>();
                grouped[key] = list;
            }
            list.Add(e);
        }

        var fixes = new List<FixAndExport>(results.Count);
        Func<FixAndExport, FixAndExport, int> compareFixes = (a, b) => CompareFixesForRanking(a.Fix, b.Fix);

        // map-iter: 序无关 (每组独立取最小; 末尾统一稳定排序)
        foreach (var exps in grouped.Values)
        {
            var fixesForGroup = new List<FixAndExport>(exps.Count);
            foreach (var e in exps)
            {
                foreach (var fix in GetFixes(e, forJSX, isTypeOnlyLocation, position) ?? [])
                {
                    fixesForGroup.Add(new FixAndExport
                    {
                        Fix = fix,
                        Export = e,
                    });
                }
            }
            fixes.AddRange(CoreUtil.MinAllFunc(fixesForGroup.ToArray(), (a, b) => compareFixes(a, b)) ?? []);
        }

        // The client will do additional sorting by SortText and Label, so we don't
        // need to consider the name in our sorting here; we only need to produce a
        // stable relative ordering between completions that the client will consider
        // equivalent.
        fixes.Sort((a, b) => CompareFixesForSorting(a.Fix, b.Fix));

        return fixes;
    }
}

// view.go:64-70 — QueryKind.
public enum QueryKind
{
    WordPrefix = 0,
    ExactMatch,
    CaseInsensitiveMatch,
}

// view.go:164-167 — FixAndExport.
public sealed class FixAndExport
{
    public Fix Fix = null!;
    public Export Export = null!;
}

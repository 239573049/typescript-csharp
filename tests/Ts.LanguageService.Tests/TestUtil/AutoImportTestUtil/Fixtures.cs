// Ported from tsgo internal/testutil/autoimporttestutil/fixtures.go (whole file).
//
// PORT-NOTEs:
//  * Go's handle structs (returned by value, occasionally by pointer from
//    NodeModuleByName) become C# classes; value copies in Go's accessors map to array
//    clones here (slices.Clone → ToArray).
//  * Go `map[string]any` file maps → Dictionary<string, object?>; VfsTestUtil.FromMap
//    accepts string and MapFile values exactly like Go's vfstest.FromMap.
//  * Go's `t.Cleanup(session.Close)` has no analog in a helper that returns a value, and
//    the ported session tests in this assembly do not close their sessions (see
//    ProjectLifetimeTests), so the fixtures mirror that: the session is live for the test
//    and is not explicitly closed afterwards.
//  * Go `panic(...)` on a fixture misuse → InvalidOperationException with the Go text.
//  * Go iterates `builder.projects` (a map) when sorting project handles; the port
//    sorts the same key set (`// map-iter: 已排序`).

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Vfs.VfsTest;

namespace Ts.TestUtil.AutoImportTestUtil;

// fixtures.go:18-25 — FileHandle. (Not sealed: ProjectFileHandle embeds it, as in Go.)
public class FileHandle
{
    private readonly string fileName;
    private readonly string content;

    internal FileHandle(string fileName, string content)
    {
        this.fileName = fileName;
        this.content = content;
    }

    public string FileName() => fileName;

    public string Content() => content;

    // fixtures.go:24 — URI.
    public global::Ts.LsProto.DocumentUri URI() =>
        global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(fileName);
}

// fixtures.go:27-34 — ProjectFileHandle.
public sealed class ProjectFileHandle : FileHandle
{
    internal readonly string exportIdentifier;

    internal ProjectFileHandle(string fileName, string content, string exportIdentifier)
        : base(fileName, content)
    {
        this.exportIdentifier = exportIdentifier;
    }
}

// fixtures.go:36-46 — NodeModulesPackageHandle.
public sealed class NodeModulesPackageHandle
{
    public readonly string Name;
    public readonly string Directory;
    private readonly FileHandle packageJSON;
    private readonly FileHandle declaration;

    internal NodeModulesPackageHandle(string name, string directory, FileHandle packageJSON, FileHandle declaration)
    {
        Name = name;
        Directory = directory;
        this.packageJSON = packageJSON;
        this.declaration = declaration;
    }

    public FileHandle PackageJSONFile() => packageJSON;

    public FileHandle DeclarationFile() => declaration;
}

// fixtures.go:48-71 — MonorepoHandle.
public sealed class MonorepoHandle
{
    internal string root = "";
    internal NodeModulesPackageHandle[] rootNodeModules = [];
    internal string[] rootDependencies = [];
    internal ProjectHandle[] packages = [];
    internal FileHandle rootTSConfig = null!;
    internal FileHandle rootPackageJSON = null!;

    public string Root() => root;

    public NodeModulesPackageHandle[] RootNodeModules() => (NodeModulesPackageHandle[])rootNodeModules.Clone();

    public string[] RootDependencies() => (string[])rootDependencies.Clone();

    public ProjectHandle[] Packages() => (ProjectHandle[])packages.Clone();

    // fixtures.go:62-67 — Package.
    public ProjectHandle Package(int index)
    {
        if (index < 0 || index >= packages.Length)
        {
            throw new InvalidOperationException($"package index {index} out of range");
        }
        return packages[index];
    }

    public FileHandle RootTSConfig() => rootTSConfig;

    public FileHandle RootPackageJSONFile() => rootPackageJSON;
}

// fixtures.go:73-108 — ProjectHandle.
public sealed class ProjectHandle
{
    internal string root = "";
    internal ProjectFileHandle[] files = [];
    internal FileHandle tsconfig = null!;
    internal FileHandle packageJSON = null!;
    internal NodeModulesPackageHandle[] nodeModules = [];
    internal string[] dependencies = [];

    public string Root() => root;

    public ProjectFileHandle[] Files() => (ProjectFileHandle[])files.Clone();

    // fixtures.go:85-90 — File.
    public ProjectFileHandle File(int index)
    {
        if (index < 0 || index >= files.Length)
        {
            throw new InvalidOperationException($"file index {index} out of range");
        }
        return files[index];
    }

    public FileHandle TSConfig() => tsconfig;

    public FileHandle PackageJSONFile() => packageJSON;

    public NodeModulesPackageHandle[] NodeModules() => (NodeModulesPackageHandle[])nodeModules.Clone();

    public string[] Dependencies() => (string[])dependencies.Clone();

    // fixtures.go:105-112 — NodeModuleByName.
    public NodeModulesPackageHandle? NodeModuleByName(string name)
    {
        foreach (NodeModulesPackageHandle handle in nodeModules)
        {
            if (handle.Name == name)
            {
                return handle;
            }
        }
        return null;
    }
}

// fixtures.go:114-135 — Fixture.
public sealed class Fixture
{
    private readonly global::Ts.Project.Session session;
    private readonly global::Ts.TestUtil.ProjectTestUtil.SessionUtils utils;
    private readonly ProjectHandle[] projects;

    internal Fixture(global::Ts.Project.Session session, global::Ts.TestUtil.ProjectTestUtil.SessionUtils utils, ProjectHandle[] projects)
    {
        this.session = session;
        this.utils = utils;
        this.projects = projects;
    }

    public global::Ts.Project.Session Session() => session;

    public global::Ts.TestUtil.ProjectTestUtil.SessionUtils Utils() => utils;

    public ProjectHandle[] Projects() => (ProjectHandle[])projects.Clone();

    // fixtures.go:125-130 — Project.
    public ProjectHandle Project(int index)
    {
        if (index < 0 || index >= projects.Length)
        {
            throw new InvalidOperationException($"project index {index} out of range");
        }
        return projects[index];
    }

    public ProjectHandle SingleProject() => Project(0);
}

// fixtures.go:137-158 — MonorepoFixture.
public sealed class MonorepoFixture
{
    private readonly global::Ts.Project.Session session;
    private readonly global::Ts.TestUtil.ProjectTestUtil.SessionUtils utils;
    private readonly MonorepoHandle monorepo;
    private readonly FileHandle[] extra;

    internal MonorepoFixture(
        global::Ts.Project.Session session,
        global::Ts.TestUtil.ProjectTestUtil.SessionUtils utils,
        MonorepoHandle monorepo,
        FileHandle[] extra)
    {
        this.session = session;
        this.utils = utils;
        this.monorepo = monorepo;
        this.extra = extra;
    }

    public global::Ts.Project.Session Session() => session;

    public global::Ts.TestUtil.ProjectTestUtil.SessionUtils Utils() => utils;

    public MonorepoHandle Monorepo() => monorepo;

    public FileHandle[] ExtraFiles() => (FileHandle[])extra.Clone();

    // fixtures.go:152-158 — ExtraFile.
    public FileHandle ExtraFile(string path)
    {
        string normalized = FileMapBuilder.NormalizeAbsolutePath(path);
        foreach (FileHandle handle in extra)
        {
            if (handle.FileName() == normalized)
            {
                return handle;
            }
        }
        throw new InvalidOperationException("extra file not found: " + path);
    }
}

// fixtures.go:160-172 — MonorepoPackageTemplate.
public sealed class MonorepoPackageTemplate
{
    public string Name = "";
    public string[] NodeModuleNames = [];
    public string[] DependencyNames = [];
}

// fixtures.go:174-185 — MonorepoSetupConfig.
public sealed class MonorepoSetupConfig
{
    public string Root = "";
    public MonorepoPackageTemplate MonorepoPackageTemplate = new();
    public MonorepoPackageConfig[] Packages = [];
    public TextFileSpec[] ExtraFiles = [];
    public SymlinkSpec[] Symlinks = [];
}

// fixtures.go:187-190 — MonorepoPackageConfig.
public sealed class MonorepoPackageConfig
{
    public int FileCount;
    public MonorepoPackageTemplate MonorepoPackageTemplate = new();
}

// fixtures.go:192-196 — TextFileSpec.
public sealed class TextFileSpec
{
    public string Path = "";
    public string Content = "";
}

// fixtures.go:198-202 — SymlinkSpec.
public sealed class SymlinkSpec
{
    public string Link = "";   // The symlink path
    public string Target = ""; // The target path the symlink points to
}

public static class AutoImportFixtures
{
    // fixtures.go:204-306 — SetupMonorepoLifecycleSession.
    public static MonorepoFixture SetupMonorepoLifecycleSession(MonorepoSetupConfig config)
    {
        var builder = new FileMapBuilder(null);

        string monorepoRoot = FileMapBuilder.NormalizeAbsolutePath(config.Root);
        string monorepoName = config.MonorepoPackageTemplate.Name;
        if (monorepoName.Length == 0)
        {
            monorepoName = "monorepo";
        }

        // Add root tsconfig.json
        string rootTSConfigPath = global::Ts.TsPath.TsPath.CombinePaths(monorepoRoot, "tsconfig.json");
        const string rootTSConfigContent = "{\n  \"compilerOptions\": {\n    \"module\": \"esnext\",\n    \"target\": \"esnext\",\n    \"strict\": true,\n    \"baseUrl\": \".\",\n    \"allowJs\": true,\n    \"checkJs\": true\n  }\n}\n";
        builder.AddTextFile(rootTSConfigPath, rootTSConfigContent);
        var rootTSConfig = new FileHandle(rootTSConfigPath, rootTSConfigContent);

        // Add root node_modules
        string rootNodeModulesDir = global::Ts.TsPath.TsPath.CombinePaths(monorepoRoot, "node_modules");
        NodeModulesPackageHandle[] rootNodeModules = builder.AddNodeModulesPackagesWithNames(rootNodeModulesDir, config.MonorepoPackageTemplate.NodeModuleNames);

        // Add root package.json with dependencies (default to all root node_modules if unspecified)
        NodeModulesPackageHandle[] rootDependencies = FileMapBuilder.SelectPackagesByName(rootNodeModules, config.MonorepoPackageTemplate.DependencyNames);
        FileHandle rootPackageJSON = builder.AddRootPackageJSON(monorepoRoot, monorepoName, rootDependencies);
        string[] rootDependencyNames = FileMapBuilder.PackageNames(rootDependencies);

        // Build each package in packages/
        string packagesDir = global::Ts.TsPath.TsPath.CombinePaths(monorepoRoot, "packages");
        var packageHandles = new List<ProjectHandle>();
        foreach (MonorepoPackageConfig pkg in config.Packages)
        {
            string pkgDir = global::Ts.TsPath.TsPath.CombinePaths(packagesDir, pkg.MonorepoPackageTemplate.Name);
            builder.AddLocalProject(pkgDir, pkg.FileCount);

            NodeModulesPackageHandle[] pkgNodeModules = [];
            if (pkg.MonorepoPackageTemplate.NodeModuleNames.Length > 0)
            {
                string pkgNodeModulesDir = global::Ts.TsPath.TsPath.CombinePaths(pkgDir, "node_modules");
                pkgNodeModules = builder.AddNodeModulesPackagesWithNames(pkgNodeModulesDir, pkg.MonorepoPackageTemplate.NodeModuleNames);
            }

            var availableDeps = new List<NodeModulesPackageHandle>(rootNodeModules);
            availableDeps.AddRange(pkgNodeModules);
            NodeModulesPackageHandle[] selectedDeps = FileMapBuilder.SelectPackagesByName(availableDeps.ToArray(), pkg.MonorepoPackageTemplate.DependencyNames);
            if (selectedDeps.Length > 0)
            {
                builder.AddPackageJSONWithDependenciesNamed(pkgDir, pkg.MonorepoPackageTemplate.Name, selectedDeps);
            }
        }

        // Add arbitrary extra files
        var extraHandles = new List<FileHandle>();
        foreach (TextFileSpec extra in config.ExtraFiles)
        {
            builder.AddTextFile(extra.Path, extra.Content);
            extraHandles.Add(new FileHandle(FileMapBuilder.NormalizeAbsolutePath(extra.Path), extra.Content));
        }

        // Add symlinks
        foreach (SymlinkSpec symlink in config.Symlinks)
        {
            builder.AddSymlink(symlink.Link, symlink.Target);
        }

        // Build project handles after all packages are created
        foreach (MonorepoPackageConfig pkg in config.Packages)
        {
            string pkgDir = global::Ts.TsPath.TsPath.CombinePaths(packagesDir, pkg.MonorepoPackageTemplate.Name);
            if (builder.projects.TryGetValue(pkgDir, out ProjectRecord? record))
            {
                packageHandles.Add(record.ToHandles());
            }
        }

        (global::Ts.Project.Session session, global::Ts.TestUtil.ProjectTestUtil.SessionUtils sessionUtils) =
            global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(builder.Files());

        // Build root node_modules handle by looking at the project record for the workspace root
        // (created as side effect of AddNodeModulesPackages)
        NodeModulesPackageHandle[] rootNodeModulesHandles = [];
        if (builder.projects.TryGetValue(monorepoRoot, out ProjectRecord? rootRecord))
        {
            rootNodeModulesHandles = rootRecord.nodeModules.ToArray();
        }

        return new MonorepoFixture(
            session,
            sessionUtils,
            new MonorepoHandle
            {
                root = monorepoRoot,
                rootNodeModules = rootNodeModulesHandles,
                rootDependencies = rootDependencyNames,
                packages = packageHandles.ToArray(),
                rootTSConfig = rootTSConfig,
                rootPackageJSON = rootPackageJSON,
            },
            extraHandles.ToArray());
    }

    // fixtures.go:308-323 — SetupLifecycleSession.
    public static Fixture SetupLifecycleSession(string projectRoot, int fileCount)
    {
        var builder = new FileMapBuilder(null);
        builder.AddLocalProject(projectRoot, fileCount);
        string nodeModulesDir = global::Ts.TsPath.TsPath.CombinePaths(projectRoot, "node_modules");
        NodeModulesPackageHandle[] deps = builder.AddNodeModulesPackages(nodeModulesDir, 1);
        builder.AddPackageJSONWithDependencies(projectRoot, deps);
        (global::Ts.Project.Session session, global::Ts.TestUtil.ProjectTestUtil.SessionUtils sessionUtils) =
            global::Ts.TestUtil.ProjectTestUtil.SessionUtils.Setup(builder.Files());
        return new Fixture(session, sessionUtils, builder.ProjectHandles());
    }
}

// fixtures.go:325-345 — fileMapBuilder's projectRecord.
internal sealed class ProjectRecord
{
    internal string root = "";
    internal List<ProjectFile> sourceFiles = [];
    internal FileHandle tsconfig = null!;
    internal FileHandle? packageJSON;
    internal List<NodeModulesPackageHandle> nodeModules = [];
    internal List<string> dependencies = [];

    // fixtures.go:355-376 — toHandles.
    internal ProjectHandle ToHandles()
    {
        var files = new ProjectFileHandle[sourceFiles.Count];
        for (int i = 0; i < sourceFiles.Count; i++)
        {
            ProjectFile file = sourceFiles[i];
            files[i] = new ProjectFileHandle(file.FileName, file.Content, file.ExportIdentifier);
        }
        FileHandle packageJSON = new("", "");
        if (this.packageJSON != null)
        {
            packageJSON = this.packageJSON;
        }
        return new ProjectHandle
        {
            root = root,
            files = files,
            tsconfig = tsconfig,
            packageJSON = packageJSON,
            nodeModules = nodeModules.ToArray(),
            dependencies = dependencies.ToArray(),
        };
    }
}

// fixtures.go:347-352 — projectFile.
internal sealed class ProjectFile
{
    internal string FileName = "";
    internal string ExportIdentifier = "";
    internal string Content = "";
}

// fixtures.go:325-341 + 378-610 — fileMapBuilder.
internal sealed class FileMapBuilder
{
    internal Dictionary<string, object?> files = [];
    internal int nextPackageID;
    internal int nextProjectID;
    internal Dictionary<string, ProjectRecord> projects = [];

    // fixtures.go:347-357 — newFileMapBuilder.
    internal FileMapBuilder(Dictionary<string, object?>? initial)
    {
        if (initial == null || initial.Count == 0)
        {
            return;
        }
        foreach ((string path, object? content) in initial)
        {
            files[NormalizeAbsolutePath(path)] = content;
        }
    }

    // fixtures.go:359-366 — ensureProjectRecord.
    internal ProjectRecord EnsureProjectRecord(string root)
    {
        if (projects.TryGetValue(root, out ProjectRecord? record))
        {
            return record;
        }
        record = new ProjectRecord { root = root };
        projects[root] = record;
        return record;
    }

    // fixtures.go:368-378 — projectHandles.
    internal ProjectHandle[] ProjectHandles()
    {
        // map-iter: 已排序 (slices.Sort over maps.Keys in Go)
        var keys = new List<string>(projects.Keys);
        keys.Sort(StringComparer.Ordinal);
        var result = new ProjectHandle[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            result[i] = projects[keys[i]].ToHandles();
        }
        return result;
    }

    // fixtures.go:380-382 — Files.
    internal Dictionary<string, object?> Files() => new(files);

    // fixtures.go:384-387 — AddTextFile.
    internal void AddTextFile(string path, string contents)
    {
        files[NormalizeAbsolutePath(path)] = contents;
    }

    // fixtures.go:389-393 — AddSymlink. The targetPath should be an absolute path.
    internal void AddSymlink(string linkPath, string targetPath)
    {
        files[NormalizeAbsolutePath(linkPath)] = VfsTestUtil.Symlink(NormalizeAbsolutePath(targetPath));
    }

    // fixtures.go:395-401 — AddNodeModulesPackages.
    internal NodeModulesPackageHandle[] AddNodeModulesPackages(string nodeModulesDir, int count)
    {
        var packages = new NodeModulesPackageHandle[count];
        for (int i = 0; i < count; i++)
        {
            packages[i] = AddNodeModulesPackage(nodeModulesDir);
        }
        return packages;
    }

    // fixtures.go:403-412 — AddNodeModulesPackagesWithNames.
    internal NodeModulesPackageHandle[] AddNodeModulesPackagesWithNames(string nodeModulesDir, string[] names)
    {
        if (names.Length == 0)
        {
            return [];
        }
        var packages = new NodeModulesPackageHandle[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            packages[i] = AddNamedNodeModulesPackage(nodeModulesDir, names[i]);
        }
        return packages;
    }

    // fixtures.go:414-416 — AddNodeModulesPackage.
    internal NodeModulesPackageHandle AddNodeModulesPackage(string nodeModulesDir)
    {
        return AddNamedNodeModulesPackage(nodeModulesDir, "");
    }

    // fixtures.go:418-451 — AddNamedNodeModulesPackage.
    internal NodeModulesPackageHandle AddNamedNodeModulesPackage(string nodeModulesDir, string name)
    {
        string normalizedDir = NormalizeAbsolutePath(nodeModulesDir);
        if (global::Ts.TsPath.TsPath.GetBaseFileName(normalizedDir) != "node_modules")
        {
            throw new InvalidOperationException("nodeModulesDir must point to a node_modules directory: " + nodeModulesDir);
        }
        nextPackageID++;
        string resolvedName = name;
        if (resolvedName.Length == 0)
        {
            resolvedName = $"pkg{nextPackageID}";
        }
        string exportName = SanitizeIdentifier(resolvedName) + "_value";
        string pkgDir = global::Ts.TsPath.TsPath.CombinePaths(normalizedDir, resolvedName);
        string packageJSONPath = global::Ts.TsPath.TsPath.CombinePaths(pkgDir, "package.json");
        string packageJSONContent = $"{{\"name\":\"{resolvedName}\",\"types\":\"index.d.ts\"}}";
        files[packageJSONPath] = packageJSONContent;
        string declarationPath = global::Ts.TsPath.TsPath.CombinePaths(pkgDir, "index.d.ts");
        string declarationContent = $"export declare const {exportName}: number;\n";
        files[declarationPath] = declarationContent;
        var packageHandle = new NodeModulesPackageHandle(
            resolvedName,
            pkgDir,
            new FileHandle(packageJSONPath, packageJSONContent),
            new FileHandle(declarationPath, declarationContent));
        string projectRoot = global::Ts.TsPath.TsPath.GetDirectoryPath(normalizedDir);
        ProjectRecord record = EnsureProjectRecord(projectRoot);
        record.nodeModules.Add(packageHandle);
        return packageHandle;
    }

    // fixtures.go:453-474 — AddLocalProject.
    internal void AddLocalProject(string projectDir, int fileCount)
    {
        if (fileCount < 0)
        {
            throw new InvalidOperationException("fileCount must be non-negative");
        }
        string dir = NormalizeAbsolutePath(projectDir);
        ProjectRecord record = EnsureProjectRecord(dir);
        nextProjectID++;
        string tsConfigPath = global::Ts.TsPath.TsPath.CombinePaths(dir, "tsconfig.json");
        const string tsConfigContent = "{\n  \"compilerOptions\": {\n    \"module\": \"esnext\",\n    \"target\": \"esnext\",\n    \"strict\": true,\n    \"allowJs\": true,\n    \"checkJs\": true\n  }\n}\n";
        files[tsConfigPath] = tsConfigContent;
        record.tsconfig = new FileHandle(tsConfigPath, tsConfigContent);
        for (int i = 1; i <= fileCount; i++)
        {
            string path = global::Ts.TsPath.TsPath.CombinePaths(dir, $"file{i}.ts");
            string exportName = $"localExport{nextProjectID}_{i}";
            string content = $"export const {exportName} = {i};\n";
            files[path] = content;
            record.sourceFiles.Add(new ProjectFile { FileName = path, ExportIdentifier = exportName, Content = content });
        }
    }

    // fixtures.go:476-479 — AddPackageJSONWithDependencies.
    internal FileHandle AddPackageJSONWithDependencies(string projectDir, NodeModulesPackageHandle[] deps)
    {
        nextProjectID++;
        return AddPackageJSONWithDependenciesNamed(projectDir, $"local-project-{nextProjectID}", deps);
    }

    // fixtures.go:481-517 — AddPackageJSONWithDependenciesNamed.
    internal FileHandle AddPackageJSONWithDependenciesNamed(string projectDir, string packageName, NodeModulesPackageHandle[] deps)
    {
        string dir = NormalizeAbsolutePath(projectDir);
        string packageJSONPath = global::Ts.TsPath.TsPath.CombinePaths(dir, "package.json");
        var dependencyLines = new List<string>();
        foreach (NodeModulesPackageHandle dep in deps)
        {
            dependencyLines.Add($"\"{dep.Name}\": \"*\"");
        }
        var builder = new StringBuilder();
        string name = packageName;
        if (name.Length == 0)
        {
            nextProjectID++;
            name = $"local-project-{nextProjectID}";
        }
        builder.Append($"{{\n  \"name\": \"{name}\"");
        if (dependencyLines.Count > 0)
        {
            builder.Append(",\n  \"dependencies\": {\n    ");
            builder.Append(string.Join(",\n    ", dependencyLines));
            builder.Append("\n  }\n");
        }
        else
        {
            builder.Append("\n");
        }
        builder.Append("}\n");
        string content = builder.ToString();
        files[packageJSONPath] = content;
        ProjectRecord record = EnsureProjectRecord(dir);
        var packageHandle = new FileHandle(packageJSONPath, content);
        record.packageJSON = packageHandle;
        record.dependencies = new List<string>(PackageNames(deps));
        return packageHandle;
    }

    // fixtures.go:519-547 — addRootPackageJSON. Creates a root package.json for a monorepo
    // without creating a project record (the root workspace config is not a project).
    internal FileHandle AddRootPackageJSON(string rootDir, string packageName, NodeModulesPackageHandle[] deps)
    {
        string dir = NormalizeAbsolutePath(rootDir);
        string packageJSONPath = global::Ts.TsPath.TsPath.CombinePaths(dir, "package.json");
        var dependencyLines = new List<string>();
        foreach (NodeModulesPackageHandle dep in deps)
        {
            dependencyLines.Add($"\"{dep.Name}\": \"*\"");
        }
        var builder = new StringBuilder();
        string pkgName = packageName;
        if (pkgName.Length == 0)
        {
            pkgName = "monorepo-root";
        }
        builder.Append($"{{\n  \"name\": \"{pkgName}\",\n  \"private\": true");
        if (dependencyLines.Count > 0)
        {
            builder.Append(",\n  \"dependencies\": {\n    ");
            builder.Append(string.Join(",\n    ", dependencyLines));
            builder.Append("\n  }\n");
        }
        else
        {
            builder.Append("\n");
        }
        builder.Append("}\n");
        string content = builder.ToString();
        files[packageJSONPath] = content;
        return new FileHandle(packageJSONPath, content);
    }

    // fixtures.go:549-563 — selectPackagesByName.
    internal static NodeModulesPackageHandle[] SelectPackagesByName(NodeModulesPackageHandle[] available, string[] names)
    {
        if (names.Length == 0)
        {
            return (NodeModulesPackageHandle[])available.Clone();
        }
        var result = new List<NodeModulesPackageHandle>();
        foreach (string name in names)
        {
            bool found = false;
            foreach (NodeModulesPackageHandle candidate in available)
            {
                if (candidate.Name == name)
                {
                    result.Add(candidate);
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                throw new InvalidOperationException($"package {name} not found");
            }
        }
        return result.ToArray();
    }

    // fixtures.go:565-574 — packageNames.
    internal static string[] PackageNames(NodeModulesPackageHandle[] deps)
    {
        if (deps.Length == 0)
        {
            return [];
        }
        var names = new string[deps.Length];
        for (int i = 0; i < deps.Length; i++)
        {
            names[i] = deps[i].Name;
        }
        return names;
    }

    // fixtures.go:576-596 — sanitizeIdentifier.
    internal static string SanitizeIdentifier(string name)
    {
        var sanitized = new StringBuilder(name.Length);
        foreach (char r in name)
        {
            if (r is >= 'a' and <= 'z')
            {
                sanitized.Append(r);
            }
            else if (r is >= 'A' and <= 'Z')
            {
                sanitized.Append(r);
            }
            else if (r is >= '0' and <= '9')
            {
                sanitized.Append(r);
            }
            else if (r is '_' or '-')
            {
                sanitized.Append('_');
            }
        }
        string result = sanitized.ToString();
        return result.Length == 0 ? "pkg" : result;
    }

    // fixtures.go:604-610 — normalizeAbsolutePath.
    internal static string NormalizeAbsolutePath(string path)
    {
        string normalized = global::Ts.TsPath.TsPath.NormalizePath(path);
        if (!global::Ts.TsPath.TsPath.PathIsAbsolute(normalized))
        {
            throw new InvalidOperationException("paths used in lifecycle tests must be absolute: " + path);
        }
        return normalized;
    }
}

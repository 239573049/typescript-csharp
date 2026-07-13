using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Project.Tests;

public sealed class ProjectServiceTests
{
    private const string CurrentDirectory = "/workspace";
    private const string DefaultLibraryPath = "/lib";
    private const string ConfigFileName = "/workspace/tsconfig.json";
    private const string MainFileName = "/workspace/src/main.ts";

    [Fact]
    public void OpenDocumentUnderConfigCreatesConfiguredProjectContainingFile()
    {
        const string source = "export const answer = 42;\n";
        global::Ts.Vfs.IFileSystem fileSystem = CreateFileSystem(new Dictionary<string, string>
        {
            [ConfigFileName] = ConfigFor("src/main.ts"),
            [MainFileName] = source,
        });
        using var service = new global::Ts.Project.ProjectService(
            fileSystem,
            CurrentDirectory,
            DefaultLibraryPath);

        global::Ts.Project.Project project = service.OpenDocument(MainFileName, source, version: 1);

        Assert.Equal(global::Ts.Project.Kind.Configured, project.Kind);
        Assert.Equal(ConfigFileName, project.ConfigFileName());
        Assert.True(project.HasFile(MainFileName));
        Assert.Same(project, service.GetDefaultProject(MainFileName));
        Assert.Same(project.GetProgram(), service.GetLanguageService(MainFileName).GetProgram());
        Assert.Equal(source, project.GetProgram()!.GetSourceFile(MainFileName)!.Text());
    }

    [Fact]
    public void ChangeDocumentReloadsProgramAndCheckerObservesNewContent()
    {
        const string oldSource = "const value: string = 1;\n";
        const string newSource = "const value: string = 'ok';\n";
        global::Ts.Vfs.IFileSystem fileSystem = CreateFileSystem(new Dictionary<string, string>
        {
            [ConfigFileName] = ConfigFor("src/main.ts"),
            [MainFileName] = oldSource,
        });
        using var service = new global::Ts.Project.ProjectService(
            fileSystem,
            CurrentDirectory,
            DefaultLibraryPath);
        global::Ts.Project.Project project = service.OpenDocument(MainFileName, oldSource, version: 1);
        global::Ts.Compiler.Program oldProgram = project.GetProgram()!;
        global::Ts.Ast.SourceFile oldFile = oldProgram.GetSourceFile(MainFileName)!;
        global::Ts.Ast.Diagnostic[] oldDiagnostics = global::Ts.Core.LargeStackExecutor.Run(() =>
            oldProgram.GetSemanticDiagnostics(TestContext.Current.CancellationToken, oldFile));

        global::Ts.Project.Project updatedProject = service.ChangeDocument(MainFileName, newSource, version: 2);
        global::Ts.Compiler.Program newProgram = updatedProject.GetProgram()!;
        global::Ts.Ast.SourceFile newFile = newProgram.GetSourceFile(MainFileName)!;
        global::Ts.Ast.Diagnostic[] newDiagnostics = global::Ts.Core.LargeStackExecutor.Run(() =>
            newProgram.GetSemanticDiagnostics(TestContext.Current.CancellationToken, newFile));

        Assert.Same(project, updatedProject);
        Assert.NotSame(oldProgram, newProgram);
        Assert.Equal(newSource, newFile.Text());
        Assert.NotEmpty(oldDiagnostics);
        Assert.True(newDiagnostics.Length < oldDiagnostics.Length);
        Assert.Equal(2, service.GetScriptInfo(MainFileName)!.Version);
        Assert.Same(newProgram, service.GetLanguageService(MainFileName).GetProgram());
    }

    [Fact]
    public void OpenOrphanDocumentCreatesInferredProject()
    {
        const string orphanFileName = "/workspace/orphan/orphan.ts";
        const string source = "export const orphan = true;\n";
        global::Ts.Vfs.IFileSystem fileSystem = CreateFileSystem(new Dictionary<string, string>
        {
            [orphanFileName] = source,
        });
        using var service = new global::Ts.Project.ProjectService(
            fileSystem,
            CurrentDirectory,
            DefaultLibraryPath);

        global::Ts.Project.Project project = service.OpenDocument(orphanFileName, source, version: 1);

        Assert.Equal(global::Ts.Project.Kind.Inferred, project.Kind);
        Assert.True(project.HasFile(orphanFileName));
        Assert.Equal([orphanFileName], project.RootFileNames);
        Assert.Same(project, service.GetDefaultProject(orphanFileName));
        Assert.Null(service.FindConfigFile(orphanFileName));
    }

    [Fact]
    public void NotifyFileChangedReloadsConfiguredProgramFromVfs()
    {
        const string dependencyFileName = "/workspace/src/dependency.ts";
        const string mainSource = "import { value } from './dependency'; export { value };\n";
        const string oldDependency = "export const value = 1;\n";
        const string newDependency = "export const value = 'updated';\n";
        global::Ts.Vfs.IFileSystem fileSystem = CreateFileSystem(new Dictionary<string, string>
        {
            [ConfigFileName] = ConfigFor("src/main.ts", "src/dependency.ts"),
            [MainFileName] = mainSource,
            [dependencyFileName] = oldDependency,
        });
        using var service = new global::Ts.Project.ProjectService(
            fileSystem,
            CurrentDirectory,
            DefaultLibraryPath);
        global::Ts.Project.Project project = service.OpenDocument(MainFileName, mainSource, version: 1);
        global::Ts.Compiler.Program oldProgram = project.GetProgram()!;

        fileSystem.WriteFile(dependencyFileName, newDependency);
        service.NotifyFileChanged(dependencyFileName);

        Assert.NotSame(oldProgram, project.GetProgram());
        Assert.Equal(newDependency, project.GetProgram()!.GetSourceFile(dependencyFileName)!.Text());
        Assert.Equal(global::Ts.Project.ProgramUpdateKind.SameFileNames, project.ProgramUpdateKind);
    }

    [Fact]
    public void NotifyConfigChangedReDiscoversRootsAndReplacesProject()
    {
        const string additionalFileName = "/workspace/src/additional.ts";
        const string mainSource = "export const main = true;\n";
        global::Ts.Vfs.IFileSystem fileSystem = CreateFileSystem(new Dictionary<string, string>
        {
            [ConfigFileName] = ConfigFor("src/main.ts"),
            [MainFileName] = mainSource,
            [additionalFileName] = "export const additional = true;\n",
        });
        using var service = new global::Ts.Project.ProjectService(
            fileSystem,
            CurrentDirectory,
            DefaultLibraryPath);
        global::Ts.Project.Project oldProject = service.OpenDocument(MainFileName, mainSource, version: 1);

        fileSystem.WriteFile(ConfigFileName, ConfigFor("src/main.ts", "src/additional.ts"));
        service.NotifyFileChanged(ConfigFileName);
        global::Ts.Project.Project newProject = service.GetDefaultProject(MainFileName)!;

        Assert.NotSame(oldProject, newProject);
        Assert.Equal(global::Ts.Project.Kind.Configured, newProject.Kind);
        Assert.True(newProject.HasFile(MainFileName));
        Assert.True(newProject.HasFile(additionalFileName));
        Assert.Equal(2, newProject.RootFileNames.Length);
    }

    private static global::Ts.Vfs.IFileSystem CreateFileSystem(IReadOnlyDictionary<string, string> files) =>
        global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);

    private static string ConfigFor(params string[] files)
    {
        var quotedFiles = new string[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            quotedFiles[i] = "\"" + files[i] + "\"";
        }

        return "{\"compilerOptions\":{\"noLib\":true,\"strict\":true,\"types\":[]},\"files\":["
            + string.Join(',', quotedFiles)
            + "]}";
    }
}

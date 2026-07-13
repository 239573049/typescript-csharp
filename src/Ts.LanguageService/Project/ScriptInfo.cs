// Foundation subset of tsgo internal/project/overlayfs.go.
//
// The pinned Go project package no longer has the historical scriptinfo.go;
// open documents are represented by immutable Overlay values. ScriptInfo keeps
// the requested public project-service registry shape while retaining those
// Overlay semantics: only an open ScriptInfo shadows the underlying VFS, edits
// replace the complete UTF-16 string, and closing removes the shadow.

using System;
using System.Threading;

namespace Ts.Project;

public sealed class ScriptInfo
{
    private readonly Lock gate = new();
    private string content;
    private int version;
    private global::Ts.Core.ScriptKind scriptKind;
    private bool isOpen;

    internal ScriptInfo(
        string fileName,
        global::Ts.TsPath.Path path,
        string content,
        int version,
        global::Ts.Core.ScriptKind scriptKind)
    {
        FileName = fileName;
        Path = path;
        this.content = content;
        this.version = version;
        this.scriptKind = scriptKind;
        isOpen = true;
    }

    public string FileName { get; }

    public global::Ts.TsPath.Path Path { get; }

    public string Content
    {
        get
        {
            using (gate.EnterScope())
            {
                return content;
            }
        }
    }

    public int Version
    {
        get
        {
            using (gate.EnterScope())
            {
                return version;
            }
        }
    }

    public global::Ts.Core.ScriptKind ScriptKind
    {
        get
        {
            using (gate.EnterScope())
            {
                return scriptKind;
            }
        }
    }

    public bool IsOpen
    {
        get
        {
            using (gate.EnterScope())
            {
                return isOpen;
            }
        }
    }

    internal void Open(string newContent, int newVersion, global::Ts.Core.ScriptKind newScriptKind)
    {
        ArgumentNullException.ThrowIfNull(newContent);
        using (gate.EnterScope())
        {
            content = newContent;
            version = newVersion;
            scriptKind = newScriptKind;
            isOpen = true;
        }
    }

    internal void Change(string newContent, int newVersion)
    {
        ArgumentNullException.ThrowIfNull(newContent);
        using (gate.EnterScope())
        {
            if (!isOpen)
            {
                // Go overlayFS.processChanges panics when a change follows close.
                throw new InvalidOperationException("cannot change a closed script: " + FileName);
            }

            content = newContent;
            version = newVersion;
        }
    }

    internal void Close()
    {
        using (gate.EnterScope())
        {
            if (!isOpen)
            {
                // Go overlayFS.processChanges panics when the overlay is absent.
                throw new InvalidOperationException("script is not open: " + FileName);
            }

            isOpen = false;
        }
    }

    internal OpenFileSnapshot? GetOpenSnapshot()
    {
        using (gate.EnterScope())
        {
            if (!isOpen)
            {
                return null;
            }

            return new OpenFileSnapshot(FileName, content, version, scriptKind);
        }
    }
}

internal readonly record struct OpenFileSnapshot(
    string FileName,
    string Content,
    int Version,
    global::Ts.Core.ScriptKind ScriptKind);

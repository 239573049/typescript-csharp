// Foundation subset of tsgo internal/project/configfileregistrybuilder.go.

using System;

namespace Ts.Project;

public static class ConfigFileDiscovery
{
    // Go: configFileRegistryBuilder.computeConfigFileName(..., false, ...).
    // tsconfig.json wins over jsconfig.json in each directory, and the search
    // stops at node_modules rather than escaping a dependency boundary.
    public static string? FindConfigFile(string fileName, global::Ts.Vfs.IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
        {
            return null;
        }

        string directory = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName);
        while (true)
        {
            string tsconfigPath = global::Ts.TsPath.TsPath.CombinePaths(directory, "tsconfig.json");
            if (fileSystem.FileExists(tsconfigPath))
            {
                return tsconfigPath;
            }

            string jsconfigPath = global::Ts.TsPath.TsPath.CombinePaths(directory, "jsconfig.json");
            if (fileSystem.FileExists(jsconfigPath))
            {
                return jsconfigPath;
            }

            if (directory.EndsWith("/node_modules", StringComparison.Ordinal))
            {
                return null;
            }

            string parent = global::Ts.TsPath.TsPath.GetDirectoryPath(directory);
            if (string.Equals(parent, directory, StringComparison.Ordinal))
            {
                return null;
            }

            directory = parent;
        }
    }
}

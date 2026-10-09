// Ported from tsgo internal/project/ata/validatepackagename.go (whole file).

using System;
using System.Globalization;

namespace Ts.Project.Ata;

// validatepackagename.go:10-19 — NameValidationResult.
public enum NameValidationResult
{
    NameOk,
    EmptyName,
    NameTooLong,
    NameStartsWithDot,
    NameStartsWithUnderscore,
    NameContainsNonURISafeCharacters,
}

public static class PackageNameValidation
{
    // validatepackagename.go:21 — maxPackageNameLength.
    internal const int MaxPackageNameLength = 214;

    // validatepackagename.go:23-30 — ValidatePackageName: validates package name using
    // rules defined at https://docs.npmjs.com/files/package.json
    public static (NameValidationResult Result, string Name, bool IsScopeName) ValidatePackageName(string packageName)
    {
        return ValidatePackageNameWorker(packageName /*supportScopedPackage*/, true);
    }

    // validatepackagename.go:32-69 — validatePackageNameWorker.
    internal static (NameValidationResult Result, string Name, bool IsScopeName) ValidatePackageNameWorker(string packageName, bool supportScopedPackage)
    {
        int packageNameLen = packageName.Length;
        if (packageNameLen == 0)
        {
            return (NameValidationResult.EmptyName, "", false);
        }
        if (packageNameLen > MaxPackageNameLength)
        {
            return (NameValidationResult.NameTooLong, "", false);
        }
        char firstChar = packageName[0];
        if (firstChar == '.')
        {
            return (NameValidationResult.NameStartsWithDot, "", false);
        }
        if (firstChar == '_')
        {
            return (NameValidationResult.NameStartsWithUnderscore, "", false);
        }
        // check if name is scope package like: starts with @ and has one '/' in the middle
        // scoped packages are not currently supported
        if (supportScopedPackage)
        {
            if (packageName.StartsWith("@", StringComparison.Ordinal))
            {
                string withoutScope = packageName[1..];
                int slash = withoutScope.IndexOf('/');
                if (slash != -1)
                {
                    string scope = withoutScope[..slash];
                    string scopedPackageName = withoutScope[(slash + 1)..];
                    if (scope.Length > 0 && scopedPackageName.Length > 0 && !scopedPackageName.Contains('/'))
                    {
                        (NameValidationResult scopeResult, _, _) = ValidatePackageNameWorker(scope /*supportScopedPackage*/, false);
                        if (scopeResult != NameValidationResult.NameOk)
                        {
                            return (scopeResult, scope, true);
                        }
                        (NameValidationResult packageResult, string packageName2, _) = ValidatePackageNameWorker(scopedPackageName /*supportScopedPackage*/, false);
                        if (packageResult != NameValidationResult.NameOk)
                        {
                            return (packageResult, scopedPackageName, false);
                        }
                        return (NameValidationResult.NameOk, "", false);
                    }
                }
            }
        }
        // Go: url.QueryEscape(packageName) != packageName.
        if (QueryEscape(packageName) != packageName)
        {
            return (NameValidationResult.NameContainsNonURISafeCharacters, "", false);
        }
        return (NameValidationResult.NameOk, "", false);
    }

    // Go net/url.QueryEscape (escape all reserved characters; space → '+').
    private static string QueryEscape(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') ||
                ch == '-' || ch == '_' || ch == '.' || ch == '~')
            {
                sb.Append(ch);
            }
            else if (ch == ' ')
            {
                sb.Append('+');
            }
            else
            {
                foreach (byte b in System.Text.Encoding.UTF8.GetBytes(ch.ToString()))
                {
                    sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }
        return sb.ToString();
    }

    // validatepackagename.go:72-98 — renderPackageNameValidationFailure.
    internal static string RenderPackageNameValidationFailure(string typing, NameValidationResult result, string name, bool isScopeName)
    {
        string kind = isScopeName ? "Scope" : "Package";
        if (name == "")
        {
            name = typing;
        }
        switch (result)
        {
            case NameValidationResult.EmptyName:
                return string.Format(CultureInfo.InvariantCulture, "'{0}':: {1} name '{2}' cannot be empty", typing, kind, name);
            case NameValidationResult.NameTooLong:
                return string.Format(CultureInfo.InvariantCulture, "'{0}':: {1} name '{2}' should be less than {3} characters", typing, kind, name, MaxPackageNameLength);
            case NameValidationResult.NameStartsWithDot:
                return string.Format(CultureInfo.InvariantCulture, "'{0}':: {1} name '{2}' cannot start with '.'", typing, kind, name);
            case NameValidationResult.NameStartsWithUnderscore:
                return string.Format(CultureInfo.InvariantCulture, "'{0}':: {1} name '{2}' cannot start with '_'", typing, kind, name);
            case NameValidationResult.NameContainsNonURISafeCharacters:
                return string.Format(CultureInfo.InvariantCulture, "'{0}':: {1} name '{2}' contains non URI safe characters", typing, kind, name);
            case NameValidationResult.NameOk:
                throw new InvalidOperationException("Unexpected Ok result");
            default:
                throw new InvalidOperationException("Unknown package name validation result");
        }
    }
}

// Ported from tsgo internal/project/ata/validatepackagename_test.go (whole file).

using System.Text;
using Ts.Project.Ata;
using Xunit;

namespace Ts.Project.Tests.Ata;

public sealed class ValidatePackageNameTests
{
    // validatepackagename_test.go:13-22 — "name cannot be too long".
    [Fact]
    public void ValidatePackageName_NameCannotBeTooLong()
    {
        var packageName = new StringBuilder();
        packageName.Append("a");
        for (int i = 0; i < 8; i++)
        {
            packageName.Append(packageName.ToString());
        }
        (NameValidationResult status, _, _) = PackageNameValidation.ValidatePackageName(packageName.ToString());
        Assert.Equal(NameValidationResult.NameTooLong, status);
    }

    // validatepackagename_test.go:23-27 — "package name cannot start with dot".
    [Fact]
    public void ValidatePackageName_PackageNameCannotStartWithDot()
    {
        (NameValidationResult status, _, _) = PackageNameValidation.ValidatePackageName(".foo");
        Assert.Equal(NameValidationResult.NameStartsWithDot, status);
    }

    // validatepackagename_test.go:28-32 — "package name cannot start with underscore".
    [Fact]
    public void ValidatePackageName_PackageNameCannotStartWithUnderscore()
    {
        (NameValidationResult status, _, _) = PackageNameValidation.ValidatePackageName("_foo");
        Assert.Equal(NameValidationResult.NameStartsWithUnderscore, status);
    }

    // validatepackagename_test.go:33-41 — "package non URI safe characters are not supported".
    [Fact]
    public void ValidatePackageName_PackageNonUriSafeCharactersAreNotSupported()
    {
        (NameValidationResult status, _, _) = PackageNameValidation.ValidatePackageName("  scope  ");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        (status, _, _) = PackageNameValidation.ValidatePackageName("; say ‘Hello from TypeScript!’ #");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        (status, _, _) = PackageNameValidation.ValidatePackageName("a/b/c");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
    }

    // validatepackagename_test.go:42-46 — "scoped package name is supported".
    [Fact]
    public void ValidatePackageName_ScopedPackageNameIsSupported()
    {
        (NameValidationResult status, _, _) = PackageNameValidation.ValidatePackageName("@scope/bar");
        Assert.Equal(NameValidationResult.NameOk, status);
    }

    // validatepackagename_test.go:47-57 — "scoped name in scoped package name cannot start with dot".
    [Fact]
    public void ValidatePackageName_ScopedNameInScopedPackageNameCannotStartWithDot()
    {
        (NameValidationResult status, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName("@.scope/bar");
        Assert.Equal(NameValidationResult.NameStartsWithDot, status);
        Assert.Equal(".scope", name);
        Assert.True(isScopeName);
        (status, name, isScopeName) = PackageNameValidation.ValidatePackageName("@.scope/.bar");
        Assert.Equal(NameValidationResult.NameStartsWithDot, status);
        Assert.Equal(".scope", name);
        Assert.True(isScopeName);
    }

    // validatepackagename_test.go:58-68 — "scoped name in scoped package name cannot start with underscore"
    // (Go reuses the dot subtest name; the underscore sibling follows it verbatim).
    [Fact]
    public void ValidatePackageName_ScopedNameInScopedPackageNameCannotStartWithUnderscore()
    {
        (NameValidationResult status, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName("@_scope/bar");
        Assert.Equal(NameValidationResult.NameStartsWithUnderscore, status);
        Assert.Equal("_scope", name);
        Assert.True(isScopeName);
        (status, name, isScopeName) = PackageNameValidation.ValidatePackageName("@_scope/_bar");
        Assert.Equal(NameValidationResult.NameStartsWithUnderscore, status);
        Assert.Equal("_scope", name);
        Assert.True(isScopeName);
    }

    // validatepackagename_test.go:69-83 — "scope name in scoped package name with non URI safe characters are not supported".
    [Fact]
    public void ValidatePackageName_ScopeNameInScopedPackageNameWithNonUriSafeCharactersAreNotSupported()
    {
        (NameValidationResult status, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName("@  scope  /bar");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        Assert.Equal("  scope  ", name);
        Assert.True(isScopeName);
        (status, name, isScopeName) = PackageNameValidation.ValidatePackageName("@; say ‘Hello from TypeScript!’ #/bar");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        Assert.Equal("; say ‘Hello from TypeScript!’ #", name);
        Assert.True(isScopeName);
        (status, name, isScopeName) = PackageNameValidation.ValidatePackageName("@  scope  /  bar  ");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        Assert.Equal("  scope  ", name);
        Assert.True(isScopeName);
    }

    // validatepackagename_test.go:84-90 — "package name in scoped package name cannot start with dot".
    [Fact]
    public void ValidatePackageName_PackageNameInScopedPackageNameCannotStartWithDot()
    {
        (NameValidationResult status, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName("@scope/.bar");
        Assert.Equal(NameValidationResult.NameStartsWithDot, status);
        Assert.Equal(".bar", name);
        Assert.False(isScopeName);
    }

    // validatepackagename_test.go:91-97 — "package name in scoped package name cannot start with underscore".
    [Fact]
    public void ValidatePackageName_PackageNameInScopedPackageNameCannotStartWithUnderscore()
    {
        (NameValidationResult status, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName("@scope/_bar");
        Assert.Equal(NameValidationResult.NameStartsWithUnderscore, status);
        Assert.Equal("_bar", name);
        Assert.False(isScopeName);
    }

    // validatepackagename_test.go:98-108 — "package name in scoped package name with non URI safe characters are not supported".
    [Fact]
    public void ValidatePackageName_PackageNameInScopedPackageNameWithNonUriSafeCharactersAreNotSupported()
    {
        (NameValidationResult status, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName("@scope/  bar  ");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        Assert.Equal("  bar  ", name);
        Assert.False(isScopeName);
        (status, name, isScopeName) = PackageNameValidation.ValidatePackageName("@scope/; say ‘Hello from TypeScript!’ #");
        Assert.Equal(NameValidationResult.NameContainsNonURISafeCharacters, status);
        Assert.Equal("; say ‘Hello from TypeScript!’ #", name);
        Assert.False(isScopeName);
    }
}

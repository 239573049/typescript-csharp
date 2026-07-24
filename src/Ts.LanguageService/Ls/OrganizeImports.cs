// Ported from tsgo internal/ls/lsutil/organizeimports.go (whole file): the organize-imports
// comparer construction, sort-order detection, and insertion-index helpers shared by
// auto-import fixes and organize-imports.
//
// PORT-NOTE(invariant-globalization): Go's removeDiacritics runs NFD (golang.org/x/text
// norm.NFD) then strips Mn marks. Under InvariantGlobalization=true string.Normalize throws
// for non-ASCII input, so RemoveDiacritics here takes an ASCII fast path (identity, matches
// Go) and for non-ASCII strings strips already-decomposed combining marks (category Mn via
// CharUnicodeInfo, which is culture-independent Unicode data) WITHOUT decomposing precomposed
// characters (e.g. U+00E9 stays intact). This only affects the opt-in "unicode" collation /
// natural-sort presets on non-ASCII specifiers; close with a generated NFD table if needed.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.StringUtil;

namespace Ts.Ls;

internal static class OrganizeImports
{
    // organizeimports.go:17-22 — FilterImportDeclarations filters out non-import declarations
    // from a list of statements.
    internal static Statement[] FilterImportDeclarations(Statement[] statements)
    {
        return CoreUtil.Filter(statements, stmt => stmt.Kind == SyntaxKind.ImportDeclaration)!;
    }

    // organizeimports.go:24-48 — GetDetectionLists returns the lists of comparers and type
    // orders to test for organize imports detection.
    internal static (Func<string, string, int>[] ComparersToTest, OrganizeImportsTypeOrder[] TypeOrdersToTest) GetDetectionLists(UserPreferences preferences)
    {
        Func<string, string, int>[] comparersToTest;
        OrganizeImportsTypeOrder[] typeOrdersToTest;
        if (preferences.OrganizeImportsSort != OrganizeImportsSort.Auto)
        {
            comparersToTest = [GetOrganizeImportsPresetStringComparer(preferences.OrganizeImportsSort)];
        }
        else if (!preferences.OrganizeImportsIgnoreCase.IsUnknown())
        {
            comparersToTest = [GetOrganizeImportsStringComparer(preferences, preferences.OrganizeImportsIgnoreCase.IsTrue())];
        }
        else
        {
            comparersToTest =
            [
                GetOrganizeImportsStringComparer(preferences, true),
                GetOrganizeImportsStringComparer(preferences, false),
            ];
        }

        if (preferences.OrganizeImportsTypeOrder != OrganizeImportsTypeOrder.Auto)
        {
            typeOrdersToTest = [preferences.OrganizeImportsTypeOrder];
        }
        else
        {
            typeOrdersToTest =
            [
                OrganizeImportsTypeOrder.Last,
                OrganizeImportsTypeOrder.Inline,
                OrganizeImportsTypeOrder.First,
            ];
        }

        return (comparersToTest, typeOrdersToTest);
    }

    // organizeimports.go:50-74 — ResolveOrganizeImportsSort.
    internal static OrganizeImportsSort ResolveOrganizeImportsSort(UserPreferences preferences)
    {
        if (preferences.OrganizeImportsSort != OrganizeImportsSort.Auto)
        {
            return preferences.OrganizeImportsSort;
        }

        if (preferences.OrganizeImportsCollation == OrganizeImportsCollation.Unicode)
        {
            switch (preferences.OrganizeImportsIgnoreCase)
            {
                case Tristate.True:
                    return OrganizeImportsSort.NaturalIgnoreCase;
                case Tristate.False:
                    return OrganizeImportsSort.Natural;
                default:
                    return OrganizeImportsSort.Auto;
            }
        }

        switch (preferences.OrganizeImportsIgnoreCase)
        {
            case Tristate.True:
                return OrganizeImportsSort.OrdinalIgnoreCase;
            case Tristate.False:
                return OrganizeImportsSort.Ordinal;
            default:
                return OrganizeImportsSort.Auto;
        }
    }

    // organizeimports.go:76-81 — getOrganizeImportsOrdinalStringComparer.
    private static Func<string, string, int> GetOrganizeImportsOrdinalStringComparer(bool ignoreCase)
    {
        if (ignoreCase)
        {
            return static (a, b) => StringUtil.StringUtil.CompareStringsCaseInsensitiveEslintCompatible(a, b);
        }
        return static (a, b) => StringUtil.StringUtil.CompareStringsCaseSensitive(a, b);
    }

    // organizeimports.go:83-87 — getOrganizeImportsNaturalStringComparer.
    private static Func<string, string, int> GetOrganizeImportsNaturalStringComparer(bool caseSensitive)
    {
        return (a, b) => CompareOrganizeImportsNaturalStrings(a, b, caseSensitive);
    }

    // organizeimports.go:89-97 — getOrganizeImportsUnicodeStringComparer.
    private static Func<string, string, int> GetOrganizeImportsUnicodeStringComparer(bool ignoreCase, UserPreferences preferences)
    {
        var caseFirst = preferences.OrganizeImportsCaseFirst;
        var numeric = preferences.OrganizeImportsNumericCollation.IsTrue();
        var accents = !preferences.OrganizeImportsAccentCollation.IsFalse();

        return (a, b) => CompareOrganizeImportsUnicodeStrings(a, b, ignoreCase, caseFirst, numeric, accents);
    }

    // organizeimports.go:99-111 — compareOrganizeImportsNaturalStrings.
    private static int CompareOrganizeImportsNaturalStrings(string a, string b, bool caseSensitive)
    {
        var cmp = CompareStringsNumeric(NaturalCollationKey(a), NaturalCollationKey(b));
        if (cmp != 0)
        {
            return cmp;
        }

        if (caseSensitive)
        {
            cmp = CompareOrganizeImportsCaseUpperFirst(a, b);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return string.CompareOrdinal(a, b);
    }

    // organizeimports.go:113-131 — compareOrganizeImportsUnicodeStrings.
    private static int CompareOrganizeImportsUnicodeStrings(string a, string b, bool ignoreCase, OrganizeImportsCaseFirst caseFirst, bool numeric, bool accents)
    {
        var cmp = CompareOrganizeImportsUnicodeKeys(NaturalCollationKey(a), NaturalCollationKey(b), numeric);
        if (cmp != 0)
        {
            return cmp;
        }

        if (accents)
        {
            cmp = CompareOrganizeImportsUnicodeKeys(StringUtil.StringUtil.ToLowerJS(a), StringUtil.StringUtil.ToLowerJS(b), numeric);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        if (!ignoreCase)
        {
            cmp = CompareOrganizeImportsCase(a, b, caseFirst);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return string.CompareOrdinal(a, b);
    }

    // organizeimports.go:133-135 — naturalCollationKey.
    private static string NaturalCollationKey(string s)
    {
        return StringUtil.StringUtil.ToLowerJS(RemoveDiacritics(s));
    }

    // organizeimports.go:137-144 — removeDiacritics. See the file-header PORT-NOTE for the
    // invariant-globalization NFD gap.
    private static string RemoveDiacritics(string s)
    {
        var ascii = true;
        foreach (var c in s)
        {
            if (c >= 0x80)
            {
                ascii = false;
                break;
            }
        }
        if (ascii)
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // organizeimports.go:146-151 — compareOrganizeImportsUnicodeKeys.
    private static int CompareOrganizeImportsUnicodeKeys(string a, string b, bool numeric)
    {
        if (numeric)
        {
            return CompareStringsNumeric(a, b);
        }
        return string.CompareOrdinal(a, b);
    }

    // organizeimports.go:153-179 — compareStringsNumeric. (Go walks UTF-8 bytes/runes; here we
    // walk UTF-16 offsets and decode code points — same code-point comparison semantics.)
    private static int CompareStringsNumeric(string a, string b)
    {
        int ai = 0, bi = 0;
        while (ai < a.Length && bi < b.Length)
        {
            if (IsASCIIDigit(a[ai]) && IsASCIIDigit(b[bi]))
            {
                var aRunEnd = AsciiDigitRunEnd(a, ai);
                var bRunEnd = AsciiDigitRunEnd(b, bi);

                var cmpNum = CompareNumericText(a[ai..aRunEnd], b[bi..bRunEnd]);
                if (cmpNum != 0)
                {
                    return cmpNum;
                }

                ai = aRunEnd;
                bi = bRunEnd;
                continue;
            }

            var aRune = char.ConvertToUtf32(a, ai);
            var bRune = char.ConvertToUtf32(b, bi);
            if (aRune != bRune)
            {
                return aRune.CompareTo(bRune);
            }

            ai += char.IsHighSurrogate(a[ai]) ? 2 : 1;
            bi += char.IsHighSurrogate(b[bi]) ? 2 : 1;
        }

        return (a.Length - ai).CompareTo(b.Length - bi);
    }

    // organizeimports.go:181-183 — isASCIIDigit.
    private static bool IsASCIIDigit(char ch)
    {
        return ch >= '0' && ch <= '9';
    }

    // organizeimports.go:185-191 — asciiDigitRunEnd.
    private static int AsciiDigitRunEnd(string s, int start)
    {
        var i = start;
        while (i < s.Length && IsASCIIDigit(s[i]))
        {
            i++;
        }
        return i;
    }

    // organizeimports.go:193-210 — compareNumericText.
    private static int CompareNumericText(string a, string b)
    {
        var aDigits = a.TrimStart('0');
        var bDigits = b.TrimStart('0');
        if (aDigits.Length == 0)
        {
            aDigits = "0";
        }
        if (bDigits.Length == 0)
        {
            bDigits = "0";
        }

        if (aDigits.Length != bDigits.Length)
        {
            return aDigits.Length.CompareTo(bDigits.Length);
        }
        var cmp = string.CompareOrdinal(aDigits, bDigits);
        if (cmp != 0)
        {
            return cmp;
        }
        return string.CompareOrdinal(a, b);
    }

    // organizeimports.go:212-214 — compareOrganizeImportsCaseUpperFirst.
    private static int CompareOrganizeImportsCaseUpperFirst(string a, string b)
    {
        return CompareOrganizeImportsCase(a, b, OrganizeImportsCaseFirst.Upper);
    }

    // organizeimports.go:216-246 — compareOrganizeImportsCase. (Go converts to []rune and
    // compares rune-wise; we walk code points. Rune.IsUpper covers category Lu; Go
    // unicode.IsUpper additionally includes Other_Uppercase — negligible for specifiers.)
    private static int CompareOrganizeImportsCase(string a, string b, OrganizeImportsCaseFirst caseFirst)
    {
        int ai = 0, bi = 0;
        int aLen = 0, bLen = 0;
        while (ai < a.Length && bi < b.Length)
        {
            var aRune = new System.Text.Rune(char.ConvertToUtf32(a, ai));
            var bRune = new System.Text.Rune(char.ConvertToUtf32(b, bi));
            var aUpper = System.Text.Rune.IsUpper(aRune);
            var bUpper = System.Text.Rune.IsUpper(bRune);
            if (aUpper != bUpper)
            {
                switch (caseFirst)
                {
                    case OrganizeImportsCaseFirst.Upper:
                        return aUpper ? -1 : 1;
                    case OrganizeImportsCaseFirst.Lower:
                        return !aUpper ? -1 : 1;
                    default:
                        return aUpper ? 1 : -1;
                }
            }
            ai += aRune.Utf16SequenceLength;
            bi += bRune.Utf16SequenceLength;
        }

        // cmp.Compare(len(aRunes), len(bRunes)) — count remaining code points.
        aLen = CodePointLength(a);
        bLen = CodePointLength(b);
        return aLen.CompareTo(bLen);
    }

    private static int CodePointLength(string s)
    {
        var n = 0;
        var i = 0;
        while (i < s.Length)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            n++;
        }
        return n;
    }

    // organizeimports.go:248-259 — getOrganizeImportsPresetStringComparer.
    private static Func<string, string, int> GetOrganizeImportsPresetStringComparer(OrganizeImportsSort sort)
    {
        switch (sort)
        {
            case OrganizeImportsSort.OrdinalIgnoreCase:
                return GetOrganizeImportsOrdinalStringComparer(true);
            case OrganizeImportsSort.Natural:
                return GetOrganizeImportsNaturalStringComparer(true);
            case OrganizeImportsSort.NaturalIgnoreCase:
                return GetOrganizeImportsNaturalStringComparer(false);
            default:
                return GetOrganizeImportsOrdinalStringComparer(false);
        }
    }

    // organizeimports.go:261-269 — getOrganizeImportsStringComparer.
    private static Func<string, string, int> GetOrganizeImportsStringComparer(UserPreferences preferences, bool ignoreCase)
    {
        if (preferences.OrganizeImportsSort != OrganizeImportsSort.Auto)
        {
            return GetOrganizeImportsPresetStringComparer(preferences.OrganizeImportsSort);
        }
        if (preferences.OrganizeImportsCollation == OrganizeImportsCollation.Unicode)
        {
            return GetOrganizeImportsUnicodeStringComparer(ignoreCase, preferences);
        }
        return GetOrganizeImportsOrdinalStringComparer(ignoreCase);
    }

    // organizeimports.go:271-296 — getModuleSpecifierExpression.
    private static Expression? GetModuleSpecifierExpression(Statement declaration)
    {
        switch (declaration.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
                var importEquals = declaration.AsImportEqualsDeclaration();
                if (importEquals.ModuleReference!.Kind == SyntaxKind.ExternalModuleReference)
                {
                    return importEquals.ModuleReference!.Expression();
                }
                return null;
            case SyntaxKind.ImportDeclaration:
                return declaration.ModuleSpecifier();
            case SyntaxKind.VariableStatement:
                var declarations = declaration.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes;
                if (declarations.Length > 0)
                {
                    var initializer = declarations[0].Initializer();
                    if (initializer != null && initializer.Kind == SyntaxKind.CallExpression)
                    {
                        var callExpr = initializer.AsCallExpression();
                        if (callExpr.Arguments!.Nodes.Length > 0)
                        {
                            return callExpr.Arguments.Nodes[0];
                        }
                    }
                }
                return null;
            default:
                return null;
        }
    }

    // organizeimports.go:298-304 — GetExternalModuleName returns the module name from a module
    // specifier expression.
    internal static string GetExternalModuleName(Expression? specifier)
    {
        if (specifier != null && AstUtilities.IsStringLiteralLike(specifier))
        {
            return specifier.Text();
        }
        return "";
    }

    // organizeimports.go:306-317 — CompareModuleSpecifiers compares two module specifiers using
    // the given comparer.
    internal static int CompareModuleSpecifiers(Expression? m1, Expression? m2, Func<string, string, int> comparer)
    {
        var name1 = GetExternalModuleName(m1);
        var name2 = GetExternalModuleName(m2);
        var cmp = CoreUtil.CompareBooleans(name1.Length == 0, name2.Length == 0);
        if (cmp != 0)
        {
            return cmp;
        }
        cmp = CoreUtil.CompareBooleans(global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(name1), global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(name2));
        if (cmp != 0)
        {
            return cmp;
        }
        return comparer(name1, name2);
    }

    // organizeimports.go:319-321 — compareImportKind.
    private static int CompareImportKind(Statement s1, Statement s2)
    {
        return GetImportKindOrder(s1).CompareTo(GetImportKindOrder(s2));
    }

    // organizeimports.go:323-340 — getImportKindOrder returns the sort order for different
    // import kinds:
    // 1. Side-effect imports
    // 2. Type-only imports
    // 3. Namespace imports
    // 4. Default imports
    // 5. Named imports
    // 6. ImportEqualsDeclarations
    // 7. Require variable statements
    private const int importKindOrderSideEffect = 0;
    private const int importKindOrderTypeOnly = 1;
    private const int importKindOrderNamespace = 2;
    private const int importKindOrderDefault = 3;
    private const int importKindOrderNamed = 4;
    private const int importKindOrderImportEquals = 5;
    private const int importKindOrderRequire = 6;
    private const int importKindOrderUnknown = 7;

    // organizeimports.go:342-367 — getImportKindOrder.
    private static int GetImportKindOrder(Statement s1)
    {
        switch (s1.Kind)
        {
            case SyntaxKind.ImportDeclaration:
                var importDecl = s1.AsImportDeclaration();
                if (importDecl.ImportClause == null)
                {
                    return importKindOrderSideEffect;
                }
                var importClause = importDecl.ImportClause.AsImportClause();
                if (importClause.IsTypeOnly())
                {
                    return importKindOrderTypeOnly;
                }
                if (importClause.NamedBindings != null && importClause.NamedBindings.Kind == SyntaxKind.NamespaceImport)
                {
                    return importKindOrderNamespace;
                }
                if (importClause.Name() != null)
                {
                    return importKindOrderDefault;
                }
                return importKindOrderNamed;
            case SyntaxKind.ImportEqualsDeclaration:
                return importKindOrderImportEquals;
            case SyntaxKind.VariableStatement:
                return importKindOrderRequire;
            default:
                return importKindOrderUnknown;
        }
    }

    // organizeimports.go:369-375 — CompareImportsOrRequireStatements compares two import or
    // require statements.
    internal static int CompareImportsOrRequireStatements(Statement s1, Statement s2, Func<string, string, int> comparer)
    {
        var cmp = CompareModuleSpecifiers(GetModuleSpecifierExpression(s1), GetModuleSpecifierExpression(s2), comparer);
        if (cmp != 0)
        {
            return cmp;
        }
        return CompareImportKind(s1, s2);
    }

    // organizeimports.go:377-397 — compareImportOrExportSpecifiers.
    private static int CompareImportOrExportSpecifiers(Node s1, Node s2, Func<string, string, int> comparer, UserPreferences preferences)
    {
        var typeOrder = preferences.OrganizeImportsTypeOrder;

        var s1Name = s1.Name()!.Text();
        var s2Name = s2.Name()!.Text();

        switch (typeOrder)
        {
            case OrganizeImportsTypeOrder.First:
            {
                var cmp = CoreUtil.CompareBooleans(s2.IsTypeOnly(), s1.IsTypeOnly());
                if (cmp != 0)
                {
                    return cmp;
                }
                return comparer(s1Name, s2Name);
            }
            case OrganizeImportsTypeOrder.Inline:
                return comparer(s1Name, s2Name);
            default: // OrganizeImportsTypeOrderLast
            {
                var cmp = CoreUtil.CompareBooleans(s1.IsTypeOnly(), s2.IsTypeOnly());
                if (cmp != 0)
                {
                    return cmp;
                }
                return comparer(s1Name, s2Name);
            }
        }
    }

    // organizeimports.go:399-411 — GetNamedImportSpecifierComparer returns a comparer function
    // for sorting import specifiers.
    internal static Func<Node, Node, int> GetNamedImportSpecifierComparer(UserPreferences preferences, Func<string, string, int>? comparer)
    {
        if (comparer == null)
        {
            var ignoreCase = false;
            if (!preferences.OrganizeImportsIgnoreCase.IsUnknown())
            {
                ignoreCase = preferences.OrganizeImportsIgnoreCase.IsTrue();
            }
            comparer = GetOrganizeImportsStringComparer(preferences, ignoreCase);
        }
        return (s1, s2) => CompareImportOrExportSpecifiers(s1, s2, comparer, preferences);
    }

    // organizeimports.go:413-418 — GetImportSpecifierInsertionIndex returns the index at which
    // to insert a new import specifier.
    internal static int GetImportSpecifierInsertionIndex(Node[] sortedImports, Node newImport, Func<Node, Node, int> comparer)
    {
        return Core.Core.BinarySearchUniqueFunc<Node>(sortedImports, (mid, value) => comparer(value, newImport)).Index;
    }

    // organizeimports.go:420-425 — GetImportDeclarationInsertIndex returns the index at which to
    // insert a new import declaration.
    internal static int GetImportDeclarationInsertIndex(Statement[] sortedImports, Statement newImport, Func<Statement, Statement, int> comparer)
    {
        return Core.Core.BinarySearchUniqueFunc<Statement>(sortedImports, (mid, value) => comparer(value, newImport)).Index;
    }

    // organizeimports.go:427-431 — GetOrganizeImportsStringComparerWithDetection returns a
    // string comparer based on detecting the order of import statements by the module specifier.
    internal static (Func<string, string, int> Comparer, bool IsSorted) GetOrganizeImportsStringComparerWithDetection(Statement[] originalImportDecls, UserPreferences preferences)
    {
        return DetectModuleSpecifierCaseBySort([originalImportDecls], GetComparers(preferences));
    }

    // organizeimports.go:433-445 — getComparers.
    private static Func<string, string, int>[] GetComparers(UserPreferences preferences)
    {
        if (preferences.OrganizeImportsSort != OrganizeImportsSort.Auto || !preferences.OrganizeImportsIgnoreCase.IsUnknown())
        {
            var ignoreCase = false;
            if (!preferences.OrganizeImportsIgnoreCase.IsUnknown())
            {
                ignoreCase = preferences.OrganizeImportsIgnoreCase.IsTrue();
            }
            return [GetOrganizeImportsStringComparer(preferences, ignoreCase)];
        }
        return
        [
            GetOrganizeImportsStringComparer(preferences, true),
            GetOrganizeImportsStringComparer(preferences, false),
        ];
    }

    // organizeimports.go:447-451 — namedImportSortResult.
    private sealed class NamedImportSortResult
    {
        internal Func<string, string, int> namedImportComparer = null!;
        internal OrganizeImportsTypeOrder typeOrder;
        internal bool isSorted;
    }

    // organizeimports.go:453-464 — DetectNamedImportOrganizationBySort detects the order of
    // named imports throughout the file by considering the named imports in each statement as
    // a group.
    internal static (Func<string, string, int>? Comparer, OrganizeImportsTypeOrder TypeOrder, bool Found) DetectNamedImportOrganizationBySort(
        Statement[] originalGroups,
        Func<string, string, int>[] comparersToTest,
        OrganizeImportsTypeOrder[] typesToTest)
    {
        var result = DetectNamedImportOrganizationBySortWorker(originalGroups, comparersToTest, typesToTest);
        if (result == null)
        {
            return (null, OrganizeImportsTypeOrder.Last, false);
        }
        return (result.namedImportComparer, result.typeOrder, true);
    }

    // organizeimports.go:466-595 — detectNamedImportOrganizationBySort.
    private static NamedImportSortResult? DetectNamedImportOrganizationBySortWorker(
        Statement[] originalGroups,
        Func<string, string, int>[] comparersToTest,
        OrganizeImportsTypeOrder[] typesToTest)
    {
        var bothNamedImports = false;
        var importDeclsWithNamed = new List<Statement>();

        foreach (var imp in originalGroups)
        {
            if (imp.AsImportDeclaration().ImportClause == null)
            {
                continue;
            }
            var clause = imp.AsImportDeclaration().ImportClause!.AsImportClause();
            if (clause.NamedBindings == null || clause.NamedBindings.Kind != SyntaxKind.NamedImports)
            {
                continue;
            }
            var namedImports = clause.NamedBindings.AsNamedImports();
            if (namedImports.Elements!.Nodes.Length == 0)
            {
                continue;
            }

            if (!bothNamedImports)
            {
                var hasTypeOnly = false;
                var hasRegular = false;
                foreach (var elem in namedImports.Elements.Nodes)
                {
                    if (elem.IsTypeOnly())
                    {
                        hasTypeOnly = true;
                    }
                    else
                    {
                        hasRegular = true;
                    }
                }
                if (hasTypeOnly && hasRegular)
                {
                    bothNamedImports = true;
                }
            }

            importDeclsWithNamed.Add(imp);
        }

        if (importDeclsWithNamed.Count == 0)
        {
            return null;
        }

        var namedImportsByDecl = new List<Node[]>(importDeclsWithNamed.Count);
        foreach (var imp in importDeclsWithNamed)
        {
            var clause = imp.AsImportDeclaration().ImportClause!.AsImportClause();
            var namedImports = clause.NamedBindings!.AsNamedImports();
            namedImportsByDecl.Add(namedImports.Elements!.Nodes);
        }

        if (!bothNamedImports || typesToTest.Length == 0)
        {
            var namesList = new List<string[]>(namedImportsByDecl.Count);
            foreach (var imports in namedImportsByDecl)
            {
                var names = new string[imports.Length];
                for (var j = 0; j < imports.Length; j++)
                {
                    names[j] = imports[j].Name()!.Text();
                }
                namesList.Add(names);
            }
            var sortState = DetectCaseSensitivityBySort(namesList, comparersToTest);
            var typeOrder = OrganizeImportsTypeOrder.Last;
            if (typesToTest.Length == 1)
            {
                typeOrder = typesToTest[0];
            }
            return new NamedImportSortResult
            {
                namedImportComparer = sortState.Comparer!,
                typeOrder = typeOrder,
                isSorted = sortState.IsSorted,
            };
        }

        // map-iter: 序无关 (bestDiff/bestComparer keyed lookups only, iterated via typesToTest)
        var bestDiff = new Dictionary<OrganizeImportsTypeOrder, int>
        {
            [OrganizeImportsTypeOrder.First] = int.MaxValue,
            [OrganizeImportsTypeOrder.Last] = int.MaxValue,
            [OrganizeImportsTypeOrder.Inline] = int.MaxValue,
        };
        var bestComparer = new Dictionary<OrganizeImportsTypeOrder, Func<string, string, int>>
        {
            [OrganizeImportsTypeOrder.First] = comparersToTest[0],
            [OrganizeImportsTypeOrder.Last] = comparersToTest[0],
            [OrganizeImportsTypeOrder.Inline] = comparersToTest[0],
        };

        foreach (var curComparer in comparersToTest)
        {
            var currDiff = new Dictionary<OrganizeImportsTypeOrder, int>
            {
                [OrganizeImportsTypeOrder.First] = 0,
                [OrganizeImportsTypeOrder.Last] = 0,
                [OrganizeImportsTypeOrder.Inline] = 0,
            };

            foreach (var importDecl in namedImportsByDecl)
            {
                foreach (var typeOrder in typesToTest)
                {
                    var prefs = new UserPreferences { OrganizeImportsTypeOrder = typeOrder };
                    var diff = MeasureSortedness(importDecl, (n1, n2) => CompareImportOrExportSpecifiers(n1, n2, curComparer, prefs));
                    currDiff[typeOrder] = currDiff[typeOrder] + diff;
                }
            }

            foreach (var typeOrder in typesToTest)
            {
                if (currDiff[typeOrder] < bestDiff[typeOrder])
                {
                    bestDiff[typeOrder] = currDiff[typeOrder];
                    bestComparer[typeOrder] = curComparer;
                }
            }
        }

        foreach (var bestTypeOrder in typesToTest)
        {
            var isBest = true;
            foreach (var testTypeOrder in typesToTest)
            {
                if (bestDiff[testTypeOrder] < bestDiff[bestTypeOrder])
                {
                    isBest = false;
                    break;
                }
            }
            if (isBest)
            {
                return new NamedImportSortResult
                {
                    namedImportComparer = bestComparer[bestTypeOrder],
                    typeOrder = bestTypeOrder,
                    isSorted = bestDiff[bestTypeOrder] == 0,
                };
            }
        }

        return new NamedImportSortResult
        {
            namedImportComparer = bestComparer[OrganizeImportsTypeOrder.Last],
            typeOrder = OrganizeImportsTypeOrder.Last,
            isSorted = bestDiff[OrganizeImportsTypeOrder.Last] == 0,
        };
    }

    // organizeimports.go:597-600 — caseSensitivityDetectionResult.

    // organizeimports.go:602-618 — DetectModuleSpecifierCaseBySort detects the order of module
    // specifiers based on import statements throughout the module/file.
    internal static (Func<string, string, int> Comparer, bool IsSorted) DetectModuleSpecifierCaseBySort(List<Statement[]> importDeclsByGroup, Func<string, string, int>[] comparersToTest)
    {
        var moduleSpecifiersByGroup = new List<string[]>(importDeclsByGroup.Count);
        foreach (var importGroup in importDeclsByGroup)
        {
            var moduleNames = new string[importGroup.Length];
            for (var i = 0; i < importGroup.Length; i++)
            {
                var expr = GetModuleSpecifierExpression(importGroup[i]);
                moduleNames[i] = expr != null ? GetExternalModuleName(expr) : "";
            }
            moduleSpecifiersByGroup.Add(moduleNames);
        }
        var result = DetectCaseSensitivityBySort(moduleSpecifiersByGroup, comparersToTest);
        return (result.Comparer!, result.IsSorted);
    }

    // organizeimports.go:620-649 — detectCaseSensitivityBySort.
    private static (Func<string, string, int>? Comparer, bool IsSorted) DetectCaseSensitivityBySort(List<string[]> originalGroups, Func<string, string, int>[] comparersToTest)
    {
        Func<string, string, int>? bestComparer = null;
        var bestDiff = int.MaxValue;

        foreach (var curComparer in comparersToTest)
        {
            var diffOfCurrentComparer = 0;

            foreach (var listToSort in originalGroups)
            {
                if (listToSort.Length <= 1)
                {
                    continue;
                }
                var diff = MeasureSortedness(listToSort, (a, b) => curComparer(a, b));
                diffOfCurrentComparer += diff;
            }

            if (diffOfCurrentComparer < bestDiff)
            {
                bestDiff = diffOfCurrentComparer;
                bestComparer = curComparer;
            }
        }

        if (bestComparer == null && comparersToTest.Length > 0)
        {
            bestComparer = comparersToTest[0];
        }

        return (bestComparer, bestDiff == 0);
    }

    // organizeimports.go:651-659 — measureSortedness.
    private static int MeasureSortedness<T>(T[] arr, Func<T, T, int> comparer)
    {
        var i = 0;
        for (var j = 0; j < arr.Length - 1; j++)
        {
            if (comparer(arr[j], arr[j + 1]) > 0)
            {
                i++;
            }
        }
        return i;
    }

    // organizeimports.go:661-695 — GetNamedImportSpecifierComparerWithDetection returns a
    // specifier comparer based on detecting the existing sort order within a single import
    // statement.
    internal static (Func<Node, Node, int> SpecifierComparer, Tristate IsSorted) GetNamedImportSpecifierComparerWithDetection(Node importDecl, SourceFile? sourceFile, UserPreferences preferences)
    {
        var (comparersToTest, typeOrdersToTest) = GetDetectionLists(preferences);

        Statement? importStmt = null;
        if (importDecl.Kind == SyntaxKind.ImportDeclaration)
        {
            importStmt = importDecl;
        }

        var specifierComparer = GetNamedImportSpecifierComparer(preferences, comparersToTest[0]);
        var isSorted = Tristate.Unknown;

        if ((ResolveOrganizeImportsSort(preferences) == OrganizeImportsSort.Auto || preferences.OrganizeImportsTypeOrder == OrganizeImportsTypeOrder.Auto) && importStmt != null)
        {
            var detectFromDecl = DetectNamedImportOrganizationBySortWorker([importStmt], comparersToTest, typeOrdersToTest);
            if (detectFromDecl != null)
            {
                isSorted = TristateExtensions.BoolToTristate(detectFromDecl.isSorted);
                specifierComparer = GetNamedImportSpecifierComparer(
                    new UserPreferences { OrganizeImportsTypeOrder = detectFromDecl.typeOrder },
                    detectFromDecl.namedImportComparer);
            }
            else if (sourceFile != null)
            {
                var allImports = FilterImportDeclarations(sourceFile.Statements!.Nodes);
                var detectFromFile = DetectNamedImportOrganizationBySortWorker(allImports, comparersToTest, typeOrdersToTest);
                if (detectFromFile != null)
                {
                    isSorted = TristateExtensions.BoolToTristate(detectFromFile.isSorted);
                    specifierComparer = GetNamedImportSpecifierComparer(
                        new UserPreferences { OrganizeImportsTypeOrder = detectFromFile.typeOrder },
                        detectFromFile.namedImportComparer);
                }
            }
        }

        return (specifierComparer, isSorted);
    }
}

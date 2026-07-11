// Ported from tsgo internal/scanner/regexp.go
//
// PORT-NOTE (UTF-16): the source text is natively UTF-16 (like Strada), so scanSourceCharacter
// reads one UTF-16 code unit at a time and Go's `pendingLowSurrogate` bookkeeping — which
// existed solely to split a non-BMP rune into UTF-16 surrogate units on UTF-8 text — is
// unnecessary and dropped. This yields identical observable behavior to tsgo.

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;

namespace Ts.Scanner;

// PORT-NOTE: pin the class names shadowed by their sibling namespaces (see Scanner.cs).
using Diagnostics = Ts.Diagnostics.Diagnostics;
using StringUtil = Ts.StringUtil.StringUtil;

[Flags]
internal enum RegularExpressionFlags
{
    None = 0,
    HasIndices = 1 << 0, // d
    Global = 1 << 1, // g
    IgnoreCase = 1 << 2, // i
    Multiline = 1 << 3, // m
    DotAll = 1 << 4, // s
    Unicode = 1 << 5, // u
    UnicodeSets = 1 << 6, // v
    Sticky = 1 << 7, // y
    AnyUnicodeMode = Unicode | UnicodeSets,
    Modifiers = IgnoreCase | Multiline | DotAll,
}

internal enum ClassSetExpressionType
{
    Unknown,
    ClassUnion,
    ClassIntersection,
    ClassSubtraction,
}

internal readonly record struct GroupNameReference(int Pos, int End, string Name);

internal readonly record struct DecimalEscapeValue(int Pos, int End, int Value);

public sealed partial class Scanner
{
    // Ported from tsgo internal/scanner/regexp.go:50 (checkRegularExpressionFlagAvailability)
    internal void CheckRegularExpressionFlagAvailability(RegularExpressionFlags flag, int pos, int size)
    {
        if (RegExpParser.RegExpFlagToFirstAvailableLanguageVersion.TryGetValue(flag, out var availableFrom) && LanguageVersion() < availableFrom)
        {
            ErrorAt(Diagnostics.This_regular_expression_flag_is_only_available_when_targeting_0_or_later, pos, size, StringUtil.ToLowerJS(availableFrom.ToString()));
        }
    }
}

// Ported from tsgo internal/scanner/regexp.go:77 (regExpParser)
internal sealed class RegExpParser
{
    // Ported from tsgo internal/scanner/regexp.go:33 (charCodeToRegExpFlag)
    internal static readonly Dictionary<int, RegularExpressionFlags> CharCodeToRegExpFlag = new()
    {
        ['d'] = RegularExpressionFlags.HasIndices,
        ['g'] = RegularExpressionFlags.Global,
        ['i'] = RegularExpressionFlags.IgnoreCase,
        ['m'] = RegularExpressionFlags.Multiline,
        ['s'] = RegularExpressionFlags.DotAll,
        ['u'] = RegularExpressionFlags.Unicode,
        ['v'] = RegularExpressionFlags.UnicodeSets,
        ['y'] = RegularExpressionFlags.Sticky,
    };

    // Ported from tsgo internal/scanner/regexp.go:44 (regExpFlagToFirstAvailableLanguageVersion)
    internal static readonly Dictionary<RegularExpressionFlags, ScriptTarget> RegExpFlagToFirstAvailableLanguageVersion = new()
    {
        [RegularExpressionFlags.HasIndices] = ScriptTarget.ES2022,
        [RegularExpressionFlags.DotAll] = ScriptTarget.ES2018,
        [RegularExpressionFlags.UnicodeSets] = ScriptTarget.ES2024,
    };

    private readonly Scanner scanner;
    internal int End;
    internal RegularExpressionFlags RegExpFlags;
    internal bool AnyUnicodeMode;
    internal bool UnicodeSetsMode;
    internal bool AnnexB;

    private bool anyUnicodeModeOrNonAnnexB;
    internal bool NamedCaptureGroups;

    // See scanClassSetExpression.
    private bool mayContainStrings;
    // The number of all (named and unnamed) capturing groups defined in the regex.
    private int numberOfCapturingGroups;
    // All named capturing groups defined in the regex.
    private readonly HashSet<string> groupSpecifiers = new();
    // All references to named capturing groups in the regex.
    private readonly List<GroupNameReference> groupNameReferences = new();
    // All numeric backreferences within the regex.
    private readonly List<DecimalEscapeValue> decimalEscapes = new();
    // A stack of scopes for named capturing groups. See scanGroupName.
    private readonly List<HashSet<string>> namedCapturingGroups = new();

    internal RegExpParser(Scanner scanner)
    {
        this.scanner = scanner;
    }

    private int Pos()
    {
        return scanner.pos;
    }

    private void SetPos(int v)
    {
        scanner.pos = v;
    }

    private void IncPos(int n)
    {
        scanner.pos += n;
    }

    // PORT-NOTE: Go's setPos helper is never called; omitted.

    private int Char()
    {
        return scanner.Char();
    }

    private int CharAt(int pos)
    {
        return scanner.CharAt(pos - Pos());
    }

    private void Error(Message msg, int pos, int length, params object?[] args)
    {
        scanner.ErrorAt(msg, pos, length, args);
    }

    private string Text()
    {
        return scanner.text;
    }

    // Ported from tsgo internal/scanner/regexp.go:136 (compareDecimalStrings)
    private static int CompareDecimalStrings(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        if (a.Length == 0)
        {
            a = "0";
        }
        if (b.Length == 0)
        {
            b = "0";
        }
        if (a.Length != b.Length)
        {
            if (a.Length < b.Length)
            {
                return -1;
            }
            return 1;
        }
        return string.CompareOrdinal(a, b);
    }

    // Disjunction ::= Alternative ('|' Alternative)*
    // Ported from tsgo internal/scanner/regexp.go:155 (scanDisjunction)
    private void ScanDisjunction(bool isInGroup)
    {
        for (; ; )
        {
            namedCapturingGroups.Add(new HashSet<string>());
            ScanAlternative(isInGroup);
            namedCapturingGroups.RemoveAt(namedCapturingGroups.Count - 1);
            if (Char() != '|')
            {
                return;
            }
            IncPos(1);
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:205 (scanAlternative)
    private void ScanAlternative(bool isInGroup)
    {
        bool isPreviousTermQuantifiable = false;
        while (Pos() < End)
        {
            int start = Pos();
            int ch = Char();
            switch (ch)
            {
                case '^':
                case '$':
                    IncPos(1);
                    isPreviousTermQuantifiable = false;
                    break;
                case '\\':
                    IncPos(1);
                    switch (Char())
                    {
                        case 'b':
                        case 'B':
                            IncPos(1);
                            isPreviousTermQuantifiable = false;
                            break;
                        default:
                            ScanAtomEscape();
                            isPreviousTermQuantifiable = true;
                            break;
                    }
                    break;
                case '(':
                    IncPos(1);
                    if (Char() == '?')
                    {
                        IncPos(1);
                        switch (Char())
                        {
                            case '=':
                            case '!':
                                IncPos(1);
                                // In Annex B, `(?=Disjunction)` and `(?!Disjunction)` are quantifiable
                                isPreviousTermQuantifiable = !anyUnicodeModeOrNonAnnexB;
                                break;
                            case '<':
                                {
                                    int groupNameStart = Pos();
                                    IncPos(1);
                                    switch (Char())
                                    {
                                        case '=':
                                        case '!':
                                            IncPos(1);
                                            isPreviousTermQuantifiable = false;
                                            break;
                                        default:
                                            ScanGroupName(false /*isReference*/);
                                            ScanExpectedChar('>');
                                            if (scanner.LanguageVersion() < ScriptTarget.ES2018)
                                            {
                                                Error(Diagnostics.Named_capturing_groups_are_only_available_when_targeting_ES2018_or_later, groupNameStart, Pos() - groupNameStart);
                                            }
                                            numberOfCapturingGroups++;
                                            isPreviousTermQuantifiable = true;
                                            break;
                                    }
                                    break;
                                }
                            default:
                                {
                                    int flagsStart = Pos();
                                    RegularExpressionFlags setFlags = ScanPatternModifiers(RegularExpressionFlags.None);
                                    if (Char() == '-')
                                    {
                                        IncPos(1);
                                        ScanPatternModifiers(setFlags);
                                        if (Pos() == flagsStart + 1)
                                        {
                                            Error(Diagnostics.Subpattern_flags_must_be_present_when_there_is_a_minus_sign, flagsStart, Pos() - flagsStart);
                                        }
                                    }
                                    ScanExpectedChar(':');
                                    isPreviousTermQuantifiable = true;
                                    break;
                                }
                        }
                    }
                    else
                    {
                        numberOfCapturingGroups++;
                        isPreviousTermQuantifiable = true;
                    }
                    ScanDisjunction(true /*isInGroup*/);
                    ScanExpectedChar(')');
                    break;
                case '{':
                    {
                        IncPos(1);
                        int digitsStart = Pos();
                        ScanDigits();
                        string minStr = scanner.tokenValue;
                        if (!anyUnicodeModeOrNonAnnexB && minStr.Length == 0)
                        {
                            isPreviousTermQuantifiable = true;
                            continue;
                        }
                        if (Char() == ',')
                        {
                            IncPos(1);
                            ScanDigits();
                            string maxStr = scanner.tokenValue;
                            if (minStr.Length == 0)
                            {
                                if (maxStr.Length != 0 || Char() == '}')
                                {
                                    Error(Diagnostics.Incomplete_quantifier_Digit_expected, digitsStart, 0);
                                }
                                else
                                {
                                    Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, start, 1, Scanner.RuneToString(ch));
                                    isPreviousTermQuantifiable = true;
                                    continue;
                                }
                            }
                            else if (maxStr.Length != 0)
                            {
                                if (CompareDecimalStrings(minStr, maxStr) > 0 && (anyUnicodeModeOrNonAnnexB || Char() == '}'))
                                {
                                    Error(Diagnostics.Numbers_out_of_order_in_quantifier, digitsStart, Pos() - digitsStart);
                                }
                            }
                        }
                        else if (minStr.Length == 0)
                        {
                            if (anyUnicodeModeOrNonAnnexB)
                            {
                                Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, start, 1, Scanner.RuneToString(ch));
                            }
                            isPreviousTermQuantifiable = true;
                            continue;
                        }
                        if (Char() != '}')
                        {
                            if (anyUnicodeModeOrNonAnnexB)
                            {
                                Error(Diagnostics.X_0_expected, Pos(), 0, "}");
                                IncPos(-1);
                            }
                            else
                            {
                                isPreviousTermQuantifiable = true;
                                continue;
                            }
                        }
                        goto case '*';
                    }
                case '*':
                case '+':
                case '?':
                    IncPos(1);
                    if (Char() == '?')
                    {
                        // Non-greedy
                        IncPos(1);
                    }
                    if (!isPreviousTermQuantifiable)
                    {
                        Error(Diagnostics.There_is_nothing_available_for_repetition, start, Pos() - start);
                    }
                    isPreviousTermQuantifiable = false;
                    break;
                case '.':
                    IncPos(1);
                    isPreviousTermQuantifiable = true;
                    break;
                case '[':
                    IncPos(1);
                    if (UnicodeSetsMode)
                    {
                        ScanClassSetExpression();
                    }
                    else
                    {
                        ScanClassRanges();
                    }
                    ScanExpectedChar(']');
                    isPreviousTermQuantifiable = true;
                    break;
                case ')':
                    if (isInGroup)
                    {
                        return;
                    }
                    goto case ']';
                case ']':
                case '}':
                    if (anyUnicodeModeOrNonAnnexB || ch == ')')
                    {
                        Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos(), 1, Scanner.RuneToString(ch));
                    }
                    IncPos(1);
                    isPreviousTermQuantifiable = true;
                    break;
                case '/':
                case '|':
                    return;
                default:
                    ScanSourceCharacter();
                    isPreviousTermQuantifiable = true;
                    break;
            }
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:354 (scanPatternModifiers)
    private RegularExpressionFlags ScanPatternModifiers(RegularExpressionFlags currFlags)
    {
        while (Pos() < End)
        {
            var (ch, size) = StringUtil.DecodeRuneAt(Text(), Pos());
            if (ch == StringUtil.RuneError || !Scanner.IsIdentifierPart(ch))
            {
                break;
            }
            if (!CharCodeToRegExpFlag.TryGetValue(ch, out var flag))
            {
                Error(Diagnostics.Unknown_regular_expression_flag, Pos(), size);
            }
            else if ((currFlags & flag) != 0)
            {
                Error(Diagnostics.Duplicate_regular_expression_flag, Pos(), size);
            }
            else if ((flag & RegularExpressionFlags.Modifiers) == 0)
            {
                Error(Diagnostics.This_regular_expression_flag_cannot_be_toggled_within_a_subpattern, Pos(), size);
            }
            else
            {
                currFlags |= flag;
                scanner.CheckRegularExpressionFlagAvailability(flag, Pos(), size);
            }
            IncPos(size);
        }
        return currFlags;
    }

    // Ported from tsgo internal/scanner/regexp.go:382 (scanAtomEscape)
    private void ScanAtomEscape()
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '\\');
        switch (Char())
        {
            case 'k':
                IncPos(1);
                if (Char() == '<')
                {
                    IncPos(1);
                    ScanGroupName(true /*isReference*/);
                    ScanExpectedChar('>');
                }
                else if (anyUnicodeModeOrNonAnnexB || NamedCaptureGroups)
                {
                    Error(Diagnostics.X_k_must_be_followed_by_a_capturing_group_name_enclosed_in_angle_brackets, Pos() - 2, 2);
                }
                break;
            case 'q':
                if (UnicodeSetsMode)
                {
                    IncPos(1);
                    Error(Diagnostics.X_q_is_only_available_inside_character_class, Pos() - 2, 2);
                    return;
                }
                goto default;
            default:
                if (!ScanCharacterClassEscape() && !ScanDecimalEscape())
                {
                    // Regex literals cannot contain line breaks here, so a character escape must consume something.
                    Debug.Assert(ScanCharacterEscape(true /*atomEscape*/).Length != 0);
                }
                break;
        }
    }

    // DecimalEscape ::= [1-9] [0-9]*
    // Ported from tsgo internal/scanner/regexp.go:410 (scanDecimalEscape)
    private bool ScanDecimalEscape()
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '\\');
        int ch = Char();
        if (ch >= '1' && ch <= '9')
        {
            int start = Pos();
            ScanDigits();
            if (!int.TryParse(scanner.tokenValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int val))
            {
                val = int.MaxValue;
            }
            decimalEscapes.Add(new DecimalEscapeValue(start, Pos(), val));
            return true;
        }
        return false;
    }

    // Ported from tsgo internal/scanner/regexp.go:436 (scanCharacterEscape)
    private string ScanCharacterEscape(bool atomEscape)
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '\\');
        int ch = Char();
        switch (ch)
        {
            case -1:
                Error(Diagnostics.Undetermined_character_escape, Pos() - 1, 1);
                return "\\";
            case 'c':
                IncPos(1);
                ch = Char();
                if (StringUtil.IsASCIILetter(ch))
                {
                    IncPos(1);
                    return Scanner.RuneToString(ch & 0x1f);
                }
                if (anyUnicodeModeOrNonAnnexB)
                {
                    Error(Diagnostics.X_c_must_be_followed_by_an_ASCII_letter, Pos() - 2, 2);
                }
                else if (atomEscape)
                {
                    IncPos(-1);
                    return "\\";
                }
                return Scanner.RuneToString(ch);
            case '^':
            case '$':
            case '/':
            case '\\':
            case '.':
            case '*':
            case '+':
            case '?':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
            case '|':
                IncPos(1);
                return Scanner.RuneToString(ch);
            default:
                {
                    IncPos(-1); // back up to include the backslash for scanEscapeSequence
                    EscapeSequenceScanningFlags flags = EscapeSequenceScanningFlags.RegularExpression;
                    if (AnnexB)
                    {
                        flags |= EscapeSequenceScanningFlags.AnnexB;
                    }
                    if (AnyUnicodeMode)
                    {
                        flags |= EscapeSequenceScanningFlags.AnyUnicodeMode;
                    }
                    if (atomEscape)
                    {
                        flags |= EscapeSequenceScanningFlags.AtomEscape;
                    }
                    return scanner.ScanEscapeSequence(flags);
                }
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:476 (scanGroupName)
    private void ScanGroupName(bool isReference)
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '<');
        scanner.tokenStart = Pos();
        scanner.ScanIdentifier(0);
        if (Pos() == scanner.tokenStart)
        {
            Error(Diagnostics.Expected_a_capturing_group_name, Pos(), 0);
        }
        else if (isReference)
        {
            groupNameReferences.Add(new GroupNameReference(scanner.tokenStart, Pos(), scanner.tokenValue));
        }
        else if (NamedCapturingGroupsContains(scanner.tokenValue))
        {
            Error(Diagnostics.Named_capturing_groups_with_the_same_name_must_be_mutually_exclusive_to_each_other, scanner.tokenStart, Pos() - scanner.tokenStart);
        }
        else
        {
            if (namedCapturingGroups.Count > 0)
            {
                namedCapturingGroups[^1].Add(scanner.tokenValue);
            }
            groupSpecifiers.Add(scanner.tokenValue);
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:494 (namedCapturingGroupsContains)
    private bool NamedCapturingGroupsContains(string name)
    {
        foreach (var group in namedCapturingGroups)
        {
            if (group.Contains(name))
            {
                return true;
            }
        }
        return false;
    }

    // Ported from tsgo internal/scanner/regexp.go:503 (isClassContentExit)
    private bool IsClassContentExit(int ch)
    {
        return ch == ']' || Pos() >= End;
    }

    // ClassRanges ::= '^'? (ClassAtom ('-' ClassAtom)?)*
    // Ported from tsgo internal/scanner/regexp.go:508 (scanClassRanges)
    private void ScanClassRanges()
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '[');
        // PORT-NOTE: Go reset pendingLowSurrogate here; unnecessary in the UTF-16 world (dropped).
        if (Char() == '^')
        {
            IncPos(1);
        }
        while (Pos() < End)
        {
            int ch = Char();
            if (IsClassContentExit(ch))
            {
                return;
            }
            int minStart = Pos();
            string minCharacter = ScanClassAtom();
            if (Char() == '-')
            {
                IncPos(1);
                ch = Char();
                if (IsClassContentExit(ch))
                {
                    return;
                }
                if (minCharacter.Length == 0 && anyUnicodeModeOrNonAnnexB)
                {
                    Error(Diagnostics.A_character_class_range_must_not_be_bounded_by_another_character_class, minStart, Pos() - 1 - minStart);
                }
                int maxStart = Pos();
                string maxCharacter = ScanClassAtom();
                if (maxCharacter.Length == 0 && anyUnicodeModeOrNonAnnexB)
                {
                    Error(Diagnostics.A_character_class_range_must_not_be_bounded_by_another_character_class, maxStart, Pos() - maxStart);
                    continue;
                }
                if (minCharacter.Length == 0)
                {
                    continue;
                }
                var (minCharacterValue, minSize) = StringUtil.DecodeJSStringRune(minCharacter);
                var (maxCharacterValue, maxSize) = StringUtil.DecodeJSStringRune(maxCharacter);
                if (minCharacter.Length == minSize && maxCharacter.Length == maxSize && minCharacterValue > maxCharacterValue)
                {
                    Error(Diagnostics.Range_out_of_order_in_character_class, minStart, Pos() - minStart);
                }
            }
        }
    }

    // ClassSetExpression ::= '^'? (ClassUnion | ClassIntersection | ClassSubtraction)
    // Ported from tsgo internal/scanner/regexp.go:563 (scanClassSetExpression)
    private void ScanClassSetExpression()
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '[');
        bool isCharacterComplement = false;
        if (Char() == '^')
        {
            IncPos(1);
            isCharacterComplement = true;
        }
        bool expressionMayContainStrings = false;
        int ch = Char();
        if (IsClassContentExit(ch))
        {
            return;
        }
        int start = Pos();
        string operand = "";
        string twoChars = "";
        if (Pos() + 1 < End)
        {
            twoChars = Text().Substring(Pos(), 2);
        }
        switch (twoChars)
        {
            case "--":
            case "&&":
                Error(Diagnostics.Expected_a_class_set_operand, Pos(), 0);
                mayContainStrings = false;
                break;
            default:
                operand = ScanClassSetOperand();
                break;
        }
        switch (Char())
        {
            case '-':
                if (Pos() + 1 < End && CharAt(Pos() + 1) == '-')
                {
                    if (isCharacterComplement && mayContainStrings)
                    {
                        Error(Diagnostics.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class, start, Pos() - start);
                    }
                    expressionMayContainStrings = mayContainStrings;
                    ScanClassSetSubExpression(ClassSetExpressionType.ClassSubtraction);
                    mayContainStrings = !isCharacterComplement && expressionMayContainStrings;
                    return;
                }
                break;
            case '&':
                if (Pos() + 1 < End && CharAt(Pos() + 1) == '&')
                {
                    ScanClassSetSubExpression(ClassSetExpressionType.ClassIntersection);
                    if (isCharacterComplement && mayContainStrings)
                    {
                        Error(Diagnostics.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class, start, Pos() - start);
                    }
                    expressionMayContainStrings = mayContainStrings;
                    mayContainStrings = !isCharacterComplement && expressionMayContainStrings;
                    return;
                }
                else
                {
                    Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos(), 1, Scanner.RuneToString(ch));
                }
                break;
            default:
                if (isCharacterComplement && mayContainStrings)
                {
                    Error(Diagnostics.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class, start, Pos() - start);
                }
                expressionMayContainStrings = mayContainStrings;
                break;
        }
        while (Pos() < End)
        {
            ch = Char();
            switch (ch)
            {
                case '-':
                    IncPos(1);
                    ch = Char();
                    if (IsClassContentExit(ch))
                    {
                        mayContainStrings = !isCharacterComplement && expressionMayContainStrings;
                        return;
                    }
                    if (ch == '-')
                    {
                        IncPos(1);
                        Error(Diagnostics.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, Pos() - 2, 2);
                        start = Pos() - 2;
                        operand = Text().Substring(start, Pos() - start);
                        continue;
                    }
                    else
                    {
                        if (operand.Length == 0)
                        {
                            Error(Diagnostics.A_character_class_range_must_not_be_bounded_by_another_character_class, start, Pos() - 1 - start);
                        }
                        int secondStart = Pos();
                        string secondOperand = ScanClassSetOperand();
                        if (isCharacterComplement && mayContainStrings)
                        {
                            Error(Diagnostics.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class, secondStart, Pos() - secondStart);
                        }
                        expressionMayContainStrings = expressionMayContainStrings || mayContainStrings;
                        if (secondOperand.Length == 0)
                        {
                            Error(Diagnostics.A_character_class_range_must_not_be_bounded_by_another_character_class, secondStart, Pos() - secondStart);
                        }
                        else if (operand.Length != 0)
                        {
                            var (minCharacterValue, minSize) = StringUtil.DecodeJSStringRune(operand);
                            var (maxCharacterValue, maxSize) = StringUtil.DecodeJSStringRune(secondOperand);
                            if (operand.Length == minSize && secondOperand.Length == maxSize && minCharacterValue > maxCharacterValue)
                            {
                                Error(Diagnostics.Range_out_of_order_in_character_class, start, Pos() - start);
                            }
                        }
                    }
                    break;
                case '&':
                    start = Pos();
                    IncPos(1);
                    if (Char() == '&')
                    {
                        IncPos(1);
                        Error(Diagnostics.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, Pos() - 2, 2);
                        if (Char() == '&')
                        {
                            Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos(), 1, Scanner.RuneToString(ch));
                            IncPos(1);
                        }
                    }
                    else
                    {
                        Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos() - 1, 1, Scanner.RuneToString(ch));
                    }
                    operand = Text().Substring(start, Pos() - start);
                    continue;
            }
            if (IsClassContentExit(Char()))
            {
                break;
            }
            start = Pos();
            twoChars = "";
            if (Pos() + 1 < End)
            {
                twoChars = Text().Substring(Pos(), 2);
            }
            switch (twoChars)
            {
                case "--":
                case "&&":
                    Error(Diagnostics.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, Pos(), 2);
                    IncPos(2);
                    operand = Text().Substring(start, Pos() - start);
                    break;
                default:
                    operand = ScanClassSetOperand();
                    break;
            }
        }
        mayContainStrings = !isCharacterComplement && expressionMayContainStrings;
    }

    // Ported from tsgo internal/scanner/regexp.go:689 (scanClassSetSubExpression)
    private void ScanClassSetSubExpression(ClassSetExpressionType expressionType)
    {
        bool expressionMayContainStrings = mayContainStrings;
        while (Pos() < End)
        {
            int ch = Char();
            if (IsClassContentExit(ch))
            {
                break;
            }
            switch (ch)
            {
                case '-':
                    IncPos(1);
                    if (Char() == '-')
                    {
                        IncPos(1);
                        if (expressionType != ClassSetExpressionType.ClassSubtraction)
                        {
                            Error(Diagnostics.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, Pos() - 2, 2);
                        }
                    }
                    else
                    {
                        Error(Diagnostics.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, Pos() - 1, 1);
                    }
                    break;
                case '&':
                    IncPos(1);
                    if (Char() == '&')
                    {
                        IncPos(1);
                        if (expressionType != ClassSetExpressionType.ClassIntersection)
                        {
                            Error(Diagnostics.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, Pos() - 2, 2);
                        }
                        if (Char() == '&')
                        {
                            Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos(), 1, Scanner.RuneToString(ch));
                            IncPos(1);
                        }
                    }
                    else
                    {
                        Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos() - 1, 1, Scanner.RuneToString(ch));
                    }
                    break;
                default:
                    switch (expressionType)
                    {
                        case ClassSetExpressionType.ClassSubtraction:
                            Error(Diagnostics.X_0_expected, Pos(), 0, "--");
                            break;
                        case ClassSetExpressionType.ClassIntersection:
                            Error(Diagnostics.X_0_expected, Pos(), 0, "&&");
                            break;
                    }
                    break;
            }
            ch = Char();
            if (IsClassContentExit(ch))
            {
                Error(Diagnostics.Expected_a_class_set_operand, Pos(), 0);
                break;
            }
            ScanClassSetOperand();
            if (expressionType == ClassSetExpressionType.ClassIntersection)
            {
                expressionMayContainStrings = expressionMayContainStrings && mayContainStrings;
            }
        }
        mayContainStrings = expressionMayContainStrings;
    }

    // Ported from tsgo internal/scanner/regexp.go:748 (scanClassSetOperand)
    private string ScanClassSetOperand()
    {
        mayContainStrings = false;
        switch (Char())
        {
            case '[':
                IncPos(1);
                ScanClassSetExpression();
                ScanExpectedChar(']');
                return "";
            case '\\':
                IncPos(1);
                if (ScanCharacterClassEscape())
                {
                    return "";
                }
                else if (Char() == 'q')
                {
                    IncPos(1);
                    if (Char() == '{')
                    {
                        IncPos(1);
                        ScanClassStringDisjunctionContents();
                        ScanExpectedChar('}');
                        return "";
                    }
                    else
                    {
                        Error(Diagnostics.X_q_must_be_followed_by_string_alternatives_enclosed_in_braces, Pos() - 2, 2);
                        return "q";
                    }
                }
                IncPos(-1);
                goto default;
            default:
                return ScanClassSetCharacter();
        }
    }

    // ClassStringDisjunctionContents ::= ClassSetCharacter* ('|' ClassSetCharacter*)*
    // Ported from tsgo internal/scanner/regexp.go:780 (scanClassStringDisjunctionContents)
    private void ScanClassStringDisjunctionContents()
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '{');
        int characterCount = 0;
        while (Pos() < End)
        {
            int ch = Char();
            switch (ch)
            {
                case '}':
                    if (characterCount != 1)
                    {
                        mayContainStrings = true;
                    }
                    return;
                case '|':
                    if (characterCount != 1)
                    {
                        mayContainStrings = true;
                    }
                    IncPos(1);
                    characterCount = 0;
                    break;
                default:
                    ScanClassSetCharacter();
                    characterCount++;
                    break;
            }
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:808 (scanClassSetCharacter)
    private string ScanClassSetCharacter()
    {
        int ch = Char();
        if (ch == '\\')
        {
            IncPos(1);
            int innerCh = Char();
            switch (innerCh)
            {
                case 'b':
                    IncPos(1);
                    return "\b";
                case '&':
                case '-':
                case '!':
                case '#':
                case '%':
                case ',':
                case ':':
                case ';':
                case '<':
                case '=':
                case '>':
                case '@':
                case '`':
                case '~':
                    IncPos(1);
                    return Scanner.RuneToString(innerCh);
                default:
                    return ScanCharacterEscape(false /*atomEscape*/);
            }
        }
        else if (Pos() + 1 < End && ch == CharAt(Pos() + 1))
        {
            switch (ch)
            {
                case '&':
                case '!':
                case '#':
                case '%':
                case '*':
                case '+':
                case ',':
                case '.':
                case ':':
                case ';':
                case '<':
                case '=':
                case '>':
                case '?':
                case '@':
                case '`':
                case '~':
                    Error(Diagnostics.A_character_class_must_not_contain_a_reserved_double_punctuator_Did_you_mean_to_escape_it_with_backslash, Pos(), 2);
                    IncPos(2);
                    return Text().Substring(Pos() - 2, 2);
            }
        }
        switch (ch)
        {
            case '/':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
            case '-':
            case '|':
                Error(Diagnostics.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, Pos(), 1, Scanner.RuneToString(ch));
                IncPos(1);
                return Scanner.RuneToString(ch);
        }
        return ScanSourceCharacter();
    }

    // Ported from tsgo internal/scanner/regexp.go:851 (scanClassAtom)
    private string ScanClassAtom()
    {
        if (Char() == '\\')
        {
            IncPos(1);
            int ch = Char();
            switch (ch)
            {
                case 'b':
                    IncPos(1);
                    return "\b";
                case '-':
                    IncPos(1);
                    return Scanner.RuneToString(ch);
                default:
                    if (ScanCharacterClassEscape())
                    {
                        return "";
                    }
                    return ScanCharacterEscape(false /*atomEscape*/);
            }
        }
        else
        {
            return ScanSourceCharacter();
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:877 (scanCharacterClassEscape)
    private bool ScanCharacterClassEscape()
    {
        Debug.Assert(Pos() > 0 && Text()[Pos() - 1] == '\\');
        bool isCharacterComplement = false;
        int start = Pos() - 1;
        int ch = Char();
        switch (ch)
        {
            case 'd':
            case 'D':
            case 's':
            case 'S':
            case 'w':
            case 'W':
                IncPos(1);
                return true;
            case 'P':
                isCharacterComplement = true;
                goto case 'p';
            case 'p':
                IncPos(1);
                if (Char() == '{')
                {
                    IncPos(1);
                    int propertyNameOrValueStart = Pos();
                    string propertyNameOrValue = ScanWordCharacters();
                    if (Char() == '=')
                    {
                        string propertyName = UnicodeProperties.NonBinaryUnicodeProperties.GetValueOrDefault(propertyNameOrValue, "");
                        if (Pos() == propertyNameOrValueStart)
                        {
                            Error(Diagnostics.Expected_a_Unicode_property_name, Pos(), 0);
                        }
                        else if (propertyName.Length == 0)
                        {
                            Error(Diagnostics.Unknown_Unicode_property_name, propertyNameOrValueStart, Pos() - propertyNameOrValueStart);
                            string suggestion = GetSpellingSuggestionForUnicodePropertyName(propertyNameOrValue);
                            if (suggestion.Length != 0)
                            {
                                Error(Diagnostics.Did_you_mean_0, propertyNameOrValueStart, Pos() - propertyNameOrValueStart, suggestion);
                            }
                        }
                        IncPos(1);
                        int propertyValueStart = Pos();
                        string propertyValue = ScanWordCharacters();
                        if (Pos() == propertyValueStart)
                        {
                            Error(Diagnostics.Expected_a_Unicode_property_value, Pos(), 0);
                        }
                        else if (propertyName.Length != 0)
                        {
                            if (UnicodeProperties.ValuesOfNonBinaryUnicodeProperties.TryGetValue(propertyName, out var values) && !values.Has(propertyValue))
                            {
                                Error(Diagnostics.Unknown_Unicode_property_value, propertyValueStart, Pos() - propertyValueStart);
                                string suggestion = GetSpellingSuggestionForUnicodePropertyValue(propertyName, propertyValue);
                                if (suggestion.Length != 0)
                                {
                                    Error(Diagnostics.Did_you_mean_0, propertyValueStart, Pos() - propertyValueStart, suggestion);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (Pos() == propertyNameOrValueStart)
                        {
                            Error(Diagnostics.Expected_a_Unicode_property_name_or_value, Pos(), 0);
                        }
                        else if (UnicodeProperties.BinaryUnicodePropertiesOfStrings.Has(propertyNameOrValue))
                        {
                            if (!UnicodeSetsMode)
                            {
                                Error(Diagnostics.Any_Unicode_property_that_would_possibly_match_more_than_a_single_character_is_only_available_when_the_Unicode_Sets_v_flag_is_set, propertyNameOrValueStart, Pos() - propertyNameOrValueStart);
                            }
                            else if (isCharacterComplement)
                            {
                                Error(Diagnostics.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class, propertyNameOrValueStart, Pos() - propertyNameOrValueStart);
                            }
                            else
                            {
                                mayContainStrings = true;
                            }
                        }
                        else if (!UnicodeProperties.ValuesOfNonBinaryUnicodeProperties["General_Category"].Has(propertyNameOrValue) && !UnicodeProperties.BinaryUnicodeProperties.Has(propertyNameOrValue))
                        {
                            Error(Diagnostics.Unknown_Unicode_property_name_or_value, propertyNameOrValueStart, Pos() - propertyNameOrValueStart);
                            string suggestion = GetSpellingSuggestionForUnicodePropertyNameOrValue(propertyNameOrValue);
                            if (suggestion.Length != 0)
                            {
                                Error(Diagnostics.Did_you_mean_0, propertyNameOrValueStart, Pos() - propertyNameOrValueStart, suggestion);
                            }
                        }
                    }
                    ScanExpectedChar('}');
                    if (!AnyUnicodeMode)
                    {
                        Error(Diagnostics.Unicode_property_value_expressions_are_only_available_when_the_Unicode_u_flag_or_the_Unicode_Sets_v_flag_is_set, start, Pos() - start);
                    }
                }
                else if (anyUnicodeModeOrNonAnnexB)
                {
                    Error(Diagnostics.X_0_must_be_followed_by_a_Unicode_property_value_expression_enclosed_in_braces, Pos() - 2, 2, Scanner.RuneToString(ch));
                }
                else
                {
                    IncPos(-1);
                    return false;
                }
                return true;
        }
        return false;
    }

    // Ported from tsgo internal/scanner/regexp.go:955 (getSpellingSuggestionForUnicodePropertyName)
    private static string GetSpellingSuggestionForUnicodePropertyName(string name)
    {
        return CoreUtil.GetSpellingSuggestionForStrings(name, UnicodeProperties.NonBinaryUnicodeProperties.Keys);
    }

    // Ported from tsgo internal/scanner/regexp.go:959 (getSpellingSuggestionForUnicodePropertyValue)
    private static string GetSpellingSuggestionForUnicodePropertyValue(string propertyName, string value)
    {
        if (!UnicodeProperties.ValuesOfNonBinaryUnicodeProperties.TryGetValue(propertyName, out var values))
        {
            return "";
        }
        return CoreUtil.GetSpellingSuggestionForStrings(value, values.Keys());
    }

    // Ported from tsgo internal/scanner/regexp.go:967 (getSpellingSuggestionForUnicodePropertyNameOrValue)
    private static string GetSpellingSuggestionForUnicodePropertyNameOrValue(string name)
    {
        return CoreUtil.GetSpellingSuggestionForStrings(name, CoreUtil.ConcatenateSeq(
            UnicodeProperties.ValuesOfNonBinaryUnicodeProperties["General_Category"].Keys(),
            UnicodeProperties.BinaryUnicodeProperties.Keys(),
            UnicodeProperties.BinaryUnicodePropertiesOfStrings.Keys()));
    }

    // Ported from tsgo internal/scanner/regexp.go:975 (scanWordCharacters)
    private string ScanWordCharacters()
    {
        int start = Pos();
        while (Pos() < End)
        {
            int ch = Char();
            if (!Scanner.IsWordCharacter(ch))
            {
                break;
            }
            IncPos(1);
        }
        return Text().Substring(start, Pos() - start);
    }

    // Ported from tsgo internal/scanner/regexp.go:987 (scanSourceCharacter)
    private string ScanSourceCharacter()
    {
        if (Pos() >= End)
        {
            return "";
        }
        // PORT-NOTE: source is natively UTF-16 (like Strada), so we read a single UTF-16 code
        // unit; Go's pendingLowSurrogate splitting of non-BMP runes on UTF-8 text is unneeded.
        if (!AnyUnicodeMode)
        {
            char c = Text()[Pos()];
            IncPos(1);
            return StringUtil.EncodeJSStringRune(c);
        }
        var (ch, size) = StringUtil.DecodeRuneAt(Text(), Pos());
        if (size == 0)
        {
            return "";
        }
        if (ch == StringUtil.RuneError)
        {
            // Invalid unit (lone surrogate) in unicode mode; consume to avoid infinite loops.
            IncPos(size);
            return "";
        }
        IncPos(size);
        return Scanner.RuneToString(ch);
    }

    // Ported from tsgo internal/scanner/regexp.go:1030 (scanExpectedChar)
    private void ScanExpectedChar(int ch)
    {
        if (Char() == ch)
        {
            IncPos(1);
        }
        else
        {
            Error(Diagnostics.X_0_expected, Pos(), 0, Scanner.RuneToString(ch));
        }
    }

    // Ported from tsgo internal/scanner/regexp.go:1038 (scanDigits)
    private void ScanDigits()
    {
        int start = Pos();
        while (Pos() < End && StringUtil.IsDigit(Char()))
        {
            IncPos(1);
        }
        scanner.tokenValue = Text().Substring(start, Pos() - start);
    }

    // Ported from tsgo internal/scanner/regexp.go:1046 (run)
    internal void Run()
    {
        // Regular expressions are checked more strictly when either in 'u' or 'v' mode, or
        // when not using the looser interpretation of the syntax from ECMA-262 Annex B.
        anyUnicodeModeOrNonAnnexB = AnyUnicodeMode || !AnnexB;

        ScanDisjunction(false /*isInGroup*/);

        foreach (var reference in groupNameReferences)
        {
            if (!groupSpecifiers.Contains(reference.Name))
            {
                Error(Diagnostics.There_is_no_capturing_group_named_0_in_this_regular_expression, reference.Pos, reference.End - reference.Pos, reference.Name);
                if (groupSpecifiers.Count > 0)
                {
                    string suggestion = CoreUtil.GetSpellingSuggestionForStrings(reference.Name, groupSpecifiers);
                    if (suggestion.Length != 0)
                    {
                        Error(Diagnostics.Did_you_mean_0, reference.Pos, reference.End - reference.Pos, suggestion);
                    }
                }
            }
        }
        foreach (var escape in decimalEscapes)
        {
            // Although a DecimalEscape with a value greater than the number of capturing groups
            // is treated as either a LegacyOctalEscapeSequence or an IdentityEscape in Annex B,
            // an error is nevertheless reported since it's most likely a mistake.
            if (escape.Value > numberOfCapturingGroups)
            {
                if (numberOfCapturingGroups > 0)
                {
                    Error(Diagnostics.This_backreference_refers_to_a_group_that_does_not_exist_There_are_only_0_capturing_groups_in_this_regular_expression, escape.Pos, escape.End - escape.Pos, numberOfCapturingGroups);
                }
                else
                {
                    Error(Diagnostics.This_backreference_refers_to_a_group_that_does_not_exist_There_are_no_capturing_groups_in_this_regular_expression, escape.Pos, escape.End - escape.Pos);
                }
            }
        }
    }
}

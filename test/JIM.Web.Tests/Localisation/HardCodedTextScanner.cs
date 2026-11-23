// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Finds user-visible English written straight into portal source rather than read from a resource file (#3030).
/// <para>
/// This is a heuristic scanner, not a Razor compiler: it walks markup, attributes and the C# inside Razor and
/// <c>.cs</c> files closely enough to tell prose from identifiers, CSS classes, routes and keys, and errs towards
/// reporting. A false positive is cheap (an <c>i18n: exempt</c> comment with a reason, on the line or one of the
/// two above it); a false negative is a string that ships untranslated, so the rules below lean that way.
/// </para>
/// </summary>
public static partial class HardCodedTextScanner
{
    /// <summary>The comment that marks a line's text as deliberately not translated. A reason must follow it.</summary>
    public const string ExemptionMarker = "i18n: exempt";

    /// <summary>How many lines above a finding are searched for <see cref="ExemptionMarker"/>.</summary>
    private const int ExemptionLookBehindLines = 2;

    /// <summary>
    /// Words that are names rather than English, and read the same in every language: the product, the company,
    /// protocols and formats. Text left with no word once these are removed is not counted.
    /// </summary>
    private static readonly string[] NonTranslatableTokens =
    [
        "Junctional Identity Manager", "junctional.io", "JIM", "Tetron", "GitHub", "MudBlazor", "PowerShell",
        "SCIM", "LDAPS", "LDAP", "OIDC", "SQL", "API", "URL", "URI", "UUID", "GUID", "CSV", "JSON", "XML", "HTTP",
        "HTTPS", "TLS", "DN", "OU", "ID", "UTC", "ISO", "AD", "SSO", "MV", "CS", "Ex", "CSO", "MVO", "SR", "PE",
        "DR", "RP", "KEY", "SCH", "ACT", "OK"
    ];

    /// <summary>Attributes whose literal value is shown to a person.</summary>
    [GeneratedRegex(
        @"^(?:(?i:title|alt|placeholder|aria-label|aria-description)|[A-Za-z]*(?:Text|Label|Title|Placeholder|Tooltip|ToolTip|Caption|Heading|Message|Description|Subtitle|Header)|SingularName|PluralName)$")]
    private static partial Regex TextAttributeName();

    [GeneratedRegex(@"[A-Za-z]{2,}")]
    private static partial Regex WordOfTwoLetters();

    [GeneratedRegex(@"&#?[A-Za-z0-9]+;")]
    private static partial Regex HtmlEntity();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    /// <summary>A line whose literals are diagnostics for developers, not text for users.</summary>
    [GeneratedRegex(
        @"\bLog(?:ger)?\.|_log(?:ger)?\.|\.Log(?:Information|Warning|Error|Debug|Trace|Critical)\(|\bthrow\b|Exception\(|Debug\.|Trace\.|Console\.|\bnameof\b|GetEnvironmentVariable|JSRuntime|JS\.Invoke|InvokeVoidAsync|InvokeAsync<|\[Description|\[Obsolete|Regex\(|\[GeneratedRegex")]
    private static partial Regex DiagnosticLine();

    /// <summary>The call or assignment a single capitalised word must sit in to count as user-visible.</summary>
    [GeneratedRegex(
        @"(?:Snackbar\.Add|ShowMessageBox(?:Async)?|Show(?:Async)?<[^>]*>|\b(?:yes|no|cancel)Text\s*:|\b(?:title|message|label|text)\s*:|\b(?:Title|Label|Text|Message|Description|HelperText|Placeholder|Tooltip|Caption|Heading|DisplayName|PrimaryText|SecondaryText|ActionText)\s*=)[^;]*$")]
    private static partial Regex UserFacingCallContext();

    [GeneratedRegex(@"^[dMyHhmsfFtzK\s:./,'-]+$")]
    private static partial Regex DateFormatPattern();

    [GeneratedRegex(@"^@(?:page|using|inject|attribute|implements|inherits|typeparam|layout|namespace|rendermode|preservewhitespace|model|addTagHelper|removeTagHelper)\b")]
    private static partial Regex DirectiveLine();

    /// <summary>A finding: one piece of hard-coded text.</summary>
    public sealed record Finding(int Line, string Text);

    /// <summary>Scans a Razor component or Razor page (<c>.razor</c>, <c>.cshtml</c>).</summary>
    public static IReadOnlyList<Finding> ScanRazor(string source)
    {
        var context = new ScanContext(source);
        var blanked = BlankRazorComments(source);
        new RazorWalker(blanked, context).Run();
        return context.Results();
    }

    /// <summary>Scans a C# file.</summary>
    public static IReadOnlyList<Finding> ScanCSharp(string source)
    {
        var context = new ScanContext(source);
        var walker = new RazorWalker(source, context);
        walker.ScanCSharpRange(0, source.Length, insideTextAttribute: false);
        return context.Results();
    }

    /// <summary>Replaces Razor and HTML comments with spaces, keeping line breaks so line numbers still match.</summary>
    private static string BlankRazorComments(string source)
    {
        var builder = new StringBuilder(source);
        Blank("@*", "*@");
        Blank("<!--", "-->");
        return builder.ToString();

        void Blank(string open, string close)
        {
            var text = builder.ToString();
            var start = text.IndexOf(open, StringComparison.Ordinal);
            while (start >= 0)
            {
                var end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + close.Length;
                for (var i = start; i < end; i++)
                {
                    if (builder[i] != '\n' && builder[i] != '\r')
                        builder[i] = ' ';
                }

                text = builder.ToString();
                start = text.IndexOf(open, end, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Is this markup text or attribute value prose?</summary>
    private static bool IsMarkupProse(string text)
    {
        var cleaned = StripNonTranslatable(HtmlEntity().Replace(text, " "));
        return WordOfTwoLetters().IsMatch(cleaned);
    }

    /// <summary>Is this C# string literal prose? <paramref name="relaxed"/> when it already sits in a text attribute.</summary>
    private static bool IsCSharpProse(string literal, string lineBefore, string fullLine, bool relaxed)
    {
        if (DiagnosticLine().IsMatch(fullLine))
            return false;

        // A fragment built around a value ("in {0}", "{0} ago") is prose however short it is.
        if (Regex.IsMatch(literal, @"(?:^|\s)\{\d+\}\s+[a-z]{2,}\b|\b[a-z]{2,}\s+\{\d+\}(?:\s|$)"))
            return true;

        var text = Placeholder().Replace(literal, " ").Trim();
        if (text.Length == 0 || DateFormatPattern().IsMatch(text))
            return false;

        var cleaned = StripNonTranslatable(text).Trim();
        if (!WordOfTwoLetters().IsMatch(cleaned))
            return false;

        var tokens = cleaned.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var wordTokens = tokens.Count(t => Regex.IsMatch(t, @"^[\p{L}'’]+[.,:;!?…)]*$|^\(?[\p{L}'’]+$|^""[\p{L}]"));
        var looksLikeCode = tokens.Any(t => Regex.IsMatch(t, @"[_/\\=<>#;{}\[\]|]|^[a-z]+[A-Z]|--|^\.|\w-\w|^\$"));
        if (looksLikeCode && wordTokens * 2 < tokens.Length * 1)
            return false;

        if (relaxed)
            return wordTokens > 0 && !Regex.IsMatch(cleaned, @"^[a-z][a-z0-9-]*$");

        if (tokens.Length >= 2)
        {
            // A phrase: mostly words, at least one with a lower-case letter (an all-capitals phrase is a constant).
            return wordTokens * 3 >= tokens.Length * 2
                   && cleaned.Any(char.IsLower)
                   && !looksLikeCode;
        }

        // A single word counts only where it is handed straight to something the user sees.
        return Regex.IsMatch(cleaned, @"^[A-Z][a-z]+[.!?:]?$") && UserFacingCallContext().IsMatch(lineBefore);
    }

    private static string StripNonTranslatable(string text)
    {
        var result = text;
        foreach (var token in NonTranslatableTokens)
            result = Regex.Replace(result, $@"(?<![\w.]){Regex.Escape(token)}s?(?![\w])", " ");

        return result;
    }

    /// <summary>Collects findings and applies the exemption marker.</summary>
    private sealed class ScanContext
    {
        private readonly string[] _lines;
        private readonly int[] _lineStarts;
        private readonly List<Finding> _findings = [];

        public ScanContext(string source)
        {
            Source = source;
            _lines = source.Split('\n');
            var starts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                    starts.Add(i + 1);
            }

            _lineStarts = starts.ToArray();
        }

        public string Source { get; }

        public int LineOf(int position)
        {
            var index = Array.BinarySearch(_lineStarts, position);
            return (index >= 0 ? index : ~index - 1) + 1;
        }

        public string LineText(int line) => line >= 1 && line <= _lines.Length ? _lines[line - 1] : string.Empty;

        public void Add(int position, string text)
        {
            var line = LineOf(position);
            for (var l = Math.Max(1, line - ExemptionLookBehindLines); l <= line; l++)
            {
                if (LineText(l).Contains(ExemptionMarker, StringComparison.Ordinal))
                    return;
            }

            _findings.Add(new Finding(line, Regex.Replace(text.Trim(), @"\s+", " ")));
        }

        public IReadOnlyList<Finding> Results() => _findings;
    }

    /// <summary>
    /// Walks Razor: markup, tags and their attributes, implicit and explicit expressions, code blocks that mix C#
    /// and markup (<c>@if</c>, <c>@foreach</c>, <c>@code</c>), and the C# literals inside all of them.
    /// </summary>
    private sealed class RazorWalker(string text, ScanContext context)
    {
        private static readonly string[] BlockKeywords = ["if", "foreach", "for", "while", "switch", "using", "lock", "do", "try"];
        private static readonly string[] ContinuationKeywords = ["else", "catch", "finally"];

        public void Run()
        {
            var position = 0;
            while (position < text.Length)
                position = ScanMarkup(position, stopAtDepthZeroClose: false, out _);
        }

        /// <summary>
        /// Scans markup from <paramref name="start"/>. When <paramref name="stopAtDepthZeroClose"/>, it returns once
        /// the element that starts the run has closed (markup embedded in a code block), or at an unmatched '}'.
        /// </summary>
        private int ScanMarkup(int start, bool stopAtDepthZeroClose, out bool hitCloseBrace)
        {
            hitCloseBrace = false;
            var textBuffer = new StringBuilder();
            var textStart = -1;
            var depth = 0;
            var i = start;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '<')
                {
                    Flush();
                    if (i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/' || text[i + 1] == '!'))
                    {
                        var (end, name, closing, selfClosing) = ScanTag(i);
                        if (!closing && !selfClosing && (name is "script" or "style"))
                        {
                            var close = text.IndexOf($"</{name}", end, StringComparison.OrdinalIgnoreCase);
                            end = close < 0 ? text.Length : text.IndexOf('>', close) + 1;
                            if (end == 0)
                                end = text.Length;
                        }
                        else if (closing)
                        {
                            depth--;
                        }
                        else if (!selfClosing && !IsVoidElement(name))
                        {
                            depth++;
                        }

                        i = end;
                        if (stopAtDepthZeroClose && depth <= 0)
                            return i;

                        continue;
                    }

                    Append(i, c);
                    i++;
                    continue;
                }

                if (c == '@')
                {
                    var next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (next == '@')
                    {
                        Append(i, '@');
                        i += 2;
                        continue;
                    }

                    if (next == ':')
                    {
                        var lineEnd = text.IndexOf('\n', i);
                        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
                        Flush();
                        var lineText = text[(i + 2)..lineEnd];
                        if (IsMarkupProse(lineText))
                            context.Add(i, lineText);
                        i = lineEnd;
                        continue;
                    }

                    if (IsAtLineStart(i) && DirectiveLine().IsMatch(text[i..Math.Min(text.Length, i + 40)]))
                    {
                        Flush();
                        var lineEnd = text.IndexOf('\n', i);
                        i = lineEnd < 0 ? text.Length : lineEnd;
                        continue;
                    }

                    if (next == '(')
                    {
                        var end = SkipBalanced(i + 1, '(', ')', collect: true, relaxed: false);
                        textBuffer.Append(' ');
                        i = end;
                        continue;
                    }

                    if (next == '{')
                    {
                        Flush();
                        i = ScanCodeBlock(i + 2);
                        continue;
                    }

                    var keyword = ReadIdentifier(i + 1);
                    if (keyword is "code" or "functions")
                    {
                        Flush();
                        var brace = text.IndexOf('{', i);
                        i = brace < 0 ? text.Length : ScanCodeBlock(brace + 1);
                        continue;
                    }

                    if (BlockKeywords.Contains(keyword))
                    {
                        Flush();
                        i = ScanControlStatement(i + 1 + keyword.Length);
                        continue;
                    }

                    if (keyword.Length > 0)
                    {
                        i = SkipImplicitExpression(i + 1);
                        textBuffer.Append(' ');
                        continue;
                    }

                    Append(i, c);
                    i++;
                    continue;
                }

                if (c == '}' && stopAtDepthZeroClose && depth <= 0)
                {
                    Flush();
                    hitCloseBrace = true;
                    return i;
                }

                Append(i, c);
                i++;
            }

            Flush();
            return i;

            void Append(int at, char ch)
            {
                if (textStart < 0 && !char.IsWhiteSpace(ch))
                    textStart = at;
                textBuffer.Append(ch);
            }

            void Flush()
            {
                var value = textBuffer.ToString();
                if (textStart >= 0 && IsMarkupProse(value))
                    context.Add(textStart, value);
                textBuffer.Clear();
                textStart = -1;
            }
        }

        /// <summary>Scans a tag from its '&lt;'; returns the index after '&gt;'.</summary>
        private (int End, string Name, bool Closing, bool SelfClosing) ScanTag(int start)
        {
            var i = start + 1;
            var closing = false;
            if (i < text.Length && text[i] == '/')
            {
                closing = true;
                i++;
            }

            if (i < text.Length && text[i] == '!')
            {
                var gt = text.IndexOf('>', i);
                return (gt < 0 ? text.Length : gt + 1, "!", false, true);
            }

            var nameStart = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '-' or ':' or '_'))
                i++;
            var name = text[nameStart..i].ToLowerInvariant();

            while (i < text.Length)
            {
                var c = text[i];
                if (c == '>')
                    return (i + 1, name, closing, i > 0 && text[i - 1] == '/');

                if (char.IsWhiteSpace(c) || c == '/')
                {
                    i++;
                    continue;
                }

                // An attribute: name[=value]
                var attrStart = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '=' and not '>' and not '/')
                {
                    if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '(')
                    {
                        i = SkipBalanced(i + 1, '(', ')', collect: false, relaxed: false);
                        continue;
                    }

                    i++;
                }

                var attrName = text[attrStart..i];
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;

                if (i >= text.Length || text[i] != '=')
                    continue;

                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;

                if (i >= text.Length)
                    break;

                var isText = TextAttributeName().IsMatch(attrName);
                var quote = text[i];
                if (quote is not '"' and not '\'')
                {
                    // Unquoted value: read to whitespace or '>'.
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>')
                        i++;
                    continue;
                }

                i = ScanAttributeValue(i + 1, quote, isText);
            }

            return (text.Length, name, closing, false);
        }

        /// <summary>Scans an attribute value after its opening quote; returns the index after the closing quote.</summary>
        private int ScanAttributeValue(int start, char quote, bool isText)
        {
            var i = start;
            var literal = new StringBuilder();
            var literalStart = -1;
            while (i < text.Length && text[i] != quote)
            {
                if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    i = SkipBalanced(i + 1, '(', ')', collect: true, relaxed: isText);
                    literal.Append(' ');
                    continue;
                }

                if (text[i] == '@' && i + 1 < text.Length && text[i + 1] != '@' && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
                {
                    i = SkipImplicitExpression(i + 1, quote, relaxed: isText);
                    literal.Append(' ');
                    continue;
                }

                if (literalStart < 0)
                    literalStart = i;
                literal.Append(text[i]);
                i++;
            }

            if (isText && literalStart >= 0 && IsMarkupProse(literal.ToString()))
                context.Add(literalStart, literal.ToString());

            return Math.Min(text.Length, i + 1);
        }

        /// <summary>
        /// Scans the body of a Razor code block from just after its '{', where C# and markup interleave; returns the
        /// index after the matching '}'.
        /// </summary>
        private int ScanCodeBlock(int start)
        {
            var i = start;
            var previousSignificant = '{';
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '}')
                    return i + 1;

                if (c == '{')
                {
                    i = ScanCodeBlock(i + 1);
                    previousSignificant = '}';
                    continue;
                }

                if (c == '<' && previousSignificant is ';' or '{' or '}' or ')' or ':'
                    && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
                {
                    i = ScanMarkup(i, stopAtDepthZeroClose: true, out _);
                    previousSignificant = '}';
                    continue;
                }

                if (c == '@' && i + 1 < text.Length && text[i + 1] == ':')
                {
                    var lineEnd = text.IndexOf('\n', i);
                    lineEnd = lineEnd < 0 ? text.Length : lineEnd;
                    var lineText = text[(i + 2)..lineEnd];
                    if (IsMarkupProse(lineText))
                        context.Add(i, lineText);
                    i = lineEnd;
                    previousSignificant = ';';
                    continue;
                }

                if (c == '@' && i + 1 < text.Length && text[i + 1] == '<')
                {
                    i++;
                    continue;
                }

                var next = ScanCSharpToken(i, relaxed: false);
                previousSignificant = text[next - 1];
                i = next;
            }

            return i;
        }

        /// <summary>Scans <c>@if (...) { ... } else { ... }</c> and its kin from just after the keyword.</summary>
        private int ScanControlStatement(int afterKeyword)
        {
            var i = afterKeyword;
            while (true)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;

                if (i < text.Length && text[i] == '(')
                {
                    i = SkipBalanced(i, '(', ')', collect: true, relaxed: false);
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                        i++;
                }

                if (i < text.Length && text[i] == '{')
                    i = ScanCodeBlock(i + 1);
                else
                    return i;

                var look = i;
                while (look < text.Length && char.IsWhiteSpace(text[look]))
                    look++;

                var word = ReadIdentifier(look);
                if (!ContinuationKeywords.Contains(word) && word != "while")
                    return i;

                i = look + word.Length;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;

                var inner = ReadIdentifier(i);
                if (inner == "if")
                    i += 2;
            }
        }

        /// <summary>Scans a whole range as C#, recording prose literals.</summary>
        public void ScanCSharpRange(int start, int end, bool insideTextAttribute)
        {
            var i = start;
            while (i < end)
                i = ScanCSharpToken(i, insideTextAttribute);
        }

        /// <summary>Consumes one C# token (recording it if it is a prose literal); returns the index after it.</summary>
        private int ScanCSharpToken(int i, bool relaxed)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var lineEnd = text.IndexOf('\n', i);
                return lineEnd < 0 ? text.Length : lineEnd;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return end < 0 ? text.Length : end + 2;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < text.Length && text[j] != '\'')
                    j += text[j] == '\\' ? 2 : 1;
                return Math.Min(text.Length, j + 1);
            }

            if (c is '"' or '$' or '@')
            {
                var literalEnd = TryReadStringLiteral(i, out var literal);
                if (literalEnd > i)
                {
                    Record(i, literal, relaxed);
                    return literalEnd;
                }
            }

            return i + 1;
        }

        private void Record(int position, string literal, bool relaxed)
        {
            var line = context.LineOf(position);
            var fullLine = context.LineText(line);
            var lineStart = context.Source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
            var before = position > lineStart ? context.Source[lineStart..position] : string.Empty;

            // A literal continuing a call from the previous line (a long Snackbar.Add) still belongs to it.
            if (line > 1 && !UserFacingCallContext().IsMatch(before))
                before = context.LineText(line - 1).TrimEnd() + " " + before;

            if (IsCSharpProse(literal, before, fullLine, relaxed))
                context.Add(position, literal);
        }

        /// <summary>
        /// Reads a C# string literal starting at <paramref name="i"/> (regular, verbatim, interpolated or raw);
        /// returns the index after it, or <paramref name="i"/> if none starts here. Interpolation holes become
        /// "{0}", and literals inside them are recorded in their own right.
        /// </summary>
        private int TryReadStringLiteral(int i, out string literal)
        {
            literal = string.Empty;
            var j = i;
            var interpolated = false;
            var verbatim = false;
            while (j < text.Length && text[j] is '$' or '@')
            {
                if (text[j] == '$')
                    interpolated = true;
                else
                    verbatim = true;
                j++;
            }

            if (j >= text.Length || text[j] != '"')
                return i;

            if (j + 2 < text.Length && text[j + 1] == '"' && text[j + 2] == '"')
            {
                var close = text.IndexOf("\"\"\"", j + 3, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 3;
                literal = text[(j + 3)..Math.Max(j + 3, end - 3)];
                return end;
            }

            var builder = new StringBuilder();
            j++;
            while (j < text.Length)
            {
                var c = text[j];
                if (c == '"')
                {
                    if (verbatim && j + 1 < text.Length && text[j + 1] == '"')
                    {
                        builder.Append('"');
                        j += 2;
                        continue;
                    }

                    j++;
                    break;
                }

                if (c == '\\' && !verbatim)
                {
                    if (j + 1 < text.Length)
                        builder.Append(text[j + 1] == '"' ? '"' : ' ');
                    j += 2;
                    continue;
                }

                if (interpolated && c == '{')
                {
                    if (j + 1 < text.Length && text[j + 1] == '{')
                    {
                        builder.Append('{');
                        j += 2;
                        continue;
                    }

                    j = SkipBalanced(j, '{', '}', collect: true, relaxed: false);
                    builder.Append("{0}");
                    continue;
                }

                if (c == '\n' && !verbatim)
                    break;

                builder.Append(c);
                j++;
            }

            literal = builder.ToString();
            return j;
        }

        /// <summary>
        /// Skips a balanced bracket run starting at <paramref name="openIndex"/> (which holds <paramref name="open"/>),
        /// recording prose literals inside when <paramref name="collect"/>; returns the index after the close.
        /// </summary>
        private int SkipBalanced(int openIndex, char open, char close, bool collect, bool relaxed)
        {
            var depth = 0;
            var i = openIndex;
            while (i < text.Length)
            {
                var c = text[i];
                if (c is '"' or '$' or '@' && (c == '"' || (i + 1 < text.Length && text[i + 1] is '"' or '$' or '@')))
                {
                    var end = TryReadStringLiteral(i, out var literal);
                    if (end > i)
                    {
                        if (collect)
                            Record(i, literal, relaxed);
                        i = end;
                        continue;
                    }
                }

                if (c == '\'')
                {
                    i = ScanCSharpToken(i, relaxed);
                    continue;
                }

                if (c == open)
                    depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                        return i + 1;
                }

                i++;
            }

            return i;
        }

        /// <summary>
        /// Skips an implicit expression (<c>@a.b(c)[d]?.e</c>) starting at the identifier; stops at
        /// <paramref name="terminator"/> when inside an attribute value.
        /// </summary>
        private int SkipImplicitExpression(int start, char terminator = '\0', bool relaxed = false)
        {
            var i = start;
            while (i < text.Length)
            {
                var identifier = ReadIdentifier(i);
                i += identifier.Length;
                if (i >= text.Length)
                    return i;

                var c = text[i];
                if (c == '(')
                {
                    i = SkipBalanced(i, '(', ')', collect: true, relaxed);
                    if (i >= text.Length)
                        return i;
                    c = text[i];
                }

                if (c == '[')
                {
                    i = SkipBalanced(i, '[', ']', collect: true, relaxed);
                    if (i >= text.Length)
                        return i;
                    c = text[i];
                }

                if (c == '!' && i + 1 < text.Length && text[i + 1] == '.')
                {
                    i++;
                    c = '.';
                }

                if (c == '?' && i + 1 < text.Length && text[i + 1] == '.')
                {
                    i++;
                    c = '.';
                }

                if (c == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_') && text[i + 1] != terminator)
                {
                    i++;
                    continue;
                }

                return i;
            }

            return i;
        }

        private string ReadIdentifier(int start)
        {
            var i = start;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;
            return start < text.Length && (char.IsLetter(text[start]) || text[start] == '_') ? text[start..i] : string.Empty;
        }

        private bool IsAtLineStart(int i)
        {
            for (var j = i - 1; j >= 0 && text[j] != '\n'; j--)
            {
                if (!char.IsWhiteSpace(text[j]))
                    return false;
            }

            return true;
        }

        private static bool IsVoidElement(string name) =>
            name is "br" or "hr" or "img" or "input" or "meta" or "link" or "area" or "base" or "col" or "embed"
                or "source" or "track" or "wbr";
    }
}

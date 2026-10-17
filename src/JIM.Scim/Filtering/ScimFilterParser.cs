// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JIM.Scim.Filtering;

/// <summary>
/// Reads SCIM filters and PATCH paths (RFC 7644 sections 3.4.2.2 and 3.5.2) into a <see cref="ScimFilter"/> tree.
/// <para>
/// The whole grammar is read, whatever a given caller can evaluate: deciding which attributes and operators to support
/// belongs to the caller, and a filter refused as unsupported is a different answer from one refused as malformed.
/// Operators, logical keywords and the literals <c>true</c>, <c>false</c> and <c>null</c> are read in any case;
/// attribute names are kept as written.
/// </para>
/// <para>
/// Nesting is bounded by <see cref="MaximumDepth"/>, because the parser descends recursively and a filter arrives
/// from outside: a request of ten thousand open parentheses must be refused, not allowed to exhaust the stack.
/// </para>
/// </summary>
public static partial class ScimFilterParser
{
    /// <summary>The deepest nesting of parentheses, <c>not</c> and value filters a filter may have.</summary>
    public const int MaximumDepth = 32;

    /// <summary>
    /// Reads a filter. Throws <see cref="ScimFilterException"/> when it does not conform to the grammar.
    /// </summary>
    public static ScimFilter Parse(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var reader = new Reader(Tokenise(filter));
        var parsed = reader.ParseOr(0);
        reader.ExpectEnd();
        return parsed;
    }

    /// <summary>
    /// Reads a PATCH path: <c>name.givenName</c>, <c>members[value eq "id"]</c> or <c>emails[type eq "work"].value</c>.
    /// Throws <see cref="ScimFilterException"/> when it does not conform to the grammar.
    /// </summary>
    public static ScimPatchPath ParsePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var reader = new Reader(Tokenise(path));
        var parsed = reader.ParsePatchPath();
        reader.ExpectEnd();
        return parsed;
    }

    #region tokens
    private enum TokenKind
    {
        OpenParenthesis,
        CloseParenthesis,
        OpenBracket,
        CloseBracket,
        String,
        Word
    }

    private sealed record Token(TokenKind Kind, string Text, int Position);

    private static List<Token> Tokenise(string text)
    {
        var tokens = new List<Token>();
        var position = 0;

        while (position < text.Length)
        {
            var character = text[position];
            if (char.IsWhiteSpace(character))
            {
                position++;
                continue;
            }

            switch (character)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.OpenParenthesis, "(", position++));
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.CloseParenthesis, ")", position++));
                    continue;
                case '[':
                    tokens.Add(new Token(TokenKind.OpenBracket, "[", position++));
                    continue;
                case ']':
                    tokens.Add(new Token(TokenKind.CloseBracket, "]", position++));
                    continue;
                case '"':
                    tokens.Add(ReadString(text, ref position));
                    continue;
            }

            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('(' or ')' or '[' or ']' or '"'))
                position++;

            tokens.Add(new Token(TokenKind.Word, text[start..position], start));
        }

        return tokens;
    }

    /// <summary>
    /// A JSON string (RFC 8259 section 7), decoded. Errors name the position only: the string is a comparison value.
    /// </summary>
    private static Token ReadString(string text, ref int position)
    {
        var start = position++;
        var value = new StringBuilder();

        while (position < text.Length)
        {
            var character = text[position++];
            if (character == '"')
                return new Token(TokenKind.String, value.ToString(), start);

            if (character != '\\')
            {
                value.Append(character);
                continue;
            }

            if (position >= text.Length)
                break;

            var escaped = text[position++];
            switch (escaped)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (position + 4 > text.Length
                        || !int.TryParse(text.AsSpan(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        throw new ScimFilterException($"The string starting at position {start} has an invalid \\u escape.");
                    value.Append((char)code);
                    position += 4;
                    break;
                default:
                    throw new ScimFilterException($"The string starting at position {start} has an invalid escape sequence.");
            }
        }

        throw new ScimFilterException($"The string starting at position {start} is not terminated.");
    }
    #endregion

    #region attribute paths
    // ATTRNAME (RFC 7644 section 3.10); "$ref" is the one name outside it the core schemas use, as a sub-attribute.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$")]
    private static partial Regex AttributeName();

    // A JSON number (RFC 8259 section 6).
    [GeneratedRegex("^-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    // Looked up by name rather than through Enum.TryParse, which would also accept a number such as "3".
    private static readonly Dictionary<string, ScimComparisonOperator> ComparisonOperators =
        Enum.GetValues<ScimComparisonOperator>().ToDictionary(o => o.ToString(), StringComparer.OrdinalIgnoreCase);

    private const string UrnPrefix = "urn:";
    private const string ReferenceSubAttribute = "$ref";

    private static ScimAttributePath ReadAttributePath(Token token)
    {
        var text = token.Text;
        string? schemaUrn = null;

        if (text.StartsWith(UrnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // The attribute follows the URN's last colon; a URN's own segments never contain a dot.
            var separator = text.LastIndexOf(':');
            schemaUrn = text[..separator];
            text = text[(separator + 1)..];
            if (schemaUrn.Length <= UrnPrefix.Length)
                throw Invalid(token, "is not an attribute path");
        }

        var parts = text.Split('.');
        if (parts.Length > 2 || !AttributeName().IsMatch(parts[0]))
            throw Invalid(token, "is not an attribute path");

        if (parts.Length == 2 && !IsSubAttributeName(parts[1]))
            throw Invalid(token, "is not an attribute path");

        return new ScimAttributePath(schemaUrn, parts[0], parts.Length == 2 ? parts[1] : null);
    }

    private static bool IsSubAttributeName(string name) =>
        AttributeName().IsMatch(name) || string.Equals(name, ReferenceSubAttribute, StringComparison.Ordinal);

    private static ScimFilterException Invalid(Token token, string problem) =>
        new($"The {Describe(token)} at position {token.Position} {problem}.");

    /// <summary>
    /// A token named for an error message. A string is described, never quoted: it is a comparison value.
    /// </summary>
    private static string Describe(Token token) =>
        token.Kind == TokenKind.String ? "string" : "'" + token.Text + "'";
    #endregion

    /// <summary>
    /// A recursive-descent reader over the tokens: <c>or</c> over <c>and</c> over a unary filter, so <c>and</c> binds
    /// more tightly, and a run of either operator groups from the left.
    /// </summary>
    private sealed class Reader(List<Token> tokens)
    {
        private int _index;

        private Token? Peek => _index < tokens.Count ? tokens[_index] : null;

        public void ExpectEnd()
        {
            if (Peek is { } extra)
                throw Invalid(extra, "was not expected after the end of the filter");
        }

        public ScimFilter ParseOr(int depth)
        {
            var left = ParseAnd(depth);
            while (IsKeyword(Peek, "or"))
            {
                _index++;
                left = new ScimLogicalFilter(ScimLogicalOperator.Or, left, ParseAnd(depth));
            }

            return left;
        }

        private ScimFilter ParseAnd(int depth)
        {
            var left = ParseUnary(depth);
            while (IsKeyword(Peek, "and"))
            {
                _index++;
                left = new ScimLogicalFilter(ScimLogicalOperator.And, left, ParseUnary(depth));
            }

            return left;
        }

        private ScimFilter ParseUnary(int depth)
        {
            var token = Next("a filter");

            if (token.Kind == TokenKind.OpenParenthesis)
                return Grouped(token, depth);

            if (token.Kind != TokenKind.Word)
                throw Invalid(token, "cannot start a filter");

            if (IsKeyword(token, "not"))
            {
                var open = Next("'(' after 'not'");
                if (open.Kind != TokenKind.OpenParenthesis)
                    throw Invalid(open, "must be '(': 'not' takes a filter in parentheses");
                return new ScimNotFilter(Grouped(open, depth));
            }

            var path = ReadAttributePath(token);

            if (Peek is { Kind: TokenKind.OpenBracket } bracket)
            {
                _index++;
                return new ScimValuePathFilter(ValuePathAttribute(path, token), Bracketed(bracket, depth));
            }

            var operatorToken = Next("a comparison operator");
            if (operatorToken.Kind != TokenKind.Word || !ComparisonOperators.TryGetValue(operatorToken.Text, out var comparison))
                throw Invalid(operatorToken, "is not a comparison operator");

            return comparison == ScimComparisonOperator.Pr
                ? new ScimComparisonFilter(path, comparison, null)
                : new ScimComparisonFilter(path, comparison, ReadValue());
        }

        public ScimPatchPath ParsePatchPath()
        {
            var token = Next("an attribute path");
            if (token.Kind != TokenKind.Word)
                throw Invalid(token, "is not an attribute path");

            var path = ReadAttributePath(token);
            if (Peek is not { Kind: TokenKind.OpenBracket } bracket)
                return new ScimPatchPath(path, null, null);

            _index++;
            var filter = Bracketed(bracket, 0);

            // A sub-attribute after the closing bracket is written ".value", with nothing between.
            string? subAttribute = null;
            if (Peek is { Kind: TokenKind.Word } after && after.Text.StartsWith('.'))
            {
                _index++;
                subAttribute = after.Text[1..];
                if (!IsSubAttributeName(subAttribute))
                    throw Invalid(after, "is not a sub-attribute");
            }

            return new ScimPatchPath(ValuePathAttribute(path, token), filter, subAttribute);
        }

        private static ScimAttributePath ValuePathAttribute(ScimAttributePath path, Token token)
        {
            // emails.value[...] filters nothing: a value filter applies to a multi-valued attribute's entries.
            if (path.SubAttributeName != null)
                throw Invalid(token, "cannot take a value filter: only an attribute's entries can be filtered");
            return path;
        }

        private ScimFilter Grouped(Token open, int depth) => Enclosed(open, depth, TokenKind.CloseParenthesis, ")");

        private ScimFilter Bracketed(Token open, int depth) => Enclosed(open, depth, TokenKind.CloseBracket, "]");

        private ScimFilter Enclosed(Token open, int depth, TokenKind closing, string closingText)
        {
            if (depth + 1 >= MaximumDepth)
                throw new ScimFilterException($"The filter is nested more than {MaximumDepth} levels deep at position {open.Position}.");

            var inner = ParseOr(depth + 1);
            var close = Next($"'{closingText}'");
            if (close.Kind != closing)
                throw Invalid(close, $"was found where '{closingText}' closing the '{open.Text}' at position {open.Position} was expected");
            return inner;
        }

        private ScimFilterValue ReadValue()
        {
            var token = Next("a comparison value");
            if (token.Kind == TokenKind.String)
                return ScimFilterValue.String(token.Text);

            if (token.Kind == TokenKind.Word)
            {
                if (IsKeyword(token, "true"))
                    return ScimFilterValue.Boolean(true);
                if (IsKeyword(token, "false"))
                    return ScimFilterValue.Boolean(false);
                if (IsKeyword(token, "null"))
                    return ScimFilterValue.Null;
                if (JsonNumber().IsMatch(token.Text))
                    return ScimFilterValue.Number(token.Text);
            }

            throw Invalid(token, "is not a comparison value: a string must be in double quotes");
        }

        private Token Next(string expected)
        {
            if (Peek is not { } token)
                throw new ScimFilterException($"The filter ended where {expected} was expected.");
            _index++;
            return token;
        }

        private static bool IsKeyword(Token? token, string keyword) =>
            token is { Kind: TokenKind.Word } && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Validation and quoting for database identifiers (schema, table, view and column names).
/// <para>
/// Identifiers cannot be parameterised, so they are the one part of a generated statement that is
/// interpolated into the command text. Two defences apply, in this order: the identifier is validated
/// (rejecting anything that is not a plausible object name), then it is quoted with the dialect's
/// quote character doubled inside, so a hostile name stays one identifier rather than escaping into
/// the surrounding statement.
/// </para>
/// </summary>
internal static class SqlIdentifier
{
    /// <summary>
    /// Both Priority 1 databases cap identifiers at 128 characters (Oracle since 12.2). A longer value
    /// is not a real object name, so it is refused rather than quoted and sent to fail at the server.
    /// </summary>
    internal const int MaxLength = 128;

    /// <summary>
    /// The longest bind-variable name accepted. Kept to the most restrictive of the supported servers
    /// so a name JIM generates for one dialect is always legal in the others.
    /// </summary>
    internal const int MaxParameterNameLength = 30;

    /// <summary>
    /// Quotes an identifier for a dialect, doubling any embedded closing quote character.
    /// </summary>
    /// <param name="identifier">The unquoted identifier.</param>
    /// <param name="openQuote">The dialect's opening quote character (for example '[').</param>
    /// <param name="closeQuote">The dialect's closing quote character (for example ']').</param>
    /// <param name="parameterName">The caller's argument name, for the exception message.</param>
    internal static string Quote(string? identifier, char openQuote, char closeQuote, string parameterName)
    {
        Validate(identifier, parameterName);

        // identifier is non-null after Validate.
        return string.Concat(openQuote, identifier!.Replace(closeQuote.ToString(), new string(closeQuote, 2), StringComparison.Ordinal), closeQuote);
    }

    /// <summary>
    /// Refuses an identifier that could not name a real database object. Control characters are the
    /// dangerous case: a NUL can truncate command text in a native layer downstream, and newlines make
    /// a generated statement unreviewable, while no legitimate table or column name contains either.
    /// </summary>
    internal static void Validate(string? identifier, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("A database identifier cannot be null, empty or whitespace.", parameterName);

        if (identifier.Length > MaxLength)
            throw new ArgumentException($"A database identifier cannot exceed {MaxLength} characters, but was {identifier.Length}.", parameterName);

        if (identifier.Any(char.IsControl))
            throw new ArgumentException("A database identifier cannot contain control characters.", parameterName);
    }

    /// <summary>
    /// Refuses a bind-variable name that is not identifier-shaped. Parameter names are interpolated
    /// into the command text alongside the dialect's prefix, so only names JIM itself generates should
    /// ever reach here; the check is a guard against a future caller passing something derived from
    /// configuration.
    /// </summary>
    internal static void ValidateParameterName(string? parameterName, string argumentName)
    {
        if (string.IsNullOrEmpty(parameterName))
            throw new ArgumentException("A parameter name cannot be null or empty.", argumentName);

        if (parameterName.Length > MaxParameterNameLength)
            throw new ArgumentException($"A parameter name cannot exceed {MaxParameterNameLength} characters, but was {parameterName.Length}.", argumentName);

        if (!char.IsAsciiLetter(parameterName[0]) && parameterName[0] != '_')
            throw new ArgumentException($"A parameter name must start with a letter or underscore, but '{parameterName}' does not.", argumentName);

        if (parameterName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException($"A parameter name may only contain letters, digits and underscores, but '{parameterName}' does not.", argumentName);
    }
}

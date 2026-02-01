using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DynamicExpresso;
using DynamicExpresso.Exceptions;
using Serilog;

namespace JIM.Application.Expressions;

/// <summary>
/// Expression evaluator implementation using DynamicExpresso.
/// Provides C#-like expression syntax with built-in functions for identity management.
/// </summary>
public class DynamicExpressoEvaluator : IExpressionEvaluator
{
    private readonly Interpreter _interpreter;

    // Static cache shared across all instances - expressions are deterministic so cache can be shared
    // This ensures expression compilation is only done once per expression string across all sync operations
    private static readonly ConcurrentDictionary<string, Lambda> _compiledExpressions = new();

    // Static cache metrics for diagnostics (shared across instances)
    private static long _cacheHits;
    private static long _cacheMisses;

    // Threshold for logging slow expressions (in milliseconds)
    private const int SlowExpressionThresholdMs = 10;

    // Word list for passphrase generation (common, easy-to-type words)
    private static readonly string[] PassphraseWords =
    [
        "apple", "banana", "cherry", "delta", "eagle", "forest", "garden", "harbor",
        "island", "jungle", "kitten", "lemon", "marble", "nectar", "orange", "planet",
        "quartz", "rabbit", "silver", "temple", "umbrella", "violet", "winter", "yellow",
        "anchor", "bridge", "castle", "dragon", "ember", "falcon", "glacier", "hammer",
        "ivory", "jasper", "knight", "lantern", "meadow", "noble", "ocean", "phoenix",
        "quest", "raven", "shadow", "thunder", "velvet", "willow", "zenith", "aurora",
        "beacon", "crystal", "dancer", "eclipse", "flame", "granite", "horizon", "indigo"
    ];

    public DynamicExpressoEvaluator()
    {
        _interpreter = new Interpreter();
        RegisterBuiltInFunctions();
    }

    /// <inheritdoc/>
    public object? Evaluate(string expression, ExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        var sw = Stopwatch.StartNew();
        try
        {
            var lambda = GetOrCompileExpression(expression);

            return lambda.Invoke(
                new Parameter("mv", context.Mv),
                new Parameter("cs", context.Cs));
        }
        finally
        {
            sw.Stop();
            if (sw.ElapsedMilliseconds > SlowExpressionThresholdMs)
            {
                Log.Warning("Slow expression evaluation: '{Expression}' took {ElapsedMs}ms",
                    expression.Length > 50 ? expression[..50] + "..." : expression,
                    sw.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Gets the current expression cache metrics.
    /// </summary>
    /// <returns>A tuple containing (CacheHits, CacheMisses).</returns>
    public (long CacheHits, long CacheMisses) GetCacheMetrics() => (_cacheHits, _cacheMisses);

    /// <inheritdoc/>
    public ExpressionValidationResult Validate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return ExpressionValidationResult.Failure("Expression cannot be empty.");
        }

        try
        {
            // Create a test interpreter with mock parameters
            var testInterpreter = new Interpreter();
            RegisterBuiltInFunctions(testInterpreter);

            testInterpreter.Parse(expression,
                new Parameter("mv", typeof(AttributeAccessor)),
                new Parameter("cs", typeof(AttributeAccessor)));

            return ExpressionValidationResult.Success();
        }
        catch (ParseException ex)
        {
            return ExpressionValidationResult.Failure(ex.Message, ex.Position);
        }
        catch (Exception ex)
        {
            return ExpressionValidationResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc/>
    public ExpressionTestResult Test(string expression, ExpressionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return ExpressionTestResult.Failure("Expression cannot be empty.");
        }

        try
        {
            var result = Evaluate(expression, context);
            return ExpressionTestResult.Success(result);
        }
        catch (Exception ex)
        {
            return ExpressionTestResult.Failure(ex.Message);
        }
    }

    private Lambda GetOrCompileExpression(string expression)
    {
        // Check for cache hit first
        if (_compiledExpressions.TryGetValue(expression, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        // Cache miss - compile and add
        Interlocked.Increment(ref _cacheMisses);
        Log.Debug("Expression cache miss - compiling: '{Expression}'",
            expression.Length > 50 ? expression[..50] + "..." : expression);

        return _compiledExpressions.GetOrAdd(expression, expr =>
        {
            return _interpreter.Parse(expr,
                new Parameter("mv", typeof(AttributeAccessor)),
                new Parameter("cs", typeof(AttributeAccessor)));
        });
    }

    private void RegisterBuiltInFunctions(Interpreter? interpreter = null)
    {
        var target = interpreter ?? _interpreter;

        // String functions - accept object? to handle AttributeAccessor indexer returns
        target.SetFunction("Trim", (Func<object?, string?>)(o => AsString(o)?.Trim()));
        target.SetFunction("Upper", (Func<object?, string?>)(o => AsString(o)?.ToUpperInvariant()));
        target.SetFunction("Lower", (Func<object?, string?>)(o => AsString(o)?.ToLowerInvariant()));
        target.SetFunction("Capitalise", (Func<object?, string?>)(o => Capitalise(AsString(o))));
        target.SetFunction("Left", (Func<object?, int, string?>)((o, count) => Left(AsString(o), count)));
        target.SetFunction("Right", (Func<object?, int, string?>)((o, count) => Right(AsString(o), count)));
        target.SetFunction("Substring", (Func<object?, int, int, string?>)((o, start, length) => SafeSubstring(AsString(o), start, length)));
        target.SetFunction("Replace", (Func<object?, object?, object?, string?>)((o, oldVal, newVal) => AsString(o)?.Replace(AsString(oldVal) ?? "", AsString(newVal) ?? "")));
        target.SetFunction("StartsWith", (Func<object?, object?, bool>)((o, prefix) => AsString(o)?.StartsWith(AsString(prefix) ?? "") ?? false));
        target.SetFunction("EndsWith", (Func<object?, object?, bool>)((o, suffix) => AsString(o)?.EndsWith(AsString(suffix) ?? "") ?? false));
        target.SetFunction("Length", (Func<object?, int>)(o => AsString(o)?.Length ?? 0));
        target.SetFunction("IsNullOrEmpty", (Func<object?, bool>)(o => string.IsNullOrEmpty(AsString(o))));
        target.SetFunction("IsNullOrWhitespace", (Func<object?, bool>)(o => string.IsNullOrWhiteSpace(AsString(o))));

        // Containment functions
        target.SetFunction("Contains", (Func<object?, object?, bool>)((o, value) => AsString(o)?.Contains(AsString(value) ?? "") ?? false));
        target.SetFunction("CollectionContains", (Func<object?, object?, bool>)((collection, value) => CollectionContains(collection, AsString(value) ?? "")));

        // Array/Collection functions - for converting delimited strings to multi-valued attributes
        target.SetFunction("Split", (Func<object?, object?, string[]>)((o, delimiter) => Split(AsString(o), AsString(delimiter))));
        target.SetFunction("Join", (Func<object?, object?, string?>)((collection, delimiter) => Join(collection, AsString(delimiter))));

        // Conditional functions
        target.SetFunction("Coalesce", (Func<object?, object?, object?>)((a, b) => a ?? b));
        target.SetFunction("IIF", (Func<bool, object?, object?, object?>)((condition, trueVal, falseVal) => condition ? trueVal : falseVal));

        // Equality comparison function - IMPORTANT: The == operator doesn't work correctly
        // when comparing object? to string because it uses reference equality.
        // Use Eq() for value-based string/object comparisons in expressions.
        target.SetFunction("Eq", (Func<object?, object?, bool>)ValueEquals);

        // Date functions
        target.SetFunction("Now", (Func<DateTime>)(() => DateTime.UtcNow));
        target.SetFunction("Today", (Func<DateTime>)(() => DateTime.UtcNow.Date));
        target.SetFunction("FormatDate", (Func<DateTime?, string, string?>)((dt, format) => dt?.ToString(format, CultureInfo.InvariantCulture)));
        target.SetFunction("ToFileTime", (Func<object?, long?>)(o => ToFileTime(o)));
        target.SetFunction("FromFileTime", (Func<object?, DateTime?>)(o => FromFileTime(o)));

        // Conversion functions
        target.SetFunction("ToString", (Func<object?, string?>)(o => o?.ToString()));
        target.SetFunction("ToInt", (Func<object?, int>)(o => int.TryParse(AsString(o), out var i) ? i : 0));

        // DN Helper functions
        target.SetFunction("EscapeDN", (Func<object?, string?>)(o => EscapeDN(AsString(o))));

        // Password generation functions
        target.SetFunction("RandomPassword", (Func<int, bool, string>)RandomPassword);
        target.SetFunction("RandomPassphrase", (Func<int, string, string>)RandomPassphrase);

        // Bitwise functions for AD userAccountControl manipulation
        target.SetFunction("EnableUser", (Func<object?, int>)(o => ClearBit(AsInt(o), 2)));  // Clear ACCOUNTDISABLE (0x0002)
        target.SetFunction("DisableUser", (Func<object?, int>)(o => SetBit(AsInt(o), 2)));   // Set ACCOUNTDISABLE (0x0002)
        target.SetFunction("SetBit", (Func<object?, int, int>)((o, bit) => SetBit(AsInt(o), bit)));
        target.SetFunction("ClearBit", (Func<object?, int, int>)((o, bit) => ClearBit(AsInt(o), bit)));
        target.SetFunction("HasBit", (Func<object?, int, bool>)((o, bit) => HasBit(AsInt(o), bit)));
    }

    /// <summary>
    /// Converts an object to string, handling null and various types.
    /// </summary>
    private static string? AsString(object? value)
    {
        return value?.ToString();
    }

    /// <summary>
    /// Converts an object to int, handling null and various types.
    /// </summary>
    private static int AsInt(object? value)
    {
        if (value == null) return 0;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is short s) return s;
        if (value is byte b) return b;
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return 0;
    }

    #region String Functions

    private static string? Capitalise(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        var result = new StringBuilder(s.Length);
        var capitaliseNext = true;

        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '\'')
            {
                result.Append(c);
                capitaliseNext = true;
            }
            else if (capitaliseNext)
            {
                result.Append(char.ToUpperInvariant(c));
                capitaliseNext = false;
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString();
    }

    private static string? Left(string? s, int count)
    {
        if (s == null) return null;
        if (count <= 0) return string.Empty;
        return s.Length <= count ? s : s[..count];
    }

    private static string? Right(string? s, int count)
    {
        if (s == null) return null;
        if (count <= 0) return string.Empty;
        return s.Length <= count ? s : s[^count..];
    }

    private static string? SafeSubstring(string? s, int start, int length)
    {
        if (s == null) return null;
        if (start < 0) start = 0;
        if (start >= s.Length) return string.Empty;
        if (length <= 0) return string.Empty;

        var actualLength = Math.Min(length, s.Length - start);
        return s.Substring(start, actualLength);
    }

    #endregion

    #region Containment Functions

    private static bool CollectionContains(object? collection, string value)
    {
        if (collection == null)
            return false;

        // Handle various collection types
        if (collection is IEnumerable<string> stringEnumerable)
        {
            return stringEnumerable.Contains(value);
        }

        if (collection is IEnumerable<object> objectEnumerable)
        {
            return objectEnumerable.Any(item => string.Equals(item?.ToString(), value, StringComparison.Ordinal));
        }

        // If it's a single value, check if it matches
        return string.Equals(collection.ToString(), value, StringComparison.Ordinal);
    }

    #endregion

    #region Array/Collection Functions

    /// <summary>
    /// Splits a string by a delimiter into an array of strings.
    /// This is useful for converting delimited single-valued attributes (e.g., "A|B|C")
    /// into multiple values for multi-valued metaverse attributes.
    /// Empty entries are removed automatically.
    /// </summary>
    /// <param name="value">The string to split.</param>
    /// <param name="delimiter">The delimiter to split on (e.g., "|", ",", ";").</param>
    /// <returns>An array of non-empty strings, or an empty array if value is null/empty.</returns>
    private static string[] Split(string? value, string? delimiter)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<string>();

        if (string.IsNullOrEmpty(delimiter))
            return new[] { value };

        return value.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Joins a collection of values into a single string with a delimiter.
    /// This is the reverse of Split - useful for converting multi-valued attributes
    /// into a single delimited string for systems that don't support MVAs.
    /// </summary>
    /// <param name="collection">The collection to join.</param>
    /// <param name="delimiter">The delimiter to use between values.</param>
    /// <returns>A joined string, or null if collection is null/empty.</returns>
    private static string? Join(object? collection, string? delimiter)
    {
        if (collection == null)
            return null;

        delimiter ??= ",";

        if (collection is IEnumerable<string> stringEnumerable)
        {
            var items = stringEnumerable.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            return items.Length > 0 ? string.Join(delimiter, items) : null;
        }

        if (collection is IEnumerable<object> objectEnumerable)
        {
            var items = objectEnumerable
                .Where(o => o != null)
                .Select(o => o.ToString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            return items.Length > 0 ? string.Join(delimiter, items!) : null;
        }

        // Single value - just return it as-is
        var singleValue = collection.ToString();
        return string.IsNullOrEmpty(singleValue) ? null : singleValue;
    }

    #endregion

    #region DN Helper Functions

    /// <summary>
    /// Escapes special characters in a Distinguished Name component.
    /// Special characters: , + " \ &lt; &gt; ; = and leading/trailing spaces, # at start.
    /// </summary>
    private static string? EscapeDN(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = new StringBuilder(value.Length * 2);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            // Escape special DN characters
            if (c == ',' || c == '+' || c == '"' || c == '\\' || c == '<' || c == '>' || c == ';' || c == '=')
            {
                result.Append('\\');
                result.Append(c);
            }
            // Escape leading space or #
            else if (i == 0 && (c == ' ' || c == '#'))
            {
                result.Append('\\');
                result.Append(c);
            }
            // Escape trailing space
            else if (i == value.Length - 1 && c == ' ')
            {
                result.Append('\\');
                result.Append(c);
            }
            // Escape newlines and carriage returns
            else if (c == '\r')
            {
                result.Append("\\0D");
            }
            else if (c == '\n')
            {
                result.Append("\\0A");
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    #endregion

    #region Password Generation Functions

    /// <summary>
    /// Generates a random password with the specified length.
    /// </summary>
    /// <param name="length">The desired password length (minimum 8).</param>
    /// <param name="extendedChars">If true, includes special characters (!@#$%^&amp;*). If false, alphanumeric only.</param>
    private static string RandomPassword(int length, bool extendedChars)
    {
        length = Math.Max(8, Math.Min(128, length)); // Clamp between 8 and 128

        const string lowercase = "abcdefghijklmnopqrstuvwxyz";
        const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string digits = "0123456789";
        const string special = "!@#$%^&*";

        var chars = lowercase + uppercase + digits;
        if (extendedChars)
        {
            chars += special;
        }

        var result = new StringBuilder(length);
        var bytes = new byte[length];

        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        // Ensure at least one of each required character type
        result.Append(lowercase[bytes[0] % lowercase.Length]);
        result.Append(uppercase[bytes[1] % uppercase.Length]);
        result.Append(digits[bytes[2] % digits.Length]);

        var startIndex = 3;
        if (extendedChars && length >= 4)
        {
            result.Append(special[bytes[3] % special.Length]);
            startIndex = 4;
        }

        // Fill the rest randomly
        for (var i = startIndex; i < length; i++)
        {
            result.Append(chars[bytes[i] % chars.Length]);
        }

        // Shuffle the result to avoid predictable positions
        return ShuffleString(result.ToString());
    }

    /// <summary>
    /// Generates a random passphrase with the specified number of words.
    /// </summary>
    /// <param name="wordCount">The number of words (minimum 3, maximum 10).</param>
    /// <param name="separator">The separator between words (default "-").</param>
    private static string RandomPassphrase(int wordCount, string separator)
    {
        wordCount = Math.Max(3, Math.Min(10, wordCount)); // Clamp between 3 and 10

        if (string.IsNullOrEmpty(separator))
        {
            separator = "-";
        }

        var bytes = new byte[wordCount];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var words = new string[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            words[i] = PassphraseWords[bytes[i] % PassphraseWords.Length];
        }

        return string.Join(separator, words);
    }

    private static string ShuffleString(string input)
    {
        var array = input.ToCharArray();
        var bytes = new byte[array.Length];

        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        for (var i = array.Length - 1; i > 0; i--)
        {
            var j = bytes[i] % (i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }

        return new string(array);
    }

    #endregion

    #region DateTime Conversion Functions

    // These functions support Active Directory Large Integer (FILETIME) attributes:
    // - accountExpires: When the account expires (0 or Int64.MaxValue = never)
    // - pwdLastSet: When the password was last set (0 = must change at next logon)
    // - lastLogon: Last interactive logon time (not replicated between DCs)
    // - lastLogonTimestamp: Last logon time (replicated, but with 9-14 day lag)
    // - lockoutTime: When the account was locked out (0 = not locked)
    // - badPasswordTime: Last failed password attempt
    //
    // FILETIME format: 100-nanosecond intervals since January 1, 1601 UTC
    // Range: 0 to 9223372036854775807 (Int64.MaxValue)

    /// <summary>
    /// Converts a DateTime to Windows FILETIME (100-nanosecond intervals since January 1, 1601 UTC).
    /// This is the format used by AD attributes like accountExpires, pwdLastSet, lastLogon, etc.
    /// A value of 0 or 9223372036854775807 (Int64.MaxValue) means "never expires" in AD.
    /// </summary>
    private static long? ToFileTime(object? value)
    {
        if (value == null)
            return null;

        DateTime? dateTime = value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,  // Handle DateTimeOffset from PostgreSQL
            string s => ParseDateTimeOrNull(s),     // Handle string dates (may return null for invalid strings)
            _ => throw new ArgumentException(
                $"ToFileTime cannot convert value of type '{value.GetType().Name}'. " +
                $"Expected DateTime, DateTimeOffset, or string. Value: '{value}'")
        };

        if (dateTime == null)
            return null;

        // Handle PostgreSQL's -infinity (DateTime.MinValue) and +infinity (DateTime.MaxValue)
        // These represent "no date" or "never" and should return null
        if (dateTime.Value == DateTime.MinValue || dateTime.Value == DateTime.MaxValue)
            return null;

        // Ensure we're working with UTC
        var utcDateTime = dateTime.Value.Kind == DateTimeKind.Utc
            ? dateTime.Value
            : dateTime.Value.ToUniversalTime();

        // FILETIME can only represent dates from 1601-01-01 onwards
        // Return null for dates before this (shouldn't happen with valid data, but be safe)
        if (utcDateTime.Year < 1601)
            return null;

        return utcDateTime.ToFileTimeUtc();
    }

    /// <summary>
    /// Attempts to parse a string as a DateTime, returning null if parsing fails.
    /// This allows graceful handling of empty strings or unparseable date formats.
    /// </summary>
    private static DateTime? ParseDateTimeOrNull(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Converts a Windows FILETIME (100-nanosecond intervals since January 1, 1601 UTC) to DateTime.
    /// This is the format used by AD attributes like accountExpires, pwdLastSet, lastLogon, etc.
    /// </summary>
    private static DateTime? FromFileTime(object? value)
    {
        if (value == null)
            return null;

        long? fileTime = value switch
        {
            long l => l,
            int i => i,
            string s => ParseLongOrNull(s),  // Handle string values (may return null for invalid numbers)
            _ => throw new ArgumentException(
                $"FromFileTime cannot convert value of type '{value.GetType().Name}'. " +
                $"Expected long, int, or string. Value: '{value}'")
        };

        if (fileTime == null || fileTime.Value <= 0)
            return null;

        // Int64.MaxValue means "never expires" in AD - return null to indicate no expiry
        if (fileTime.Value == long.MaxValue)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid FILETIME value
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse a string as a long, returning null if parsing fails.
    /// This allows graceful handling of empty strings or unparseable numbers.
    /// </summary>
    private static long? ParseLongOrNull(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        return long.TryParse(s, out var parsed) ? parsed : null;
    }

    #endregion

    #region Bitwise Functions

    /// <summary>
    /// Sets a bit in the value (value | bit).
    /// </summary>
    private static int SetBit(int value, int bit) => value | bit;

    /// <summary>
    /// Clears a bit in the value (value &amp; ~bit).
    /// </summary>
    private static int ClearBit(int value, int bit) => value & ~bit;

    /// <summary>
    /// Checks if a bit is set in the value ((value &amp; bit) == bit).
    /// </summary>
    private static bool HasBit(int value, int bit) => (value & bit) == bit;

    #endregion

    #region Equality Functions

    /// <summary>
    /// Performs value-based equality comparison between two objects.
    /// This is necessary because the == operator in expressions uses reference equality
    /// when comparing object? to string (since the indexer returns object?).
    /// </summary>
    /// <remarks>
    /// IMPORTANT: In C#, when you write `mv["attr"] == "value"`:
    /// - mv["attr"] returns object? (from the AttributeAccessor indexer)
    /// - "value" is a string literal
    /// - The == operator uses static type analysis, so it compares object? to string
    /// - For object comparisons, == uses reference equality, NOT value equality
    /// - This means two strings with the same content but different instances return false!
    ///
    /// Use Eq() for value-based comparisons: `Eq(mv["attr"], "value")`
    /// </remarks>
    private static bool ValueEquals(object? a, object? b)
    {
        // Handle null cases
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        // String comparison (most common case)
        if (a is string strA && b is string strB)
            return string.Equals(strA, strB, StringComparison.Ordinal);

        // If one is string and other is object, convert and compare
        if (a is string && b is not string)
            return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
        if (b is string && a is not string)
            return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);

        // Numeric comparisons - handle cross-type numeric equality
        if (IsNumeric(a) && IsNumeric(b))
        {
            try
            {
                var decA = Convert.ToDecimal(a);
                var decB = Convert.ToDecimal(b);
                return decA == decB;
            }
            catch
            {
                // Fall through to default Equals
            }
        }

        // Default to Equals method
        return a.Equals(b);
    }

    private static bool IsNumeric(object? obj)
    {
        return obj is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    #endregion
}

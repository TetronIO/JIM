// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JIM.Models.Logic;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Pure candidate-value computation for Unique Value Generation (#242): placement of a token against a base
/// value, the only-if-taken suffix and letter strategy, sequence rendering and width checking, and random
/// token drawing. Nothing here touches a repository or a reservation set; <see cref="UniqueValueGenerationServer"/>
/// is what turns a candidate into a checked, reserved, assigned value. Kept separate from that orchestration so
/// the placement and rendering rules (FR 23 to 27) can be unit-tested directly, with no database and no
/// asynchrony bar the random draw's use of <see cref="RandomNumberGenerator"/>.
/// </summary>
internal static class UniqueValueCandidates
{
    /// <summary>
    /// Places <paramref name="token"/> against <paramref name="baseValue"/> per FR 23: before the first
    /// <c>@</c> in the base value where there is one, otherwise appended, with <paramref name="separator"/>
    /// (null means none) between the two. A null or empty token returns the base value unchanged; a null or
    /// empty base value returns the token on its own ("a token with no base value is the value on its own").
    /// </summary>
    public static string Place(string? baseValue, string? token, string? separator)
    {
        if (string.IsNullOrEmpty(token))
            return baseValue ?? string.Empty;

        if (string.IsNullOrEmpty(baseValue))
            return token;

        var sep = separator ?? string.Empty;
        var atIndex = baseValue.IndexOf('@');
        return atIndex >= 0
            ? string.Concat(baseValue.AsSpan(0, atIndex), sep, token, baseValue.AsSpan(atIndex))
            : baseValue + sep + token;
    }

    /// <summary>
    /// The <see cref="GeneratedValueTokenKind.OnlyIfTaken"/> candidate for <paramref name="attemptIndex"/>
    /// (zero-based): the bare base value at attempt 0, then base + suffix for suffix =
    /// <paramref name="suffixStart"/>, <paramref name="suffixStart"/> + 1, ... at every attempt after. The
    /// caller is responsible for the <see cref="GenerationOutcomeKind.NoBaseValue"/> case (a null or
    /// whitespace-only <paramref name="baseValue"/>); this method assumes a usable base value has already been
    /// confirmed.
    /// </summary>
    public static string OnlyIfTakenCandidate(string baseValue, int attemptIndex, GeneratedValueSuffixStyle style, int suffixStart, string? separator)
    {
        if (attemptIndex == 0)
            return baseValue;

        var suffixValue = suffixStart + (attemptIndex - 1);
        var suffix = style == GeneratedValueSuffixStyle.Letter
            ? ToBijectiveBase26Letters(suffixValue)
            : suffixValue.ToString(CultureInfo.InvariantCulture);

        return Place(baseValue, suffix, separator);
    }

    /// <summary>
    /// Renders <paramref name="n"/> (1-based) in bijective base 26, lower case: 1 = "a", ..., 26 = "z",
    /// 27 = "aa", 28 = "ab", and so on without limit. Unlike ordinary base 26 this has no digit for zero, which
    /// is what lets it represent every positive integer with a strictly increasing string length and no gaps
    /// (ordinary base-26-with-a-zero-digit would render 26 as "a0", not "z"). <paramref name="n"/> must be at
    /// least 1.
    /// </summary>
    public static string ToBijectiveBase26Letters(int n)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), n, "Must be 1 or higher.");

        var chars = new Stack<char>();
        while (n > 0)
        {
            n--;
            chars.Push((char)('a' + n % 26));
            n /= 26;
        }

        return new string(chars.ToArray());
    }

    /// <summary>
    /// Renders a sequence <paramref name="number"/> per FR 25: zero-padded to <paramref name="fixedWidth"/>
    /// digits when set and the number still fits, or, when it no longer fits, either left unpadded
    /// (<see cref="GeneratedValueWidthOverflowBehaviour.AllowLonger"/>, in which case the returned
    /// <c>WidthExceeded</c> is false: the width became a minimum rather than a limit) or reported as exceeded
    /// (<see cref="GeneratedValueWidthOverflowBehaviour.StopAndReport"/>, the default). No padding is applied
    /// when <paramref name="fixedWidth"/> is null.
    /// </summary>
    public static (string Text, bool WidthExceeded) RenderSequenceNumber(long number, int? fixedWidth, GeneratedValueWidthOverflowBehaviour overflow)
    {
        var raw = number.ToString(CultureInfo.InvariantCulture);

        if (!fixedWidth.HasValue)
            return (raw, false);

        if (raw.Length <= fixedWidth.Value)
            return (raw.PadLeft(fixedWidth.Value, '0'), false);

        return overflow == GeneratedValueWidthOverflowBehaviour.AllowLonger
            ? (raw, false)
            : (raw, true);
    }

    /// <summary>
    /// Draws a fresh random token per FR 27, from <see cref="RandomNumberGenerator"/> only, never
    /// <see cref="Random"/>: a lower-case GUID (<see cref="GeneratedValueRandomFormat.Guid"/>, ignoring
    /// <paramref name="length"/>), a lower-case hexadecimal string of <paramref name="length"/> characters
    /// (<see cref="GeneratedValueRandomFormat.Hex"/>), or a decimal digit string of
    /// <paramref name="length"/> characters (<see cref="GeneratedValueRandomFormat.Digits"/>). When
    /// <paramref name="forceNonZeroLeadingDigit"/> is set (a Number or Long Number target: FR 27), the first
    /// digit of a Digits token is drawn from 1 to 9 so the rendered number does not lose a leading zero and so
    /// under-run the configured length.
    /// </summary>
    public static string GenerateRandomToken(GeneratedValueRandomFormat format, int? length, bool forceNonZeroLeadingDigit)
    {
        switch (format)
        {
            case GeneratedValueRandomFormat.Guid:
                return CreateCryptographicGuid().ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

            case GeneratedValueRandomFormat.Hex:
                return RandomHexString(length!.Value);

            case GeneratedValueRandomFormat.Digits:
                return RandomDigitString(length!.Value, forceNonZeroLeadingDigit);

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unrecognised random format.");
        }
    }

    /// <summary>
    /// A GUID built from 16 bytes drawn from <see cref="RandomNumberGenerator"/>, rather than
    /// <see cref="Guid.NewGuid"/>, so the random token's source is provably cryptographic rather than relying
    /// on the runtime's <see cref="Guid.NewGuid"/> implementation, which .NET does not itself contract as a
    /// CSPRNG.
    /// </summary>
    private static Guid CreateCryptographicGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }

    private static string RandomHexString(int length)
    {
        var bytes = new byte[(length + 1) / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }

    private static string RandomDigitString(int length, bool forceNonZeroLeadingDigit)
    {
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var minInclusive = i == 0 && forceNonZeroLeadingDigit ? 1 : 0;
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(minInclusive, 10)));
        }

        return builder.ToString();
    }
}

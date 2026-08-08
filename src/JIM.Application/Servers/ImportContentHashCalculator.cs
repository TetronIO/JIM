// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Application.Servers;

/// <summary>
/// Pure, stateless calculator (issue #1082) that computes a canonical content hash and a schema
/// fingerprint for a Connected System import object. No I/O; never calls a repository method.
/// <para>
/// <b>Admission-ticket semantics (SPEC-1082 D1):</b> the hash is ALWAYS computed from the incoming
/// import object, never from a Connected System Object's stored attribute values. This is
/// deliberate: comparing two independently computed "incoming hashes" (the one stamped on a
/// previous import, and the one computed for the current import) means storage-layer
/// serialisation quirks (Npgsql <see cref="DateTime"/> normalisation, decimal scale, resolved
/// reference DN case) can never cause a false "unchanged" result. Any divergence between the
/// incoming hash and reality can only widen towards a hash MISMATCH, which degrades to the
/// honest diff: wasted work, never a missed change. Do not add a code path that hashes a
/// <see cref="ConnectedSystemObject"/>'s stored values instead.
/// </para>
/// <para>
/// <b>Stamp-ordering invariant (D6):</b> a hash value only means something when it was stamped
/// AFTER the attribute-value writes it describes have committed. This class has no opinion on
/// stamping; it only computes values. See <c>ISyncRepository.StampImportStateAsync</c>.
/// </para>
/// <para>
/// <b>Known safe false-positive mismatches (D4, documented not fixed):</b> resolved reference DN
/// case changes hash as different (case-sensitive) even though the honest diff matches them
/// case-insensitively; text value case is case-sensitive in both, so it is consistent; decimal
/// scale is normalised in both, so it is also consistent. A mismatch that the honest diff then
/// finds to be a no-op simply re-stamps: one wasted diff, safe by construction.
/// </para>
/// </summary>
public static class ImportContentHashCalculator
{
    /// <summary>
    /// Input to the fingerprint (D5). Bumping this invalidates every stored hash lazily (no mass
    /// UPDATE): the next Full Import for each object recomputes a fingerprint that no longer
    /// matches the stored one, so the skip predicate falls through to the honest diff, which then
    /// re-stamps. Bump this if the canonical encoding in this class changes in any way that could
    /// change a hash for the same logical input (attribute ordering, value ordering, byte layout).
    /// </summary>
    public const int ImportContentHashAlgorithmVersion = 1;

    /// <summary>
    /// Decimal normaliser (SPEC-1082 D3): dividing by a same-valued literal with the maximum decimal
    /// scale (28 digits) forces .NET's decimal division to drop insignificant trailing zeros from
    /// the result's scale, without changing its numeric value. This mirrors the diff's own
    /// scale-insensitive <see cref="HashSet{T}"/>&lt;decimal&gt; comparison (:2070), so 5.0m and
    /// 5.00m hash identically.
    /// </summary>
    private const decimal DecimalScaleNormaliser = 1.000000000000000000000000000M;

    /// <summary>
    /// Computes the canonical content hash of an import object, for the schema described by
    /// <paramref name="objectType"/>. Deterministic: the same logical payload (regardless of value
    /// order or duplicate values within a multi-valued attribute) always yields the same hash.
    /// O(total values); does not allocate a buffer proportional to the object's total value count,
    /// streaming into an <see cref="IncrementalHash"/> instead (D13).
    /// </summary>
    public static Guid CalculateContentHash(ConnectedSystemImportObject importObject, ConnectedSystemObjectType objectType)
    {
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Consider each schema attribute whose name matches an import attribute (OrdinalIgnoreCase,
        // mirroring UpdateConnectedSystemObjectFromImportObject's matching), sorted by upper-invariant
        // name (ordinal) so hash computation does not depend on schema or import attribute order.
        var candidateAttributes = objectType.Attributes
            .Select(schemaAttribute => (
                SchemaAttribute: schemaAttribute,
                ImportAttribute: importObject.Attributes.FirstOrDefault(
                    a => a.Name.Equals(schemaAttribute.Name, StringComparison.OrdinalIgnoreCase))))
            .Where(pair => pair.ImportAttribute != null)
            .OrderBy(pair => pair.SchemaAttribute.Name.ToUpperInvariant(), StringComparer.Ordinal);

        foreach (var (schemaAttribute, importAttribute) in candidateAttributes)
        {
            var encodedValues = EncodeAttributeValues(schemaAttribute.Type, importAttribute!);

            // Attributes with zero values after dedupe are omitted entirely (D3): absent and
            // present-but-empty are semantically identical in the diff (:2300-2306), both deleting
            // all stored values, so they must hash identically too.
            if (encodedValues.Count == 0)
                continue;

            AppendLengthPrefixed(incrementalHash, Encoding.UTF8.GetBytes(schemaAttribute.Name.ToUpperInvariant()));
            incrementalHash.AppendData(new[] { (byte)schemaAttribute.Type });
            incrementalHash.AppendData(BitConverter.GetBytes(encodedValues.Count));

            foreach (var value in encodedValues)
                AppendLengthPrefixed(incrementalHash, value);
        }

        var digest = incrementalHash.GetHashAndReset();
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Computes the schema fingerprint for an object type (D5): a hash over the shape of every
    /// attribute (name, type, plurality, selection) plus <see cref="ImportContentHashAlgorithmVersion"/>.
    /// Intended to be computed once per object type per run and compared against the stored
    /// fingerprint at skip time; a mismatch disqualifies the skip (schema redefinition or algorithm
    /// bump), never triggers a mass invalidation write.
    /// </summary>
    public static Guid CalculateTypeFingerprint(ConnectedSystemObjectType objectType)
    {
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incrementalHash.AppendData(BitConverter.GetBytes(ImportContentHashAlgorithmVersion));

        var orderedAttributes = objectType.Attributes.OrderBy(a => a.Name.ToUpperInvariant(), StringComparer.Ordinal);
        foreach (var attribute in orderedAttributes)
        {
            AppendLengthPrefixed(incrementalHash, Encoding.UTF8.GetBytes(attribute.Name.ToUpperInvariant()));
            incrementalHash.AppendData(new[] { (byte)attribute.Type });
            incrementalHash.AppendData(new[] { (byte)attribute.AttributePlurality });
            incrementalHash.AppendData(new[] { attribute.Selected ? (byte)1 : (byte)0 });
        }

        var digest = incrementalHash.GetHashAndReset();
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Builds the deduplicated, sorted, binary-encoded value list for one attribute, reading the
    /// value list appropriate to <paramref name="schemaType"/> (mirroring the diff's own per-type
    /// value list selection). Dedupe comparers mirror the diff/dedup exactly, so pre-dedup and
    /// post-dedup forms of the same payload hash identically.
    /// </summary>
    private static List<byte[]> EncodeAttributeValues(AttributeDataType schemaType, ConnectedSystemImportObjectAttribute importAttribute)
    {
        switch (schemaType)
        {
            case AttributeDataType.Text:
                return importAttribute.StringValues
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .Select(v => Encoding.UTF8.GetBytes(v))
                    .ToList();

            case AttributeDataType.Reference:
                return importAttribute.ReferenceValues
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .Select(v => Encoding.UTF8.GetBytes(v))
                    .ToList();

            case AttributeDataType.Number:
                return importAttribute.IntValues
                    .Distinct()
                    .OrderBy(v => v)
                    .Select(BitConverter.GetBytes)
                    .ToList();

            case AttributeDataType.LongNumber:
                return importAttribute.LongValues
                    .Distinct()
                    .OrderBy(v => v)
                    .Select(BitConverter.GetBytes)
                    .ToList();

            case AttributeDataType.Decimal:
                return importAttribute.DecimalValues
                    .Select(v => v / DecimalScaleNormaliser)
                    .Distinct()
                    .OrderBy(v => v)
                    .Select(EncodeDecimal)
                    .ToList();

            case AttributeDataType.DateTime:
                // DateTime attributes are single-valued by nature (see ConnectedSystemImportObjectAttribute.DateTimeValue).
                // Ticks-only encoding mirrors DateTime == semantics, which ignores Kind.
                return importAttribute.DateTimeValue.HasValue
                    ? new List<byte[]> { BitConverter.GetBytes(importAttribute.DateTimeValue.Value.Ticks) }
                    : new List<byte[]>();

            case AttributeDataType.Binary:
                return importAttribute.ByteValues
                    .Distinct(ByteArrayEqualityComparer.Instance)
                    .OrderBy(v => v, ByteArrayLexicographicComparer.Instance)
                    .ToList();

            case AttributeDataType.Guid:
                return importAttribute.GuidValues
                    .Distinct()
                    .OrderBy(v => v)
                    .Select(v => v.ToByteArray())
                    .ToList();

            case AttributeDataType.Boolean:
                // Boolean attributes are single-valued by nature (see ConnectedSystemImportObjectAttribute.BoolValue).
                return importAttribute.BoolValue.HasValue
                    ? new List<byte[]> { new[] { importAttribute.BoolValue.Value ? (byte)1 : (byte)0 } }
                    : new List<byte[]>();

            case AttributeDataType.NotSet:
            default:
                throw new ArgumentOutOfRangeException(nameof(schemaType), schemaType, "Unsupported attribute data type for content hash calculation.");
        }
    }

    private static byte[] EncodeDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var bytes = new byte[16];
        for (var i = 0; i < 4; i++)
            BitConverter.GetBytes(bits[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    /// <summary>
    /// Writes a 4-byte little-endian length prefix followed by the value's bytes. Length prefixes
    /// make the overall encoding injective (D3): without them, ["ab","c"] and ["a","bc"] would
    /// concatenate to the same byte sequence.
    /// </summary>
    private static void AppendLengthPrefixed(IncrementalHash incrementalHash, byte[] value)
    {
        incrementalHash.AppendData(BitConverter.GetBytes(value.Length));
        incrementalHash.AppendData(value);
    }

    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => (x, y) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => x.AsSpan().SequenceEqual(y)
        };

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }

    private sealed class ByteArrayLexicographicComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayLexicographicComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return x.AsSpan().SequenceCompareTo(y);
        }
    }
}

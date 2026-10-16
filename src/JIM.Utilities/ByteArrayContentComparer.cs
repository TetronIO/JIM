// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// Compares byte arrays by their content rather than by reference, so a set or dictionary of Binary attribute values
/// treats two arrays holding the same bytes as one value.
/// </summary>
public sealed class ByteArrayContentComparer : IEqualityComparer<byte[]>
{
    /// <summary>The shared instance; the comparer holds no state.</summary>
    public static ByteArrayContentComparer Instance { get; } = new();

    private ByteArrayContentComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(byte[]? x, byte[]? y) => (x, y) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        _ => x.AsSpan().SequenceEqual(y)
    };

    /// <inheritdoc />
    public int GetHashCode(byte[] obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}

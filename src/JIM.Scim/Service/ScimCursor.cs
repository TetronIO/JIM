// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// The cursors of RFC 9865: the id of the last resource a page held, as base64url (unreserved URL characters only,
/// 22 of them). A cursor is a position in id order, not a page number, so deletions during a walk cannot shift it.
/// It carries nothing a client could not already see, so it needs no signing; one that does not decode is refused.
/// </summary>
public static class ScimCursor
{
    private const int EncodedLength = 22;

    public static string Encode(Guid lastId) =>
        Convert.ToBase64String(lastId.ToByteArray(bigEndian: true)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string cursor, out Guid lastId)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        lastId = Guid.Empty;
        if (cursor.Length != EncodedLength || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            return false;

        var bytes = new byte[16];
        if (!Convert.TryFromBase64String(cursor.Replace('-', '+').Replace('_', '/') + "==", bytes, out var written) || written != 16)
            return false;

        lastId = new Guid(bytes, bigEndian: true);
        return true;
    }
}

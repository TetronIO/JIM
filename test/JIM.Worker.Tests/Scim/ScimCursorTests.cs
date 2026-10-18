// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Service;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// RFC 9865 cursors: the last id a page held, written in unreserved URL characters, and read back exactly.
/// </summary>
public class ScimCursorTests
{
    [Test]
    public void Encode_ThenDecode_IsTheSameId([Range(0, 49)] int seed)
    {
        var id = new Guid(Enumerable.Range(0, 16).Select(i => (byte)((seed * 31 + i * 17) % 256)).ToArray());

        var cursor = ScimCursor.Encode(id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cursor, Does.Match("^[A-Za-z0-9_-]{22}$"));
            Assert.That(ScimCursor.TryDecode(cursor, out var decoded), Is.True);
            Assert.That(decoded, Is.EqualTo(id));
        }
    }

    [TestCase("")]
    [TestCase("AAAA")]
    [TestCase("not a cursor at all!!!")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAA=")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAA")]
    public void TryDecode_SomethingNotACursor_IsFalse(string cursor)
    {
        Assert.That(ScimCursor.TryDecode(cursor, out _), Is.False);
    }
}

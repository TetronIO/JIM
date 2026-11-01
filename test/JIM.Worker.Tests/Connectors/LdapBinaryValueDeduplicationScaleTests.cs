// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Scale guard for issue #2672: the LDAP Connector de-duplicates each entry's Binary attribute values as it reads them,
/// by content, and that must stay linear in the number of values however alike they are.
/// </summary>
[TestFixture]
public class LdapBinaryValueDeduplicationScaleTests
{
    /// <summary>
    /// An entry holding 50,000 Security Identifiers on one Binary attribute, 1,000 of them repeats of an earlier value,
    /// each in an array of its own as the directory hands it over. Every value is the same length and begins and ends with the same
    /// bytes (a Security Identifier opens with its revision and its last byte is almost always zero), which is all the
    /// de-duplication's private comparer hashed, so every value landed in one bucket and was compared with every value
    /// before it. Measured when #2672 was fixed, this took about 13 s before the fix and about 15 ms after; the bound sits
    /// well clear of both, so a return of the colliding hash fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void GetAttributeBinaryValues_LargeAttributeOfValuesSharingFirstAndLastBytes_RemovesDuplicatesWithinBound()
    {
        const int distinctCount = 49_000;
        const int duplicateCount = 1_000;

        var values = new List<byte[]>(distinctCount + duplicateCount);
        for (var i = 0; i < distinctCount; i++)
            values.Add(SecurityIdentifier(i));
        for (var i = 0; i < duplicateCount; i++)
            values.Add(SecurityIdentifier(i * 7));

        var attribute = new DirectoryAttribute("sIDHistory", values.Cast<object>().ToArray());

        var stopwatch = Stopwatch.StartNew();
        var unique = LdapConnectorUtilities.GetAttributeBinaryValues(attribute, "sIDHistory", "CN=Large,OU=Users,DC=panoply,DC=local");
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unique, Is.EqualTo(values.Take(distinctCount)), "each value is kept once, where it first appeared");
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
                $"De-duplicating {values.Count:N0} Binary values that share their length and first and last bytes took " +
                $"{stopwatch.Elapsed.TotalSeconds:N1}s; each value must be looked up by a hash of its whole content, not compared with every other.");
        }
    }

    /// <summary>
    /// A Security Identifier of its own for each <paramref name="relativeId"/>, in one domain, in a new array each time
    /// it is asked for: 28 bytes, opening with revision 1, ending with the relative identifier little-endian, whose top
    /// byte is zero for any identifier below 16,777,216.
    /// </summary>
    private static byte[] SecurityIdentifier(int relativeId)
    {
        var value = new byte[28];
        value[0] = 1;
        value[1] = 5;
        value[7] = 5;
        BitConverter.GetBytes(21).CopyTo(value, 8);
        BitConverter.GetBytes(1_234_567_890).CopyTo(value, 12);
        BitConverter.GetBytes(987_654_321).CopyTo(value, 16);
        BitConverter.GetBytes(555_555_555).CopyTo(value, 20);
        BitConverter.GetBytes(1_000 + relativeId).CopyTo(value, 24);
        return value;
    }
}

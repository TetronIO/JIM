using System;
using System.Linq;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

[TestFixture]
public class IdentifierParserTests
{
    // Well-known test GUID for consistent testing
    // String: 550e8400-e29b-41d4-a716-446655440000
    private static readonly Guid TestGuid = new("550e8400-e29b-41d4-a716-446655440000");

    // Microsoft byte order (little-endian first 3 components) - what .NET Guid.ToByteArray() produces
    private static readonly byte[] TestGuidMicrosoftBytes =
    [
        0x00, 0x84, 0x0e, 0x55, // time_low (reversed)
        0x9b, 0xe2,             // time_mid (reversed)
        0xd4, 0x41,             // time_hi_version (reversed)
        0xa7, 0x16,             // clock_seq
        0x44, 0x66, 0x55, 0x44, 0x00, 0x00 // node
    ];

    // RFC 4122 byte order (big-endian first 3 components) - network byte order
    private static readonly byte[] TestGuidRfc4122Bytes =
    [
        0x55, 0x0e, 0x84, 0x00, // time_low (big-endian)
        0xe2, 0x9b,             // time_mid (big-endian)
        0x41, 0xd4,             // time_hi_version (big-endian)
        0xa7, 0x16,             // clock_seq (same in both formats)
        0x44, 0x66, 0x55, 0x44, 0x00, 0x00 // node (same in both formats)
    ];

    #region FromString Tests

    [Test]
    public void FromString_StandardFormat_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("550e8400-e29b-41d4-a716-446655440000");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_BracedFormat_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("{550e8400-e29b-41d4-a716-446655440000}");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_NoHyphensFormat_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("550e8400e29b41d4a716446655440000");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_UrnFormat_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("urn:uuid:550e8400-e29b-41d4-a716-446655440000");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_UppercaseUrnFormat_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("URN:UUID:550E8400-E29B-41D4-A716-446655440000");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_MixedCaseFormat_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("550E8400-e29b-41D4-a716-446655440000");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_WithLeadingTrailingWhitespace_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromString("  550e8400-e29b-41d4-a716-446655440000  ");
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromString_NullValue_ThrowsArgumentNullExceptionAsync()
    {
        Assert.Throws<ArgumentNullException>(() => IdentifierParser.FromString(null!));
    }

    [Test]
    public void FromString_EmptyString_ThrowsArgumentExceptionAsync()
    {
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromString(string.Empty));
    }

    [Test]
    public void FromString_InvalidString_ThrowsArgumentExceptionAsync()
    {
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromString("not-a-guid"));
    }

    [Test]
    public void FromString_TooShort_ThrowsArgumentExceptionAsync()
    {
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromString("550e8400-e29b-41d4"));
    }

    #endregion

    #region TryFromString Tests

    [Test]
    public void TryFromString_ValidGuid_ReturnsTrueAndCorrectGuidAsync()
    {
        var success = IdentifierParser.TryFromString("550e8400-e29b-41d4-a716-446655440000", out var result);
        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void TryFromString_UrnFormat_ReturnsTrueAndCorrectGuidAsync()
    {
        var success = IdentifierParser.TryFromString("urn:uuid:550e8400-e29b-41d4-a716-446655440000", out var result);
        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void TryFromString_NullValue_ReturnsFalseAsync()
    {
        var success = IdentifierParser.TryFromString(null, out var result);
        Assert.That(success, Is.False);
        Assert.That(result, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void TryFromString_EmptyString_ReturnsFalseAsync()
    {
        var success = IdentifierParser.TryFromString(string.Empty, out var result);
        Assert.That(success, Is.False);
        Assert.That(result, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void TryFromString_WhitespaceOnly_ReturnsFalseAsync()
    {
        var success = IdentifierParser.TryFromString("   ", out var result);
        Assert.That(success, Is.False);
        Assert.That(result, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void TryFromString_InvalidString_ReturnsFalseAsync()
    {
        var success = IdentifierParser.TryFromString("not-a-guid", out var result);
        Assert.That(success, Is.False);
        Assert.That(result, Is.EqualTo(Guid.Empty));
    }

    #endregion

    #region FromMicrosoftBytes Tests

    [Test]
    public void FromMicrosoftBytes_ValidBytes_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromMicrosoftBytes(TestGuidMicrosoftBytes);
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromMicrosoftBytes_NullBytes_ThrowsArgumentNullExceptionAsync()
    {
        Assert.Throws<ArgumentNullException>(() => IdentifierParser.FromMicrosoftBytes(null!));
    }

    [Test]
    public void FromMicrosoftBytes_TooShort_ThrowsArgumentExceptionAsync()
    {
        var shortBytes = new byte[15];
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromMicrosoftBytes(shortBytes));
    }

    [Test]
    public void FromMicrosoftBytes_TooLong_ThrowsArgumentExceptionAsync()
    {
        var longBytes = new byte[17];
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromMicrosoftBytes(longBytes));
    }

    [Test]
    public void FromMicrosoftBytes_EmptyArray_ThrowsArgumentExceptionAsync()
    {
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromMicrosoftBytes(Array.Empty<byte>()));
    }

    #endregion

    #region FromRfc4122Bytes Tests

    [Test]
    public void FromRfc4122Bytes_ValidBytes_ReturnsCorrectGuidAsync()
    {
        var result = IdentifierParser.FromRfc4122Bytes(TestGuidRfc4122Bytes);
        Assert.That(result, Is.EqualTo(TestGuid));
    }

    [Test]
    public void FromRfc4122Bytes_NullBytes_ThrowsArgumentNullExceptionAsync()
    {
        Assert.Throws<ArgumentNullException>(() => IdentifierParser.FromRfc4122Bytes(null!));
    }

    [Test]
    public void FromRfc4122Bytes_TooShort_ThrowsArgumentExceptionAsync()
    {
        var shortBytes = new byte[15];
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromRfc4122Bytes(shortBytes));
    }

    [Test]
    public void FromRfc4122Bytes_TooLong_ThrowsArgumentExceptionAsync()
    {
        var longBytes = new byte[17];
        Assert.Throws<ArgumentException>(() => IdentifierParser.FromRfc4122Bytes(longBytes));
    }

    #endregion

    #region ToRfc4122Bytes Tests

    [Test]
    public void ToRfc4122Bytes_ReturnsCorrectBytesAsync()
    {
        var result = IdentifierParser.ToRfc4122Bytes(TestGuid);
        Assert.That(result, Is.EqualTo(TestGuidRfc4122Bytes));
    }

    [Test]
    public void ToRfc4122Bytes_EmptyGuid_ReturnsAllZerosAsync()
    {
        var result = IdentifierParser.ToRfc4122Bytes(Guid.Empty);
        Assert.That(result, Is.EqualTo(new byte[16]));
    }

    #endregion

    #region ToMicrosoftBytes Tests

    [Test]
    public void ToMicrosoftBytes_ReturnsCorrectBytesAsync()
    {
        var result = IdentifierParser.ToMicrosoftBytes(TestGuid);
        Assert.That(result, Is.EqualTo(TestGuidMicrosoftBytes));
    }

    [Test]
    public void ToMicrosoftBytes_EquivalentToGuidToByteArrayAsync()
    {
        var result = IdentifierParser.ToMicrosoftBytes(TestGuid);
        var expected = TestGuid.ToByteArray();
        Assert.That(result, Is.EqualTo(expected));
    }

    #endregion

    #region Round-Trip Tests

    [Test]
    public void RoundTrip_MicrosoftBytes_PreservesBytesAsync()
    {
        var guid = IdentifierParser.FromMicrosoftBytes(TestGuidMicrosoftBytes);
        var result = IdentifierParser.ToMicrosoftBytes(guid);
        Assert.That(result, Is.EqualTo(TestGuidMicrosoftBytes));
    }

    [Test]
    public void RoundTrip_Rfc4122Bytes_PreservesBytesAsync()
    {
        var guid = IdentifierParser.FromRfc4122Bytes(TestGuidRfc4122Bytes);
        var result = IdentifierParser.ToRfc4122Bytes(guid);
        Assert.That(result, Is.EqualTo(TestGuidRfc4122Bytes));
    }

    [Test]
    public void RoundTrip_StringFormats_ProduceSameGuidAsync()
    {
        var formats = new[]
        {
            "550e8400-e29b-41d4-a716-446655440000",
            "{550e8400-e29b-41d4-a716-446655440000}",
            "550e8400e29b41d4a716446655440000",
            "urn:uuid:550e8400-e29b-41d4-a716-446655440000"
        };

        var guids = formats.Select(IdentifierParser.FromString).ToList();
        Assert.That(guids.Distinct().Count(), Is.EqualTo(1));
        Assert.That(guids[0], Is.EqualTo(TestGuid));
    }

    [Test]
    public void CrossFormat_MicrosoftAndRfc4122Bytes_ProduceSameGuidAsync()
    {
        var fromMicrosoft = IdentifierParser.FromMicrosoftBytes(TestGuidMicrosoftBytes);
        var fromRfc4122 = IdentifierParser.FromRfc4122Bytes(TestGuidRfc4122Bytes);
        Assert.That(fromRfc4122, Is.EqualTo(fromMicrosoft));
    }

    [Test]
    public void CrossFormat_StringAndMicrosoftBytes_ProduceSameGuidAsync()
    {
        var fromString = IdentifierParser.FromString("550e8400-e29b-41d4-a716-446655440000");
        var fromBytes = IdentifierParser.FromMicrosoftBytes(TestGuidMicrosoftBytes);
        Assert.That(fromBytes, Is.EqualTo(fromString));
    }

    #endregion

    #region ToAdLdapFilterString Tests

    [Test]
    public void ToAdLdapFilterString_ReturnsCorrectFormatAsync()
    {
        var result = IdentifierParser.ToAdLdapFilterString(TestGuid);

        // Should be Microsoft byte order escaped as \xx\xx...
        Assert.That(result, Does.StartWith("\\"));
        Assert.That(result.Length, Is.EqualTo(48)); // 16 bytes * 3 chars per byte (\xx)

        // Verify it matches Microsoft byte order
        var expectedParts = TestGuidMicrosoftBytes.Select(b => $"\\{b:x2}");
        var expected = string.Concat(expectedParts);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ToAdLdapFilterString_EmptyGuid_ReturnsAllZeroEscapedAsync()
    {
        var result = IdentifierParser.ToAdLdapFilterString(Guid.Empty);
        Assert.That(result, Is.EqualTo("\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00\\00"));
    }

    #endregion

    #region Normalise Tests

    [Test]
    public void Normalise_StandardFormat_ReturnsCanonicalFormatAsync()
    {
        var result = IdentifierParser.Normalise("550e8400-e29b-41d4-a716-446655440000");
        Assert.That(result, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void Normalise_UppercaseFormat_ReturnsLowercaseAsync()
    {
        var result = IdentifierParser.Normalise("550E8400-E29B-41D4-A716-446655440000");
        Assert.That(result, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void Normalise_BracedFormat_RemovesBracesAsync()
    {
        var result = IdentifierParser.Normalise("{550E8400-E29B-41D4-A716-446655440000}");
        Assert.That(result, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void Normalise_NoHyphensFormat_AddsHyphensAsync()
    {
        var result = IdentifierParser.Normalise("550E8400E29B41D4A716446655440000");
        Assert.That(result, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void Normalise_UrnFormat_RemovesUrnPrefixAsync()
    {
        var result = IdentifierParser.Normalise("urn:uuid:550E8400-E29B-41D4-A716-446655440000");
        Assert.That(result, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void Normalise_AllFormats_ProduceSameResultAsync()
    {
        var formats = new[]
        {
            "550e8400-e29b-41d4-a716-446655440000",
            "550E8400-E29B-41D4-A716-446655440000",
            "{550e8400-e29b-41d4-a716-446655440000}",
            "550e8400e29b41d4a716446655440000",
            "urn:uuid:550e8400-e29b-41d4-a716-446655440000"
        };

        var results = formats.Select(IdentifierParser.Normalise).ToList();
        Assert.That(results.Distinct().Count(), Is.EqualTo(1));
        Assert.That(results[0], Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void Normalise_NullValue_ThrowsArgumentNullExceptionAsync()
    {
        Assert.Throws<ArgumentNullException>(() => IdentifierParser.Normalise(null!));
    }

    [Test]
    public void Normalise_InvalidString_ThrowsArgumentExceptionAsync()
    {
        Assert.Throws<ArgumentException>(() => IdentifierParser.Normalise("not-a-guid"));
    }

    #endregion

    #region TryNormalise Tests

    [Test]
    public void TryNormalise_ValidGuid_ReturnsTrueAndNormalisedStringAsync()
    {
        var success = IdentifierParser.TryNormalise("550E8400-E29B-41D4-A716-446655440000", out var result);
        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Test]
    public void TryNormalise_NullValue_ReturnsFalseAsync()
    {
        var success = IdentifierParser.TryNormalise(null, out var result);
        Assert.That(success, Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryNormalise_InvalidString_ReturnsFalseAsync()
    {
        var success = IdentifierParser.TryNormalise("not-a-guid", out var result);
        Assert.That(success, Is.False);
        Assert.That(result, Is.Null);
    }

    #endregion

    #region Real-World Scenario Tests

    [Test]
    public void Scenario_ActiveDirectoryObjectGuidRoundTripAsync()
    {
        // Simulate importing objectGUID from Active Directory (Microsoft byte order)
        // and then exporting it back to AD
        var adBytes = TestGuidMicrosoftBytes;
        var guid = IdentifierParser.FromMicrosoftBytes(adBytes);
        var exportBytes = IdentifierParser.ToMicrosoftBytes(guid);

        Assert.That(exportBytes, Is.EqualTo(adBytes));
    }

    [Test]
    public void Scenario_CsvImportExportRoundTripAsync()
    {
        // Simulate importing a GUID string from CSV and exporting it back
        var csvValue = "550e8400-e29b-41d4-a716-446655440000";
        var guid = IdentifierParser.FromString(csvValue);
        var exportValue = guid.ToString("D").ToLowerInvariant();

        Assert.That(exportValue, Is.EqualTo(csvValue));
    }

    [Test]
    public void Scenario_CrossPlatformByteOrderConversionAsync()
    {
        // Simulate receiving UUID bytes from a PostgreSQL database (RFC 4122)
        // and converting for export to Active Directory (Microsoft)
        var postgresBytes = TestGuidRfc4122Bytes;
        var guid = IdentifierParser.FromRfc4122Bytes(postgresBytes);
        var adBytes = IdentifierParser.ToMicrosoftBytes(guid);

        Assert.That(adBytes, Is.EqualTo(TestGuidMicrosoftBytes));

        // And the reverse: AD to PostgreSQL
        var guid2 = IdentifierParser.FromMicrosoftBytes(adBytes);
        var postgresBytes2 = IdentifierParser.ToRfc4122Bytes(guid2);

        Assert.That(postgresBytes2, Is.EqualTo(postgresBytes));
    }

    [Test]
    public void Scenario_RandomGuidByteOrderConsistencyAsync()
    {
        // Verify that a randomly generated GUID maintains consistency
        // when converted between Microsoft and RFC 4122 byte orders
        var randomGuid = Guid.NewGuid();

        var msBytes = IdentifierParser.ToMicrosoftBytes(randomGuid);
        var rfc4122Bytes = IdentifierParser.ToRfc4122Bytes(randomGuid);

        // Convert back and verify we get the same GUID
        var fromMs = IdentifierParser.FromMicrosoftBytes(msBytes);
        var fromRfc = IdentifierParser.FromRfc4122Bytes(rfc4122Bytes);

        Assert.That(fromMs, Is.EqualTo(randomGuid));
        Assert.That(fromRfc, Is.EqualTo(randomGuid));
        Assert.That(fromMs, Is.EqualTo(fromRfc));
    }

    #endregion
}

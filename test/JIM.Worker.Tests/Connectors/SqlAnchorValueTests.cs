// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the conversion of an anchor between what a database driver hands over and the string form JIM
/// carries it as: an external ID, a Connected System Pagination Token, or a Delta Import watermark. A
/// lossy conversion here silently skips or repeats rows between pages, and a conversion that differs by
/// direction leaves an exported object that no import can find, which is the worst kind of defect: it
/// looks like a successful run.
/// </summary>
/// <remarks>
/// The real providers are used rather than a stand-in, because the one dialect-specific thing in here is
/// GUID byte order and a stand-in that passed bytes through unchanged would prove nothing about it.
/// </remarks>
[TestFixture]
public class SqlAnchorValueTests
{
    private SqlServerProvider _sqlServer = null!;
    private OracleProvider _oracle = null!;

    [SetUp]
    public void SetUp()
    {
        _sqlServer = new SqlServerProvider();
        _oracle = new OracleProvider();
    }

    #region Decimal

    [Test]
    public void ToTokenString_DecimalWithTrailingZeros_ProducesTheCanonicalForm()
    {
        var token = SqlAnchorValue.ToTokenString(_sqlServer, 5.00m, AttributeDataType.Decimal);

        Assert.That(token, Is.EqualTo(DecimalAttributeValue.ToCanonicalString(5.00m)),
            "Numerically equal decimals must produce identical tokens, or a resumed page compares against a different string for the same value.");
    }

    [TestCase("0.5")]
    [TestCase("-12345.6789")]
    [TestCase("79228162514264337593543950335")]
    [TestCase("0.0000001")]
    public void RoundTrip_Decimal_PreservesTheValueExactly(string literal)
    {
        var original = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        var token = SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.Decimal);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.Decimal, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True, $"'{literal}' is a valid decimal anchor and must parse back.");
            Assert.That(value, Is.EqualTo(original), "Routing a decimal through double would lose digits that a 38-digit Oracle NUMBER key relies on.");
            Assert.That(token, Does.Not.Contain("E").IgnoreCase, "Exponent notation would not compare as the database does.");
        }
    }

    [Test]
    [SetCulture("de-DE")]
    public void ToTokenString_DecimalUnderACommaDecimalCulture_StillUsesTheInvariantForm()
    {
        var token = SqlAnchorValue.ToTokenString(_sqlServer, 1.5m, AttributeDataType.Decimal);

        Assert.That(token, Is.EqualTo("1.5"),
            "A culture-sensitive format would write '1,5' and make the persisted watermark unreadable on a differently configured host.");
    }

    [Test]
    [SetCulture("de-DE")]
    public void TryFromTokenString_DecimalUnderACommaDecimalCulture_StillParsesTheInvariantForm()
    {
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, "1.5", AttributeDataType.Decimal, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(1.5m));
        }
    }

    [Test]
    public void TryFromTokenString_DecimalOverflow_ReturnsFalseRatherThanRounding()
    {
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, "792281625142643375935439503350", AttributeDataType.Decimal, out _);

        Assert.That(parsed, Is.False, "A value outside decimal's range must surface as an error, never as a silently rounded anchor.");
    }

    #endregion

    #region Other anchor types

    [Test]
    public void RoundTrip_Number_PreservesTheValue()
    {
        var token = SqlAnchorValue.ToTokenString(_sqlServer, 4711, AttributeDataType.Number);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.Number, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(4711));
        }
    }

    [Test]
    public void RoundTrip_LongNumber_PreservesTheValue()
    {
        var token = SqlAnchorValue.ToTokenString(_sqlServer, long.MaxValue, AttributeDataType.LongNumber);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.LongNumber, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(long.MaxValue));
        }
    }

    [Test]
    public void RoundTrip_Text_PreservesTheValue()
    {
        var token = SqlAnchorValue.ToTokenString(_sqlServer, "EMP-0042", AttributeDataType.Text);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.Text, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo("EMP-0042"));
        }
    }

    [Test]
    public void RoundTrip_Guid_PreservesTheValue()
    {
        var original = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        var token = SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.Guid);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.Guid, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(original));
            Assert.That(token, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"), "The canonical hyphenated form is what every provider round-trips predictably.");
        }
    }

    [Test]
    public void RoundTrip_DateTime_PreservesTheValueAsUtc()
    {
        var original = new DateTime(2026, 7, 14, 22, 0, 0, 123, DateTimeKind.Utc);

        var token = SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.DateTime);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.DateTime, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(original));
            Assert.That(((DateTime)value!).Kind, Is.EqualTo(DateTimeKind.Utc), "JIM stores every DateTime in UTC; an unspecified kind would shift the watermark by the host's offset.");
        }
    }

    [Test]
    public void RoundTrip_Binary_PreservesTheBytes()
    {
        var original = new byte[] { 0x00, 0x0F, 0xA1, 0xFF };

        var token = SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.Binary);
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.Binary, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(original));
        }
    }

    #endregion

    #region GUID byte order

    [Test]
    public void ToTokenString_AnOracleRaw16Value_ReadsTheBytesInTheDialectsOwnOrder()
    {
        // What an Oracle RAW(16) primary key defaulted from SYS_GUID() hands back: bytes, never a Guid.
        // Rendering them without the dialect's conversion is what made such a table unimportable.
        var original = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var raw16 = IdentifierParser.ToRfc4122Bytes(original);

        var token = SqlAnchorValue.ToTokenString(_oracle, raw16, AttributeDataType.Guid);

        Assert.That(token, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"),
            "Oracle stores a GUID big-endian; reading it any other way transposes the first three components without any error.");
    }

    [Test]
    public void ToTokenString_TheSameGuidInEitherDialectsBytes_ComposesTheSameToken()
    {
        var original = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SqlAnchorValue.ToTokenString(_oracle, IdentifierParser.ToRfc4122Bytes(original), AttributeDataType.Guid),
                Is.EqualTo(SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.Guid)),
                "An anchor token identifies the object, so the same identifier must produce the same string whichever dialect it came out of.");

            Assert.That(SqlAnchorValue.ToTokenString(_sqlServer, IdentifierParser.ToMicrosoftBytes(original), AttributeDataType.Guid),
                Is.EqualTo(SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.Guid)));
        }
    }

    [Test]
    public void ToTokenString_AGuidValueAgainstSqlServer_ComposesTheHyphenatedFormItAlwaysDid()
    {
        // The regression guard for the dialect this Connector already worked against: SqlClient hands
        // back a Guid for a uniqueidentifier, and the token it composes must not have moved.
        var original = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.That(SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.Guid), Is.EqualTo("11111111-2222-3333-4444-555555555555"));
    }

    [Test]
    public void RoundTrip_GuidThroughTheOracleDialect_ReturnsTheBytesThatDialectBinds()
    {
        var original = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        var token = SqlAnchorValue.ToTokenString(_oracle, IdentifierParser.ToRfc4122Bytes(original), AttributeDataType.Guid);
        var parsed = SqlAnchorValue.TryFromTokenString(_oracle, token, AttributeDataType.Guid, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(IdentifierParser.ToRfc4122Bytes(original)),
                "A token is read back to be bound, so it has to come back in the shape the driver takes, not the shape JIM holds.");
        }
    }

    #endregion

    #region Dates

    [Test]
    public void ToTokenString_AValueCarryingItsOwnOffset_NormalisesItToTheInstantItNames()
    {
        // What a driver returns for a datetimeoffset or a TIMESTAMP WITH TIME ZONE column. Convert.
        // ToDateTime throws for it (DateTimeOffset does not implement IConvertible), which made such a
        // column unusable as a watermark as well as as an anchor.
        var original = new DateTimeOffset(2026, 7, 14, 23, 0, 0, TimeSpan.FromHours(1));

        var token = SqlAnchorValue.ToTokenString(_sqlServer, original, AttributeDataType.DateTime);

        Assert.That(token, Is.EqualTo(SqlAnchorValue.ToTokenString(_sqlServer, original.UtcDateTime, AttributeDataType.DateTime)),
            "The same instant must produce the same string whether it arrived with an offset or without one.");
    }

    [Test]
    public void ToTokenString_TheSameInstantWithDifferentOffsets_ComposesTheSameToken()
    {
        var here = new DateTimeOffset(2026, 7, 14, 23, 0, 0, TimeSpan.FromHours(1));
        var there = here.ToOffset(TimeSpan.FromHours(-5));

        Assert.That(SqlAnchorValue.ToTokenString(_sqlServer, there, AttributeDataType.DateTime),
            Is.EqualTo(SqlAnchorValue.ToTokenString(_sqlServer, here, AttributeDataType.DateTime)),
            "A server that reports the same instant in a different offset must not look like a different row.");
    }

    #endregion

    #region Rejections

    [Test]
    public void ToTokenString_BooleanAnchor_Throws()
    {
        Assert.Throws<NotSupportedException>(() => SqlAnchorValue.ToTokenString(_sqlServer, true, AttributeDataType.Boolean),
            "A two-state column cannot order a keyset page, so accepting it would produce an import that never terminates.");
    }

    [Test]
    public void ToTokenString_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SqlAnchorValue.ToTokenString(_sqlServer, null!, AttributeDataType.Text),
            "A null anchor means the row cannot be positioned, which is a configuration error rather than a page boundary.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-number")]
    public void TryFromTokenString_UnusableToken_ReturnsFalse(string? token)
    {
        var parsed = SqlAnchorValue.TryFromTokenString(_sqlServer, token, AttributeDataType.Number, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(value, Is.Null, "A failed parse must not leave a partially converted anchor behind.");
        }
    }

    #endregion
}

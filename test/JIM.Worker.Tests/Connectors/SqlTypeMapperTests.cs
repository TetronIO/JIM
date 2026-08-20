// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the SQL-type-to-JIM-type mapping table that the SQL Database Connector's schema discovery
/// depends on. Every row of the table in the PRD is asserted for both Priority 1 providers, because a
/// silently wrong mapping corrupts values on import long before anyone notices.
/// </summary>
[TestFixture]
public class SqlTypeMapperTests
{
    #region Text

    [TestCase("varchar")]
    [TestCase("nvarchar")]
    [TestCase("char")]
    [TestCase("nchar")]
    [TestCase("text")]
    [TestCase("ntext")]
    public void Map_SqlServerCharacterType_ReturnsText(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Text), $"'{typeName}' holds character data, which JIM represents as Text.");
    }

    [TestCase("VARCHAR2")]
    [TestCase("NVARCHAR2")]
    [TestCase("CHAR")]
    [TestCase("NCHAR")]
    [TestCase("CLOB")]
    [TestCase("NCLOB")]
    [TestCase("LONG")]
    public void Map_OracleCharacterType_ReturnsText(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Text), $"'{typeName}' holds character data, which JIM represents as Text.");
    }

    [Test]
    public void Map_TypeNameCarryingASizeSuffix_IgnoresTheSuffix()
    {
        // Oracle's catalogue reports sizes inside the type name for some types; the family is what matters.
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("VARCHAR2(50)"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Text), "The declared size does not change which JIM type a column family maps to.");
    }

    #endregion

    #region Number and LongNumber

    [TestCase("int")]
    [TestCase("integer")]
    [TestCase("smallint")]
    [TestCase("tinyint")]
    public void Map_SqlServerIntegerType_ReturnsNumber(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Number), $"'{typeName}' fits inside a 32-bit integer, which is JIM's Number.");
    }

    [TestCase("INT")]
    [TestCase("INTEGER")]
    [TestCase("SMALLINT")]
    public void Map_OracleIntegerType_ReturnsNumber(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Number), $"'{typeName}' fits inside a 32-bit integer, which is JIM's Number.");
    }

    [Test]
    public void Map_SqlServerBigInt_ReturnsLongNumber()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType("bigint"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.LongNumber), "A 64-bit integer overflows JIM's Number, so it must map to LongNumber.");
    }

    [Test]
    public void Map_OracleBigInt_ReturnsLongNumber()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("BIGINT"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.LongNumber), "A 64-bit integer overflows JIM's Number, so it must map to LongNumber.");
    }

    #endregion

    #region Boolean

    [Test]
    public void Map_SqlServerBit_ReturnsBoolean()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType("bit"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean), "'bit' is SQL Server's Boolean type.");
    }

    [Test]
    public void Map_OracleBoolean_ReturnsBoolean()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("BOOLEAN"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean), "Oracle Database 23ai carries a native BOOLEAN type.");
    }

    [Test]
    public void Map_OracleNumberOnePrecisionWithoutTheOptIn_ReturnsNumber()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 1, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Number), "NUMBER(1) is a number until an administrator declares it a flag; inferring Boolean would silently reinterpret real numeric data. One digit with no scale is a whole number, so Number is the narrowest type that holds it.");
    }

    [Test]
    public void Map_OracleNumberOnePrecisionWithTheOptIn_ReturnsBoolean()
    {
        var options = new SqlTypeMappingOptions { TreatSingleDigitNumberAsBoolean = true };

        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 1, Scale: 0), options);

        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean), "The opt-in exists so an estate that stores flags as NUMBER(1) can say so.");
    }

    [Test]
    public void Map_OracleNumberWiderThanOneDigitWithTheOptIn_ReturnsNumber()
    {
        var options = new SqlTypeMappingOptions { TreatSingleDigitNumberAsBoolean = true };

        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 5, Scale: 0), options);

        Assert.That(result, Is.EqualTo(AttributeDataType.Number), "The opt-in is scoped to NUMBER(1); a wider whole-number column falls through to ordinary precision-based inference.");
    }

    #endregion

    #region Oracle whole-number precision inference (#1354)

    // Oracle has one numeric type, so the declared precision and scale are the only signal available
    // for whether a column holds a whole number and how wide it is. Discarding them made every Oracle
    // NUMBER a Decimal, which put every built-in numeric Metaverse Attribute out of reach.

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(9)]
    public void Map_OracleWholeNumberWithinIntRange_ReturnsNumber(int precision)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: precision, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Number), $"NUMBER({precision},0) holds at most {new string('9', precision)}, which fits a 32-bit whole number.");
    }

    [TestCase(10)]
    [TestCase(15)]
    [TestCase(18)]
    public void Map_OracleWholeNumberBeyondIntButWithinLongRange_ReturnsLongNumber(int precision)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: precision, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.LongNumber), $"NUMBER({precision},0) overflows a 32-bit whole number but fits a 64-bit one. Ten digits already exceed int.MaxValue, so this is the ordinary Oracle primary key.");
    }

    [TestCase(19)]
    [TestCase(28)]
    [TestCase(38)]
    public void Map_OracleWholeNumberBeyondLongRange_ReturnsDecimal(int precision)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: precision, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), $"NUMBER({precision},0) can exceed long.MaxValue, so narrowing it would risk silently losing a value. Nineteen digits straddle the boundary, which is why it is not treated as safe.");
    }

    [TestCase(9, 4)]
    [TestCase(10, 2)]
    [TestCase(38, 38)]
    public void Map_OracleNumberWithScale_ReturnsDecimal(int precision, int scale)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: precision, Scale: scale), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), $"NUMBER({precision},{scale}) is genuinely fractional; narrowing it to a whole number would discard the fraction.");
    }

    [Test]
    public void Map_OracleNumberWithNegativeScale_ReturnsDecimal()
    {
        // NUMBER(10,-2) rounds to hundreds and can hold twelve digits, so the usual precision
        // arithmetic does not apply. Refused rather than reasoned about.
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 10, Scale: -2), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), "A negative scale widens the range beyond what the declared precision states, so the narrowing arithmetic does not hold.");
    }

    [Test]
    public void Map_OracleUnconstrainedNumber_ReturnsDecimal()
    {
        // The catalogue reports no DATA_PRECISION for a floating NUMBER, which means up to 38 digits.
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), "An unconstrained NUMBER states no width, so the widest exact type is the only safe answer.");
    }

    [Test]
    public void Map_OracleWholeNumberWithNullScale_UsesPrecisionInference()
    {
        // A catalogue that reports a precision but no scale means a whole number, matching the
        // 'Scale ?? 0' convention the Boolean opt-in already uses.
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 5), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Number), "An absent scale is zero, so the column is a whole number of the declared width.");
    }

    [TestCase("int", AttributeDataType.Number)]
    [TestCase("bigint", AttributeDataType.LongNumber)]
    [TestCase("smallint", AttributeDataType.Number)]
    public void Map_SqlServerIntegerTypeWithCataloguePrecision_IgnoresPrecisionInference(string typeName, AttributeDataType expected)
    {
        // SQL Server's catalogue reports a numeric precision for its integer types (int is 10), which
        // would map to LongNumber if the Oracle inference leaked across. The named type states the
        // width exactly, so it stays authoritative.
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName, Precision: 10, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(expected), $"'{typeName}' states its own width, so precision-based inference must not apply to SQL Server.");
    }

    [Test]
    public void Map_SqlServerNumericWithZeroScale_RemainsDecimal()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType("numeric", Precision: 5, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), "A SQL Server author who writes numeric(5,0) rather than int has chosen an exact numeric; JIM does not second-guess a definitive named type.");
    }

    #endregion

    #region DateTime

    [TestCase("datetime")]
    [TestCase("datetime2")]
    [TestCase("smalldatetime")]
    [TestCase("date")]
    [TestCase("datetimeoffset")]
    public void Map_SqlServerDateOrTimeType_ReturnsDateTime(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.DateTime), $"'{typeName}' carries a point in time; JIM stores every one of them as UTC DateTime.");
    }

    [TestCase("DATE")]
    [TestCase("TIMESTAMP")]
    [TestCase("TIMESTAMP(6)")]
    [TestCase("TIMESTAMP(6) WITH TIME ZONE")]
    [TestCase("TIMESTAMP WITH TIME ZONE")]
    [TestCase("TIMESTAMP(6) WITH LOCAL TIME ZONE")]
    public void Map_OracleDateOrTimeType_ReturnsDateTime(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.DateTime), $"'{typeName}' carries a point in time; JIM stores every one of them as UTC DateTime.");
    }

    [TestCase("timestamp")]
    [TestCase("rowversion")]
    public void Map_SqlServerRowVersionType_ReturnsBinary(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Binary), "SQL Server's 'timestamp' is a row version, not a point in time; mapping it to DateTime would fabricate dates from opaque bytes.");
    }

    #endregion

    #region Offset-carrying columns

    [TestCase("datetimeoffset")]
    [TestCase("datetimeoffset(3)")]
    [TestCase("DATETIMEOFFSET")]
    public void CarriesAnOffset_SqlServerDateTimeOffset_IsOffsetCarrying(string typeName)
    {
        Assert.That(SqlTypeMapper.CarriesAnOffset(new SqlColumnType(typeName)), Is.True,
            "'datetimeoffset' states the offset of every value it holds, so the Database Time Zone must not be applied to it in either direction.");
    }

    [TestCase("TIMESTAMP WITH TIME ZONE")]
    [TestCase("TIMESTAMP(3) WITH TIME ZONE")]
    [TestCase("TIMESTAMP(6) WITH TIME ZONE")]
    public void CarriesAnOffset_OracleTimeStampWithTimeZone_IsOffsetCarrying(string typeName)
    {
        // Verified against Oracle Database Free 23ai: ODP.NET returns a DateTimeOffset carrying the
        // stored offset for this column, and the value it returns does not change with the session's
        // time zone. It genuinely carries its own offset.
        Assert.That(SqlTypeMapper.CarriesAnOffset(new SqlColumnType(typeName)), Is.True,
            "Oracle's TIMESTAMP WITH TIME ZONE stores the offset alongside the value and the driver hands both back.");
    }

    [TestCase("TIMESTAMP WITH LOCAL TIME ZONE")]
    [TestCase("TIMESTAMP(3) WITH LOCAL TIME ZONE")]
    [TestCase("TIMESTAMP(6) WITH LOCAL TIME ZONE")]
    [TestCase("timestamp(3) with local time zone")]
    public void CarriesAnOffset_OracleTimeStampWithLocalTimeZone_IsNotOffsetCarrying(string typeName)
    {
        // The catalogue names this column as though it carried an offset, but the wire says otherwise:
        // verified against Oracle Database Free 23ai, ODP.NET hands it back as a bare DateTime with
        // Kind=Unspecified, already converted into the session's time zone. Import reads that CLR type
        // and applies the Database Time Zone; classifying it as offset-carrying here made export skip
        // the same conversion, so the two directions disagreed about one column.
        Assert.That(SqlTypeMapper.CarriesAnOffset(new SqlColumnType(typeName)), Is.False,
            "A TIMESTAMP WITH LOCAL TIME ZONE column reaches JIM as a zoneless wall-clock reading, so it is interpreted through the Connected System's Database Time Zone like any other zoneless column.");
    }

    [TestCase("datetime2")]
    [TestCase("datetime")]
    [TestCase("date")]
    [TestCase("TIMESTAMP")]
    [TestCase("TIMESTAMP(6)")]
    [TestCase("DATE")]
    public void CarriesAnOffset_ZonelessDateOrTimeType_IsNotOffsetCarrying(string typeName)
    {
        Assert.That(SqlTypeMapper.CarriesAnOffset(new SqlColumnType(typeName)), Is.False,
            $"'{typeName}' states nothing about an offset, so its values are wall-clock time in the zone the administrator declared.");
    }

    #endregion

    #region Guid

    [Test]
    public void Map_SqlServerUniqueIdentifier_ReturnsGuid()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType("uniqueidentifier"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Guid), "'uniqueidentifier' is SQL Server's GUID type.");
    }

    [Test]
    public void Map_OracleRaw16WithoutTheGuidOptIn_ReturnsBinary()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("RAW", MaxLength: 16), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Binary), "RAW(16) is just as likely to carry a digest as a GUID, so only an administrator can say it is a GUID.");
    }

    [Test]
    public void Map_OracleRaw16WithTheGuidOptIn_ReturnsGuid()
    {
        var options = new SqlTypeMappingOptions { TreatRaw16AsGuid = true };

        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("RAW", MaxLength: 16), options);

        Assert.That(result, Is.EqualTo(AttributeDataType.Guid), "The opt-in is how an estate declares that its RAW(16) columns carry GUID content.");
    }

    [Test]
    public void Map_OracleRawOtherThan16BytesWithTheGuidOptIn_ReturnsBinary()
    {
        var options = new SqlTypeMappingOptions { TreatRaw16AsGuid = true };

        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("RAW", MaxLength: 8), options);

        Assert.That(result, Is.EqualTo(AttributeDataType.Binary), "A GUID is exactly 16 bytes; anything else cannot be one whatever the configuration says.");
    }

    #endregion

    #region Binary

    [TestCase("varbinary")]
    [TestCase("binary")]
    [TestCase("image")]
    public void Map_SqlServerBinaryType_ReturnsBinary(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Binary), $"'{typeName}' holds opaque bytes.");
    }

    [TestCase("BLOB")]
    [TestCase("RAW")]
    [TestCase("LONG RAW")]
    public void Map_OracleBinaryType_ReturnsBinary(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Binary), $"'{typeName}' holds opaque bytes.");
    }

    #endregion

    #region Decimal

    [TestCase("decimal")]
    [TestCase("numeric")]
    [TestCase("money")]
    [TestCase("smallmoney")]
    public void Map_SqlServerExactNumericType_ReturnsDecimal(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName, Precision: 18, Scale: 2), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), $"'{typeName}' is exact numeric; a Text mapping would compare lexicographically and break scoping criteria.");
    }

    [Test]
    public void Map_OracleNumber_ReturnsDecimal()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 10, Scale: 2), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), "NUMBER is exact numeric; a Text mapping would compare lexicographically and break scoping criteria.");
    }

    [TestCase("float")]
    [TestCase("real")]
    [TestCase("double precision")]
    public void Map_SqlServerApproximateNumericType_ReturnsDecimal(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), $"'{typeName}' is approximate, but Decimal keeps numeric comparison semantics; the binary-to-decimal precision caveat is documented rather than solved by a Text mapping.");
    }

    [TestCase("FLOAT")]
    [TestCase("BINARY_FLOAT")]
    [TestCase("BINARY_DOUBLE")]
    [TestCase("DOUBLE PRECISION")]
    [TestCase("REAL")]
    public void Map_OracleApproximateNumericType_ReturnsDecimal(string typeName)
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType(typeName), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), $"'{typeName}' is approximate, but Decimal keeps numeric comparison semantics; the binary-to-decimal precision caveat is documented rather than solved by a Text mapping.");
    }

    #endregion

    #region Unmappable types

    [TestCase("geography")]
    [TestCase("hierarchyid")]
    [TestCase("sql_variant")]
    [TestCase("xml")]
    [TestCase("time")]
    public void Map_UnmappableSqlServerType_ThrowsRatherThanDegradingToText(string typeName)
    {
        AssertUnmappable(SqlDatabaseType.SqlServer, typeName);
    }

    [TestCase("XMLTYPE")]
    [TestCase("BFILE")]
    [TestCase("INTERVAL DAY(2) TO SECOND(6)")]
    [TestCase("ROWID")]
    public void Map_UnmappableOracleType_ThrowsRatherThanDegradingToText(string typeName)
    {
        AssertUnmappable(SqlDatabaseType.Oracle, typeName);
    }

    private static void AssertUnmappable(SqlDatabaseType databaseType, string typeName)
    {
        var exception = Assert.Throws<SqlTypeMappingException>(() =>
            SqlTypeMapper.Map(databaseType, new SqlColumnType(typeName), SqlTypeMappingOptions.Default));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.SqlTypeName, Is.EqualTo(typeName), "The administrator needs to know which column type JIM could not map.");
            Assert.That(exception.Message, Does.Contain(typeName), "A mapping failure must name the offending type so it can be fixed or excluded.");
        }
    }

    [Test]
    public void Map_EmptyTypeName_Throws()
    {
        Assert.Throws<SqlTypeMappingException>(() =>
            SqlTypeMapper.Map(SqlDatabaseType.SqlServer, new SqlColumnType("   "), SqlTypeMappingOptions.Default));
    }

    #endregion

    #region Provider entry points

    [Test]
    public void MapColumnType_SqlServerProvider_DelegatesToTheSqlServerDialect()
    {
        var provider = new SqlServerProvider();

        var result = provider.MapColumnType(new SqlColumnType("timestamp"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Binary), "The provider must map with its own dialect's meaning of a type name, not a shared one.");
    }

    [Test]
    public void MapColumnType_OracleProvider_DelegatesToTheOracleDialect()
    {
        var provider = new OracleProvider();

        var result = provider.MapColumnType(new SqlColumnType("TIMESTAMP"), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.DateTime), "The provider must map with its own dialect's meaning of a type name, not a shared one.");
    }

    #endregion
}

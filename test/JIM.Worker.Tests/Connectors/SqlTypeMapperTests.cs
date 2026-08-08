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
    public void Map_OracleNumberOnePrecisionWithoutTheOptIn_ReturnsDecimal()
    {
        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 1, Scale: 0), SqlTypeMappingOptions.Default);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), "NUMBER(1) is a number until an administrator declares it a flag; inferring Boolean would silently reinterpret real numeric data.");
    }

    [Test]
    public void Map_OracleNumberOnePrecisionWithTheOptIn_ReturnsBoolean()
    {
        var options = new SqlTypeMappingOptions { TreatSingleDigitNumberAsBoolean = true };

        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 1, Scale: 0), options);

        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean), "The opt-in exists so an estate that stores flags as NUMBER(1) can say so.");
    }

    [Test]
    public void Map_OracleNumberWiderThanOneDigitWithTheOptIn_ReturnsDecimal()
    {
        var options = new SqlTypeMappingOptions { TreatSingleDigitNumberAsBoolean = true };

        var result = SqlTypeMapper.Map(SqlDatabaseType.Oracle, new SqlColumnType("NUMBER", Precision: 5, Scale: 0), options);

        Assert.That(result, Is.EqualTo(AttributeDataType.Decimal), "The opt-in is scoped to NUMBER(1); wider columns cannot hold a two-state value.");
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

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers what the JIM SQL Connector does with a column that maps to a Decimal attribute: that it reads
/// the value the column holds rather than whichever CLR type the driver inferred for it, that it still
/// reads the approximate binary types no driver will hand over as a decimal, and that a number wider
/// than a CLR decimal fails in terms an administrator can act on.
/// </summary>
/// <remarks>
/// The Decimal attribute type exists so an exact decimal stays exact. A driver that materialises an
/// exact numeric column as a Single or a Double has already spent that guarantee before the Connector
/// sees the value, whatever the value happens to round back to; see <see cref="FakeDriverNumber"/> for
/// what each driver was measured doing and why these tests use the values they do.
/// </remarks>
[TestFixture]
public class SqlConnectorDecimalPrecisionTests
{
    /// <summary>
    /// One object type, one table, one anchor column, and one Decimal-mapped column beside it.
    /// </summary>
    private const string SalaryDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] }
          ]
        }
        """;

    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    #region Reading an exact decimal exactly

    [Test]
    public async Task ImportAsync_AnExactNumericColumnTheDriverInfersADoubleFor_ImportsTheValueTheColumnHolds()
    {
        // 1234567890.1234567 has 17 significant digits, and a Double preserves 15. Convert.ToDecimal
        // rounds a Double to exactly those 15, so reading this column through the CLR type the driver
        // inferred yields 1234567890.12346: the last two digits are gone before the Connector sees the
        // value, and no error is raised anywhere.
        const decimal salary = 1234567890.1234567m;

        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"],
            [1, FakeDriverNumber.InferredAsADouble(salary)]);

        var run = await RunImportAsync(provider, SalaryDocument, SalarySystem());

        Assert.That(ImportedSalary(run), Is.EqualTo(salary),
            "A Decimal attribute exists so an exact decimal stays exact; a value that has been through binary floating point is no longer that value.");
    }

    [Test]
    public async Task ImportAsync_AnExactNumericColumnTheDriverInfersASingleFor_ImportsTheValueTheColumnHolds()
    {
        // 12345.678 has 8 significant digits, and a Single preserves 7. Convert.ToDecimal rounds a Single
        // to exactly those 7, so the inferred CLR type yields 12345.68: a value out by more than a
        // hundredth, which for a currency column is a wrong amount rather than a rounding artefact.
        const decimal salary = 12345.678m;

        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"],
            [1, FakeDriverNumber.InferredAsASingle(salary)]);

        var run = await RunImportAsync(provider, SalaryDocument, SalarySystem());

        Assert.That(ImportedSalary(run), Is.EqualTo(salary));
    }

    [Test]
    public async Task ImportAsync_ADecimalAnchorTheDriverInfersADoubleFor_PagesOnAndComposesTheExactValue()
    {
        // An Oracle primary key is typically a NUMBER, so the anchor is the Decimal column that matters
        // most: its value both identifies the object and positions the next page. Two rows at one a page
        // makes the run write an anchor into a pagination token and bind it back as the next page's
        // boundary, which is where a dropped digit would resume from the wrong row.
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "LEDGER_ID" ] }
              ]
            }
            """;

        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["LEDGER_ID", "DISPLAY_NAME"],
            [FakeDriverNumber.InferredAsADouble(1234567890.1234567m), "Ada"],
            [FakeDriverNumber.InferredAsADouble(1234567890.1234568m), "Grace"]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("LEDGER_ID", AttributeDataType.Decimal, isExternalId: true),
                    Attribute("DISPLAY_NAME", AttributeDataType.Text))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem, pageSize: 1);

        var anchors = run.ImportObjects
            .Select(importObject => importObject.Attributes.Single(attribute => attribute.Name == "LEDGER_ID").DecimalValues.Single())
            .ToList();

        Assert.That(anchors, Is.EqualTo(new[] { 1234567890.1234567m, 1234567890.1234568m }),
            "Two anchors that differ only in their last digit are one anchor once a Double has rounded them, which pages past a row or reads it twice.");
    }

    [Test]
    public async Task ImportAsync_ADecimalValueInARelatedTable_ImportsTheValueTheColumnHolds()
    {
        // A related table's values are read through a different query and a different reader, so the
        // guarantee has to hold there too or a multi-valued Decimal attribute is exact on one path only.
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "schema": "HR",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "relatedTables": [
                    {
                      "attributeName": "Bonuses",
                      "schema": "HR",
                      "table": "EMPLOYEE_BONUSES",
                      "valueColumn": "AMOUNT",
                      "joinColumns": [ "EMPLOYEE_ID" ]
                    }
                  ]
                }
              ]
            }
            """;

        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1]);
        provider.Catalogue.AddRows("HR", "EMPLOYEE_BONUSES", ["EMPLOYEE_ID", "AMOUNT"],
            [1, FakeDriverNumber.InferredAsADouble(1234567890.1234567m)],
            [1, FakeDriverNumber.InferredAsASingle(12345.678m)]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                    Attribute("Bonuses", AttributeDataType.Decimal, plurality: AttributePlurality.MultiValued))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem);

        var bonuses = run.ImportObjects.Single().Attributes.Single(attribute => attribute.Name == "Bonuses").DecimalValues;

        Assert.That(bonuses, Is.EquivalentTo(new[] { 1234567890.1234567m, 12345.678m }));
    }

    #endregion

    #region A number beyond what a decimal can hold

    /// <param name="asOracleReportsIt">Which driver's refusal to stand in for. The two dialects report the same problem as different exception types, and both have to end up as the same error.</param>
    [TestCase(true)]
    [TestCase(false)]
    public void ImportAsync_ANumberWiderThanADecimalCanHold_FailsNamingTheObjectTypeTheColumnAndTheLimit(bool asOracleReportsIt)
    {
        // Oracle's NUMBER holds 38 significant digits and Microsoft SQL Server's decimal 38; a CLR
        // decimal holds 28 to 29. The driver's own refusal names neither the column nor the row, so an
        // administrator is told only that a cast failed somewhere in a run over half a million rows.
        var provider = new FakeSqlProvider { DialectUnderTest = asOracleReportsIt ? SqlDatabaseType.Oracle : SqlDatabaseType.SqlServer };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"],
            [1, FakeDriverNumber.BeyondDecimal(asOracleReportsIt)]);

        Assert.That(async () => await RunImportAsync(provider, SalaryDocument, SalarySystem()),
            Throws.InstanceOf<InvalidDataException>()
                .With.Message.Contains("'Person'")
                .And.Message.Contains("'SALARY'")
                .And.Message.Contains("28"),
            "A wide value has to be reported as the Object Type, the column and the limit it exceeded, or there is nothing for an administrator to act on.");
    }

    [Test]
    public void ImportAsync_ANumberWiderThanADecimalCanHold_IsNeverRoundedOrTruncated()
    {
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"],
            [1, FakeDriverNumber.BeyondDecimal(asOracleReportsIt: true)]);

        // Stated as its own test because the alternative reading of "handle the overflow" is to shorten
        // the value and carry on, which is precisely the silent corruption the Decimal attribute exists
        // to prevent.
        Assert.That(async () => await RunImportAsync(provider, SalaryDocument, SalarySystem()),
            Throws.InstanceOf<InvalidDataException>(),
            "A number JIM cannot hold exactly is refused, never shortened to fit.");
    }

    #endregion

    #region Types that are not exact decimals, and dialects that already were

    [Test]
    public async Task ImportAsync_AnApproximateBinaryColumnNoDriverWillHandOverAsADecimal_StillImportsTheDriverValue()
    {
        // Microsoft SQL Server's float and real, and Oracle's BINARY_FLOAT and BINARY_DOUBLE, map to
        // Decimal so that JIM compares them as numbers rather than as text. They are not exact decimals
        // and both drivers refuse to pretend otherwise, so the Connector has to fall back to the value
        // the driver does have; the PRD documents that this round trip is not bit-exact.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"],
            [1, FakeDriverNumber.Approximate(1234.5)],
            [2, FakeDriverNumber.Approximate(6789.25)]);

        var run = await RunImportAsync(provider, SalaryDocument, SalarySystem());

        var salaries = run.ImportObjects
            .Select(importObject => importObject.Attributes.Single(attribute => attribute.Name == "SALARY").DecimalValues.Single())
            .ToList();

        Assert.That(salaries, Is.EqualTo(new[] { 1234.5m, 6789.25m }),
            "A refused decimal accessor means the column is not an exact numeric, not that the row cannot be imported.");
    }

    [Test]
    public async Task ImportAsync_AMicrosoftSqlServerDecimalColumn_ImportsExactlyWhatItAlwaysDid()
    {
        // Microsoft.Data.SqlClient already materialises decimal, numeric, money and smallmoney as a CLR
        // decimal, so nothing about those columns changes; this is the guard that says so.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"],
            [1, 1234567890.1234567m],
            [2, -0.0000001m],
            [3, 79228162514264337593543950335m]);

        var run = await RunImportAsync(provider, SalaryDocument, SalarySystem());

        var salaries = run.ImportObjects
            .Select(importObject => importObject.Attributes.Single(attribute => attribute.Name == "SALARY").DecimalValues.Single())
            .ToList();

        Assert.That(salaries, Is.EqualTo(new[] { 1234567890.1234567m, -0.0000001m, 79228162514264337593543950335m }),
            "The widest value a CLR decimal holds is imported unchanged; only a value beyond it is refused.");
    }

    [Test]
    public async Task ImportAsync_ANullDecimalColumn_ImportsNoValueRatherThanZero()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "SALARY"], [1, null]);

        var run = await RunImportAsync(provider, SalaryDocument, SalarySystem());

        Assert.That(run.ImportObjects.Single().Attributes.Any(attribute => attribute.Name == "SALARY"), Is.False,
            "A NULL is the absence of a value, and importing it as zero would be a salary nobody has.");
    }

    #endregion

    #region Test helpers

    /// <summary>
    /// Everything one Full Import produced, in the order the pages came back.
    /// </summary>
    private sealed record SqlImportRun(List<ConnectedSystemImportResult> Pages)
    {
        internal List<ConnectedSystemImportObject> ImportObjects => [.. Pages.SelectMany(page => page.ImportObjects)];
    }

    /// <summary>
    /// Drives a Full Import to completion the way the Worker does: open once, call until no pagination
    /// tokens come back, then close.
    /// </summary>
    private async Task<SqlImportRun> RunImportAsync(
        FakeSqlProvider provider,
        string objectTypesDocument,
        ConnectedSystem connectedSystem,
        int pageSize = 10,
        int callLimit = 25)
    {
        var progress = new RecordingConnectorProgress();
        var pages = new List<ConnectedSystemImportResult>();
        // Disposal releases the import connection, so the Connector needs no explicit close here.
        using var connector = new SqlConnector { ProviderFactory = _ => provider };

        var settingValues = provider.DatabaseType == SqlDatabaseType.Oracle
            ? SqlConnectorSettingValues.CreateOracle(connector, SqlConnectorConstants.OracleEncryptionNativeNetworkEncryption)
            : SqlConnectorSettingValues.CreateSqlServer(connector);

        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, objectTypesDocument);
        connector.OpenImportConnection(settingValues, null, _logger);

        var runProfile = new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = pageSize };
        var paginationTokens = new List<ConnectedSystemPaginationToken>();
        var initialPage = true;

        while (initialPage || paginationTokens.Count > 0)
        {
            initialPage = false;

            var result = await connector.ImportAsync(connectedSystem, runProfile, paginationTokens, null, _logger, CancellationToken.None, progress);
            pages.Add(result);
            paginationTokens = result.PaginationTokens;

            Assert.That(pages, Has.Count.LessThanOrEqualTo(callLimit),
                "The import never stopped returning pagination tokens, which is an infinite import.");
        }

        return new SqlImportRun(pages);
    }

    private static ConnectedSystem SalarySystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType("Person",
                Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                Attribute("SALARY", AttributeDataType.Decimal))
        ]
    };

    private static ConnectedSystemObjectType ObjectType(string name, params ConnectedSystemObjectTypeAttribute[] attributes) =>
        new() { Name = name, Selected = true, Attributes = [.. attributes] };

    private static ConnectedSystemObjectTypeAttribute Attribute(
        string name,
        AttributeDataType type,
        bool isExternalId = false,
        AttributePlurality plurality = AttributePlurality.SingleValued) =>
        new() { Name = name, Type = type, Selected = true, IsExternalId = isExternalId, AttributePlurality = plurality };

    private static decimal ImportedSalary(SqlImportRun run) =>
        run.ImportObjects.Single().Attributes.Single(attribute => attribute.Name == "SALARY").DecimalValues.Single();

    #endregion
}

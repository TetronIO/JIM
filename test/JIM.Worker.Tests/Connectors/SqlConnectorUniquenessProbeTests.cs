// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The JIM SQL Connector's uniqueness probe (#1941; Unique Value Generation, #242, release 3): one parameterised
/// <c>IN</c> lookup against the Object Type's own table or view, the control value check, and every way a database
/// can fail to answer becoming "could not determine" rather than "not found". No test here touches a database server;
/// the dialect seam is substituted, and the real dialects are exercised against their servers at runtime.
/// </summary>
[TestFixture]
public class SqlConnectorUniquenessProbeTests
{
    private const string PersonDocument = """
        {
          "objectTypes": [
            {
              "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ],
              "relatedTables": [
                { "attributeName": "EmailAliases", "schema": "HR", "table": "EMPLOYEE_ALIASES", "valueColumn": "ALIAS", "joinColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
          ]
        }
        """;

    private readonly List<SqlConnector> _connectors = [];
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var connector in _connectors)
            connector.Dispose();

        _connectors.Clear();
        (_logger as IDisposable)?.Dispose();
    }

    // ---- The statement ----

    [Test]
    public async Task ProbeAsync_OneBatch_IsOneParameterisedInLookupAgainstTheObjectTypesTableAsync()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);

        await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs", "joe.bloggs1"], "asmith"), _logger, CancellationToken.None);

        var probe = provider.ExecutedStatements.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.CommandText, Is.EqualTo(
                "SELECT DISTINCT [USERNAME] FROM [HR].[EMPLOYEES] WHERE [USERNAME] IN (@probe0, @probe1, @probe2)"));
            Assert.That(probe.Parameters, Is.EqualTo(new Dictionary<string, object?>
            {
                ["probe0"] = "joe.bloggs",
                ["probe1"] = "joe.bloggs1",
                ["probe2"] = "asmith"
            }));
        }
    }

    /// <summary>
    /// A candidate is built from source data, so it may carry anything; it is bound as a parameter and never reaches
    /// the statement text.
    /// </summary>
    [Test]
    public async Task ProbeAsync_InjectionAttempt_IsBoundAndNeverInterpolatedAsync()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);
        const string hostile = "x'); DROP TABLE [HR].[EMPLOYEES]; --";

        await connector.ProbeAsync(Request("USERNAME", [hostile], null), _logger, CancellationToken.None);

        var probe = provider.ExecutedStatements.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.CommandText, Does.Not.Contain("DROP"));
            Assert.That(probe.Parameters["probe0"], Is.EqualTo(hostile));
        }
    }

    [Test]
    public async Task ProbeAsync_ColumnNameCarryingTheQuoteCharacter_StaysOneIdentifierAsync()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);

        // Only the statement text matters here, so the stand-in refuses it rather than trying to read a column it
        // does not hold.
        provider.FailWhenCommandTextContains = " IN (";

        await connector.ProbeAsync(Request("USER]NAME", ["joe.bloggs"], null), _logger, CancellationToken.None);

        Assert.That(provider.ExecutedStatementTexts.Single(), Does.StartWith("SELECT DISTINCT [USER]]NAME] FROM [HR].[EMPLOYEES] WHERE [USER]]NAME] IN ("));
    }

    [Test]
    public async Task ProbeAsync_ObjectTypeReadFromASelectStatement_SearchesTheStatementAsync()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "select": "SELECT * FROM [HR].[EMPLOYEES] WHERE [ACTIVE] = 1", "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """;
        var provider = PersonDatabase();
        var connector = Open(provider, document);

        await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs"], null), _logger, CancellationToken.None);

        Assert.That(provider.ExecutedStatementTexts.Single(), Is.EqualTo(
            "SELECT DISTINCT [USERNAME] FROM (SELECT * FROM [HR].[EMPLOYEES] WHERE [ACTIVE] = 1) [JIM_SOURCE] WHERE LOWER([USERNAME]) IN (LOWER(@probe0))"),
            "a statement has no catalogue entry to say how its columns compare, so the probe compares lower-cased values");
    }

    /// <summary>
    /// A multi-valued attribute lives in a related table of its own, so that is where its values are searched.
    /// </summary>
    [Test]
    public async Task ProbeAsync_RelatedTableAttribute_SearchesTheRelatedTablesValueColumnAsync()
    {
        var provider = PersonDatabase();
        provider.Catalogue.AddRows("HR", "EMPLOYEE_ALIASES", ["EMPLOYEE_ID", "ALIAS"], [1, "jbloggs@example.com"]);
        var connector = Open(provider);

        var result = await connector.ProbeAsync(Request("EmailAliases", ["jbloggs@example.com", "jbloggs1@example.com"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ExecutedStatementTexts.Single(), Is.EqualTo(
                "SELECT DISTINCT [ALIAS] FROM [HR].[EMPLOYEE_ALIASES] WHERE [ALIAS] IN (@probe0, @probe1)"));
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
        }
    }

    // ---- Answers ----

    [Test]
    public async Task ProbeAsync_CandidateHeldByTheDatabase_IsFoundAndTheControlConfirmsTheRestAsync()
    {
        var connector = Open(PersonDatabase());

        var result = await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs", "joe.bloggs1"], "asmith"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Reason, Is.Null);
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
        }
    }

    [Test]
    public async Task ProbeAsync_ControlValueNotReturned_EveryCandidateUndeterminedAsync()
    {
        var connector = Open(PersonDatabase());

        var result = await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs1"], "no.such.person"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
        }
    }

    /// <summary>
    /// Generated-value uniqueness ignores case at every check (PRD FR 31), the probe included, whatever the column's
    /// collation: <c>JOE.BLOGGS</c> is in use when <c>joe.bloggs</c> is.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ProbeAsync_ValueHeldInAnotherCase_IsFoundWhateverTheCollationAsync(bool caseSensitiveCollation)
    {
        var connector = Open(PersonDatabase(caseSensitiveCollation));

        var result = await connector.ProbeAsync(Request("USERNAME", ["JOE.BLOGGS"], null), _logger, CancellationToken.None);

        Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
    }

    /// <summary>
    /// A column whose collation already ignores case (Microsoft SQL Server's default) is compared as it stands, so an
    /// index on it can answer the query.
    /// </summary>
    [Test]
    public async Task ProbeAsync_ColumnCollationIgnoresCase_ComparesTheColumnAsItStandsAsync()
    {
        var provider = PersonDatabase(caseSensitiveCollation: false);
        var connector = Open(provider);

        await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs"], null), _logger, CancellationToken.None);

        Assert.That(provider.ExecutedStatementTexts.Single(), Is.EqualTo(
            "SELECT DISTINCT [USERNAME] FROM [HR].[EMPLOYEES] WHERE [USERNAME] IN (@probe0)"));
    }

    /// <summary>
    /// A column whose collation compares case (Oracle Database's default, or a case-sensitive Microsoft SQL Server
    /// collation) is compared lower-cased on both sides, so a differently cased value still matches.
    /// </summary>
    [Test]
    public async Task ProbeAsync_ColumnCollationComparesCase_ComparesLowerCasedValuesAsync()
    {
        var provider = PersonDatabase(caseSensitiveCollation: true);
        var connector = Open(provider);

        await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs", "joe.bloggs1"], null), _logger, CancellationToken.None);

        Assert.That(provider.ExecutedStatementTexts.Single(), Is.EqualTo(
            "SELECT DISTINCT [USERNAME] FROM [HR].[EMPLOYEES] WHERE LOWER([USERNAME]) IN (LOWER(@probe0), LOWER(@probe1))"));
    }

    /// <summary>
    /// A dialect that cannot say how a column compares is treated as comparing case, so the probe still ignores it.
    /// </summary>
    [Test]
    public async Task ProbeAsync_DialectCannotReportTheColumnsCollation_ComparesLowerCasedValuesAsync()
    {
        var provider = PersonDatabase(caseSensitiveCollation: false, canReportColumnCollation: false);
        var connector = Open(provider);

        var result = await connector.ProbeAsync(Request("USERNAME", ["JOE.BLOGGS"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ExecutedStatementTexts.Single(), Does.Contain("WHERE LOWER([USERNAME]) IN (LOWER(@probe0))"));
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
        }
    }

    /// <summary>
    /// Reading the collation is an optimisation: when it fails, the probe compares lower-cased values, which is right
    /// whatever the collation, rather than failing.
    /// </summary>
    [Test]
    public async Task ProbeAsync_ReadingTheColumnsCollationFails_StillAnswersComparingLowerCasedValuesAsync()
    {
        var provider = PersonDatabase(caseSensitiveCollation: true);
        provider.FailWhenCommandTextContains = FakeSqlProvider.ColumnIgnoresCaseQuery;
        var connector = Open(provider);

        var result = await connector.ProbeAsync(Request("USERNAME", ["JOE.BLOGGS", "joe.bloggs1"], "asmith"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ExecutedStatementTexts.Single(), Does.Contain("WHERE LOWER([USERNAME]) IN ("));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
        }
    }

    [Test]
    public async Task ProbeAsync_SeveralBatchesForOneAttribute_ReadTheColumnsCollationOnceAsync()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);

        await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs"], null), _logger, CancellationToken.None);
        await connector.ProbeAsync(Request("USERNAME", ["ada.lovelace"], null), _logger, CancellationToken.None);

        Assert.That(provider.ExecutedCommandTexts.Count(text => text == FakeSqlProvider.ColumnIgnoresCaseQuery), Is.EqualTo(1));
    }

    /// <summary>
    /// Microsoft SQL Server compares text ignoring trailing spaces, so a CHAR column it matched hands the value back
    /// blank-padded; reading the padding as part of the value would miss every collision on such a column.
    /// </summary>
    [Test]
    public async Task ProbeAsync_FixedWidthColumnReturnsPaddedValues_StillFoundAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "USERNAME"], [1, "asmith    "], [2, "joe.bloggs"]);
        var connector = Open(provider);

        var result = await connector.ProbeAsync(Request("USERNAME", ["asmith", "joe.bloggs1"], "joe.bloggs"), _logger, CancellationToken.None);

        Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
    }

    // ---- Failures ----

    [Test]
    public async Task ProbeAsync_DatabaseRefusesTheStatement_FailedNeverNotFoundAsync()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);
        provider.FailWhenCommandTextContains = " IN (";
        provider.FailureDetail = ("The SELECT permission was denied on the object 'EMPLOYEES'.", "42000");

        var result = await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs", "joe.bloggs1"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("SELECT permission was denied"));
        }
    }

    /// <summary>
    /// A driver may report a statement cancelled with the run as a database error of its own. The run was cancelled,
    /// and that must reach the caller; read as a refused probe, it would let the run carry on.
    /// </summary>
    [Test]
    public void ProbeAsync_RunCancelledAndTheDriverReportsADatabaseError_PropagatesTheCancellation()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);
        using var run = new CancellationTokenSource();
        provider.BeforeExecute = _ => run.Cancel();
        provider.FailWhenCommandTextContains = " IN (";
        provider.FailureDetail = ("Operation cancelled by user.", "HY008");

        Assert.That(async () => await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs"], null), _logger, run.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ProbeAsync_ObjectTypeNotInTheDocument_UndeterminedAsync()
    {
        var connector = Open(PersonDatabase());

        var result = await connector.ProbeAsync(Request("USERNAME", ["joe.bloggs"], null, objectType: "Contractor"), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Reason, Does.Contain("Contractor"));
        }
    }

    [Test]
    public async Task ProbeAsync_AttributeNameThatCannotBeAColumn_UndeterminedWithoutRunningAnythingAsync()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);

        var result = await connector.ProbeAsync(Request("USER\nNAME", ["joe.bloggs"], null), _logger, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(provider.ExecutedStatements, Is.Empty);
        }
    }

    // ---- Connection lifetime ----

    [Test]
    public void OpenUniquenessProbeConnection_OpensItsOwnConnection()
    {
        var provider = PersonDatabase();

        Open(provider);

        Assert.That(provider.OpenConnections, Has.Count.EqualTo(1));
    }

    [Test]
    public void OpenUniquenessProbeConnection_DatabaseUnreachable_Throws()
    {
        var connector = Track(new SqlConnector { ProviderFactory = _ => new FakeSqlProvider { OpenFailure = new FakeDbException("A network-related error occurred.") } });

        Assert.That(() => ((IConnectorUniquenessProbe)connector).OpenUniquenessProbeConnection(ConnectedSystem(connector, PersonDocument), _logger),
            Throws.Exception);
    }

    [Test]
    public void CloseUniquenessProbeConnection_ReleasesTheConnection()
    {
        var provider = PersonDatabase();
        var connector = Open(provider);

        connector.CloseUniquenessProbeConnection();

        Assert.That(provider.OpenConnections.Single().State, Is.EqualTo(System.Data.ConnectionState.Closed));
    }

    [Test]
    public void CloseUniquenessProbeConnection_NeverOpened_DoesNotThrow()
    {
        IConnectorUniquenessProbe connector = Track(new SqlConnector());

        Assert.That(connector.CloseUniquenessProbeConnection, Throws.Nothing);
    }

    [Test]
    public void ProbeAsync_BeforeOpen_Throws()
    {
        IConnectorUniquenessProbe connector = Track(new SqlConnector());

        Assert.That(() => connector.ProbeAsync(Request("USERNAME", ["joe.bloggs"], null), _logger, CancellationToken.None),
            Throws.InvalidOperationException);
    }

    [Test]
    public void CanProbeAttribute_ANamedAttribute_IsProbed()
    {
        IConnectorUniquenessProbe connector = Track(new SqlConnector());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.CanProbeAttribute("USERNAME"), Is.True);
            Assert.That(connector.CanProbeAttribute(" "), Is.False);
        }
    }

    private static FakeSqlProvider PersonDatabase(bool caseSensitiveCollation = false, bool canReportColumnCollation = true)
    {
        var provider = new FakeSqlProvider { CaseSensitiveCollation = caseSensitiveCollation, CanReportColumnCollation = canReportColumnCollation };
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "USERNAME"], [1, "asmith"], [2, "joe.bloggs"], [3, "joe.bloggs"]);
        return provider;
    }

    private IConnectorUniquenessProbe Open(FakeSqlProvider provider, string document = PersonDocument)
    {
        var connector = Track(new SqlConnector { ProviderFactory = _ => provider });
        IConnectorUniquenessProbe probe = connector;
        probe.OpenUniquenessProbeConnection(ConnectedSystem(connector, document), _logger);
        return probe;
    }

    private SqlConnector Track(SqlConnector connector)
    {
        _connectors.Add(connector);
        return connector;
    }

    private static ConnectedSystem ConnectedSystem(SqlConnector connector, string document)
    {
        var settingValues = SqlConnectorSettingValues.CreateSqlServer(connector);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, document);
        return new ConnectedSystem { Name = "HR Database", SettingValues = settingValues };
    }

    private static UniquenessProbeRequest Request(string attribute, IReadOnlyList<string> candidates, string? controlValue, string objectType = "Person") => new()
    {
        ObjectTypeName = objectType,
        AttributeName = attribute,
        Candidates = candidates,
        ControlValue = controlValue
    };
}

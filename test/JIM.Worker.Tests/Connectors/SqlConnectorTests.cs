// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the JIM SQL Connector's declared shape: what it says it can do, what it asks an
/// administrator for, where it says it connects, and what happens when a database cannot be reached.
/// No test here touches a database server; the dialect seam is substituted instead.
/// </summary>
[TestFixture]
public class SqlConnectorTests
{
    private SqlConnector _connector = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new SqlConnector();
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _connector.Dispose();
        (_logger as IDisposable)?.Dispose();
    }

    #region IConnector members

    [Test]
    public void Name_ReturnsSqlConnectorConstant()
    {
        Assert.That(_connector.Name, Is.EqualTo(ConnectorConstants.SqlConnectorName));
    }

    [Test]
    public void Description_IsSupplied()
    {
        Assert.That(_connector.Description, Is.Not.Null.And.Not.Empty);
    }

    #endregion

    #region IConnectorCapabilities members

    [Test]
    public void Capabilities_MatchSqlConnectorContract()
    {
        // The declaration the PRD's requirement 19 fixes. These are mirrored to the database by
        // reflection, so what is written here is what an administrator is offered in the portal.
        Assert.Multiple(() =>
        {
            Assert.That(_connector.SupportsFullImport, Is.True, nameof(_connector.SupportsFullImport));
            Assert.That(_connector.SupportsDeltaImport, Is.True, nameof(_connector.SupportsDeltaImport));
            Assert.That(_connector.SupportsExport, Is.True, nameof(_connector.SupportsExport));
            Assert.That(_connector.SupportsPaging, Is.True, nameof(_connector.SupportsPaging));
            Assert.That(_connector.SupportsAutoConfirmExport, Is.True, nameof(_connector.SupportsAutoConfirmExport));
            Assert.That(_connector.SupportsUserSelectedExternalId, Is.True, nameof(_connector.SupportsUserSelectedExternalId));

            Assert.That(_connector.SupportsPartitions, Is.False, nameof(_connector.SupportsPartitions));
            Assert.That(_connector.SupportsPartitionContainers, Is.False, nameof(_connector.SupportsPartitionContainers));
            Assert.That(_connector.SupportsSecondaryExternalId, Is.False, nameof(_connector.SupportsSecondaryExternalId));
            Assert.That(_connector.SupportsFilePaths, Is.False, nameof(_connector.SupportsFilePaths));
            Assert.That(_connector.SupportsParallelExport, Is.False, nameof(_connector.SupportsParallelExport));
            Assert.That(_connector.SupportsUserSelectedAttributeTypes, Is.False, nameof(_connector.SupportsUserSelectedAttributeTypes));
            Assert.That(_connector.SupportsPasswordSet, Is.False, nameof(_connector.SupportsPasswordSet));
            Assert.That(_connector.SupportsPasswordPolicyDiscovery, Is.False, nameof(_connector.SupportsPasswordPolicyDiscovery));
        });
    }

    #endregion

    #region Settings declaration

    [Test]
    public void GetSettings_SettingNames_AreUnique()
    {
        var names = _connector.GetSettings().Select(s => s.Name).ToList();

        Assert.That(names, Is.Unique, "Setting values are matched to settings by name, so a duplicate name is ambiguous.");
    }

    [Test]
    public void GetSettings_NoSettingIsNamedMode()
    {
        // ConnectedSystemExtensions keys File Connector semantics to a setting literally named "Mode",
        // so any other Connector declaring one would inherit behaviour that has nothing to do with it.
        Assert.That(_connector.GetSettings().Select(s => s.Name), Has.No.Member("Mode"));
    }

    [Test]
    public void GetSettings_DatabaseType_IsRequiredDropDownOfThePriorityOneProviders()
    {
        var setting = GetSetting(SqlConnectorConstants.SettingDatabaseType);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Required, Is.True);
            Assert.That(setting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
            Assert.That(setting.DropDownValues, Is.EquivalentTo(new[]
            {
                SqlConnectorConstants.DatabaseTypeSqlServer,
                SqlConnectorConstants.DatabaseTypeOracle
            }));
        });
    }

    [Test]
    public void GetSettings_Password_IsEncryptedAtRestAndCarriesNoDefault()
    {
        var setting = GetSetting(SqlConnectorConstants.SettingPassword);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.StringEncrypted),
                "The password must be encrypted at rest through the existing credential protection mechanism.");
            Assert.That(setting.Required, Is.True);
            Assert.That(setting.DefaultStringValue, Is.Null, "Defaults are never applied to encrypted settings, so declaring one would mislead.");
        });
    }

    [Test]
    public void GetSettings_DatabaseTimeZone_IsRequiredAndVisiblyDefaultsToUtc()
    {
        var setting = GetSetting(SqlConnectorConstants.SettingDatabaseTimeZone);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Required, Is.True, "Zoneless date and time columns are ambiguous, so the interpretation is never left unstated.");
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.String),
                "A String setting is what carries its default value through to the administrator; a Text setting would show empty.");
            Assert.That(setting.DefaultStringValue, Is.EqualTo(SqlConnectorConstants.DefaultDatabaseTimeZone));
            Assert.That(setting.DefaultStringValue, Is.EqualTo("UTC"));
        });
    }

    [Test]
    public void GetSettings_ConnectionTimeout_IsAnIntegerWithADefault()
    {
        var setting = GetSetting(SqlConnectorConstants.SettingConnectionTimeout);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
            Assert.That(setting.DefaultIntValue, Is.EqualTo(SqlConnectorConstants.DefaultConnectionTimeoutSeconds));
        });
    }

    [Test]
    public void GetSettings_OracleTypeMappingOptIns_AreCheckBoxesDefaultingToOff()
    {
        // Neither reinterpretation is inferable from the catalogue, so both start off: guessing would
        // silently change what a NUMBER(1) or a RAW(16) column means.
        foreach (var name in new[] { SqlConnectorConstants.SettingTreatNumber1AsBoolean, SqlConnectorConstants.SettingTreatRaw16AsGuid })
        {
            var setting = GetSetting(name);

            Assert.Multiple(() =>
            {
                Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.CheckBox), name);
                Assert.That(setting.DefaultCheckboxValue, Is.False, name);
                Assert.That(setting.RequiredWhenSetting, Is.EqualTo(SqlConnectorConstants.SettingDatabaseType), name);
                Assert.That(setting.RequiredWhenValue, Is.EqualTo(SqlConnectorConstants.DatabaseTypeOracle), name);
            });
        }
    }

    [Test]
    public void GetSettings_EveryRequiredWhenSetting_NamesASettingThatExistsAndAValueItCanHold()
    {
        var settings = _connector.GetSettings();

        foreach (var setting in settings.Where(s => !string.IsNullOrEmpty(s.RequiredWhenSetting)))
        {
            var controller = settings.SingleOrDefault(s => s.Name == setting.RequiredWhenSetting);

            Assert.Multiple(() =>
            {
                Assert.That(controller, Is.Not.Null,
                    $"'{setting.Name}' is conditional on '{setting.RequiredWhenSetting}', which is not a setting this Connector declares; the condition could never be met.");
                Assert.That(setting.RequiredWhenValue, Is.Not.Null.And.Not.Empty, $"'{setting.Name}' declares a condition with no value to compare against.");
            });

            if (controller!.Type == ConnectedSystemSettingType.DropDown)
                Assert.That(controller.DropDownValues, Has.Member(setting.RequiredWhenValue),
                    $"'{setting.Name}' waits for '{controller.Name}' to hold '{setting.RequiredWhenValue}', which is not one of its options.");

            if (controller.Type == ConnectedSystemSettingType.CheckBox)
                Assert.That(setting.RequiredWhenValue, Is.EqualTo("true").Or.EqualTo("false"),
                    $"A checkbox controller's value is compared as \"true\" or \"false\"; '{setting.Name}' waits for '{setting.RequiredWhenValue}'.");
        }
    }

    [Test]
    public void GetSettings_ConditionalSettings_ComeAfterTheSettingTheyDependOn()
    {
        // Settings render in declaration order, so a condition whose controller is further down the
        // form asks an administrator to answer a question before the one that reveals it.
        var settings = _connector.GetSettings();

        foreach (var setting in settings.Where(s => !string.IsNullOrEmpty(s.RequiredWhenSetting)))
        {
            var controllerIndex = settings.FindIndex(s => s.Name == setting.RequiredWhenSetting);
            var settingIndex = settings.FindIndex(s => s.Name == setting.Name);

            Assert.That(controllerIndex, Is.LessThan(settingIndex),
                $"'{setting.Name}' is revealed by '{setting.RequiredWhenSetting}', which is declared after it.");
        }
    }

    [Test]
    public void GetSettings_NoSettingDeclaresARequiredGroup()
    {
        // Deliberate: required-group validation is not gated by a setting's RequiredWhen condition
        // (ConnectorSettingValidator.Validate), so a group of Oracle-only settings would block saving a
        // Microsoft SQL Server Connected System. The either/or choice is expressed with conditions instead.
        Assert.That(_connector.GetSettings().Select(s => s.RequiredGroup), Has.All.Null.Or.Empty);
    }

    [Test]
    public void GetSettings_ForAMicrosoftSqlServerSystem_AsksForADatabaseNameAndNotForOracleDetails()
    {
        var settingValues = CreateSettingValues();
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeSqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(IsRequired(settingValues, SqlConnectorConstants.SettingDatabaseName), Is.True);
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy), Is.False);
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleServiceName), Is.False);
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleSid), Is.False);
        });
    }

    [Test]
    public void GetSettings_ForAnOracleSystem_AsksHowTheDatabaseIsIdentifiedAndNotForADatabaseName()
    {
        var settingValues = CreateSettingValues();
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeOracle);

        Assert.Multiple(() =>
        {
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingDatabaseName), Is.False);
            Assert.That(IsRequired(settingValues, SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy), Is.True);

            // Until that question is answered neither field is asked for, which is what keeps the
            // either/or choice expressible: only ever one of them is required.
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleServiceName), Is.False);
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleSid), Is.False);
        });
    }

    [Test]
    public void GetSettings_ForAnOracleSystemIdentifiedByServiceName_AsksForTheServiceNameOnly()
    {
        var settingValues = CreateSettingValues();
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeOracle);
        SetString(settingValues, SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, SqlConnectorConstants.OracleIdentifiedByServiceName);

        Assert.Multiple(() =>
        {
            Assert.That(IsRequired(settingValues, SqlConnectorConstants.SettingOracleServiceName), Is.True);
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleSid), Is.False);
        });
    }

    [Test]
    public void GetSettings_ForAnOracleSystemIdentifiedBySid_AsksForTheSidOnly()
    {
        var settingValues = CreateSettingValues();
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeOracle);
        SetString(settingValues, SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, SqlConnectorConstants.OracleIdentifiedBySid);

        Assert.Multiple(() =>
        {
            Assert.That(IsRequired(settingValues, SqlConnectorConstants.SettingOracleSid), Is.True);
            Assert.That(IsRelevant(settingValues, SqlConnectorConstants.SettingOracleServiceName), Is.False);
        });
    }

    #endregion

    #region IConnectorSecureEndpoint members

    [Test]
    public void ResolveSecureEndpoint_EncryptionDisabled_ReturnsNull()
    {
        // The null answer is what stops the shared certificate diagnosis path probing a host that this
        // Connected System never connects to over TLS.
        var settingValues = CreateSqlServerSettingValues(useTls: false);

        Assert.That(_connector.ResolveSecureEndpoint(settingValues), Is.Null);
    }

    [Test]
    public void ResolveSecureEndpoint_EncryptionEnabled_ReturnsTheConfiguredHostAndPort()
    {
        var settingValues = CreateSqlServerSettingValues(useTls: true);
        SetInt(settingValues, SqlConnectorConstants.SettingPort, 14330);
        SetInt(settingValues, SqlConnectorConstants.SettingConnectionTimeout, 45);

        var endpoint = _connector.ResolveSecureEndpoint(settingValues);

        Assert.That(endpoint, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(endpoint!.Host, Is.EqualTo("db.example.com"));
            Assert.That(endpoint.Port, Is.EqualTo(14330));
            Assert.That(endpoint.Timeout, Is.EqualTo(TimeSpan.FromSeconds(45)));
            Assert.That(endpoint.ServerDescription, Is.Not.Null.And.Not.Empty);
            Assert.That(endpoint.SecureTransportName, Is.EqualTo("TLS"));
        });
    }

    [Test]
    public void ResolveSecureEndpoint_EncryptionEnabledWithNoPort_ReturnsTheDialectsDefaultPort()
    {
        // The port is optional because the two dialects default differently, so the probe has to resolve
        // the same port a connection would have used rather than guessing one.
        var settingValues = CreateSqlServerSettingValues(useTls: true);
        SetInt(settingValues, SqlConnectorConstants.SettingPort, null);

        Assert.That(_connector.ResolveSecureEndpoint(settingValues)?.Port, Is.EqualTo(1433));
    }

    [Test]
    public void ResolveSecureEndpoint_OracleWithNoPort_ReturnsTheOracleTcpsPort()
    {
        var settingValues = CreateSettingValues();
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeOracle);
        SetString(settingValues, SqlConnectorConstants.SettingHost, "hr.example.com");
        SetInt(settingValues, SqlConnectorConstants.SettingPort, null);
        SetCheckbox(settingValues, SqlConnectorConstants.SettingUseTls, true);

        var endpoint = _connector.ResolveSecureEndpoint(settingValues);

        Assert.Multiple(() =>
        {
            Assert.That(endpoint?.Port, Is.EqualTo(2484));
            Assert.That(endpoint?.SecureTransportName, Is.EqualTo("TCPS"), "TCPS is what an Oracle administrator calls the encrypted transport.");
        });
    }

    [Test]
    public void ResolveSecureEndpoint_NoHostSupplied_ReturnsNull()
    {
        var settingValues = CreateSqlServerSettingValues(useTls: true);
        SetString(settingValues, SqlConnectorConstants.SettingHost, null);

        Assert.That(_connector.ResolveSecureEndpoint(settingValues), Is.Null);
    }

    [Test]
    public void ResolveSecureEndpoint_NoDatabaseTypeChosen_ReturnsNull()
    {
        var settingValues = CreateSqlServerSettingValues(useTls: true);
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, null);

        Assert.That(_connector.ResolveSecureEndpoint(settingValues), Is.Null,
            "Without a database type there is no dialect, so no port to probe.");
    }

    #endregion

    #region ValidateSettingValues

    [Test]
    public void ValidateSettingValues_ReachableDatabase_ReportsNoProblems()
    {
        var provider = new FakeSqlProvider();
        var connector = CreateConnectorWith(provider);

        var results = connector.ValidateSettingValues(CreateSqlServerSettingValues(useTls: false), _logger);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_ReachableDatabase_RunsTheDialectsOwnConnectivityQuery()
    {
        var provider = new FakeSqlProvider();
        var connector = CreateConnectorWith(provider);

        connector.ValidateSettingValues(CreateSqlServerSettingValues(useTls: false), _logger);

        Assert.That(provider.ExecutedCommandTexts, Is.EqualTo(new[] { provider.ConnectivityTestCommandText }),
            "The connectivity test is the cheapest statement the dialect offers, not one the Connector invents.");
    }

    [Test]
    public void ValidateSettingValues_UnreachableHost_ReportsAFailureCarryingTheDriversAccount()
    {
        var failure = new FakeDbException("A network-related or instance-specific error occurred while establishing a connection.");
        var connector = CreateConnectorWith(new FakeSqlProvider { OpenFailure = failure });

        var results = connector.ValidateSettingValues(CreateSqlServerSettingValues(useTls: false), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].IsValid, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("network-related"));
            Assert.That(results[0].Exception, Is.SameAs(failure), "The administrator needs the driver's own account, not a summary of it.");
        });
    }

    [Test]
    public void ValidateSettingValues_RefusedCredentials_ReportsAFailureWithoutRepeatingThePassword()
    {
        var connector = CreateConnectorWith(new FakeSqlProvider { OpenFailure = new FakeDbException("Login failed for user 'jim_sync'.") });
        var settingValues = CreateSqlServerSettingValues(useTls: false);
        SetEncrypted(settingValues, SqlConnectorConstants.SettingPassword, "sup3rs3cret");

        var results = connector.ValidateSettingValues(settingValues, _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].IsValid, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("Login failed"));
            Assert.That(results[0].ErrorMessage, Does.Not.Contain("sup3rs3cret"), "Credentials never travel into a message an administrator or a log can see.");
        });
    }

    [Test]
    public void ValidateSettingValues_NoHostSupplied_ReportsAFailureRatherThanThrowing()
    {
        var connector = CreateConnectorWith(new FakeSqlProvider());
        var settingValues = CreateSqlServerSettingValues(useTls: false);
        SetString(settingValues, SqlConnectorConstants.SettingHost, null);

        var results = connector.ValidateSettingValues(settingValues, _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
    }

    [Test]
    public void ValidateSettingValues_UnknownTimeZone_ReportsAFailureNamingTheSetting()
    {
        var connector = CreateConnectorWith(new FakeSqlProvider());
        var settingValues = CreateSqlServerSettingValues(useTls: false);
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseTimeZone, "Middle/Earth");

        var results = connector.ValidateSettingValues(settingValues, _logger);

        Assert.That(results, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(results[0].IsValid, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain(SqlConnectorConstants.SettingDatabaseTimeZone));
            Assert.That(results[0].SettingValue?.Setting.Name, Is.EqualTo(SqlConnectorConstants.SettingDatabaseTimeZone));
        });
    }

    [Test]
    public void ValidateSettingValues_UtcTimeZone_IsAccepted()
    {
        var connector = CreateConnectorWith(new FakeSqlProvider());
        var settingValues = CreateSqlServerSettingValues(useTls: false);
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseTimeZone, "UTC");

        Assert.That(connector.ValidateSettingValues(settingValues, _logger), Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_EncryptionDisabled_NeverAsksTheDriverToTrustAServerCertificate()
    {
        var provider = new FakeSqlProvider();
        var connector = CreateConnectorWith(provider);

        connector.ValidateSettingValues(CreateSqlServerSettingValues(useTls: false), _logger);

        Assert.That(provider.BuiltConnectionSettings, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(provider.BuiltConnectionSettings[0].UseTls, Is.False);
            Assert.That(provider.BuiltConnectionSettings[0].PinnedServerCertificatePath, Is.Null);
        });
    }

    [Test]
    public void ValidateSettingValues_Credentials_ReachTheProviderDecryptedAndNeverInAConnectionString()
    {
        var provider = new FakeSqlProvider();
        var connector = CreateConnectorWith(provider);
        var settingValues = CreateSqlServerSettingValues(useTls: false);

        connector.ValidateSettingValues(settingValues, _logger);

        var built = provider.BuiltConnectionSettings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(built.Username, Is.EqualTo("jim_sync"));
            Assert.That(built.Password, Is.EqualTo("sup3rs3cret"));
            Assert.That(built.ToString(), Does.Not.Contain("sup3rs3cret"),
                "The settings a provider is handed are the sort of thing that ends up in a log line, so the password stays redacted.");
        });
    }

    #endregion

    #region Helpers

    private SqlConnector CreateConnectorWith(ISqlProvider provider)
    {
        // The dialect seam is what makes a connectivity test assertable without a database server.
        _connector.Dispose();
        _connector = new SqlConnector { ProviderFactory = _ => provider };
        return _connector;
    }

    private ConnectorSetting GetSetting(string name)
    {
        var setting = _connector.GetSettings().SingleOrDefault(s => s.Name == name);
        Assert.That(setting, Is.Not.Null, $"The Connector does not declare a setting named '{name}'.");
        return setting!;
    }

    /// <summary>
    /// Materialises the Connector's declared settings the way JIM does when a Connected System is
    /// created, so a test sees the same defaults an administrator would.
    /// </summary>
    private List<ConnectedSystemSettingValue> CreateSettingValues()
    {
        return _connector.GetSettings().Select(setting =>
        {
            var definitionSetting = new ConnectorDefinitionSetting
            {
                Name = setting.Name,
                Description = setting.Description,
                Category = setting.Category,
                Type = setting.Type,
                DefaultCheckboxValue = setting.DefaultCheckboxValue,
                DefaultStringValue = setting.DefaultStringValue,
                DefaultIntValue = setting.DefaultIntValue,
                DropDownValues = setting.DropDownValues,
                Required = setting.Required,
                RequiredGroup = setting.RequiredGroup,
                RequiredGroupCardinality = setting.RequiredGroupCardinality,
                RequiredWhenSetting = setting.RequiredWhenSetting,
                RequiredWhenValue = setting.RequiredWhenValue
            };

            var settingValue = new ConnectedSystemSettingValue { Setting = definitionSetting };

            if (definitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: { } defaultCheckboxValue })
                settingValue.CheckboxValue = defaultCheckboxValue;

            if (definitionSetting.Type is ConnectedSystemSettingType.String or ConnectedSystemSettingType.DropDown or ConnectedSystemSettingType.File &&
                !string.IsNullOrEmpty(definitionSetting.DefaultStringValue))
                settingValue.StringValue = definitionSetting.DefaultStringValue;

            if (definitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: { } defaultIntValue })
                settingValue.IntValue = defaultIntValue;

            return settingValue;
        }).ToList();
    }

    /// <summary>
    /// A complete, valid Microsoft SQL Server configuration.
    /// </summary>
    private List<ConnectedSystemSettingValue> CreateSqlServerSettingValues(bool useTls)
    {
        var settingValues = CreateSettingValues();
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeSqlServer);
        SetString(settingValues, SqlConnectorConstants.SettingHost, "db.example.com");
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseName, "HR");
        SetString(settingValues, SqlConnectorConstants.SettingUsername, "jim_sync");
        SetEncrypted(settingValues, SqlConnectorConstants.SettingPassword, "sup3rs3cret");
        SetCheckbox(settingValues, SqlConnectorConstants.SettingUseTls, useTls);
        return settingValues;
    }

    private static void SetString(List<ConnectedSystemSettingValue> settingValues, string name, string? value) =>
        Find(settingValues, name).StringValue = value;

    private static void SetEncrypted(List<ConnectedSystemSettingValue> settingValues, string name, string? value) =>
        Find(settingValues, name).StringEncryptedValue = value;

    private static void SetInt(List<ConnectedSystemSettingValue> settingValues, string name, int? value) =>
        Find(settingValues, name).IntValue = value;

    private static void SetCheckbox(List<ConnectedSystemSettingValue> settingValues, string name, bool value) =>
        Find(settingValues, name).CheckboxValue = value;

    private static ConnectedSystemSettingValue Find(List<ConnectedSystemSettingValue> settingValues, string name) =>
        settingValues.Single(sv => sv.Setting.Name == name);

    private static bool IsRelevant(List<ConnectedSystemSettingValue> settingValues, string name) =>
        ConnectorSettingValidator.IsConditionMet(settingValues, Find(settingValues, name).Setting);

    private static bool IsRequired(List<ConnectedSystemSettingValue> settingValues, string name) =>
        ConnectorSettingValidator.IsSettingRequired(settingValues, Find(settingValues, name).Setting);

    #endregion
}

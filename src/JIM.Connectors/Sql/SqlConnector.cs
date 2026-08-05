// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Data.Common;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace JIM.Connectors.Sql;

/// <summary>
/// Synchronises with relational databases holding identity data: HR systems, payroll, student records
/// and line-of-business applications. Microsoft SQL Server and Oracle Database are supported, through
/// fully managed ADO.NET drivers, so nothing native is installed and JIM stays air-gap deployable.
/// Implementation plan: engineering/plans/SQL_DATABASE_CONNECTOR.md (issue #170).
/// </summary>
/// <remarks>
/// Everything a database server does differently from another one lives behind
/// <see cref="ISqlProvider"/>, so this class never branches on which server it is talking to.
/// </remarks>
public class SqlConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorImportUsingCalls, IConnectorCertificateAware, IConnectorCredentialAware, IConnectorSecureEndpoint, IConnectorPhases, IDisposable
{
    private ICertificateProvider? _certificateProvider;
    private ICredentialProtection? _credentialProtection;
    private bool _disposed;

    /// <summary>
    /// What an open import session needs. Established once by <see cref="OpenImportConnection"/> and held
    /// for the whole run, because JIM calls <see cref="ImportAsync"/> once per page against the same
    /// Connector instance; released by <see cref="CloseImportConnection"/>.
    /// </summary>
    private DbConnection? _importConnection;
    private ISqlProvider? _importProvider;
    private SqlSchemaConfiguration? _importConfiguration;
    private TimeZoneInfo _importDatabaseTimeZone = TimeZoneInfo.Utc;

    /// <summary>
    /// The server certificate this Connector has decided to accept in addition to the operating system's
    /// own trust anchors, materialised for the driver. Held for the Connector's lifetime rather than any
    /// one connection's: a Connector opens several over its life (a settings test, a schema discovery, a
    /// run), and each of them needs the file again.
    /// </summary>
    private SqlTrustedServerCertificateFile? _trustedServerCertificateFile;

    /// <summary>
    /// How a provider is obtained for a database type. Tests substitute the dialect seam here so that
    /// connection handling can be exercised without a database server; production always resolves the
    /// real provider.
    /// </summary>
    internal Func<SqlDatabaseType, ISqlProvider> ProviderFactory { get; init; } = SqlProviderFactory.Create;

    #region IConnector members
    public string Name => ConnectorConstants.SqlConnectorName;

    public string? Description => "Enables bi-directional synchronisation with relational databases, including Microsoft SQL Server and Oracle Database.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;
    public bool SupportsExport => true;

    // A database has no equivalent of a directory's naming contexts or containers: a Connected System
    // addresses one database, and object types name their own tables within it.
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;

    // References carry the referenced row's anchor, which is the external ID, so there is no second
    // identifier of the kind an LDAP directory needs a DN for.
    public bool SupportsSecondaryExternalId => false;

    // Only the administrator knows which column identifies a row for JIM's purposes: a primary key is a
    // strong suggestion, but views have none and a natural key is often the better anchor.
    public bool SupportsUserSelectedExternalId => true;

    // The catalogue states every column's type, so JIM maps them rather than asking.
    public bool SupportsUserSelectedAttributeTypes => false;

    // A committed transaction is a verified write, so an export needs no confirming import.
    public bool SupportsAutoConfirmExport => true;

    // False for the first release: parallel batches against one database can deadlock on hot pages.
    // The provider seam keeps enabling it later a capability flip rather than a reshape.
    public bool SupportsParallelExport => false;

    public bool SupportsPaging => true;

    // Connections are made over the network; nothing is read from or written to a file path.
    public bool SupportsFilePaths => false;

    // Passwords in a database are an application's own concern, stored however that application chose,
    // so there is no password channel to offer and no policy to discover.
    public bool SupportsPasswordSet => false;
    public bool SupportsPasswordPolicyDiscovery => false;

    // Column names are whatever the schema's designer chose, so no standard vocabulary applies and the
    // Attribute Flow editor matches names against every standard instead.
    #endregion

    #region IConnectorSettings members
    public List<ConnectorSetting> GetSettings()
    {
        // Settings render in declaration order, so this list is also the form an administrator fills in.
        // Conditional settings are declared after the setting that reveals them.
        return new List<ConnectorSetting>
        {
            new() { Name = "Database Server", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "Database Server Info", Description = "Enter the details of the database server to connect to. JIM builds the connection string itself from these values.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new()
            {
                Name = SqlConnectorConstants.SettingDatabaseType,
                Required = true,
                Description = "Which database server this Connected System runs on. It decides which details are asked for below, and which dialect JIM speaks.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { SqlConnectorConstants.DatabaseTypeSqlServer, SqlConnectorConstants.DatabaseTypeOracle }
            },
            new() { Name = SqlConnectorConstants.SettingHost, Required = true, Description = "The database server's hostname or IP address. For a named Microsoft SQL Server instance, use the SERVER\\INSTANCE form.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            // Deliberately optional, and deliberately without a default: the two servers listen on
            // different ports, and Oracle uses a different one again for TCPS, so a single default
            // integer would be wrong for most administrators. Left blank, the dialect's own default applies.
            new() { Name = SqlConnectorConstants.SettingPort, Required = false, Description = "The port the database server listens on. Leave blank to use the default for the database type: 1433 for Microsoft SQL Server, 1521 for Oracle Database, or 2484 for Oracle Database with encryption enabled.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
            new() { Name = SqlConnectorConstants.SettingDatabaseName, RequiredWhenSetting = SqlConnectorConstants.SettingDatabaseType, RequiredWhenValue = SqlConnectorConstants.DatabaseTypeSqlServer, Description = "The database to connect to on this server.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            // Oracle addresses a database by service name or by SID, never both. The choice is asked
            // first and each field then depends on it, so exactly one of them is ever required. A
            // RequiredGroup would express the either/or, but group validation is not gated by a
            // setting's condition, so an Oracle-only group would block saving a Microsoft SQL Server
            // Connected System.
            new()
            {
                Name = SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy,
                RequiredWhenSetting = SqlConnectorConstants.SettingDatabaseType,
                RequiredWhenValue = SqlConnectorConstants.DatabaseTypeOracle,
                Description = "How this Oracle Database is addressed. Service Name is the modern form and the one to use unless the estate still addresses the database by its System Identifier (SID).",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { SqlConnectorConstants.OracleIdentifiedByServiceName, SqlConnectorConstants.OracleIdentifiedBySid }
            },
            new() { Name = SqlConnectorConstants.SettingOracleServiceName, RequiredWhenSetting = SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, RequiredWhenValue = SqlConnectorConstants.OracleIdentifiedByServiceName, Description = "The Oracle service name, i.e. HRPROD.example.com.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = SqlConnectorConstants.SettingOracleSid, RequiredWhenSetting = SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, RequiredWhenValue = SqlConnectorConstants.OracleIdentifiedBySid, Description = "The Oracle System Identifier (SID), i.e. HRPROD.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            new() { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = SqlConnectorConstants.SettingUsername, Required = true, Description = "The database account JIM connects as. Give it the least privilege the Run Profiles need: read-only on an import-only Connected System.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = SqlConnectorConstants.SettingPassword, Required = true, Description = "The password for that database account. Stored encrypted, and never written to a log or a configuration snapshot.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            new() { Name = "Transport Security", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new()
            {
                Name = SqlConnectorConstants.SettingSqlServerEncryptConnection,
                DefaultCheckboxValue = true,
                Description = "Encrypt the connection to the database server. On by default, which is what a current SQL Server estate already does. The server's certificate is always validated: against the operating system's certificate bundle, and against any certificates added in Admin > Certificates. When a connection test fails on trust, JIM shows you the certificate the server presented so you can add it there.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.CheckBox,
                RequiredWhenSetting = SqlConnectorConstants.SettingDatabaseType,
                RequiredWhenValue = SqlConnectorConstants.DatabaseTypeSqlServer
            },
            new()
            {
                Name = SqlConnectorConstants.SettingOracleEncryption,
                DefaultStringValue = SqlConnectorConstants.DefaultOracleEncryption,
                Description = "How the connection to Oracle Database is protected. Native Network Encryption encrypts the session on the ordinary listener with no certificate at either end, and is how Oracle estates usually encrypt client traffic. TCPS is TLS, needing a separately configured listener (usually port 2484) and a server certificate; where that certificate is not one the operating system already vouches for, add it in Admin > Certificates.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues =
                [
                    SqlConnectorConstants.OracleEncryptionNativeNetworkEncryption,
                    SqlConnectorConstants.OracleEncryptionTcps,
                    SqlConnectorConstants.OracleEncryptionNone
                ],
                RequiredWhenSetting = SqlConnectorConstants.SettingDatabaseType,
                RequiredWhenValue = SqlConnectorConstants.DatabaseTypeOracle
            },
            new() { Name = SqlConnectorConstants.SettingConnectionTimeout, Required = true, Description = "How long to wait, in seconds, before giving up on trying to connect.", DefaultIntValue = SqlConnectorConstants.DefaultConnectionTimeoutSeconds, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Date and Time", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new()
            {
                Name = SqlConnectorConstants.SettingDatabaseTimeZone,
                Required = true,
                DefaultStringValue = SqlConnectorConstants.DefaultDatabaseTimeZone,
                Description = "The time zone that date and time columns carrying no offset are recorded in. JIM stores every date and time in UTC, so this is what it interprets those columns with on import, and inverts on export. Columns that do carry an offset are unaffected. Enter UTC, or an IANA time zone name such as Europe/London.",
                Category = ConnectedSystemSettingCategory.General,
                Type = ConnectedSystemSettingType.String
            },

            new() { Name = "Type Mapping", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },

            // Both opt-ins are checkboxes, which always count as having a value, so making them
            // conditional hides them for Microsoft SQL Server without ever making them required.
            new()
            {
                Name = SqlConnectorConstants.SettingTreatNumber1AsBoolean,
                RequiredWhenSetting = SqlConnectorConstants.SettingDatabaseType,
                RequiredWhenValue = SqlConnectorConstants.DatabaseTypeOracle,
                DefaultCheckboxValue = false,
                Description = "Oracle Database has no boolean column type, so flags are usually held as NUMBER(1). Turn this on if that is what NUMBER(1) columns mean in this database; leave it off and they import as numbers. It applies to every NUMBER(1) column in the schema, so only turn it on where that is true of all of them.",
                Category = ConnectedSystemSettingCategory.General,
                Type = ConnectedSystemSettingType.CheckBox
            },
            new()
            {
                Name = SqlConnectorConstants.SettingTreatRaw16AsGuid,
                RequiredWhenSetting = SqlConnectorConstants.SettingDatabaseType,
                RequiredWhenValue = SqlConnectorConstants.DatabaseTypeOracle,
                DefaultCheckboxValue = false,
                Description = "Oracle Database holds GUIDs in RAW(16) columns, but so it does digests and other binary values, and the catalogue cannot tell them apart. Turn this on if RAW(16) columns in this database hold GUIDs; leave it off and they import as binary values.",
                Category = ConnectedSystemSettingCategory.General,
                Type = ConnectedSystemSettingType.CheckBox
            },

            new() { Name = "Object Type Configuration", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.Heading },
            new()
            {
                Name = SqlConnectorConstants.SettingObjectTypes,
                Required = true,
                Description = ObjectTypesSettingDescription,
                Category = ConnectedSystemSettingCategory.Schema,
                Type = ConnectedSystemSettingType.Text
            }
        };
    }

    /// <summary>
    /// The Object Types setting's description: what the document says, and a complete example to start
    /// from. Long, deliberately. It is the only place an administrator writing the document by hand can
    /// learn its shape from inside JIM, and an empty box with no example is the worst first experience
    /// this Connector could offer.
    /// </summary>
    /// <remarks>
    /// The example itself lives in <see cref="SqlConnectorConstants.ObjectTypesExample"/> and is parsed
    /// by the Connector's own unit tests, so it cannot drift out of step with what the parser accepts.
    /// </remarks>
    private static string ObjectTypesSettingDescription =>
        "Which Connected System Object Types this database holds, and where each one's objects come from, as a JSON document. " +
        "Each object type needs a 'name' and 'anchorColumns' (the column or columns whose values identify a row), and exactly one source: " +
        "a 'table' (a table or a view, optionally qualified with a 'schema') or a 'select' statement where a view cannot be created. " +
        "'columns' declares what a column's type cannot say, which today means naming the object type a column's values point at ('referencesObjectType'); " +
        "JIM never guesses that from a foreign key, but it does suggest one where the database declares it. " +
        "'relatedTables' turns a table of one row per value into a multi-valued attribute: name the attribute, its 'valueColumn', and the 'joinColumns' " +
        "that join a row back to its parent (one per anchor column, in the same order), plus 'referencesObjectType' where those values are references, as group membership is. " +
        "Every other column is discovered and typed automatically. Example:" + Environment.NewLine + Environment.NewLine +
        SqlConnectorConstants.ObjectTypesExample;

    /// <summary>
    /// Validates SqlConnector setting values using custom business logic.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        // generic required, required-group and required-when validation is handled centrally by ConnectorSettingValidator
        // (invoked by the application layer before this method); only SQL-specific rules live here.

        var timeZoneResult = ValidateDatabaseTimeZone(settingValues);
        if (timeZoneResult != null)
            response.Add(timeZoneResult);

        var objectTypesResult = ValidateObjectTypes(settingValues);
        if (objectTypesResult != null)
            response.Add(objectTypesResult);

        // validate that we can actually reach the database with the supplied setting values
        var connectivityResult = TestDatabaseConnectivity(settingValues, logger);
        if (connectivityResult != null)
            response.Add(connectivityResult);

        return response;
    }
    #endregion

    #region IConnectorSchema members
    /// <summary>
    /// Discovers the schema of the object types this Connected System is configured for: their columns
    /// typed by the dialect's own mapping, their related tables as multi-valued attributes, and their
    /// anchors as recommended external IDs.
    /// </summary>
    /// <exception cref="SqlSchemaConfigurationException">The Object Types document is unusable, or names something the database does not have.</exception>
    /// <exception cref="InvalidSettingValuesException">A setting a connection cannot be made without is missing.</exception>
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        logger.Verbose($"GetSchemaAsync() called for {Name}");

        var provider = TryResolveProvider(settings)
            ?? throw new InvalidSettingValuesException($"Choose a {SqlConnectorConstants.SettingDatabaseType} before discovering the schema.");

        // Parsed before anything is connected to: a document that cannot be used makes the connection
        // pointless, and the administrator's problem is the document either way.
        var configuration = SqlSchemaConfiguration.Parse(GetString(settings, SqlConnectorConstants.SettingObjectTypes));
        var connectionSettings = BuildConnectionSettings(settings);

        // Schema discovery runs on a Blazor Server circuit, and opening a connection is synchronous all
        // the way down (including the certificate-trust retry), so it goes to the thread pool rather
        // than blocking the circuit. Everything after it is genuinely asynchronous.
        using var connection = await Task.Run(() => OpenConnection(provider, connectionSettings, settings, logger));

        var schema = new SqlConnectorSchema(provider, connection, configuration, BuildTypeMappingOptions(settings), logger);
        return await schema.GetSchemaAsync();
    }
    #endregion

    #region IConnectorImportUsingCalls members
    /// <summary>
    /// Opens the connection this import run reads through, and resolves everything it will need for
    /// every page: the dialect, the Object Types document, and how zoneless date and time columns are to
    /// be interpreted.
    /// </summary>
    /// <param name="persistedConnectorData">
    /// Unused by a Full Import, which reads everything there is rather than resuming from a watermark.
    /// A Delta Import is where it earns its place.
    /// </param>
    /// <exception cref="InvalidSettingValuesException">A setting a connection cannot be made without is missing or unusable.</exception>
    /// <exception cref="SqlSchemaConfigurationException">The Object Types document is unusable.</exception>
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settingValues);
        logger.Verbose($"OpenImportConnection() called for {Name}");

        var provider = TryResolveProvider(settingValues)
            ?? throw new InvalidSettingValuesException($"Choose a {SqlConnectorConstants.SettingDatabaseType} before running an import.");

        // Parsed before connecting, for the same reason schema discovery does: a document that cannot be
        // used makes the connection pointless, and the administrator's problem is the document either way.
        var configuration = SqlSchemaConfiguration.Parse(GetString(settingValues, SqlConnectorConstants.SettingObjectTypes));
        var databaseTimeZone = ResolveDatabaseTimeZone(settingValues);

        _importConnection = OpenConnection(provider, BuildConnectionSettings(settingValues), settingValues, logger);
        _importProvider = provider;
        _importConfiguration = configuration;
        _importDatabaseTimeZone = databaseTimeZone;
    }

    /// <summary>
    /// Reads one page of objects per configured Object Type, from the connection
    /// <see cref="OpenImportConnection"/> established.
    /// </summary>
    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(runProfile);
        logger.Verbose($"ImportAsync() called for {Name}");

        if (_importConnection == null || _importProvider == null || _importConfiguration == null)
            throw new InvalidOperationException("Must call OpenImportConnection() before ImportAsync()!");

        var import = new SqlConnectorImport(_importProvider, _importConnection, _importConfiguration, _importDatabaseTimeZone,
            connectedSystem, runProfile, paginationTokens, logger, cancellationToken, progress);

        return runProfile.RunType switch
        {
            ConnectedSystemRunType.FullImport => import.GetFullImportObjectsAsync(),

            // Delta Import is the next phase of this Connector's implementation plan; the capability is
            // declared because the Connector will support it, and it is not reachable until then.
            ConnectedSystemRunType.DeltaImport => throw new NotSupportedException("Delta Import is not yet implemented for the JIM SQL Connector."),
            _ => throw new InvalidDataException($"Unsupported import run-type: {runProfile.RunType}")
        };
    }

    /// <summary>
    /// Releases the import connection, whether the import succeeded or failed.
    /// </summary>
    /// <returns>
    /// Always null: a Full Import has no state for JIM to carry into the next run, and a non-null return
    /// would override state JIM already holds.
    /// </returns>
    public string? CloseImportConnection()
    {
        _importConnection?.Dispose();
        _importConnection = null;
        _importProvider = null;
        _importConfiguration = null;
        _importDatabaseTimeZone = TimeZoneInfo.Utc;

        return null;
    }
    #endregion

    #region IConnectorPhases members
    /// <summary>
    /// The steps this Connector performs, so an administrator watching an import can see where the time
    /// is going rather than one message at a time. A Full Import asks the database how many rows there
    /// are before reading any, because that answer is what turns the read into a percentage and a time
    /// remaining; a Delta Import asks what has changed instead, and then reads those rows.
    /// </summary>
    /// <remarks>
    /// Export declares nothing: it acts per object, in a transaction each, and JIM already reports
    /// accurate per-batch counts around the call, so a step would say less than the counts already do.
    /// </remarks>
    public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        return runProfile.RunType switch
        {
            ConnectedSystemRunType.FullImport =>
            [
                new ConnectorPhase(SqlConnectorPhases.Count, SqlConnectorPhases.CountName),
                new ConnectorPhase(SqlConnectorPhases.Fetch, SqlConnectorPhases.FetchName)
            ],
            ConnectedSystemRunType.DeltaImport =>
            [
                new ConnectorPhase(SqlConnectorPhases.QueryChanges, SqlConnectorPhases.QueryChangesName),
                new ConnectorPhase(SqlConnectorPhases.Fetch, SqlConnectorPhases.FetchName)
            ],
            _ => []
        };
    }
    #endregion

    #region IConnectorSecureEndpoint members
    /// <summary>
    /// The database server this system's settings connect to over an encrypted transport, so JIM can look
    /// at the certificate that server presents without any caller naming a host of their own.
    /// </summary>
    /// <returns>
    /// The endpoint, or null where this Connected System is not configured for an encrypted connection,
    /// in which case there is no certificate to look at. That null is the only thing stopping the shared
    /// certificate diagnosis path probing a server this system never connects to over TLS.
    /// </returns>
    public SecureEndpoint? ResolveSecureEndpoint(List<ConnectedSystemSettingValue> settingValues)
    {
        // Nothing encrypted is being attempted, so there is no certificate to look at.
        // Only a TLS-protected transport has a server certificate to look at. Oracle's Native Network
        // Encryption encrypts the session without one, so returning an endpoint for it would send the
        // shared diagnosis path hunting for a certificate that does not exist.
        if (ResolveEncryption(settingValues) != SqlConnectionEncryption.Tls)
            return null;

        var host = GetString(settingValues, SqlConnectorConstants.SettingHost);
        if (string.IsNullOrWhiteSpace(host))
            return null;

        // The dialect decides the port when the administrator leaves it blank, so the probe has to
        // resolve the same one a connection would have used rather than assuming a default of its own.
        var provider = TryResolveProvider(settingValues);
        if (provider == null)
            return null;

        var port = GetInt(settingValues, SqlConnectorConstants.SettingPort) ?? provider.GetDefaultPort(SqlConnectionEncryption.Tls);
        var timeoutSeconds = GetInt(settingValues, SqlConnectorConstants.SettingConnectionTimeout) ?? SqlConnectorConstants.DefaultConnectionTimeoutSeconds;

        return new SecureEndpoint(host, port, TimeSpan.FromSeconds(timeoutSeconds), "database server", provider.SecureTransportName);
    }
    #endregion

    #region IConnectorCertificateAware members
    /// <summary>
    /// Sets the certificate provider for JIM Store certificate validation.
    /// </summary>
    public void SetCertificateProvider(ICertificateProvider? certificateProvider)
    {
        _certificateProvider = certificateProvider;
    }
    #endregion

    #region IConnectorCredentialAware members
    /// <summary>
    /// Sets the credential protection service for decrypting stored passwords.
    /// </summary>
    public void SetCredentialProtection(ICredentialProtection? credentialProtection)
    {
        _credentialProtection = credentialProtection;
    }
    #endregion

    #region Connections
    /// <summary>
    /// Opens a connection to the database this Connected System is configured for, supplying JIM's own
    /// certificate store as an additional trust anchor where the driver has a way of taking one.
    /// </summary>
    /// <remarks>
    /// The order matters and is what keeps trust additive. An ordinary connection is attempted first, so
    /// the operating system's certificate bundle always gets the first say and nothing JIM does can
    /// narrow it. Only when that is refused does JIM look at what the server presented, and only a
    /// certificate that JIM's own certificate store vouches for, and that passes every check JIM makes
    /// of it (validity period, the name it was issued for), is then accepted for a second attempt.
    /// </remarks>
    /// <exception cref="ServerCertificateRejectedException">The server's certificate was refused, and this says which certificate and why.</exception>
    private DbConnection OpenConnection(ISqlProvider provider, SqlConnectionSettings connectionSettings, List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        try
        {
            return Open(provider, connectionSettings);
        }
        catch (DbException ex) when (connectionSettings.Encryption == SqlConnectionEncryption.Tls)
        {
            // A driver reports a refused certificate as an ordinary connection failure, so before
            // reporting one, go and look at what the server actually presented. No further check that
            // TLS is in use is needed here: ResolveSecureEndpoint returns nothing unless it is.
            byte[]? vouchedForCertificate = null;

            var rejection = ServerCertificateDiagnosis.Describe(this, settingValues, _certificateProvider, ex, logger,
                (endpoint, trustedCertificates) =>
                {
                    var reading = ReadServerCertificate(endpoint, trustedCertificates, logger);

                    // Sound on every count JIM checks, which leaves only one explanation for the refusal:
                    // what vouches for it is JIM's certificate store rather than the operating system's
                    // bundle, and the driver only knows about the latter.
                    if (reading is { Diagnostic.FailureReason: ServerCertificateFailureReason.None, Chain.Leaf: { } leaf } &&
                        IsVouchedForByJimCertificateStore(leaf.Data, trustedCertificates))
                        vouchedForCertificate = leaf.Data;

                    return reading?.Diagnostic;
                });

            if (rejection != null)
                throw rejection;

            // Nothing to add to what the driver already decided: no certificate JIM vouches for, no way
            // to tell this driver about one, or this attempt was already the retry.
            if (vouchedForCertificate == null || !provider.SupportsPinnedServerCertificate || connectionSettings.PinnedServerCertificatePath != null)
                throw;

            // A failure to prepare the file is reported as the connection failure it accompanies, rather
            // than replacing the driver's account of what went wrong with a filesystem one.
            if (!TryPrepareTrustedServerCertificateFile(vouchedForCertificate, logger))
                throw;

            logger.Information("The {Transport} connection to {Host} was refused by the driver, but the server's certificate is one the JIM certificate store vouches for, so it is being supplied to the driver as an additional trust anchor",
                provider.SecureTransportName, LogSanitiser.Sanitise(connectionSettings.Host));

            return Open(provider, connectionSettings with { PinnedServerCertificatePath = _trustedServerCertificateFile!.FilePath });
        }
    }

    /// <summary>
    /// Materialises the certificate for the driver, which will only take a trust anchor as a path.
    /// </summary>
    /// <returns>Whether the file is ready to be handed to a connection.</returns>
    private bool TryPrepareTrustedServerCertificateFile(byte[] derEncodedCertificate, ILogger logger)
    {
        try
        {
            _trustedServerCertificateFile?.Dispose();
            _trustedServerCertificateFile = SqlTrustedServerCertificateFile.Create(derEncodedCertificate, logger);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException or CryptographicException)
        {
            _trustedServerCertificateFile = null;
            logger.Warning(ex, "Could not prepare the server certificate from the JIM certificate store for this connection. The connection will use the operating system trust anchors only");
            return false;
        }
    }

    /// <summary>
    /// Looks at the certificate a server presents, connecting again over plain TLS purely to see it.
    /// </summary>
    /// <remarks>
    /// Overridable so the trust decision above can be exercised in tests without standing up a TLS
    /// server; production always reads the real server.
    /// </remarks>
    internal virtual ServerCertificateReading? ReadServerCertificate(SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> trustedCertificates, ILogger logger)
    {
        return ServerCertificateProbe.Read(endpoint.Host, endpoint.Port, trustedCertificates, endpoint.Timeout,
            logger, endpoint.ServerDescription, endpoint.SecureTransportName);
    }

    /// <summary>
    /// Opens one connection, leaving nothing behind when it fails.
    /// </summary>
    private static DbConnection Open(ISqlProvider provider, SqlConnectionSettings connectionSettings)
    {
        var connection = provider.CreateConnection(provider.BuildConnectionString(connectionSettings));

        try
        {
            // Anything the dialect cannot express in a connection string is applied here, while the
            // connection is built but not yet open. Oracle's Native Network Encryption is the case that
            // needs it: without this the connection string would be correct and the session unencrypted.
            provider.ConfigureConnection(connection, connectionSettings);

            connection.Open();
            return connection;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or SocketException or IOException)
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the certificate a server presented chains to something an administrator added in
    /// Admin &gt; Certificates.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than "would this validate": the operating system's own anchors are
    /// excluded, because a certificate they already vouch for and that a driver still refused was
    /// refused for some other reason, and accepting it anyway would waive that reason.
    /// </remarks>
    private static bool IsVouchedForByJimCertificateStore(byte[] derEncodedCertificate, IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        if (trustedCertificates.Count == 0)
            return false;

        using var certificate = X509CertificateLoader.LoadCertificate(derEncodedCertificate);
        using var chain = new X509Chain();

        // Air-gapped deployments cannot reach a revocation list or responder, matching the connection itself.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;

        foreach (var trustedCertificate in trustedCertificates)
        {
            chain.ChainPolicy.CustomTrustStore.Add(trustedCertificate);
            chain.ChainPolicy.ExtraStore.Add(trustedCertificate);
        }

        return chain.Build(certificate);
    }
    #endregion

    #region private methods
    /// <summary>
    /// Resolves the provider for the configured database type, or null where no usable type is set.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, for the callers that answer a question rather than doing work:
    /// a half-configured Connected System has no dialect yet, and that is an answer, not a failure.
    /// </remarks>
    private ISqlProvider? TryResolveProvider(List<ConnectedSystemSettingValue> settingValues)
    {
        var databaseType = GetString(settingValues, SqlConnectorConstants.SettingDatabaseType) switch
        {
            SqlConnectorConstants.DatabaseTypeSqlServer => SqlDatabaseType.SqlServer,
            SqlConnectorConstants.DatabaseTypeOracle => SqlDatabaseType.Oracle,
            _ => SqlDatabaseType.NotSet
        };

        return databaseType == SqlDatabaseType.NotSet ? null : ProviderFactory(databaseType);
    }

    /// <summary>
    /// Turns the discrete settings an administrator supplied into what a provider needs to build its own
    /// connection string. The password is decrypted here and goes no further than the provider.
    /// </summary>
    /// <exception cref="InvalidSettingValuesException">A setting a connection cannot be made without is missing.</exception>
    private SqlConnectionSettings BuildConnectionSettings(List<ConnectedSystemSettingValue> settingValues)
    {
        var host = GetString(settingValues, SqlConnectorConstants.SettingHost);
        var username = GetString(settingValues, SqlConnectorConstants.SettingUsername);
        var password = GetEncrypted(settingValues, SqlConnectorConstants.SettingPassword);

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidSettingValuesException(
                $"Missing setting values for {SqlConnectorConstants.SettingHost}, {SqlConnectorConstants.SettingUsername} or {SqlConnectorConstants.SettingPassword}.");

        // Decrypt the password if credential protection is available. If it is not, or the value is
        // plain text, it is used as-is.
        var decryptedPassword = _credentialProtection?.Unprotect(password) ?? password;

        var identifiedBy = GetString(settingValues, SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy);

        return new SqlConnectionSettings
        {
            Host = host,
            Port = GetInt(settingValues, SqlConnectorConstants.SettingPort),
            DatabaseName = GetString(settingValues, SqlConnectorConstants.SettingDatabaseName),

            // Only ever one of these is configured: the identification question decides which field the
            // administrator was asked for, so reading both would risk sending a stale value.
            ServiceName = identifiedBy == SqlConnectorConstants.OracleIdentifiedByServiceName ? GetString(settingValues, SqlConnectorConstants.SettingOracleServiceName) : null,
            Sid = identifiedBy == SqlConnectorConstants.OracleIdentifiedBySid ? GetString(settingValues, SqlConnectorConstants.SettingOracleSid) : null,
            Username = username,
            Password = decryptedPassword,
            Encryption = ResolveEncryption(settingValues),
            ConnectionTimeoutSeconds = GetInt(settingValues, SqlConnectorConstants.SettingConnectionTimeout) ?? SqlConnectorConstants.DefaultConnectionTimeoutSeconds
        };
    }

    /// <summary>
    /// The time zone a zoneless date and time column is recorded in, as the administrator declared it.
    /// Validated when the Connected System is saved, so a failure here means the setting changed to
    /// something this deployment does not know since; the run fails rather than silently taking UTC and
    /// moving every date by the offset.
    /// </summary>
    /// <exception cref="InvalidSettingValuesException">The configured time zone is not one this deployment recognises.</exception>
    private static TimeZoneInfo ResolveDatabaseTimeZone(List<ConnectedSystemSettingValue> settingValues)
    {
        var timeZone = GetString(settingValues, SqlConnectorConstants.SettingDatabaseTimeZone);

        if (string.IsNullOrWhiteSpace(timeZone))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidSettingValuesException(
                $"{SqlConnectorConstants.SettingDatabaseTimeZone} '{timeZone}' is not a time zone this deployment recognises, so date and time columns carrying no offset cannot be interpreted.");
        }
    }

    /// <summary>
    /// Works out how this Connected System's traffic is protected, from whichever encryption setting its
    /// database type asks for.
    /// <para>
    /// The two are asked separately because the answers are not the same shape: SQL Server has one
    /// encrypted transport and so takes a checkbox, while Oracle has two unrelated mechanisms whose
    /// choice decides which listener and port the connection goes to. Both default to encrypted, so a
    /// Connected System is never quietly the least protected thing on the network.
    /// </para>
    /// </summary>
    private static SqlConnectionEncryption ResolveEncryption(List<ConnectedSystemSettingValue> settingValues)
    {
        var databaseType = GetString(settingValues, SqlConnectorConstants.SettingDatabaseType);

        if (databaseType == SqlConnectorConstants.DatabaseTypeOracle)
        {
            // A Connected System created before this setting existed has no stored value, so an absent
            // one means the default rather than no encryption.
            var oracleEncryption = GetString(settingValues, SqlConnectorConstants.SettingOracleEncryption);
            if (string.IsNullOrWhiteSpace(oracleEncryption))
                oracleEncryption = SqlConnectorConstants.DefaultOracleEncryption;

            return oracleEncryption switch
            {
                SqlConnectorConstants.OracleEncryptionTcps => SqlConnectionEncryption.Tls,
                SqlConnectorConstants.OracleEncryptionNone => SqlConnectionEncryption.None,
                _ => SqlConnectionEncryption.OracleNativeNetworkEncryption
            };
        }

        // Microsoft SQL Server, and the fallback while a database type has not been chosen yet: encrypted
        // unless an administrator has deliberately cleared the checkbox.
        return GetCheckbox(settingValues, SqlConnectorConstants.SettingSqlServerEncryptConnection) == false
            ? SqlConnectionEncryption.None
            : SqlConnectionEncryption.Tls;
    }

    /// <summary>
    /// Checks that the time zone an administrator named is one this deployment knows, because a zoneless
    /// date and time column cannot be interpreted without it and a typo would otherwise only surface
    /// mid-import.
    /// </summary>
    private static ConnectorSettingValueValidationResult? ValidateDatabaseTimeZone(List<ConnectedSystemSettingValue> settingValues)
    {
        var settingValue = settingValues.SingleOrDefault(sv => sv.Setting.Name == SqlConnectorConstants.SettingDatabaseTimeZone);
        var timeZone = settingValue?.StringValue;

        // Required, so a missing value is already reported by the generic validator; there is nothing
        // here to add to that.
        if (settingValue == null || string.IsNullOrWhiteSpace(timeZone))
            return null;

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return null;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"{SqlConnectorConstants.SettingDatabaseTimeZone} '{timeZone}' is not a time zone this deployment recognises. Enter UTC, or an IANA time zone name such as Europe/London or America/New_York.",
                SettingValue = settingValue,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Checks that the Object Types document can be used, so that a Connected System is never saved
    /// with configuration that schema discovery would then refuse. A missing document is left alone:
    /// the setting is required, and the generic validator has already said so.
    /// </summary>
    private static ConnectorSettingValueValidationResult? ValidateObjectTypes(List<ConnectedSystemSettingValue> settingValues)
    {
        var settingValue = settingValues.SingleOrDefault(sv => sv.Setting.Name == SqlConnectorConstants.SettingObjectTypes);
        if (settingValue == null || string.IsNullOrWhiteSpace(settingValue.StringValue))
            return null;

        try
        {
            SqlSchemaConfiguration.Parse(settingValue.StringValue);
            return null;
        }
        catch (SqlSchemaConfigurationException ex)
        {
            return new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = ex.Message,
                SettingValue = settingValue,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// The per-Connected System choices that change how a column's SQL type maps onto a JIM attribute
    /// type. Both are Oracle-only opt-ins, and both are off unless an administrator turned them on.
    /// </summary>
    private static SqlTypeMappingOptions BuildTypeMappingOptions(List<ConnectedSystemSettingValue> settingValues)
    {
        return new SqlTypeMappingOptions
        {
            TreatSingleDigitNumberAsBoolean = GetCheckbox(settingValues, SqlConnectorConstants.SettingTreatNumber1AsBoolean) ?? false,
            TreatRaw16AsGuid = GetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid) ?? false
        };
    }

    /// <summary>
    /// Opens a connection and runs the dialect's cheapest statement on it, so that configuration which
    /// cannot reach its database is never saved.
    /// </summary>
    /// <returns>The problem to report, or null where the database answered.</returns>
    private ConnectorSettingValueValidationResult? TestDatabaseConnectivity(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        try
        {
            var provider = TryResolveProvider(settingValues);
            if (provider == null)
                return Failure(settingValues, SqlConnectorConstants.SettingDatabaseType,
                    $"Choose a {SqlConnectorConstants.SettingDatabaseType} before testing connectivity.");

            var connectionSettings = BuildConnectionSettings(settingValues);

            using var connection = OpenConnection(provider, connectionSettings, settingValues, logger);
            using var command = provider.CreateCommand(connection, provider.ConnectivityTestCommandText);
            command.ExecuteScalar();

            return null;
        }
        catch (InvalidSettingValuesException)
        {
            return Failure(settingValues, SqlConnectorConstants.SettingHost,
                $"Unable to test connectivity until {SqlConnectorConstants.SettingHost}, {SqlConnectorConstants.SettingUsername} and {SqlConnectorConstants.SettingPassword} have been supplied.");
        }
        catch (ServerCertificateRejectedException ex)
        {
            // Already carries the certificate and what to do about it, which is what the portal renders.
            return Failure(settingValues, SqlConnectorConstants.SettingHost, ex.Message, ex);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or NotSupportedException or SocketException or IOException or TimeoutException)
        {
            // The driver's own account of the failure is what an administrator needs (a refused login, an
            // unknown database, an unreachable host all read differently), and it never contains the
            // credential: JIM hands the password to the driver's connection string builder and nowhere else.
            logger.Error(ex, "TestDatabaseConnectivity failed");
            return Failure(settingValues, SqlConnectorConstants.SettingHost, $"Unable to connect. Message: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds a failed validation result, attached to the setting the problem belongs to where there is one.
    /// </summary>
    private static ConnectorSettingValueValidationResult Failure(List<ConnectedSystemSettingValue> settingValues, string settingName, string errorMessage, Exception? exception = null)
    {
        return new ConnectorSettingValueValidationResult
        {
            IsValid = false,
            ErrorMessage = errorMessage,
            SettingValue = settingValues.SingleOrDefault(sv => sv.Setting.Name == settingName),
            Exception = exception
        };
    }

    private static string? GetString(List<ConnectedSystemSettingValue> settingValues, string settingName) =>
        settingValues.SingleOrDefault(sv => sv.Setting.Name == settingName)?.StringValue;

    private static string? GetEncrypted(List<ConnectedSystemSettingValue> settingValues, string settingName) =>
        settingValues.SingleOrDefault(sv => sv.Setting.Name == settingName)?.StringEncryptedValue;

    private static int? GetInt(List<ConnectedSystemSettingValue> settingValues, string settingName) =>
        settingValues.SingleOrDefault(sv => sv.Setting.Name == settingName)?.IntValue;

    private static bool? GetCheckbox(List<ConnectedSystemSettingValue> settingValues, string settingName) =>
        settingValues.SingleOrDefault(sv => sv.Setting.Name == settingName)?.CheckboxValue;
    #endregion

    #region IDisposable members
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Belt and braces: the Worker closes an import connection explicitly, but a Connector
            // disposed without that must not leave a session open on a customer's database.
            _importConnection?.Dispose();
            _importConnection = null;

            _trustedServerCertificateFile?.Dispose();
            _trustedServerCertificateFile = null;
        }

        _disposed = true;
    }
    #endregion
}

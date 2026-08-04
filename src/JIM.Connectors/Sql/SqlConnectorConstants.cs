// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// Setting names, drop-down values and defaults for the JIM SQL Connector. Centralised so the
/// Connector, its collaborators and its tests all reference the same identifiers.
/// </summary>
/// <remarks>
/// A setting's name is its identity: setting values are matched to settings by name, and the name is
/// what an administrator reads in the portal, the REST API and PowerShell. Renaming one orphans the
/// values already supplied for it, so these are as permanent as any other stored identifier.
/// </remarks>
public static class SqlConnectorConstants
{
    #region Connectivity settings

    public const string SettingDatabaseType = "Database Type";

    public const string SettingHost = "Host";

    public const string SettingPort = "Port";

    public const string SettingDatabaseName = "Database Name";

    public const string SettingOracleDatabaseIdentifiedBy = "Oracle Database Identified By";

    public const string SettingOracleServiceName = "Oracle Service Name";

    public const string SettingOracleSid = "Oracle SID";

    public const string SettingUsername = "Username";

    public const string SettingPassword = "Password";

    public const string SettingUseTls = "Encrypt Connection";

    public const string SettingConnectionTimeout = "Connection Timeout";

    #endregion

    #region General settings

    public const string SettingDatabaseTimeZone = "Database Time Zone";

    public const string SettingTreatNumber1AsBoolean = "Treat NUMBER(1) Columns as Boolean";

    public const string SettingTreatRaw16AsGuid = "Treat RAW(16) Columns as Guid";

    #endregion

    #region Database Type drop-down values

    public const string DatabaseTypeSqlServer = "Microsoft SQL Server";

    public const string DatabaseTypeOracle = "Oracle Database";

    #endregion

    #region Oracle Database Identified By drop-down values

    public const string OracleIdentifiedByServiceName = "Service Name";

    public const string OracleIdentifiedBySid = "SID";

    #endregion

    #region Defaults

    /// <summary>
    /// Long enough to absorb a busy server or a slow network, short enough that an administrator
    /// testing a wrong host is not left waiting.
    /// </summary>
    public const int DefaultConnectionTimeoutSeconds = 30;

    /// <summary>
    /// How date and time columns carrying no offset are interpreted unless an administrator says
    /// otherwise. UTC is the only defensible default: it is what JIM stores in, and it is the one
    /// answer that never silently shifts a value by an hour twice a year.
    /// </summary>
    public const string DefaultDatabaseTimeZone = "UTC";

    #endregion
}

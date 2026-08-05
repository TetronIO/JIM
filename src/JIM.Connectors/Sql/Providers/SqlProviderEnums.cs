// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The database servers the JIM SQL Connector can address. Priority 1 members ship first; Priority 2
/// members (PostgreSQL, MySQL/MariaDB) are additive behind <see cref="ISqlProvider"/>.
/// </summary>
internal enum SqlDatabaseType
{
    NotSet = 0,
    SqlServer = 1,
    Oracle = 2
}

/// <summary>
/// How a connection to a database server is protected in transit.
/// <para>
/// The distinction that matters here is not "encrypted or not" but "TLS or not": only a TLS transport
/// presents a server certificate, and only a TLS transport therefore has anything for JIM's certificate
/// diagnosis path to look at. Oracle's Native Network Encryption encrypts the session without a
/// certificate on either side, so it sits apart from <see cref="Tls"/> rather than alongside it.
/// </para>
/// </summary>
internal enum SqlConnectionEncryption
{
    /// <summary>
    /// JIM does not require the connection to be encrypted. It is not a refusal: a server that insists
    /// on encryption is still obliged, this simply declines to make it a condition of connecting.
    /// </summary>
    None = 0,

    /// <summary>
    /// TLS. A Microsoft SQL Server connection with encryption required, or an Oracle Database connection
    /// over TCPS. The server presents a certificate, which JIM validates and reports on when refused.
    /// </summary>
    Tls = 1,

    /// <summary>
    /// Oracle Native Network Encryption: the Oracle Advanced Networking encryption negotiated inside the
    /// Oracle Net session, on the ordinary listener, with no certificate anywhere. It is how Oracle
    /// estates ordinarily encrypt client traffic, TCPS being comparatively rare because it needs a
    /// certificate and a separately configured listener.
    /// </summary>
    OracleNativeNetworkEncryption = 2
}

/// <summary>
/// How a database hands back the key it generated for an inserted row.
/// </summary>
internal enum SqlGeneratedKeyRetrieval
{
    /// <summary>
    /// The insert statement returns the key as a single-row result set (SQL Server's OUTPUT clause).
    /// </summary>
    ResultSet = 0,

    /// <summary>
    /// The insert statement writes the key into a bound output parameter (Oracle's RETURNING ... INTO).
    /// </summary>
    OutputParameter = 1
}

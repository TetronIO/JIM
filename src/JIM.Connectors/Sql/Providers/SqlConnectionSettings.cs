// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The discrete Connectivity settings an administrator supplies, from which a provider builds its own
/// connection string. JIM never asks for a connection string directly: hand-written strings hide
/// credentials in plain text and let a deployment quietly disable encryption.
/// <para>
/// <see cref="ToString"/> is overridden to redact the password. A record's generated ToString would
/// otherwise print every member, and this type is exactly the sort of thing that ends up interpolated
/// into an exception message or a debug log line.
/// </para>
/// </summary>
internal sealed record SqlConnectionSettings
{
    /// <summary>
    /// Host name or address of the database server.
    /// </summary>
    internal required string Host { get; init; }

    /// <summary>
    /// Listener port. Null means the provider's default for the chosen transport.
    /// </summary>
    internal int? Port { get; init; }

    /// <summary>
    /// The database (initial catalog) to connect to. Used by SQL Server.
    /// </summary>
    internal string? DatabaseName { get; init; }

    /// <summary>
    /// The Oracle service name. Mutually exclusive with <see cref="Sid"/>.
    /// </summary>
    internal string? ServiceName { get; init; }

    /// <summary>
    /// The Oracle System Identifier, for estates that still address databases that way.
    /// </summary>
    internal string? Sid { get; init; }

    internal string? Username { get; init; }

    /// <summary>
    /// Held only long enough to build a connection string. Persisted encrypted, never logged.
    /// </summary>
    internal string? Password { get; init; }

    /// <summary>
    /// Whether the connection must be encrypted in transit. Server certificate trust is resolved
    /// against the operating system bundle plus Admin &gt; Certificates; there is deliberately no
    /// blanket trust-server-certificate option.
    /// </summary>
    internal bool UseTls { get; init; }

    internal int? ConnectionTimeoutSeconds { get; init; }

    public override string ToString()
    {
        return $"{nameof(SqlConnectionSettings)} {{ Host = {Host}, Port = {Port?.ToString() ?? "(default)"}, DatabaseName = {DatabaseName}, ServiceName = {ServiceName}, Sid = {Sid}, Username = {Username}, Password = (redacted), UseTls = {UseTls}, ConnectionTimeoutSeconds = {ConnectionTimeoutSeconds} }}";
    }
}

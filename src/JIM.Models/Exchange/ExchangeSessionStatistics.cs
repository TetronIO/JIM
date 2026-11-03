// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// What one Exchange session has cost (#2021, PRD requirement 27), for the delivery lane's summary: a healthy
/// session serves a whole batch over one Kerberos login and a handful of connections.
/// </summary>
public sealed record ExchangeSessionStatistics
{
    /// <summary>Kerberos logins made, the first included.</summary>
    public int KerberosLogins { get; init; }

    /// <summary>Connections opened to the Exchange server.</summary>
    public int Connections { get; init; }

    /// <summary>Commands run in the session.</summary>
    public int Commands { get; init; }

    /// <summary>A session that never opened.</summary>
    public static ExchangeSessionStatistics None { get; } = new();
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;

namespace JIM.Connectors.Sql;

/// <summary>
/// Thrown when a connection could not be opened and the driver's own message hides the cause, so the
/// message here names it (see <see cref="Providers.ISqlProvider.DescribeConnectionFailure"/>). The
/// driver's exception is the inner exception.
/// <para>
/// A <see cref="DbException"/> deliberately: every caller of an open already handles the driver's own
/// failure as one, and a failure to connect reads the same whichever of the two carries it.
/// </para>
/// </summary>
internal sealed class SqlConnectionFailedException : DbException
{
    internal SqlConnectionFailedException(string message, DbException innerException) : base(message, innerException)
    {
    }
}

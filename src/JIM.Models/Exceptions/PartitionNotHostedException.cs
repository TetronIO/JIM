// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// Thrown when an AD/Samba AD import is configured against one or more Partitions that the connected
/// domain controller does not host. AD's crossRef-based partition discovery (CN=Partitions,
/// CN=Configuration) lists every domain in the forest, including domains the connected domain controller
/// does not hold a naming context for; a domain controller does not chase referrals, so an import against
/// a foreign partition would otherwise silently return zero objects.
/// </summary>
public class PartitionNotHostedException : OperationalException
{
    public PartitionNotHostedException(string message) : base(message) { }
}

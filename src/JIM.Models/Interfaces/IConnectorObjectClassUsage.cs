// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

/// <summary>
/// Implement this where your Connected System attaches classes to individual objects rather than declaring them in
/// its schema, so that JIM can find out which ones are actually in use.
/// </summary>
/// <remarks>
/// This exists because an RFC 4512 directory's schema cannot answer the question. An auxiliary class's definition
/// says what attributes it contributes, never which entries carry it, so the only source of truth is the entries.
/// <para>
/// What you return are suggestions. JIM never changes a Connected System's schema from them; they narrow what the
/// portal offers an administrator, who decides what JIM actually manages.
/// </para>
/// </remarks>
public interface IConnectorObjectClassUsage
{
    /// <summary>
    /// Reads objects of one type and counts the classes they carry.
    /// </summary>
    /// <remarks>
    /// Request only the attribute that names the classes. This may run over an entire population, and the
    /// difference between reading one attribute and reading whole objects is the difference between a task an
    /// administrator will run and one they will cancel.
    /// <para>
    /// Honour cancellation by stopping and returning what you have, with
    /// <see cref="ObjectClassUsageResult.Partial"/> set: a cancelled sample that reports its partial findings is
    /// useful, and one that throws away an hour of reading is not.
    /// </para>
    /// </remarks>
    /// <param name="connectedSystem">The Connected System being sampled, including its schema and containers.</param>
    /// <param name="request">Which type to sample, how many objects to stop at, and what page size to use.</param>
    /// <param name="logger">Use this log to record information in the JIM logs.</param>
    /// <param name="cancellationToken">Check this between pages; an administrator can cancel the task at any point.</param>
    /// <param name="progress">Narrates what you are doing and how many objects you have read, for the Activity an administrator is watching.</param>
    public Task<ObjectClassUsageResult> ReadObjectClassUsageAsync(
        ConnectedSystem connectedSystem,
        ObjectClassUsageRequest request,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress);
}

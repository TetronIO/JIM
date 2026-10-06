// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Worker.UniqueValues;

/// <summary>
/// What a <see cref="UniquenessProbeSession"/> needs from the worker (Unique Value Generation, #242, release 3): the
/// Connected Systems it probes, a Connector to probe each with, and the control values JIM holds. Owned and disposed
/// by the session, or by the synchronisation run when it never needed one.
/// </summary>
public interface IUniquenessProbeSessionHost : IAsyncDisposable
{
    /// <summary>
    /// The Connected System with everything a probe needs: its Connector Definition, setting values, selected
    /// partitions, and object types with their attributes. Null when it does not exist.
    /// </summary>
    public Task<ConnectedSystem?> LoadConnectedSystemAsync(int connectedSystemId);

    /// <summary>
    /// A new Connector instance for <paramref name="connectedSystem"/>, configured with credential protection and the
    /// JIM certificate store exactly as the import and export paths configure theirs.
    /// </summary>
    public IConnector CreateConnector(ConnectedSystem connectedSystem);

    /// <summary>
    /// Up to <paramref name="maximumCount"/> values Connected System Objects in the target already hold for the
    /// attribute, for the session to choose a control value from.
    /// </summary>
    public Task<IReadOnlyList<string>> GetControlValuesAsync(int connectedSystemObjectTypeAttributeId, int maximumCount);
}

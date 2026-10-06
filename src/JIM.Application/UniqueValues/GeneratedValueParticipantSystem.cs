// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// What <see cref="GeneratedValueParticipation.DescribeParticipants"/> needs to know about one Connected System a
/// generated value may be exported to (Unique Value Generation, #242, release 3): its names, and what its Connector
/// says about probing. Kept a plain description, so the participation rules stay a pure function the panel, the REST
/// API and Sync Preview all share; the caller decides how to answer it (the application layer asks a Connector
/// created through the Connector factory, without opening a connection).
/// </summary>
/// <param name="Id">The Connected System's id.</param>
/// <param name="Name">The Connected System's name.</param>
/// <param name="ConnectorName">The Connector's name.</param>
/// <param name="ConnectorCanProbe">Whether the Connector declares and implements the uniqueness probe.</param>
/// <param name="CanProbeAttribute">The Connector's own answer to whether a system-wide search for the named attribute
/// means anything. Only asked when <paramref name="ConnectorCanProbe"/> is true.</param>
public sealed record GeneratedValueParticipantSystem(
    int Id,
    string Name,
    string ConnectorName,
    bool ConnectorCanProbe,
    Func<string, bool> CanProbeAttribute);

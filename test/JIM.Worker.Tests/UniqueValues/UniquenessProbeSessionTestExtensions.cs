// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Worker.UniqueValues;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Probing one attribute through a session, for tests about a single attribute's behaviour.
/// </summary>
internal static class UniquenessProbeSessionTestExtensions
{
    public static async Task<UniquenessProbeSessionResult> ProbeAsync(this UniquenessProbeSession session, UniquenessProbeTarget target, IReadOnlyList<string> candidates) =>
        (await session.ProbeAttributesAsync(target.ConnectedSystemId, [new UniquenessProbeBatch(target.ConnectedSystemObjectTypeAttributeId, candidates)]))[0];
}

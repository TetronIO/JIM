// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// One call <see cref="RecordingSyncRepository"/> recorded: the method's name, how many items each collection argument held
/// when the call was made (null for an argument that is not a collection), and the ids each argument of ids named.
/// </summary>
public sealed record RecordedSyncRepositoryCall(string Method, IReadOnlyList<int?> CollectionCounts, IReadOnlyList<List<Guid>?> Ids);

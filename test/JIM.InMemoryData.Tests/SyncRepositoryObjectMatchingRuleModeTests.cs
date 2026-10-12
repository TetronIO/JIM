// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// Covers GetObjectMatchingRuleModeAsync, which Sync Preview reads to make the join decision the run makes (#2176): in
/// simple mode the Connected System Object Type's own Object Matching Rules apply, in advanced mode only each import
/// Synchronisation Rule's.
/// </summary>
[TestFixture]
public class SyncRepositoryObjectMatchingRuleModeTests
{
    [TestCase(ObjectMatchingRuleMode.ConnectedSystem)]
    [TestCase(ObjectMatchingRuleMode.SyncRule)]
    public async Task GetObjectMatchingRuleModeAsync_SeededSystem_ReturnsItsModeAsync(ObjectMatchingRuleMode mode)
    {
        var repo = new SyncRepository();
        repo.SeedConnectedSystem(new ConnectedSystem { Id = 4, Name = "Corporate Directory", ObjectMatchingRuleMode = mode });

        Assert.That(await repo.GetObjectMatchingRuleModeAsync(4), Is.EqualTo(mode));
    }

    [Test]
    public async Task GetObjectMatchingRuleModeAsync_UnknownSystem_ReturnsNullAsync()
    {
        Assert.That(await new SyncRepository().GetObjectMatchingRuleModeAsync(99), Is.Null);
    }
}

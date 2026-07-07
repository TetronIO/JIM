// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// Covers GetLatestSyncRuleConfigurationChangeAsync, the configuration watermark a Full Synchronisation
/// compares against its last completed sync to decide whether the unchanged-object optimisation must be
/// disabled for the run (a configuration change must reach every object, not just objects whose source
/// data changed; see SyncFullSyncTaskProcessor).
/// </summary>
[TestFixture]
public class SyncRepositoryConfigurationChangeTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    [Test]
    public async Task GetLatestSyncRuleConfigurationChangeAsync_NoRules_ReturnsNullAsync()
    {
        var result = await _repo.GetLatestSyncRuleConfigurationChangeAsync();

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetLatestSyncRuleConfigurationChangeAsync_RulesOnly_ReturnsLatestRuleTimestampAsync()
    {
        _repo.SeedSyncRule(new SyncRule { Id = 1, Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        _repo.SeedSyncRule(new SyncRule
        {
            Id = 2,
            Created = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            LastUpdated = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var result = await _repo.GetLatestSyncRuleConfigurationChangeAsync();

        Assert.That(result, Is.EqualTo(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task GetLatestSyncRuleConfigurationChangeAsync_MappingNewerThanRule_ReturnsMappingTimestampAsync()
    {
        // An attribute priority reorder or "Null is a value" change stamps the MAPPING's LastUpdated,
        // not the rule's, so the watermark must consider mappings too.
        var rule = new SyncRule
        {
            Id = 1,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastUpdated = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 10,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastUpdated = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _repo.SeedSyncRule(rule);

        var result = await _repo.GetLatestSyncRuleConfigurationChangeAsync();

        Assert.That(result, Is.EqualTo(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task GetLatestSyncRuleConfigurationChangeAsync_NeverUpdated_FallsBackToCreatedAsync()
    {
        // A newly created mapping has no LastUpdated yet; its creation is itself a configuration change.
        var rule = new SyncRule { Id = 1, Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 10,
            Created = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _repo.SeedSyncRule(rule);

        var result = await _repo.GetLatestSyncRuleConfigurationChangeAsync();

        Assert.That(result, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}

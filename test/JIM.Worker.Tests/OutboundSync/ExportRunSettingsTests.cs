// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using Moq;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// The settings an export run reads once rather than per batch (#2895): which Synchronisation Rules ask for an Initial
/// Password, and the data type of the attributes external IDs are recorded in. Each is read the first time a batch needs
/// it, through that batch's repository, and remembered for the run; only what has not been read yet is read.
/// </summary>
[TestFixture]
public class ExportRunSettingsTests
{
    [Test]
    public async Task GetRulesAskingForAnInitialPasswordAsync_SameRulesTwice_ReadsOnceAndAnswersTheSameAsync()
    {
        var repository = new Mock<ISyncRepository>();
        repository.Setup(r => r.GetSyncRuleIdsWithInitialPasswordEnabledAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync([1]);
        using var settings = new ExportRunSettings();

        var first = await settings.GetRulesAskingForAnInitialPasswordAsync([1, 2], repository.Object);
        var second = await settings.GetRulesAskingForAnInitialPasswordAsync([2, 1], repository.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EquivalentTo(new[] { 1 }));
            Assert.That(second, Is.EquivalentTo(first));
            repository.Verify(r => r.GetSyncRuleIdsWithInitialPasswordEnabledAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
        }
    }

    [Test]
    public async Task GetRulesAskingForAnInitialPasswordAsync_NewRuleLater_ReadsOnlyTheNewRuleAsync()
    {
        var repository = new Mock<ISyncRepository>();
        var asked = new List<int[]>();
        repository.Setup(r => r.GetSyncRuleIdsWithInitialPasswordEnabledAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids) =>
            {
                asked.Add([.. ids.Order()]);
                return ids.Where(id => id == 3).ToHashSet();
            });
        using var settings = new ExportRunSettings();

        await settings.GetRulesAskingForAnInitialPasswordAsync([1], repository.Object);
        var answer = await settings.GetRulesAskingForAnInitialPasswordAsync([1, 3], repository.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer, Is.EquivalentTo(new[] { 3 }));
            Assert.That(asked, Is.EqualTo(new[] { new[] { 1 }, new[] { 3 } }));
        }
    }

    [Test]
    public async Task GetRulesAskingForAnInitialPasswordAsync_ReadFails_IsNotRememberedAsync()
    {
        var repository = new Mock<ISyncRepository>();
        repository.SetupSequence(r => r.GetSyncRuleIdsWithInitialPasswordEnabledAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ThrowsAsync(new InvalidOperationException("The database could not be reached."))
            .ReturnsAsync([1]);
        using var settings = new ExportRunSettings();

        Assert.That(async () => await settings.GetRulesAskingForAnInitialPasswordAsync([1], repository.Object), Throws.InvalidOperationException);
        var answer = await settings.GetRulesAskingForAnInitialPasswordAsync([1], repository.Object);

        Assert.That(answer, Is.EquivalentTo(new[] { 1 }), "the next batch reads again and gets the answer");
    }

    /// <summary>
    /// Parallel export workers start their first batches together; the first to ask reads, the other waits for it.
    /// </summary>
    [Test]
    public async Task GetRulesAskingForAnInitialPasswordAsync_TwoWorkersAtOnce_ReadsOnceAsync()
    {
        var repository = new Mock<ISyncRepository>();
        var release = new TaskCompletionSource<HashSet<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(r => r.GetSyncRuleIdsWithInitialPasswordEnabledAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .Returns(release.Task);
        using var settings = new ExportRunSettings();

        var first = settings.GetRulesAskingForAnInitialPasswordAsync([1], repository.Object);
        var second = settings.GetRulesAskingForAnInitialPasswordAsync([1], repository.Object);
        release.SetResult([1]);
        var answers = await Task.WhenAll(first, second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers.Select(a => a.Single()), Is.All.EqualTo(1));
            repository.Verify(r => r.GetSyncRuleIdsWithInitialPasswordEnabledAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
        }
    }

    [Test]
    public async Task GetAttributeTypesAsync_SameAttributesTwice_ReadsOnceAndAnswersTheSameAsync()
    {
        var repository = new Mock<ISyncRepository>();
        repository.Setup(r => r.GetAttributesByIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => ids.Where(id => id != 99).ToDictionary(id => id,
                id => new ConnectedSystemObjectTypeAttribute { Id = id, Name = $"a{id}", Type = id == 1 ? AttributeDataType.Number : AttributeDataType.Text }));
        using var settings = new ExportRunSettings();

        var first = await settings.GetAttributeTypesAsync([1, 2, 99], repository.Object);
        var second = await settings.GetAttributeTypesAsync([99, 2, 1], repository.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first[1], Is.EqualTo(AttributeDataType.Number));
            Assert.That(first[2], Is.EqualTo(AttributeDataType.Text));
            Assert.That(first.ContainsKey(99), Is.False, "an attribute that does not exist has no type");
            Assert.That(second, Is.EquivalentTo(first));
            repository.Verify(r => r.GetAttributesByIdsAsync(It.IsAny<IEnumerable<int>>()), Times.Once,
                "an attribute found not to exist is remembered too");
        }
    }
}

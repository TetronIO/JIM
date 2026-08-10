// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Services;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What the portal asks when it needs to say "this one needs you" (#1221 items 4 and 5): how many accounts are
/// waiting on a person, and what the target actually said.
/// <para>
/// The two counts are deliberately never summed. Parked work is fixed where it is reported, by correcting the
/// Synchronisation Rule's password settings; expired work cannot be fixed there at all. A single figure covering
/// both would tell an administrator a number without telling them what to do with it, so the separation is
/// asserted here rather than left to the surfaces to remember.
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordAttentionTests
{
    private const int ConnectedSystemId = 42;
    private const int OtherConnectedSystemId = 99;
    private const int SyncRuleId = 7;
    private const int OtherSyncRuleId = 8;

    private SyncRepository _syncRepo = null!;
    private InitialPasswordDeliveryServer _server = null!;

    [SetUp]
    public void Setup()
    {
        _syncRepo = new SyncRepository();
        _server = new InitialPasswordDeliveryServer(_syncRepo, new PasswordGeneratorService(), () => new TestCredentialProtection());
    }

    #region Attention counts

    [Test]
    public async Task GetAttentionBySyncRuleAsync_ParkedAndExpiredOnOneRule_CountsThemSeparatelyAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked);
        await StageAsync(PendingInitialPasswordStatus.Parked);
        await StageAsync(PendingInitialPasswordStatus.Expired);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attention[SyncRuleId].ParkedCount, Is.EqualTo(2));
            Assert.That(attention[SyncRuleId].ExpiredCount, Is.EqualTo(1),
                "expired is its own count: those accounts cannot be helped by changing these settings");
        }
    }

    [Test]
    public async Task GetAttentionBySyncRuleAsync_AccountsStillBeingRetried_AreNotAttentionAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Pending);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        Assert.That(attention, Does.Not.ContainKey(SyncRuleId),
            "an account JIM will try again on the next run is not waiting on a person");
    }

    [Test]
    public async Task GetAttentionBySyncRuleAsync_SettledRule_IsAbsentRatherThanZeroAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, syncRuleId: OtherSyncRuleId);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId, OtherSyncRuleId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attention, Does.Not.ContainKey(SyncRuleId),
                "a settled rule is absent, so a list can render nothing on lookup failure alone");
            Assert.That(attention[OtherSyncRuleId].ParkedCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetAttentionBySyncRuleAsync_RulesNotAskedFor_AreNotReturnedAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, syncRuleId: OtherSyncRuleId);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        Assert.That(attention, Is.Empty, "a page asks about the rules on it, and gets those");
    }

    [Test]
    public async Task GetAttentionByConnectedSystemAsync_RecordWhoseRuleHasGone_IsStillCountedAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, syncRuleId: null);

        var attention = await _server.GetAttentionByConnectedSystemAsync([ConnectedSystemId]);

        Assert.That(attention[ConnectedSystemId].ParkedCount, Is.EqualTo(1),
            "counted against the system it lives in, not through a Synchronisation Rule that was deleted");
    }

    [Test]
    public async Task GetAttentionByConnectedSystemAsync_AnotherSystemsWork_IsNotCountedAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, connectedSystemId: OtherConnectedSystemId);

        var attention = await _server.GetAttentionByConnectedSystemAsync([ConnectedSystemId]);

        Assert.That(attention, Is.Empty);
    }

    [Test]
    public async Task GetAttentionAsync_NothingAsked_QueriesNothingAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _server.GetAttentionBySyncRuleAsync([]), Is.Empty);
            Assert.That(await _server.GetAttentionByConnectedSystemAsync([]), Is.Empty);
        }
    }

    #endregion

    #region Parked reasons

    [Test]
    public async Task GetParkedReasonsAsync_AccountsRefusedForTheSameReason_AreOneGroupAsync()
    {
        const string refusal = "0000052D: Password does not meet complexity requirements.";
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: refusal);
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: refusal);
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: refusal);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reasons.Count, Is.EqualTo(1), "three accounts refused for one reason is one problem");
            Assert.That(reasons[0].AccountCount, Is.EqualTo(3));
            Assert.That(reasons[0].TargetMessage, Is.EqualTo(refusal),
                "verbatim: the code is the one thing precise enough to search for");
        }
    }

    [Test]
    public async Task GetParkedReasonsAsync_SeveralReasons_PutsTheBiggestFirstAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: "Too short.");
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: "Not complex enough.");
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: "Not complex enough.");

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reasons[0].TargetMessage, Is.EqualTo("Not complex enough."));
            Assert.That(reasons[0].AccountCount, Is.EqualTo(2));
            Assert.That(reasons[1].AccountCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetParkedReasonsAsync_ReportsTheEarliestAttemptThatProducedItAsync()
    {
        var oldest = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: "Too short.", lastAttemptedAt: oldest.AddDays(4));
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: "Too short.", lastAttemptedAt: oldest);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons[0].FirstSeenAt, Is.EqualTo(oldest),
            "a fault that arrived this morning reads differently from one nobody has looked at for a month");
    }

    [Test]
    public async Task GetParkedReasonsAsync_ExpiredAndRetryingRecords_AreNotReportedAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Expired, targetMessage: "Too short.");
        await StageAsync(PendingInitialPasswordStatus.Pending, targetMessage: "The directory was unreachable.");

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons, Is.Empty,
            "this panel says what saving will release, and saving releases only what is parked");
    }

    [Test]
    public async Task GetParkedReasonsAsync_AnotherRulesParkedWork_IsNotReportedAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: "Too short.", syncRuleId: OtherSyncRuleId);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons, Is.Empty);
    }

    [Test]
    public async Task GetParkedReasonsAsync_TargetSaidNothing_StillReportsTheGroupAsync()
    {
        await StageAsync(PendingInitialPasswordStatus.Parked, targetMessage: null);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reasons.Count, Is.EqualTo(1),
                "a silent refusal is still a refusal holding an account up; dropping it would lose the account");
            Assert.That(reasons[0].TargetMessage, Is.Null);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Stages one account's outstanding record in the given state.
    /// </summary>
    private async Task StageAsync(
        PendingInitialPasswordStatus status,
        int connectedSystemId = ConnectedSystemId,
        int? syncRuleId = SyncRuleId,
        string? targetMessage = null,
        DateTime? lastAttemptedAt = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Status = ConnectedSystemObjectStatus.Normal
        };
        _syncRepo.SeedConnectedSystemObject(cso);

        await _syncRepo.StageInitialPasswordsAsync([
            new PendingInitialPassword
            {
                ConnectedSystemObjectId = cso.Id,
                ConnectedSystemId = connectedSystemId,
                SyncRuleId = syncRuleId,
                Status = status,
                TargetMessage = targetMessage,
                FailureReason = status == PendingInitialPasswordStatus.Parked ? PasswordSetFailureReason.PolicyRejection : null,
                LastAttemptedAt = lastAttemptedAt,
                CreatedAt = DateTime.UtcNow
            }
        ]);
    }

    #endregion
}

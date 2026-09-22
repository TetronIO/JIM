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
/// What the Synchronisation Rule surfaces ask when they need to say "this one needs you" (#1221 items 4 and 5,
/// re-pointed at the Password Delivery Service's queue by #1697): how many accounts are waiting on a person, and
/// what the target actually said.
/// <para>
/// Initial passwords are staged onto the queue as <see cref="PendingPasswordChangeOrigin.Provisioned"/> rows
/// (#1697), so what is under test here reads that queue rather than the old dedicated initial-password store.
/// </para>
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
    private const int SyncRuleId = 7;
    private const int OtherSyncRuleId = 8;

    private SyncRepository _syncRepo = null!;
    private InitialPasswordServer _server = null!;

    [SetUp]
    public void Setup()
    {
        _syncRepo = new SyncRepository();
        _server = new InitialPasswordServer(_syncRepo, new PasswordGeneratorService(), () => new TestCredentialProtection());
    }

    #region Attention counts

    [Test]
    public async Task GetAttentionBySyncRuleAsync_ParkedAndExpiredOnOneRule_CountsThemSeparatelyAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked);
        await StageAsync(PendingPasswordChangeStatus.Parked);
        await StageAsync(PendingPasswordChangeStatus.Expired);

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
        await StageAsync(PendingPasswordChangeStatus.Pending);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        Assert.That(attention, Does.Not.ContainKey(SyncRuleId),
            "an account JIM will try again shortly is not waiting on a person");
    }

    [Test]
    public async Task GetAttentionBySyncRuleAsync_SettledRule_IsAbsentRatherThanZeroAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked, syncRuleId: OtherSyncRuleId);

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
        await StageAsync(PendingPasswordChangeStatus.Parked, syncRuleId: OtherSyncRuleId);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        Assert.That(attention, Is.Empty, "a page asks about the rules on it, and gets those");
    }

    [Test]
    public async Task GetAttentionAsync_NothingAsked_QueriesNothingAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked);

        Assert.That(await _server.GetAttentionBySyncRuleAsync([]), Is.Empty);
    }

    /// <summary>
    /// This queue also carries <see cref="PendingPasswordChangeOrigin.Explicit"/> and
    /// <see cref="PendingPasswordChangeOrigin.Propagated"/> rows, which belong to Password Synchronisation's own
    /// attention surface, not to a Synchronisation Rule's initial-password indicator. A rule reading its own
    /// attention must not count another feature's rows just because they happen to share the queue.
    /// </summary>
    [Test]
    public async Task GetAttentionBySyncRuleAsync_ExplicitAndPropagatedRows_AreNotCountedAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked, origin: PendingPasswordChangeOrigin.Explicit);
        await StageAsync(PendingPasswordChangeStatus.Parked, origin: PendingPasswordChangeOrigin.Propagated);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        Assert.That(attention, Does.Not.ContainKey(SyncRuleId),
            "an Explicit or Propagated row is not initial-password work, whatever its Synchronisation Rule");
    }

    /// <summary>
    /// A Provisioned row's Synchronisation Rule is null once the rule that provisioned it has been deleted. Unlike
    /// the Connected System attention surface, which still has a system to count the row against, there is no
    /// rule left to attribute it to here: it is counted nowhere rather than under a rule id that no longer exists.
    /// </summary>
    [Test]
    public async Task GetAttentionBySyncRuleAsync_ProvisionedRowWithNoRule_IsCountedNowhereAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked, syncRuleId: null);

        var attention = await _server.GetAttentionBySyncRuleAsync([SyncRuleId]);

        Assert.That(attention, Is.Empty);
    }

    #endregion

    #region Parked reasons

    [Test]
    public async Task GetParkedReasonsAsync_AccountsRefusedForTheSameReason_AreOneGroupAsync()
    {
        const string refusal = "0000052D: Password does not meet complexity requirements.";
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: refusal);
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: refusal);
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: refusal);

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
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Too short.");
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Not complex enough.");
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Not complex enough.");

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
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Too short.", lastAttemptedAt: oldest.AddDays(4));
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Too short.", lastAttemptedAt: oldest);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons[0].FirstSeenAt, Is.EqualTo(oldest),
            "a fault that arrived this morning reads differently from one nobody has looked at for a month");
    }

    [Test]
    public async Task GetParkedReasonsAsync_ExpiredAndRetryingRecords_AreNotReportedAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Expired, targetMessage: "Too short.");
        await StageAsync(PendingPasswordChangeStatus.Pending, targetMessage: "The directory was unreachable.");

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons, Is.Empty,
            "this panel says what saving will release, and saving releases only what is parked");
    }

    [Test]
    public async Task GetParkedReasonsAsync_AnotherRulesParkedWork_IsNotReportedAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Too short.", syncRuleId: OtherSyncRuleId);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons, Is.Empty);
    }

    [Test]
    public async Task GetParkedReasonsAsync_TargetSaidNothing_StillReportsTheGroupAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: null);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reasons.Count, Is.EqualTo(1),
                "a silent refusal is still a refusal holding an account up; dropping it would lose the account");
            Assert.That(reasons[0].TargetMessage, Is.Null);
        }
    }

    /// <summary>
    /// An Explicit row parked for its own reason must not be reported as though it were this rule's initial
    /// password work; the two are different features that only happen to share a queue and a status.
    /// </summary>
    [Test]
    public async Task GetParkedReasonsAsync_AnExplicitRowOnTheSameSystem_IsNotReportedAsync()
    {
        await StageAsync(PendingPasswordChangeStatus.Parked, targetMessage: "Too short.",
            origin: PendingPasswordChangeOrigin.Explicit);

        var reasons = await _server.GetParkedReasonsAsync(SyncRuleId);

        Assert.That(reasons, Is.Empty);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Stages one queued password change in the given state, as a Provisioned row unless overridden.
    /// </summary>
    private async Task StageAsync(
        PendingPasswordChangeStatus status,
        int connectedSystemId = ConnectedSystemId,
        int? syncRuleId = SyncRuleId,
        string? targetMessage = null,
        DateTime? lastAttemptedAt = null,
        PendingPasswordChangeOrigin origin = PendingPasswordChangeOrigin.Provisioned)
    {
        await _syncRepo.QueuePasswordChangesAsync([
            new PendingPasswordChange
            {
                MetaverseObjectId = Guid.NewGuid(),
                ConnectedSystemId = connectedSystemId,
                SyncRuleId = syncRuleId,
                Origin = origin,
                Status = status,
                TargetMessage = targetMessage,
                FailureReason = status == PendingPasswordChangeStatus.Parked ? PasswordSetFailureReason.PolicyRejection : null,
                LastAttemptedAt = lastAttemptedAt,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            }
        ]);
    }

    #endregion
}

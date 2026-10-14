// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Active Directory refuses to create an enabled user that has no password (<c>0000052D</c>), and JIM only sets an
/// Initial Password once the account exists (#2039). So when JIM says an Initial Password will follow a Create
/// (<see cref="PendingExport.InitialPasswordFollows"/>), the LDAP Connector creates the account disabled, adding
/// ACCOUNTDISABLE (0x2) to the <c>userAccountControl</c> value the Attribute Flow sends, and reports the attribute as
/// awaiting the password; the account is enabled once the password is set.
/// <para>
/// The rewrite is narrow on purpose: real Active Directory only (Samba AD accepts an enabled account with no password,
/// and keeping its behaviour unchanged keeps the Samba lab honest), a Create only, a single integer value only, and the
/// Pending Export itself is never touched, since the value the Synchronisation Rule wants is still the one JIM confirms
/// against. Every test passes the directory type explicitly, because the export's constructor defaults it to Active
/// Directory.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorExportInitialPasswordTests
{
    private const string UserDn = "CN=Joe Bloggs,OU=Users,DC=corp,DC=local";
    private const string UserAccountControl = "userAccountControl";

    #region BuildAddRequestWithOverflow

    [TestCase(512, "514", TestName = "BuildAddRequestWithOverflow_ActiveDirectoryEnabledCreateWithInitialPassword_SetsAccountDisable(NormalAccount)")]
    [TestCase(66048, "66050", TestName = "BuildAddRequestWithOverflow_ActiveDirectoryEnabledCreateWithInitialPassword_SetsAccountDisable(NormalAccountPasswordNeverExpires)")]
    public void BuildAddRequestWithOverflow_ActiveDirectoryEnabledCreateWithInitialPassword_SetsAccountDisable(int flowValue, string sentValue)
    {
        var pendingExport = UserCreate(initialPasswordFollows: true, IntChange(flowValue));

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SentValues(addRequest, UserAccountControl), Is.EqualTo(new[] { sentValue }));
            Assert.That(awaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
        }
    }

    [Test]
    public void BuildAddRequestWithOverflow_Rewrite_NeverChangesThePendingExport()
    {
        // The flow's value is what the confirming import is judged against; rewriting the change itself would have
        // JIM confirm the interim disabled value as though the Synchronisation Rule had asked for it.
        var change = IntChange(512);
        var pendingExport = UserCreate(initialPasswordFollows: true, change);
        var changeCountBefore = pendingExport.AttributeValueChanges.Count;

        Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.IntValue, Is.EqualTo(512));
            Assert.That(change.StringValue, Is.Null);
            Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(changeCountBefore));
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
        }
    }

    // Named rather than typed: the directory type is internal to the Connector, and an NUnit test method is public.
    [TestCase(nameof(LdapDirectoryType.SambaAD))]
    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    [TestCase(nameof(LdapDirectoryType.Generic))]
    public void BuildAddRequestWithOverflow_NotActiveDirectory_SendsTheFlowsValue(string directoryType)
    {
        var pendingExport = UserCreate(initialPasswordFollows: true, IntChange(512));

        var (addRequest, _, awaitingInitialPassword) = Export(Enum.Parse<LdapDirectoryType>(directoryType)).BuildAddRequestWithOverflow(pendingExport, UserDn);

        AssertSentUnchanged(addRequest, awaitingInitialPassword, "512");
    }

    [Test]
    public void BuildAddRequestWithOverflow_NoInitialPasswordFollows_SendsTheFlowsValue()
    {
        // Without a password on its way the account would stay disabled for good; the refusal, with its remedies, is
        // the better outcome (see LdapAccountEnableRefusalClassificationTests).
        var pendingExport = UserCreate(initialPasswordFollows: false, IntChange(512));

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        AssertSentUnchanged(addRequest, awaitingInitialPassword, "512");
    }

    [Test]
    public void BuildAddRequestWithOverflow_AlreadyDisabled_SendsTheFlowsValue()
    {
        var pendingExport = UserCreate(initialPasswordFollows: true, IntChange(514));

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        AssertSentUnchanged(addRequest, awaitingInitialPassword, "514");
    }

    [Test]
    public void BuildAddRequestWithOverflow_NoUserAccountControl_AddsNone()
    {
        // Active Directory creates such an account disabled on its own; JIM never invents an attribute the
        // Synchronisation Rule does not flow.
        var pendingExport = UserCreate(initialPasswordFollows: true);

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addRequest.Attributes.Cast<DirectoryAttribute>().Select(a => a.Name), Has.None.EqualTo(UserAccountControl).IgnoreCase);
            Assert.That(awaitingInitialPassword, Is.Null);
        }
    }

    [Test]
    public void BuildAddRequestWithOverflow_MultipleUserAccountControlValues_SendsThemUnchanged()
    {
        var pendingExport = UserCreate(initialPasswordFollows: true, IntChange(512), IntChange(66048));

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SentValues(addRequest, UserAccountControl), Is.EqualTo(new[] { "512", "66048" }));
            Assert.That(awaitingInitialPassword, Is.Null);
        }
    }

    [Test]
    public void BuildAddRequestWithOverflow_NonIntegerUserAccountControl_SendsItUnchanged()
    {
        var pendingExport = UserCreate(initialPasswordFollows: true, StringChange("enabled"));

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        AssertSentUnchanged(addRequest, awaitingInitialPassword, "enabled");
    }

    [Test]
    public void BuildAddRequestWithOverflow_Update_SendsTheFlowsValue()
    {
        // The proactive rewrite is for a Create only: an Update writes to an account that may already have a password,
        // so it is sent as the flow asks, and withheld only if Active Directory refuses it (see the #2158 region below).
        var pendingExport = UserCreate(initialPasswordFollows: true, IntChange(512));
        pendingExport.ChangeType = PendingExportChangeType.Update;

        var (addRequest, _, awaitingInitialPassword) = Export(LdapDirectoryType.ActiveDirectory).BuildAddRequestWithOverflow(pendingExport, UserDn);

        AssertSentUnchanged(addRequest, awaitingInitialPassword, "512");
    }

    #endregion

    #region Through the export path

    [Test]
    public async Task ExecuteAsync_SequentialCreate_SendsTheAccountDisabledAndReportsItAsync()
    {
        AddRequest? sent = null;
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Callback<DirectoryRequest>(request => sent = (AddRequest)request)
            .Returns(LdapTestResponses.Create<AddResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequest(It.IsAny<SearchRequest>()))
            .Returns(LdapTestResponses.Create<SearchResponse>(ResultCode.Success));

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor)
            .ExecuteAsync([UserCreate(initialPasswordFollows: true, IntChange(512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].SecondaryExternalId, Is.EqualTo(UserDn));
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
            Assert.That(SentValues(sent!, UserAccountControl), Is.EqualTo(new[] { "514" }));
        }
    }

    [Test]
    public async Task ExecuteAsync_ConcurrentCreate_SendsTheAccountDisabledAndReportsItAsync()
    {
        var sent = new System.Collections.Concurrent.ConcurrentBag<AddRequest>();
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequestAsync(It.IsAny<AddRequest>()))
            .Callback<DirectoryRequest>(request => sent.Add((AddRequest)request))
            .ReturnsAsync(LdapTestResponses.Create<AddResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ReturnsAsync(LdapTestResponses.Create<SearchResponse>(ResultCode.Success));

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor, concurrency: 4)
            .ExecuteAsync([UserCreate(initialPasswordFollows: true, IntChange(512)), UserCreate(initialPasswordFollows: true, IntChange(66048))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.AttributesAwaitingInitialPassword), Has.All.EqualTo(new[] { UserAccountControl }));
            Assert.That(sent.SelectMany(request => SentValues(request, UserAccountControl)), Is.EquivalentTo(new[] { "514", "66050" }));
        }
    }

    [Test]
    public async Task ExecuteAsync_CreateWithNothingRewritten_ReportsNothingAwaitingAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Returns(LdapTestResponses.Create<AddResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequest(It.IsAny<SearchRequest>()))
            .Returns(LdapTestResponses.Create<SearchResponse>(ResultCode.Success));

        var results = await Export(LdapDirectoryType.SambaAD, executor)
            .ExecuteAsync([UserCreate(initialPasswordFollows: true, IntChange(512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.Null);
        }
    }

    #endregion

    #region An enabling Update while a password is owed (#2158)

    // Active Directory's refusal to enable an account that has no password (ERROR_PASSWORD_RESTRICTIONS under
    // WILL_NOT_PERFORM); the same fixture LdapAccountEnableRefusalClassificationTests uses.
    private const string ActiveDirectoryRefusal = "0000052D: SvcErr: DSID-031A12C5, problem 5003 (WILL_NOT_PERFORM), data 0\n";

    /// <summary>
    /// The reported case: a pre-hire's start date arrives while the Initial Password is still queued, and the Attribute
    /// Flow enables the account with an Update. Active Directory refuses that for want of a password; because JIM said a
    /// password follows, the Connector sends the same modifications again with ACCOUNTDISABLE added, and reports
    /// <c>userAccountControl</c> as awaiting the password, which the delivery completes.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_SequentialUpdateRefusedForWantOfAPassword_ResendsItDisabledAndReportsItAsync()
    {
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEnablementWithoutAPassword(sent);

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor)
            .ExecuteAsync([UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512), UpdateChange("description", "Starter"))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
            Assert.That(results[0].SetsAccountEnabledState, Is.True);
            Assert.That(sent.Select(request => SentModifyValues(request, UserAccountControl).Single()), Is.EqualTo(new[] { "512", "514" }),
                "the flow's value first, then the same request disabled");
            Assert.That(sent.Select(request => SentModifyValues(request, "description").Single()), Is.EqualTo(new[] { "Starter", "Starter" }),
                "the refused modify applied nothing, so every other change it carried is sent again with it");
        }
    }

    [Test]
    public async Task ExecuteAsync_ConcurrentUpdateRefusedForWantOfAPassword_ResendsItDisabledAndReportsItAsync()
    {
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEnablementWithoutAPassword(sent);

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor, concurrency: 4)
            .ExecuteAsync([
                UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512)),
                UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 66048))
            ], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(results.Select(r => r.AttributesAwaitingInitialPassword), Has.All.EqualTo(new[] { UserAccountControl }));
            Assert.That(sent.Select(request => SentModifyValues(request, UserAccountControl).Single()),
                Is.EquivalentTo(new[] { "512", "514", "66048", "66050" }));
        }
    }

    /// <summary>
    /// An executor that returns the refusal rather than throwing it is read the same way: the Connector raises every
    /// non-success modify response itself, so one path decides what a refusal means.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_UpdateRefusedWithAResponseTheConnectorRaises_ResendsItDisabledAsync()
    {
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEnablementWithoutAPassword(sent, returnsTheRefusal: true);

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor)
            .ExecuteAsync([UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
            Assert.That(sent.Select(request => SentModifyValues(request, UserAccountControl).Single()), Is.EqualTo(new[] { "512", "514" }));
        }
    }

    /// <summary>
    /// An account that already has a password is enabled straight away: nothing is withheld on the strength of the
    /// flag alone, only on Active Directory's own refusal.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_UpdateAcceptedWhileAPasswordFollows_SendsOnlyTheFlowsValueAsync()
    {
        var sent = new List<ModifyRequest>();
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Callback<DirectoryRequest>(request => sent.Add((ModifyRequest)request))
            .Returns(LdapTestResponses.Create<ModifyResponse>(ResultCode.Success));

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor)
            .ExecuteAsync([UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.Null);
            Assert.That(sent.Select(request => SentModifyValues(request, UserAccountControl).Single()), Is.EqualTo(new[] { "512" }));
        }
    }

    /// <summary>
    /// The resend follows the refusal, not the directory type: Samba AD accepts an enabled account with no password
    /// today and so never refuses, but one that speaks Active Directory's refusal is held exactly as Active Directory is,
    /// rather than failing.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_SambaAdUpdateRefusedForWantOfAPassword_AlsoResendsItDisabledAsync()
    {
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEnablementWithoutAPassword(sent);

        var results = await Export(LdapDirectoryType.SambaAD, executor)
            .ExecuteAsync([UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
        }
    }

    /// <summary>
    /// Only a single integer value without ACCOUNTDISABLE is the enable JIM can withhold. A value that already
    /// disables the account was refused for some other reason, and JIM never invents a value from one it cannot read,
    /// so each is reported as the refusal it was, sent once.
    /// </summary>
    [TestCase("514", TestName = "ExecuteAsync_UpdateRefused_DoesNotResend(AlreadyDisabled)")]
    [TestCase("enabled", TestName = "ExecuteAsync_UpdateRefused_DoesNotResend(NonInteger)")]
    [TestCase("512|66048", TestName = "ExecuteAsync_UpdateRefused_DoesNotResend(TwoValues)")]
    public async Task ExecuteAsync_UpdateAlreadyDisabledOrNotASingleInteger_DoesNotResendAsync(string flowValues)
    {
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEveryModify(sent);
        var changes = flowValues.Split('|').Select(value => UpdateChange(UserAccountControl, value)).ToArray();

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor)
            .ExecuteAsync([UserUpdate(initialPasswordFollows: true, changes)], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
            Assert.That(sent, Has.Count.EqualTo(1));
        }
    }

    /// <summary>
    /// A resend refused again fails exactly as the refusal would have without it: classified, with the remedies, and
    /// nothing reported as awaiting a password.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_ResentUpdateRefusedAgain_ReportsTheFailureAndNothingAwaitingAsync()
    {
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEveryModify(sent);

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor)
            .ExecuteAsync([UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.Null);
            Assert.That(sent.Select(request => SentModifyValues(request, UserAccountControl).Single()), Is.EqualTo(new[] { "512", "514" }),
                "resent once, never again");
        }
    }

    /// <summary>
    /// A renamed account is renamed once: the rename succeeded before the modify was refused, so only the modify is sent
    /// again, at the new Distinguished Name, and the result still reports where the object now lives.
    /// </summary>
    [TestCase(1, TestName = "ExecuteAsync_RenamedUpdateRefusedForWantOfAPassword_RenamesOnceAndResendsOnlyTheModify(Sequential)")]
    [TestCase(4, TestName = "ExecuteAsync_RenamedUpdateRefusedForWantOfAPassword_RenamesOnceAndResendsOnlyTheModify(Concurrent)")]
    public async Task ExecuteAsync_RenamedUpdateRefusedForWantOfAPassword_RenamesOnceAndResendsOnlyTheModifyAsync(int concurrency)
    {
        const string renamedDn = "CN=Joe Bloggs,OU=Starters,DC=corp,DC=local";
        var sent = new List<ModifyRequest>();
        var executor = DirectoryRefusingEnablementWithoutAPassword(sent);
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyDNRequest>())).Returns(LdapTestResponses.Create<ModifyDNResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyDNRequest>())).ReturnsAsync(LdapTestResponses.Create<ModifyDNResponse>(ResultCode.Success));
        var update = UserUpdate(initialPasswordFollows: true, UpdateChange(UserAccountControl, 512), UpdateChange("distinguishedName", renamedDn));

        var results = await Export(LdapDirectoryType.ActiveDirectory, executor, concurrency).ExecuteAsync([update], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].SecondaryExternalId, Is.EqualTo(renamedDn));
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
            Assert.That(sent.Select(request => request.DistinguishedName), Is.All.EqualTo(renamedDn));
            Assert.That(sent.Select(request => SentModifyValues(request, UserAccountControl).Single()), Is.EqualTo(new[] { "512", "514" }));
        }

        executor.Verify(e => e.SendRequest(It.IsAny<ModifyDNRequest>()), Times.Exactly(concurrency == 1 ? 1 : 0));
        executor.Verify(e => e.SendRequestAsync(It.IsAny<ModifyDNRequest>()), Times.Exactly(concurrency == 1 ? 0 : 1));
    }

    /// <summary>
    /// A directory that, like Active Directory, refuses a modify writing <c>userAccountControl</c> without ACCOUNTDISABLE
    /// to an account that has no password, applying none of the request, and accepts everything else. Records every
    /// modify it is sent, on either path.
    /// </summary>
    private static Mock<ILdapOperationExecutor> DirectoryRefusingEnablementWithoutAPassword(List<ModifyRequest> sent, bool returnsTheRefusal = false) =>
        DirectoryAnsweringModifies(sent, request => SentModifyValues(request, UserAccountControl) is [var value]
            && int.TryParse(value, out var flags) && (flags & LdapConnectorConstants.UAC_ACCOUNTDISABLE) == 0, returnsTheRefusal);

    private static Mock<ILdapOperationExecutor> DirectoryRefusingEveryModify(List<ModifyRequest> sent) =>
        DirectoryAnsweringModifies(sent, _ => true, returnsTheRefusal: false);

    private static Mock<ILdapOperationExecutor> DirectoryAnsweringModifies(List<ModifyRequest> sent, Func<ModifyRequest, bool> refuses, bool returnsTheRefusal)
    {
        DirectoryResponse Answer(DirectoryRequest request)
        {
            var modify = (ModifyRequest)request;
            lock (sent)
                sent.Add(modify);

            if (!refuses(modify))
                return LdapTestResponses.Create<ModifyResponse>(ResultCode.Success);

            var refusal = LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal);
            return returnsTheRefusal ? refusal : throw new DirectoryOperationException(refusal);
        }

        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>())).Returns((DirectoryRequest request) => Answer(request));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyRequest>())).Returns((DirectoryRequest request) => Task.FromResult(Answer(request)));
        return executor;
    }

    private static PendingExportAttributeValueChange UpdateChange(string attributeName, int value) => new()
    {
        Attribute = new ConnectedSystemObjectTypeAttribute { Id = AttributeIdFor(attributeName), Name = attributeName, ConnectedSystemObjectType = UserType },
        ChangeType = PendingExportAttributeChangeType.Update,
        IntValue = value
    };

    private static PendingExportAttributeValueChange UpdateChange(string attributeName, string value) => new()
    {
        Attribute = new ConnectedSystemObjectTypeAttribute { Id = AttributeIdFor(attributeName), Name = attributeName, ConnectedSystemObjectType = UserType },
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value
    };

    private static int AttributeIdFor(string attributeName) => attributeName switch
    {
        "distinguishedName" => 1,
        UserAccountControl => 2,
        _ => 4
    };

    private static PendingExport UserUpdate(bool initialPasswordFollows, params PendingExportAttributeValueChange[] changes)
    {
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = UserType };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            InitialPasswordFollows = initialPasswordFollows,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = UserType,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = dnAttribute, AttributeId = dnAttribute.Id, StringValue = UserDn }]
            },
            AttributeValueChanges = [.. changes]
        };
    }

    private static string[] SentModifyValues(ModifyRequest request, string attributeName) =>
        request.Modifications.Cast<DirectoryAttributeModification>()
            .Where(modification => modification.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(modification => modification.GetValues(typeof(string)).Cast<string>())
            .ToArray();

    #endregion

    #region Helpers

    private static LdapConnectorExport Export(LdapDirectoryType directoryType, Mock<ILdapOperationExecutor>? executor = null, int concurrency = 1) =>
        new((executor ?? new Mock<ILdapOperationExecutor>()).Object,
            [new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }],
            Log.Logger,
            concurrency,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
            directoryType,
            placeholderMemberDn: null);

    private static readonly ConnectedSystemObjectType UserType = new() { Name = "user" };

    private static PendingExportAttributeValueChange IntChange(int value) => new()
    {
        Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = UserAccountControl, ConnectedSystemObjectType = UserType },
        ChangeType = PendingExportAttributeChangeType.Add,
        IntValue = value
    };

    private static PendingExportAttributeValueChange StringChange(string value) => new()
    {
        Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = UserAccountControl, ConnectedSystemObjectType = UserType },
        ChangeType = PendingExportAttributeChangeType.Add,
        StringValue = value
    };

    private static PendingExport UserCreate(bool initialPasswordFollows, params PendingExportAttributeValueChange[] userAccountControlChanges) => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = PendingExportChangeType.Create,
        InitialPasswordFollows = initialPasswordFollows,
        ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = UserType },
        AttributeValueChanges =
        [
            new PendingExportAttributeValueChange
            {
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = UserType },
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = UserDn
            },
            new PendingExportAttributeValueChange
            {
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "sAMAccountName", ConnectedSystemObjectType = UserType },
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = "jbloggs"
            },
            .. userAccountControlChanges
        ]
    };

    private static string[] SentValues(AddRequest addRequest, string attributeName) =>
        addRequest.Attributes.Cast<DirectoryAttribute>()
            .Where(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a.GetValues(typeof(string)).Cast<string>())
            .ToArray();

    private static void AssertSentUnchanged(AddRequest addRequest, IReadOnlyList<string>? awaitingInitialPassword, string flowValue)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SentValues(addRequest, UserAccountControl), Is.EqualTo(new[] { flowValue }));
            Assert.That(awaitingInitialPassword, Is.Null);
        }
    }

    #endregion
}

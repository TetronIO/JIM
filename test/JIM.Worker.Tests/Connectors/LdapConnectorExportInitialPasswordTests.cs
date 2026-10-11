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
        // Only an account being created can be waiting for its first password; an Update writes to one that exists.
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

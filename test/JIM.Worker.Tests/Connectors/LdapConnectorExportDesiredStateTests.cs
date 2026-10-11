// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An export that adds a value the directory already holds, or removes one it does not, asks for a state that already
/// exists. An LDAP modify is atomic, so the directory rejects the whole request for that one value and applies none of
/// the others it carried. These tests cover how the LDAP Connector reaches the desired state anyway: by attaching the
/// Permissive Modify control where the directory advertises it, and otherwise by applying the refused modify again in
/// parts, halving any part refused again, without ever reporting a rejected modify as a success or counting a value as
/// in place when the directory could hold another.
/// </summary>
[TestFixture]
public class LdapConnectorExportDesiredStateTests
{
    private const string GroupDn = "CN=Engineers,OU=Groups,DC=test,DC=local";
    private const string PermissiveModifyOid = "1.2.840.113556.1.4.1413";
    private const string Alice = "CN=Alice,OU=Users,DC=test,DC=local";
    private const string Bob = "CN=Bob,OU=Users,DC=test,DC=local";
    private const string Carol = "CN=Carol,OU=Users,DC=test,DC=local";
    private const string Dave = "CN=Dave,OU=Users,DC=test,DC=local";

    // Active Directory's and Samba AD's own words, as the domain controllers answer.
    private const string SambaMemberAlreadyPresent = "The object exists. Attribute member already exists for target GUID 06a38326-feac-4cef-bde2-1792c97fcf52";
    private const string ActiveDirectoryMemberAlreadyPresent = "00000562: UpdErr: DSID-031A11E2, problem 6005 (ENTRY_EXISTS), data 0";
    private const string ActiveDirectoryMemberNotInGroup = "00000561: SvcErr: DSID-031A1254, problem 5003 (WILL_NOT_PERFORM), data 0";

    // Samba AD answers unwillingToPerform for the removal of a member the group does not hold, even with Permissive
    // Modify attached, in its own words rather than Active Directory's code.
    private const string SambaMemberNotPresent = "Attribute member doesn't exist for target GUID df8c9ffe-ae21-492c-8e77-ea8833e35f6a";

    // And, in other words, for the removal of a member the group held once and has since lost: Samba keeps the removed
    // link, and answers from it.
    private const string SambaMemberAlreadyRemoved = "Attribute member already deleted for target GUID 06a38326-feac-4cef-bde2-1792c97fcf52";

    // ERROR_MEMBERS_PRIMARY_GROUP: the member cannot be removed because this is its primary group. A genuine refusal.
    private const string ActiveDirectoryMembersPrimaryGroup = "0000055E: SvcErr: DSID-031A1254, problem 5003 (WILL_NOT_PERFORM), data 0";

    private static readonly IList<ConnectedSystemSettingValue> Settings =
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }
    ];

    #region The rootDSE says whether the control is offered

    [Test]
    public void AdvertisesPermissiveModify_ControlListedInSupportedControl_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { SupportedControls = ["1.2.840.113556.1.4.319", PermissiveModifyOid] };

        Assert.That(rootDse.AdvertisesPermissiveModify, Is.True);
    }

    [Test]
    public void AdvertisesPermissiveModify_ControlNotListed_ReturnsFalse()
    {
        // OpenLDAP's rootDSE, as a stock slapd answers it: the control is not among those it lists.
        var rootDse = new LdapConnectorRootDse
        {
            SupportedControls = ["2.16.840.1.113730.3.4.18", "2.16.840.1.113730.3.4.2", "1.3.6.1.4.1.4203.1.10.1", "1.3.6.1.1.22",
                "1.2.840.113556.1.4.319", "1.2.826.0.1.3344810.2.3", "1.3.6.1.1.13.2", "1.3.6.1.1.13.1", "1.3.6.1.1.12"]
        };

        Assert.That(rootDse.AdvertisesPermissiveModify, Is.False);
    }

    [Test]
    public void AdvertisesPermissiveModify_SupportedControlNotRead_ReturnsFalse()
    {
        var rootDse = new LdapConnectorRootDse { SupportedControls = null };

        Assert.That(rootDse.AdvertisesPermissiveModify, Is.False);
    }

    #endregion

    #region Where the directory advertises Permissive Modify

    [TestCase(1, TestName = "Export_DirectoryAdvertisesPermissiveModify_AttachesItToAModifyThatAddsValues(sequential)")]
    [TestCase(4, TestName = "Export_DirectoryAdvertisesPermissiveModify_AttachesItToAModifyThatAddsValues(concurrent)")]
    public async Task Export_DirectoryAdvertisesPermissiveModify_AttachesItToAModifyThatAddsValuesAsync(int concurrency)
    {
        var directory = new FakeDirectory().With("member", Alice);
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Bob))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ModifyRequests, Has.Count.EqualTo(1));
            Assert.That(CarriesPermissiveModify(directory.ModifyRequests[0]), Is.True);
        }
    }

    [TestCase(1, TestName = "Export_DirectoryAdvertisesPermissiveModify_AttachesItToAModifyThatDeletesValues(sequential)")]
    [TestCase(4, TestName = "Export_DirectoryAdvertisesPermissiveModify_AttachesItToAModifyThatDeletesValues(concurrent)")]
    public async Task Export_DirectoryAdvertisesPermissiveModify_AttachesItToAModifyThatDeletesValuesAsync(int concurrency)
    {
        var directory = new FakeDirectory().With("member", Alice);
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Alice))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(CarriesPermissiveModify(directory.ModifyRequests.Single()), Is.True);
        }
    }

    [TestCase(1, TestName = "Export_DirectoryDoesNotAdvertisePermissiveModify_AttachesNoControl(sequential)")]
    [TestCase(4, TestName = "Export_DirectoryDoesNotAdvertisePermissiveModify_AttachesNoControl(concurrent)")]
    public async Task Export_DirectoryDoesNotAdvertisePermissiveModify_AttachesNoControlAsync(int concurrency)
    {
        // 389 Directory Server refuses the control outright (unavailableCriticalExtension), so it must never be sent
        // to a directory that has not said it understands it.
        var directory = new FakeDirectory().With("member", Alice);
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: false);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Bob), Remove("member", Alice))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ModifyRequests.Any(CarriesPermissiveModify), Is.False);
        }
    }

    [TestCase(1, TestName = "Export_ModifyThatOnlyReplaces_IsSentUnchanged(sequential)")]
    [TestCase(4, TestName = "Export_ModifyThatOnlyReplaces_IsSentUnchanged(concurrent)")]
    public async Task Export_ModifyThatOnlyReplaces_IsSentUnchangedAsync(int concurrency)
    {
        var directory = new FakeDirectory().With("description", "Old");
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync([GroupUpdate(Replace("description", "New"))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ModifyRequests, Has.Count.EqualTo(1));
            Assert.That(CarriesPermissiveModify(directory.ModifyRequests[0]), Is.False,
                "A replace cannot meet a value already present or already absent, so it has no use for the control");
            Assert.That(directory.ValuesOf("description"), Is.EquivalentTo(new[] { "New" }));
        }
    }

    [TestCase(1, TestName = "Export_CreateWhoseMembersOverflowTheBatch_AttachesItToEveryOverflowModify(sequential)")]
    [TestCase(4, TestName = "Export_CreateWhoseMembersOverflowTheBatch_AttachesItToEveryOverflowModify(concurrent)")]
    public async Task Export_CreateWhoseMembersOverflowTheBatch_AttachesItToEveryOverflowModifyAsync(int concurrency)
    {
        var directory = new FakeDirectory();
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true, batchSize: 10);
        var members = Enumerable.Range(1, 25).Select(i => $"CN=User{i:D2},OU=Users,DC=test,DC=local").ToArray();

        var results = await export.ExecuteAsync([GroupCreate(members)], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ModifyRequests, Has.Count.EqualTo(2), "25 members in batches of 10: 10 on the add, then two overflow modifies");
            Assert.That(directory.ModifyRequests.All(CarriesPermissiveModify), Is.True);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(members));
        }
    }

    [TestCase(1, TestName = "Export_DirectoryHonoursPermissiveModify_AddOfAPresentMemberSucceedsInOneRequest(sequential)")]
    [TestCase(4, TestName = "Export_DirectoryHonoursPermissiveModify_AddOfAPresentMemberSucceedsInOneRequest(concurrent)")]
    public async Task Export_DirectoryHonoursPermissiveModify_AddOfAPresentMemberSucceedsInOneRequestAsync(int concurrency)
    {
        var directory = new FakeDirectory { HonoursPermissiveModify = true }.With("member", Alice);
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Alice), Add("member", Bob), Remove("member", Dave))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ModifyRequests, Has.Count.EqualTo(1));
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Alice, Bob }));
        }
    }

    [Test]
    public async Task Export_DirectoryStillRejectsDespitePermissiveModify_AppliesTheModifyInPartsAsync()
    {
        // OpenLDAP honours the control for an add but still answers noSuchAttribute for the delete of an absent value.
        var directory = new FakeDirectory { HonoursPermissiveModify = false }.With("member", Alice);
        var export = CreateExport(directory, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Alice), Remove("member", Dave))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.Empty);
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
        }
    }

    #endregion

    #region Where it does not: the modify applied in parts

    [TestCase(1, TestName = "Export_AddOfAMemberAlreadyPresent_AppliesTheOtherMembersAndSucceeds(sequential)")]
    [TestCase(4, TestName = "Export_AddOfAMemberAlreadyPresent_AppliesTheOtherMembersAndSucceeds(concurrent)")]
    public async Task Export_AddOfAMemberAlreadyPresent_AppliesTheOtherMembersAndSucceedsAsync(int concurrency)
    {
        var directory = new FakeDirectory().With("member", Alice);
        var export = CreateExport(directory, concurrency);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Alice), Add("member", Bob), Add("member", Carol))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Alice, Bob, Carol }),
                "The directory rejected the whole modify for Alice; Bob and Carol must still be added");
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
        }
    }

    [TestCase(1, TestName = "Export_DeleteOfAMemberAlreadyAbsent_RemovesTheOtherMembersAndSucceeds(sequential)")]
    [TestCase(4, TestName = "Export_DeleteOfAMemberAlreadyAbsent_RemovesTheOtherMembersAndSucceeds(concurrent)")]
    public async Task Export_DeleteOfAMemberAlreadyAbsent_RemovesTheOtherMembersAndSucceedsAsync(int concurrency)
    {
        var directory = new FakeDirectory().With("member", Alice, Bob, Carol);
        var export = CreateExport(directory, concurrency);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Dave), Remove("member", Alice), Remove("member", Bob))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Carol }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
        }
    }

    [TestCase(1, TestName = "Export_GenuineRefusalOfOneValue_FailsTheExport(sequential)")]
    [TestCase(4, TestName = "Export_GenuineRefusalOfOneValue_FailsTheExport(concurrent)")]
    public async Task Export_GenuineRefusalOfOneValue_FailsTheExportAsync(int concurrency)
    {
        var directory = new FakeDirectory
        {
            RefuseValue = (modification, value) => value == Carol
                ? Rejection(ResultCode.InsufficientAccessRights, "Insufficient access rights to add Carol")
                : null
        }.With("member", Alice);
        var export = CreateExport(directory, concurrency);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Alice), Add("member", Carol))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False, "One value the directory genuinely refused means the export did not reach its desired state");
            Assert.That(results.Single().ErrorMessage, Does.Contain("Insufficient access rights to add Carol"));
            Assert.That(directory.ValuesOf("member"), Does.Not.Contain(Carol));
        }
    }

    [Test]
    public async Task Export_AtomicModifyRefusedForAnotherReason_IsNotRetriedValueByValueAsync()
    {
        var directory = new FakeDirectory
        {
            RefuseValue = (_, _) => Rejection(ResultCode.InsufficientAccessRights, "Insufficient access rights")
        };
        var export = CreateExport(directory);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Alice), Add("member", Bob))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(directory.ModifyRequests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Export_ModifyThatOnlyReplacesRejectedWithValueExists_FailsWithoutRetryingAsync()
    {
        // A replace that the directory answers with attributeOrValueExists (duplicate values in the list, say) is not
        // a value already in place: nothing about it can be achieved by applying it in parts.
        var directory = new FakeDirectory
        {
            RefuseValue = (modification, _) => modification.Operation == DirectoryAttributeOperation.Replace
                ? Rejection(ResultCode.AttributeOrValueExists, "duplicate values provided")
                : null
        };
        var export = CreateExport(directory);

        var results = await export.ExecuteAsync([GroupUpdate(Replace("description", "New"))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(directory.ModifyRequests, Has.Count.EqualTo(1));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.Zero);
        }
    }

    [Test]
    public async Task Export_Fallback_SendsTheReplacesTogetherThenOnlyTheValuesAsync()
    {
        var directory = new FakeDirectory().With("member", Alice).With("description", "Old").With("info", "Old");
        var export = CreateExport(directory);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("member", Alice), Replace("description", "New"), Add("member", Bob), Replace("info", "New"))],
            CancellationToken.None);

        var retries = directory.ModifyRequests.Skip(1).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(Modifications(retries[0]).Select(m => m.Operation), Is.All.EqualTo(DirectoryAttributeOperation.Replace));
            Assert.That(Modifications(retries[0]).Select(m => m.Name), Is.EquivalentTo(new[] { "description", "info" }));
            Assert.That(retries.Skip(1).SelectMany(Modifications).Select(m => m.Operation), Is.All.EqualTo(DirectoryAttributeOperation.Add),
                "The replaces were applied once; every later request carries only member values");
            Assert.That(directory.ValuesOf("description"), Is.EquivalentTo(new[] { "New" }));
            Assert.That(directory.ValuesOf("info"), Is.EquivalentTo(new[] { "New" }));
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Alice, Bob }));
        }
    }

    [Test]
    public async Task Export_Fallback_AddsBeforeItDeletes_SoAGroupThatMustHaveAMemberIsNeverLeftEmptyAsync()
    {
        // groupOfNames must always hold a member. Atomically, removing Alice and adding Carol is fine; in parts,
        // removing Alice first would leave the group empty and be refused.
        var directory = new FakeDirectory { AttributeThatMustHaveAValue = "member" }.With("member", Alice);
        var export = CreateExport(directory);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Alice), Remove("member", Dave), Add("member", Carol))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Carol }));
        }
    }

    [Test]
    public async Task Export_Fallback_AddsAClassBeforeTheAttributesItAllowsAndRemovesOneAfterThemAsync()
    {
        var directory = new FakeDirectory
        {
            ClassRequiredByAttribute = new(StringComparer.OrdinalIgnoreCase) { ["jimBadge"] = "jimBadgeHolder", ["jimLegacyCode"] = "jimLegacy" }
        }.With("member", Alice).With("objectClass", "top", "group", "jimLegacy").With("jimLegacyCode", "L1");
        var export = CreateExport(directory);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("member", Alice), Add("objectClass", "jimBadgeHolder"), Replace("jimBadge", "B7"),
                Remove("jimLegacyCode", "L1"), Remove("objectClass", "jimLegacy"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("objectClass"), Is.EquivalentTo(new[] { "top", "group", "jimBadgeHolder" }));
            Assert.That(directory.ValuesOf("jimBadge"), Is.EquivalentTo(new[] { "B7" }));
            Assert.That(directory.ValuesOf("jimLegacyCode"), Is.Empty);
        }
    }

    [Test]
    public async Task Export_Fallback_CountsEveryValueAlreadyInDesiredStateAcrossTheRunAsync()
    {
        var first = new FakeDirectory().With("member", Alice, Bob);
        var export = CreateExport(first);

        await export.ExecuteAsync(
            [GroupUpdate(Add("member", Alice), Add("member", Bob), Add("member", Carol), Remove("member", Dave))],
            CancellationToken.None);

        Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(3), "Alice and Bob already present, Dave already absent");
    }

    [TestCase(1, TestName = "Export_RenameThenAddOfAPresentMember_ReportsTheRenamedDn(sequential)")]
    [TestCase(4, TestName = "Export_RenameThenAddOfAPresentMember_ReportsTheRenamedDn(concurrent)")]
    public async Task Export_RenameThenAddOfAPresentMember_ReportsTheRenamedDnAsync(int concurrency)
    {
        const string renamedDn = "CN=Platform Engineers,OU=Groups,DC=test,DC=local";
        var directory = new FakeDirectory().With("member", Alice);
        var export = CreateExport(directory, concurrency);

        var results = await export.ExecuteAsync([GroupUpdate(Replace("distinguishedName", renamedDn), Add("member", Alice), Add("member", Bob))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(results.Single().SecondaryExternalId, Is.EqualTo(renamedDn));
            Assert.That(directory.ModifyRequests.Select(r => r.DistinguishedName), Is.All.EqualTo(renamedDn));
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Alice, Bob }));
        }
    }

    #endregion

    #region Active Directory and Samba AD answer group membership with their own codes

    [TestCase(nameof(LdapDirectoryType.SambaAD), SambaMemberAlreadyPresent)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), ActiveDirectoryMemberAlreadyPresent)]
    public async Task Export_ActiveDirectoryFamilyAnswersEntryAlreadyExistsForAPresentMember_AddsTheOtherMembersAsync(
        string directoryTypeName, string message)
    {
        var directoryType = Enum.Parse<LdapDirectoryType>(directoryTypeName);
        var directory = new FakeDirectory { ValuePresentCode = ResultCode.EntryAlreadyExists, ValuePresentMessage = message }.With("member", Alice);
        var export = CreateExport(directory, directoryType: directoryType);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Alice), Add("member", Bob))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Alice, Bob }));
        }
    }

    [TestCase(nameof(LdapDirectoryType.SambaAD), SambaMemberNotPresent, true)]
    [TestCase(nameof(LdapDirectoryType.SambaAD), SambaMemberNotPresent, false)]
    [TestCase(nameof(LdapDirectoryType.SambaAD), SambaMemberAlreadyRemoved, true)]
    [TestCase(nameof(LdapDirectoryType.SambaAD), SambaMemberAlreadyRemoved, false)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), ActiveDirectoryMemberNotInGroup, false)]
    public async Task Export_ActiveDirectoryFamilyAnswersMemberNotInGroup_RemovesTheOtherMembersAsync(
        string directoryTypeName, string message, bool advertisesPermissiveModify)
    {
        var directoryType = Enum.Parse<LdapDirectoryType>(directoryTypeName);
        var directory = new FakeDirectory { ValueAbsentCode = ResultCode.UnwillingToPerform, ValueAbsentMessage = message }
            .With("member", Alice, Bob);
        var export = CreateExport(directory, directoryType: directoryType, advertisesPermissiveModify: advertisesPermissiveModify);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Dave), Remove("member", Alice))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { Bob }));
        }
    }

    [Test]
    public async Task Export_EntryAlreadyExistsOutsideTheActiveDirectoryFamily_FailsTheExportAsync()
    {
        var directory = new FakeDirectory { ValuePresentCode = ResultCode.EntryAlreadyExists, ValuePresentMessage = "Entry already exists" }
            .With("member", Alice);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync([GroupUpdate(Add("member", Alice), Add("member", Bob))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(directory.ValuesOf("member"), Does.Not.Contain(Bob));
        }
    }

    [Test]
    public async Task Export_ActiveDirectoryEntryAlreadyExistsOnAnAttributeOtherThanMember_FailsTheExportAsync()
    {
        // entryAlreadyExists is also how Active Directory reports an account name already in use; only group
        // membership is read as "the value is already there".
        var directory = new FakeDirectory { ValuePresentCode = ResultCode.EntryAlreadyExists, ValuePresentMessage = ActiveDirectoryMemberAlreadyPresent }
            .With("otherMailbox", "a@test.local");
        var export = CreateExport(directory, directoryType: LdapDirectoryType.ActiveDirectory);

        var results = await export.ExecuteAsync([GroupUpdate(Add("otherMailbox", "a@test.local"), Add("otherMailbox", "b@test.local"))], CancellationToken.None);

        Assert.That(results.Single().Success, Is.False);
    }

    [Test]
    public async Task Export_ActiveDirectoryUnwillingToPerformForAnotherReason_FailsTheExportAsync()
    {
        var directory = new FakeDirectory
        {
            ValueAbsentCode = ResultCode.UnwillingToPerform,
            ValueAbsentMessage = ActiveDirectoryMembersPrimaryGroup
        }.With("member", Alice);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.ActiveDirectory);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Dave), Remove("member", Alice))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(directory.ValuesOf("member"), Does.Contain(Alice));
        }
    }

    #endregion

    #region A value kept under a new case must not be removed with the old one

    // Jane marries: her primary address moves to her new name, and the old one stays as an alias. Active Directory
    // compares proxyAddresses without regard to case, so JIM stages an add of the alias alongside the delete of the old
    // primary that the directory treats as the same value.
    private const string JaneDoePrimary = "SMTP:jane.doe@test.local";
    private const string JaneDoeAlias = "smtp:jane.doe@test.local";
    private const string JaneSmithPrimary = "SMTP:jane.smith@test.local";
    private const string JaneStaleAlias = "smtp:j.doe@test.local";

    [TestCase(1, TestName = "Export_CaseVariantOfAValueBeingRemoved_DirectoryHonoursPermissiveModify_KeepsTheCaseVariant(sequential)")]
    [TestCase(4, TestName = "Export_CaseVariantOfAValueBeingRemoved_DirectoryHonoursPermissiveModify_KeepsTheCaseVariant(concurrent)")]
    public async Task Export_CaseVariantOfAValueBeingRemoved_DirectoryHonoursPermissiveModify_KeepsTheCaseVariantAsync(int concurrency)
    {
        // Under Permissive Modify, an add of the alias sent ahead of the delete is skipped as already present, and the
        // delete then takes the address away altogether while the export reports success.
        var directory = new FakeDirectory { HonoursPermissiveModify = true }.With("proxyAddresses", JaneDoePrimary);
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("proxyAddresses", JaneSmithPrimary), Add("proxyAddresses", JaneDoeAlias), Remove("proxyAddresses", JaneDoePrimary))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(new[] { JaneSmithPrimary, JaneDoeAlias }));
        }
    }

    [TestCase(nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    public async Task Export_CaseVariantOfAValueBeingRemoved_WithoutPermissiveModify_KeepsTheCaseVariantAsync(string directoryTypeName)
    {
        var directory = new FakeDirectory().With("proxyAddresses", JaneDoePrimary);
        var export = CreateExport(directory, directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("proxyAddresses", JaneSmithPrimary), Add("proxyAddresses", JaneDoeAlias), Remove("proxyAddresses", JaneDoePrimary))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(new[] { JaneSmithPrimary, JaneDoeAlias }));
        }
    }

    [TestCase(1, TestName = "Export_CaseVariantWhileAnotherValueIsAlreadyAbsent_FallbackKeepsTheCaseVariant(sequential)")]
    [TestCase(4, TestName = "Export_CaseVariantWhileAnotherValueIsAlreadyAbsent_FallbackKeepsTheCaseVariant(concurrent)")]
    public async Task Export_CaseVariantWhileAnotherValueIsAlreadyAbsent_FallbackKeepsTheCaseVariantAsync(int concurrency)
    {
        // An alias already gone makes the directory refuse the whole modify, so the fallback applies it in parts. Its add
        // of the new-case alias is refused as already present while the old primary is still there, and must be sent
        // again once that delete has gone through, not counted as done.
        var directory = new FakeDirectory().With("proxyAddresses", JaneDoePrimary);
        var export = CreateExport(directory, concurrency, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("proxyAddresses", JaneSmithPrimary), Add("proxyAddresses", JaneDoeAlias),
                Remove("proxyAddresses", JaneDoePrimary), Remove("proxyAddresses", JaneStaleAlias))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(new[] { JaneSmithPrimary, JaneDoeAlias }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1), "Only the stale alias was already in its desired state");
        }
    }

    [Test]
    public async Task Export_CaseVariantRefusedForAnotherReasonWhenSentAgain_FailsTheExportAsync()
    {
        var directory = new FakeDirectory
        {
            RefuseValue = (modification, value) => modification.Operation == DirectoryAttributeOperation.Add && value == JaneDoeAlias
                ? Rejection(ResultCode.InsufficientAccessRights, "Insufficient access rights to add the alias")
                : null
        }.With("proxyAddresses", JaneDoePrimary);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("proxyAddresses", JaneSmithPrimary), Add("proxyAddresses", JaneDoeAlias),
                Remove("proxyAddresses", JaneDoePrimary), Remove("proxyAddresses", JaneStaleAlias))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False, "The alias never reached the directory");
            Assert.That(results.Single().ErrorMessage, Does.Contain("Insufficient access rights to add the alias"));
        }
    }

    [Test]
    public async Task Export_ValueAlreadyPresentAndStillPresentAfterTheDeletes_CountsAsAchievedAsync()
    {
        var directory = new FakeDirectory().With("proxyAddresses", JaneSmithPrimary, JaneDoeAlias);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("proxyAddresses", JaneDoeAlias), Remove("proxyAddresses", JaneStaleAlias))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(new[] { JaneSmithPrimary, JaneDoeAlias }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(2));
        }
    }

    [TestCase(true, nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(false, nameof(LdapDirectoryType.OpenLDAP))]
    public async Task Export_CollidingPairSplitAcrossModifyBatches_KeepsTheCaseVariantAsync(bool permissiveModify, string directoryTypeName)
    {
        // Twelve addresses out and twelve in, ten to a modification: the new-case alias falls in the first batch of adds
        // and the old primary in the last batch of deletes. Sent in that order, the delete would take the alias away.
        var outgoing = Enumerable.Range(1, 11).Select(i => $"smtp:old{i:D2}@test.local").Append(JaneDoePrimary).ToArray();
        var incoming = new[] { JaneDoeAlias }.Concat(Enumerable.Range(1, 11).Select(i => $"smtp:new{i:D2}@test.local")).ToArray();
        var directory = new FakeDirectory { HonoursPermissiveModify = permissiveModify }.With("proxyAddresses", outgoing);
        var export = CreateExport(directory, advertisesPermissiveModify: permissiveModify, batchSize: 10,
            directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdate([.. incoming.Select(value => Add("proxyAddresses", value)), .. outgoing.Select(value => Remove("proxyAddresses", value))])],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(incoming));
            Assert.That(directory.ModifyRequests.SelectMany(Modifications).Select(m => m.Count), Is.All.LessThanOrEqualTo(10),
                "Every modification still respects the Modify Batch Size");
        }
    }

    [Test]
    public async Task Export_GroupThatMustHaveAMemberLosingAllButACaseVariantOfOne_IsNeverLeftEmptyAsync()
    {
        // groupOfNames must always hold a member. Twelve members leave, and the last of them is kept under a new case.
        // Ten deletes to a modification: the deletes left for a second request must not empty the group.
        var members = Enumerable.Range(1, 12).Select(i => $"CN=Member{i:D2},OU=Users,DC=test,DC=local").ToArray();
        var keptUnderANewCase = members[^1].ToLowerInvariant();
        var directory = new FakeDirectory { AttributeThatMustHaveAValue = "member" }.With("member", members);
        var export = CreateExport(directory, batchSize: 10, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate([.. members.Select(member => Remove("member", member)), Add("member", keptUnderANewCase)])],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { keptUnderANewCase }));
        }
    }

    [TestCase(1, TestName = "Export_CaseVariantWhoseOldSpellingIsAlreadyGone_FallbackKeepsTheNewSpelling(sequential)")]
    [TestCase(4, TestName = "Export_CaseVariantWhoseOldSpellingIsAlreadyGone_FallbackKeepsTheNewSpelling(concurrent)")]
    public async Task Export_CaseVariantWhoseOldSpellingIsAlreadyGone_FallbackKeepsTheNewSpellingAsync(int concurrency)
    {
        // The old primary has already gone, so the directory refuses the whole modify and the fallback applies it in
        // parts. Nothing the directory holds matches the new-case alias, so its add is accepted; a delete of the old
        // spelling sent after it matches it, compared without regard to case, and takes it away.
        var directory = new FakeDirectory().With("proxyAddresses", JaneSmithPrimary);
        var export = CreateExport(directory, concurrency, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Add("proxyAddresses", JaneDoeAlias), Remove("proxyAddresses", JaneDoePrimary))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(new[] { JaneSmithPrimary, JaneDoeAlias }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1), "Only the old primary was already in its desired state");
        }
    }

    [Test]
    public async Task Export_CaseVariantWhileASingleValuedAddOpensTheFallback_DirectoryHonoursPermissiveModify_KeepsTheCaseVariantAsync()
    {
        // The directory holds Intern, not the Contractor the export replaces, so the add of Employee is refused and the
        // whole modify with it. In parts, under Permissive Modify, an add of the alias sent ahead of the delete of the
        // old primary is skipped as already present, and that delete then takes the address away.
        var directory = new FakeDirectory { HonoursPermissiveModify = true, SingleValuedAttributes = { "employeeType" } }
            .With("proxyAddresses", JaneDoePrimary)
            .With("employeeType", "Intern");
        var export = CreateExport(directory, advertisesPermissiveModify: true);

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"],
                Add("proxyAddresses", JaneSmithPrimary), Add("proxyAddresses", JaneDoeAlias), Remove("proxyAddresses", JaneDoePrimary),
                Add("employeeType", "Employee"), Remove("employeeType", "Contractor"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False, "The directory holds a different value from the one the export replaces");
            Assert.That(directory.ValuesOf("proxyAddresses"), Is.EquivalentTo(new[] { JaneSmithPrimary, JaneDoeAlias }));
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Intern" }));
        }
    }

    [Test]
    public async Task Export_SambaMemberAddedUnderANewCaseWhoseOldSpellingIsAlreadyGone_AddsTheMemberAsync()
    {
        // As a Samba AD domain controller answers it (ActiveDirectoryExportProbeTests): the group no longer holds Alice, so
        // the removal of her old spelling is refused and the modify applied in parts. An add of her new spelling sent on
        // its own first is accepted, and the removal of the old spelling then takes her away again.
        var aliceUnderANewCase = Alice.ToLowerInvariant();
        var directory = new FakeDirectory
        {
            ValuePresentCode = ResultCode.EntryAlreadyExists,
            ValuePresentMessage = SambaMemberAlreadyPresent,
            ValueAbsentCode = ResultCode.UnwillingToPerform,
            ValueAbsentMessage = SambaMemberNotPresent
        }.With("member", Bob);
        var export = CreateExport(directory, advertisesPermissiveModify: true, directoryType: LdapDirectoryType.SambaAD);

        var results = await export.ExecuteAsync([GroupUpdate(Remove("member", Alice), Add("member", aliceUnderANewCase))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { aliceUnderANewCase, Bob }));
        }
    }

    [TestCase(1, TestName = "Export_SambaMemberKeptUnderANewCaseWhileAnotherIsAlreadyGone_KeepsTheMember(sequential)")]
    [TestCase(4, TestName = "Export_SambaMemberKeptUnderANewCaseWhileAnotherIsAlreadyGone_KeepsTheMember(concurrent)")]
    public async Task Export_SambaMemberKeptUnderANewCaseWhileAnotherIsAlreadyGone_KeepsTheMemberAsync(int concurrency)
    {
        // Samba AD refuses the removal of a member the group does not hold even under Permissive Modify, so the modify is
        // applied in parts. Where the directory honours the control for adds, the add of Alice under her new case, sent on
        // its own ahead of the delete of her old spelling, is skipped as already present, and that delete then takes her
        // out of the group.
        var aliceUnderANewCase = Alice.ToLowerInvariant();
        var directory = new FakeDirectory
        {
            HonoursPermissiveModify = true,
            PermissiveModifyCoversDeletes = false,
            ValuePresentCode = ResultCode.EntryAlreadyExists,
            ValuePresentMessage = SambaMemberAlreadyPresent,
            ValueAbsentCode = ResultCode.UnwillingToPerform,
            ValueAbsentMessage = SambaMemberNotPresent
        }.With("member", Alice, Bob);
        var export = CreateExport(directory, concurrency, advertisesPermissiveModify: true, directoryType: LdapDirectoryType.SambaAD);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Remove("member", Dave), Remove("member", Alice), Add("member", aliceUnderANewCase))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { aliceUnderANewCase, Bob }));
        }
    }

    [Test]
    public async Task Export_GroupThatMustHaveAMemberKeepingOneUnderANewCaseWhileAnotherIsAlreadyGone_IsNeverLeftEmptyAsync()
    {
        // groupOfNames must always hold a member, and this directory refuses any modify that would leave it without one,
        // so success means the group was never emptied. Old is kept under a new case, Alice leaves, and Bob has already
        // gone, so the whole modify is refused and applied in parts.
        const string old = "CN=Old,OU=Users,DC=test,DC=local";
        var oldUnderANewCase = old.ToLowerInvariant();
        var directory = new FakeDirectory { AttributeThatMustHaveAValue = "member" }.With("member", old, Alice);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate(Remove("member", old), Remove("member", Alice), Remove("member", Bob), Add("member", oldUnderANewCase))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(new[] { oldUnderANewCase }));
        }
    }

    #endregion

    #region A single-valued attribute changed by an add and a remove

    [TestCase(false, TestName = "Export_SingleValuedAttributeChangedByAnAddAndARemove_HoldsTheNewValue(without Permissive Modify)")]
    [TestCase(true, TestName = "Export_SingleValuedAttributeChangedByAnAddAndARemove_HoldsTheNewValue(with Permissive Modify)")]
    public async Task Export_SingleValuedAttributeChangedByAnAddAndARemove_HoldsTheNewValueAsync(bool permissiveModify)
    {
        // Active Directory refuses an add to a single-valued attribute that already holds a value with
        // attributeOrValueExists, which reads exactly like a value already present. Counting that add as done and then
        // deleting the old value would leave the attribute empty while the export reported success.
        var directory = new FakeDirectory { HonoursPermissiveModify = permissiveModify, SingleValuedAttributes = { "employeeType" } }
            .With("employeeType", "Contractor");
        var export = CreateExport(directory, advertisesPermissiveModify: permissiveModify);

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"], Add("employeeType", "Employee"), Remove("employeeType", "Contractor"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Employee" }));
        }
    }

    [TestCase(1, TestName = "Export_SingleValuedAttributeHoldingAnotherValue_AddRefused_FailsTheExport(sequential)")]
    [TestCase(4, TestName = "Export_SingleValuedAttributeHoldingAnotherValue_AddRefused_FailsTheExport(concurrent)")]
    public async Task Export_SingleValuedAttributeHoldingAnotherValue_AddRefused_FailsTheExportAsync(int concurrency)
    {
        // Nothing in the export removes the value the directory holds, so the refusal does not say the new one is there.
        var directory = new FakeDirectory { SingleValuedAttributes = { "employeeType" } }.With("employeeType", "Intern");
        var export = CreateExport(directory, concurrency);

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"], Add("employeeType", "Employee"))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False, "The directory holds a different value from the one the export asked for");
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Intern" }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.Zero);
        }
    }

    [Test]
    public async Task Export_SingleValuedAttributeWhoseOldValueIsAlreadyGoneAndHoldsAnother_FailsTheExportAsync()
    {
        // The export replaces Contractor with Employee, but the directory holds Intern. The delete of Contractor is
        // refused as already absent, so the add is refused again even after it, and nothing says the value held is the
        // one the export asked for.
        var directory = new FakeDirectory { SingleValuedAttributes = { "employeeType" } }.With("employeeType", "Intern");
        var export = CreateExport(directory);

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"], Add("employeeType", "Employee"), Remove("employeeType", "Contractor"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Intern" }));
        }
    }

    [TestCase(nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(nameof(LdapDirectoryType.SambaAD))]
    [TestCase(nameof(LdapDirectoryType.Generic))]
    public async Task Export_SingleValuedAttributeHoldingAnotherValue_ActiveDirectoryFamilyAnswersValueExists_FailsTheExportAsync(string directoryTypeName)
    {
        // Active Directory and Samba AD answer attributeOrValueExists for an add to a single-valued attribute holding any
        // value, so the answer never says the value held is the one asked for. An unrecognised directory is treated the
        // same way, as the cautious reading.
        var directory = new FakeDirectory { SingleValuedAttributes = { "employeeType" } }.With("employeeType", "Intern");
        var export = CreateExport(directory, directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"], Add("employeeType", "Employee"), Add("description", "Staff"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False, "The directory holds a different value from the one the export asked for");
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Intern" }));
        }
    }

    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    public async Task Export_SingleValuedAttributeAlreadyHoldingTheValue_RfcDirectory_CountsAsAchievedAsync(string directoryTypeName)
    {
        // OpenLDAP and 389 Directory Server answer attributeOrValueExists for an add to a single-valued attribute only
        // when it holds that very value; a different value is a constraint violation. So the answer means what it says.
        var directory = new FakeDirectory
        {
            SingleValuedAttributes = { "employeeType" },
            SingleValuedConflictCode = ResultCode.ConstraintViolation,
            SingleValuedConflictMessage = "attribute 'employeeType' cannot have multiple values"
        }.With("employeeType", "Employee");
        var export = CreateExport(directory, directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"], Add("employeeType", "Employee"), Add("description", "Staff"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Employee" }));
            Assert.That(directory.ValuesOf("description"), Is.EquivalentTo(new[] { "Staff" }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
        }
    }

    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    public async Task Export_SingleValuedAttributeHoldingAnotherValue_RfcDirectory_FailsTheExportAsync(string directoryTypeName)
    {
        var directory = new FakeDirectory
        {
            SingleValuedAttributes = { "employeeType" },
            SingleValuedConflictCode = ResultCode.ConstraintViolation,
            SingleValuedConflictMessage = "attribute 'employeeType' cannot have multiple values"
        }.With("employeeType", "Intern");
        var export = CreateExport(directory, directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"], Add("employeeType", "Employee"), Remove("employeeType", "Contractor"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Intern" }));
        }
    }

    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(nameof(LdapDirectoryType.SambaAD))]
    public async Task Export_SingleValuedAttributeChangedByAnAddAndARemoveWhileAnotherValueIsAlreadyAbsent_HoldsTheNewValueAsync(
        string directoryTypeName)
    {
        // The directory holds exactly the value the export replaces, but a description the export removes has already
        // gone, so the whole modify is refused and applied in parts. In parts, the new value must not reach the directory
        // ahead of the delete of the old: OpenLDAP and 389 Directory Server refuse it as a constraint violation, which
        // says nothing about a value already in place, while the old value is still there.
        var directory = SingleValuedDirectory(directoryTypeName).With("employeeType", "Contractor").With("description", "Staff");
        var export = CreateExport(directory, directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"],
                Add("employeeType", "Employee"), Remove("employeeType", "Contractor"), Remove("description", "Temporary"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Employee" }));
            Assert.That(directory.ValuesOf("description"), Is.EquivalentTo(new[] { "Staff" }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1), "Only the description was already in its desired state");
        }
    }

    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    public async Task Export_SingleValuedAttributeWhoseOldValueIsAlreadyGone_RfcDirectory_AddsTheNewValueAsync(string directoryTypeName)
    {
        // Applied in parts, the delete of the old value and the add of the new go in one request, which the directory
        // refuses because the old value has already gone. Sent apart, the delete is found already in place and the add
        // goes through.
        var directory = SingleValuedDirectory(directoryTypeName).With("description", "Staff");
        var export = CreateExport(directory, directoryType: Enum.Parse<LdapDirectoryType>(directoryTypeName));

        var results = await export.ExecuteAsync(
            [GroupUpdateWithSingleValued(["employeeType"],
                Add("employeeType", "Employee"), Remove("employeeType", "Contractor"), Remove("description", "Temporary"))],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("employeeType"), Is.EquivalentTo(new[] { "Employee" }));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(2), "The old value and the description were already gone");
        }
    }

    #endregion

    #region Finding the values already in place by halving

    [TestCase(1, TestName = "Export_OneValueAlreadyPresentAmongManyAdds_FindsItInFarFewerRequestsThanValues(sequential)")]
    [TestCase(4, TestName = "Export_OneValueAlreadyPresentAmongManyAdds_FindsItInFarFewerRequestsThanValues(concurrent)")]
    public async Task Export_OneValueAlreadyPresentAmongManyAdds_FindsItInFarFewerRequestsThanValuesAsync(int concurrency)
    {
        // On a large OpenLDAP group every accepted modify rewrites the entry and logs its old members, so applying a
        // refused 64-value modify one value at a time costs 63 heavy writes for one stale value. Halving costs a handful.
        var users = Users(64);
        var directory = new FakeDirectory().With("member", users[39]);
        var export = CreateExport(directory, concurrency, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync([GroupUpdate([.. users.Select(user => Add("member", user))])], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(users));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1));
            Assert.That(directory.ModifyRequests, Has.Count.LessThanOrEqualTo(13),
                "The refused modify, then two halves at each of six levels: one value at a time took 65");
        }
    }

    [Test]
    public async Task Export_SeveralValuesAlreadyInPlace_CountsValuesNotRequestsAsync()
    {
        var users = Users(64);
        var directory = new FakeDirectory().With("member", users[4], users[19], users[49]);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate([.. users.Select(user => Add("member", user)), Remove("member", Dave), Remove("member", Carol)])],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(users));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(5), "Three members already present and two already absent");
            Assert.That(directory.ModifyRequests, Has.Count.LessThanOrEqualTo(40));
        }
    }

    [Test]
    public async Task Export_MembersKeptUnderANewCaseOneOfWhomIsAlreadyGone_HalvesBetweenPairsNeverWithinOneAsync()
    {
        // Eight members are kept under a new case and the old spelling of one has already gone, so the directory refuses
        // the request carrying the pairs. Halving splits it between pairs, so every new spelling still follows the removal
        // of its old one, and the group, which must hold a member, is never emptied.
        var members = Users(8);
        var underANewCase = members.Select(member => member.ToLowerInvariant()).ToArray();
        var directory = new FakeDirectory { AttributeThatMustHaveAValue = "member" }.With("member", members[..7]);
        var export = CreateExport(directory, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync(
            [GroupUpdate([.. members.Select(member => Remove("member", member)), .. underANewCase.Select(member => Add("member", member))])],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(underANewCase));
            Assert.That(export.ValuesAlreadyInDesiredState, Is.EqualTo(1), "Only the eighth member's old spelling was already gone");
            Assert.That(directory.ModifyRequests, Has.Count.LessThanOrEqualTo(9),
                "The refused modify, two halves at each of three levels, then the last pair's removal and add apart");
        }
    }

    #endregion

    #region A cancelled run does not abandon a batch partway

    [TestCase(1, TestName = "Export_RunCancelledWhileTheFallbackIsUnderway_CompletesTheUpdate(sequential)")]
    [TestCase(4, TestName = "Export_RunCancelledWhileTheFallbackIsUnderway_CompletesTheUpdate(concurrent)")]
    public async Task Export_RunCancelledWhileTheFallbackIsUnderway_CompletesTheUpdateAsync(int concurrency)
    {
        var users = Users(64);
        using var cancellation = new CancellationTokenSource();
        var directory = new FakeDirectory
        {
            AfterModify = sent =>
            {
                if (sent == 3)
                    cancellation.Cancel();
            }
        }.With("member", users[39]);
        var export = CreateExport(directory, concurrency, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync([GroupUpdate([.. users.Select(user => Add("member", user))])], cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(users));
        }
    }

    [Test]
    public async Task Export_RunCancelledPartwayThroughASequentialBatch_ReturnsEveryResultOfTheBatchAsync()
    {
        // JIM records a batch's results only once the Connector returns them, so a batch abandoned partway would lose
        // the result of every export it had already completed, the External ID of a created object among them.
        const string entryUuid = "4f0a5b1c-2d3e-4f50-8a9b-0c1d2e3f4a5b";
        using var cancellation = new CancellationTokenSource();
        var directory = new FakeDirectory
        {
            EntryUuid = entryUuid,
            AfterModify = sent =>
            {
                if (sent == 1)
                    cancellation.Cancel();
            }
        };
        var export = CreateExport(directory, batchSize: 10, directoryType: LdapDirectoryType.OpenLDAP);
        var members = Users(15);

        var results = await export.ExecuteAsync(
            [GroupCreate(members), GroupUpdate(Add("member", members[0]), Add("member", Carol)), GroupUpdate(Remove("member", Dave), Add("member", Bob))],
            cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancellation.IsCancellationRequested, Is.True, "The run was cancelled while the create was under way");
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results.Select(result => result.Success), Is.All.True);
            Assert.That(results[0].ExternalId, Is.EqualTo(entryUuid), "The created object's External ID reaches JIM");
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(members.Append(Carol).Append(Bob)));
        }
    }

    [Test]
    public async Task Export_RunCancelledPartwayThroughAConcurrentBatch_ReturnsEveryResultOfTheBatchAsync()
    {
        // Two exports at a time, so six of the eight are still waiting their turn when the first modify cancels the run.
        // Abandoning them would return no results at all, losing those of the exports already written.
        var users = Users(8);
        using var cancellation = new CancellationTokenSource();
        var directory = new FakeDirectory { AfterModify = _ => cancellation.Cancel() };
        var export = CreateExport(directory, concurrency: 2, directoryType: LdapDirectoryType.OpenLDAP);

        var results = await export.ExecuteAsync([.. users.Select(user => GroupUpdate(Add("member", user)))], cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancellation.IsCancellationRequested, Is.True, "The run was cancelled while the first update was under way");
            Assert.That(results, Has.Count.EqualTo(8));
            Assert.That(results.Select(result => result.Success), Is.All.True);
            Assert.That(directory.ValuesOf("member"), Is.EquivalentTo(users));
        }
    }

    #endregion

    #region Helpers

    private static string[] Users(int count) =>
        [.. Enumerable.Range(1, count).Select(i => $"CN=User{i:D2},OU=Users,DC=test,DC=local")];

    private static LdapConnectorExport CreateExport(
        FakeDirectory directory,
        int concurrency = 1,
        bool advertisesPermissiveModify = false,
        int batchSize = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
        LdapDirectoryType directoryType = LdapDirectoryType.ActiveDirectory) =>
        new(directory, Settings, Log.Logger, concurrency, batchSize, directoryType,
            directoryAdvertisesPermissiveModify: advertisesPermissiveModify);

    /// <summary>
    /// A directory whose employeeType holds one value at most, refusing an add while it holds a different value as the
    /// named kind of directory does: OpenLDAP and 389 Directory Server with constraintViolation, the rest with
    /// attributeOrValueExists.
    /// </summary>
    private static FakeDirectory SingleValuedDirectory(string directoryTypeName) =>
        Enum.Parse<LdapDirectoryType>(directoryTypeName) is LdapDirectoryType.OpenLDAP or LdapDirectoryType.DirectoryServer389
            ? new FakeDirectory
            {
                SingleValuedAttributes = { "employeeType" },
                SingleValuedConflictCode = ResultCode.ConstraintViolation,
                SingleValuedConflictMessage = "attribute 'employeeType' cannot have multiple values"
            }
            : new FakeDirectory { SingleValuedAttributes = { "employeeType" } };

    private static bool CarriesPermissiveModify(ModifyRequest request) =>
        request.Controls.Cast<DirectoryControl>().Any(control => control.Type == PermissiveModifyOid);

    private static List<DirectoryAttributeModification> Modifications(ModifyRequest request) =>
        request.Modifications.Cast<DirectoryAttributeModification>().ToList();

    private static DirectoryOperationException Rejection(ResultCode resultCode, string message) =>
        new(LdapTestResponses.Create<ModifyResponse>(resultCode, message), message);

    private static (PendingExportAttributeChangeType ChangeType, string Attribute, string Value) Add(string attribute, string value) =>
        (PendingExportAttributeChangeType.Add, attribute, value);

    private static (PendingExportAttributeChangeType ChangeType, string Attribute, string Value) Remove(string attribute, string value) =>
        (PendingExportAttributeChangeType.Remove, attribute, value);

    private static (PendingExportAttributeChangeType ChangeType, string Attribute, string Value) Replace(string attribute, string value) =>
        (PendingExportAttributeChangeType.Update, attribute, value);

    private static PendingExport GroupUpdate(params (PendingExportAttributeChangeType ChangeType, string Attribute, string Value)[] changes) =>
        GroupUpdateWithSingleValued([], changes);

    /// <summary>
    /// An update whose attributes are all multi-valued in the Connected System's schema except those named, which are
    /// single-valued: the target of a multi-valued Metaverse attribute (#435), whose change JIM stages as an add of the
    /// new value and a remove of the old.
    /// </summary>
    private static PendingExport GroupUpdateWithSingleValued(
        string[] singleValuedAttributes,
        params (PendingExportAttributeChangeType ChangeType, string Attribute, string Value)[] changes)
    {
        var objectType = new ConnectedSystemObjectType { Name = "group" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = objectType };
        var attributes = new Dictionary<string, ConnectedSystemObjectTypeAttribute>(StringComparer.OrdinalIgnoreCase)
        {
            ["distinguishedName"] = dnAttribute
        };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = objectType,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { AttributeId = dnAttribute.Id, StringValue = GroupDn }]
            },
            AttributeValueChanges = changes.Select(change => new PendingExportAttributeValueChange
            {
                Attribute = AttributeNamed(change.Attribute),
                ChangeType = change.ChangeType,
                StringValue = change.Value
            }).ToList()
        };

        ConnectedSystemObjectTypeAttribute AttributeNamed(string name)
        {
            if (!attributes.TryGetValue(name, out var attribute))
            {
                attribute = new ConnectedSystemObjectTypeAttribute
                {
                    Id = attributes.Count + 1,
                    Name = name,
                    ConnectedSystemObjectType = objectType,
                    AttributePlurality = singleValuedAttributes.Contains(name, StringComparer.OrdinalIgnoreCase)
                        ? AttributePlurality.SingleValued
                        : AttributePlurality.MultiValued
                };
                attributes[name] = attribute;
            }

            return attribute;
        }
    }

    private static PendingExport GroupCreate(IEnumerable<string> members)
    {
        var objectType = new ConnectedSystemObjectType { Name = "group" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = objectType };
        var memberAttribute = new ConnectedSystemObjectTypeAttribute
            { Id = 2, Name = "member", ConnectedSystemObjectType = objectType, AttributePlurality = AttributePlurality.MultiValued };

        var changes = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = dnAttribute, ChangeType = PendingExportAttributeChangeType.Add, StringValue = GroupDn }
        };
        changes.AddRange(members.Select(member => new PendingExportAttributeValueChange
        {
            Attribute = memberAttribute,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = member
        }));

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = objectType },
            AttributeValueChanges = changes
        };
    }

    /// <summary>
    /// One directory entry that answers requests the way an LDAP directory does. A modify is atomic: when any Add names
    /// a value already present, or any Delete names one that is not, the whole request is rejected and nothing in it is
    /// applied, unless it carries the Permissive Modify control and this directory honours it.
    /// </summary>
    private sealed class FakeDirectory : ILdapOperationExecutor
    {
        private readonly Dictionary<string, HashSet<string>> _entry = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public List<ModifyRequest> ModifyRequests { get; } = [];
        public bool HonoursPermissiveModify { get; init; }
        public ResultCode ValuePresentCode { get; init; } = ResultCode.AttributeOrValueExists;
        public string ValuePresentMessage { get; init; } = "modify/add: member: value #0 already exists";
        public ResultCode ValueAbsentCode { get; init; } = ResultCode.NoSuchAttribute;
        public string ValueAbsentMessage { get; init; } = "modify/delete: member: no such value";

        /// <summary>
        /// Whether Permissive Modify, where honoured, also covers the delete of a value already absent. Samba AD refuses the
        /// removal of a member the group does not hold even with it.
        /// </summary>
        public bool PermissiveModifyCoversDeletes { get; init; } = true;

        /// <summary>The entryUUID a search of the entry returns, or null to return no entry.</summary>
        public string? EntryUuid { get; init; }

        /// <summary>A genuine refusal for a particular value, or null to let it through.</summary>
        public Func<DirectoryAttributeModification, string?, DirectoryOperationException?>? RefuseValue { get; init; }

        /// <summary>An attribute the entry's class requires to hold at least one value, as groupOfNames requires member.</summary>
        public string? AttributeThatMustHaveAValue { get; init; }

        /// <summary>Attributes the entry may hold only while it carries the named object class.</summary>
        public Dictionary<string, string> ClassRequiredByAttribute { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Attributes that hold one value at most. An add to one that already holds a different value is refused with
        /// <see cref="SingleValuedConflictCode"/>, Permissive Modify or not, at the point in the modify it arrives: a
        /// delete earlier in the same request makes room for it, one later does not.
        /// </summary>
        public HashSet<string> SingleValuedAttributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How an add to a single-valued attribute holding a different value is refused: attributeOrValueExists by
        /// default, as Active Directory and Samba AD answer it; OpenLDAP and 389 Directory Server answer constraintViolation.
        /// </summary>
        public ResultCode SingleValuedConflictCode { get; init; } = ResultCode.AttributeOrValueExists;

        public string SingleValuedConflictMessage { get; init; } = "00002083: AtrErr: problem 1006 (ATT_OR_VALUE_EXISTS)";

        /// <summary>Called after each modify is answered, with how many have been sent so far.</summary>
        public Action<int>? AfterModify { get; init; }

        public FakeDirectory With(string attribute, params string[] values)
        {
            Values(_entry, attribute).UnionWith(values);
            return this;
        }

        public IReadOnlyCollection<string> ValuesOf(string attribute)
        {
            lock (_lock)
                return _entry.TryGetValue(attribute, out var values) ? values.ToList() : [];
        }

        public DirectoryResponse SendRequest(DirectoryRequest request) => request switch
        {
            ModifyRequest modify => Modify(modify),
            AddRequest add => Add(add),
            ModifyDNRequest => LdapTestResponses.Create<ModifyDNResponse>(ResultCode.Success),
            SearchRequest search => EntryUuid == null
                ? LdapTestResponses.EmptySearchResponse()
                : LdapTestResponses.SearchResponseWith(search.DistinguishedName, ("entryUUID", EntryUuid)),
            _ => throw new NotSupportedException($"The fake directory does not answer {request.GetType().Name}")
        };

        public DirectoryResponse SendRequest(DirectoryRequest request, TimeSpan timeout) => SendRequest(request);

        public Task<DirectoryResponse> SendRequestAsync(DirectoryRequest request)
        {
            try
            {
                return Task.FromResult(SendRequest(request));
            }
            catch (DirectoryOperationException ex)
            {
                return Task.FromException<DirectoryResponse>(ex);
            }
        }

        private AddResponse Add(AddRequest request)
        {
            lock (_lock)
            {
                foreach (var attribute in request.Attributes.Cast<DirectoryAttribute>())
                    Values(_entry, attribute.Name).UnionWith(StringValues(attribute));
            }

            return LdapTestResponses.Create<AddResponse>(ResultCode.Success);
        }

        private ModifyResponse Modify(ModifyRequest request)
        {
            try
            {
                lock (_lock)
                {
                    ModifyRequests.Add(request);
                    var permissive = HonoursPermissiveModify &&
                                     request.Controls.Cast<DirectoryControl>().Any(control => control.Type == PermissiveModifyOid);

                    // Work on a copy, committed only when every modification is accepted: a modify is atomic.
                    var working = _entry.ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var modification in request.Modifications.Cast<DirectoryAttributeModification>())
                        Apply(working, modification, permissive);

                    CheckSchema(working);

                    _entry.Clear();
                    foreach (var (name, values) in working)
                        _entry[name] = values;
                }
            }
            finally
            {
                AfterModify?.Invoke(ModifyRequests.Count);
            }

            return LdapTestResponses.Create<ModifyResponse>(ResultCode.Success);
        }

        private void Apply(Dictionary<string, HashSet<string>> entry, DirectoryAttributeModification modification, bool permissive)
        {
            var current = Values(entry, modification.Name);
            var named = StringValues(modification);

            if (named.Count == 0 && RefuseValue?.Invoke(modification, null) is { } wholeAttributeRefusal)
                throw wholeAttributeRefusal;

            foreach (var value in named)
            {
                if (modification.Operation == DirectoryAttributeOperation.Add && SingleValuedAttributes.Contains(modification.Name) &&
                    current.Count > 0 && !current.Contains(value))
                    throw Rejection(SingleValuedConflictCode, SingleValuedConflictMessage);
                if (!permissive && modification.Operation == DirectoryAttributeOperation.Add && current.Contains(value))
                    throw Rejection(ValuePresentCode, ValuePresentMessage);
                if (!(permissive && PermissiveModifyCoversDeletes) && modification.Operation == DirectoryAttributeOperation.Delete &&
                    !current.Contains(value))
                    throw Rejection(ValueAbsentCode, ValueAbsentMessage);
                if (RefuseValue?.Invoke(modification, value) is { } refusal)
                    throw refusal;
            }

            switch (modification.Operation)
            {
                case DirectoryAttributeOperation.Add:
                    current.UnionWith(named);
                    break;
                case DirectoryAttributeOperation.Delete when named.Count == 0:
                    current.Clear();
                    break;
                case DirectoryAttributeOperation.Delete:
                    current.ExceptWith(named);
                    break;
                case DirectoryAttributeOperation.Replace:
                    current.Clear();
                    current.UnionWith(named);
                    break;
            }
        }

        private void CheckSchema(Dictionary<string, HashSet<string>> entry)
        {
            if (AttributeThatMustHaveAValue != null && Values(entry, AttributeThatMustHaveAValue).Count == 0)
                throw Rejection(ResultCode.ObjectClassViolation, $"object class 'groupOfNames' requires attribute '{AttributeThatMustHaveAValue}'");

            var classes = Values(entry, "objectClass");
            var disallowed = ClassRequiredByAttribute
                .FirstOrDefault(rule => Values(entry, rule.Key).Count > 0 && !classes.Contains(rule.Value));
            if (disallowed.Key != null)
                throw Rejection(ResultCode.ObjectClassViolation, $"attribute '{disallowed.Key}' not allowed");
        }

        private static HashSet<string> Values(Dictionary<string, HashSet<string>> entry, string attribute)
        {
            if (!entry.TryGetValue(attribute, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                entry[attribute] = values;
            }

            return values;
        }

        private static List<string> StringValues(DirectoryAttribute attribute) =>
            Enumerable.Range(0, attribute.Count).Select(i => attribute[i] as string ?? string.Empty).ToList();
    }

    #endregion
}

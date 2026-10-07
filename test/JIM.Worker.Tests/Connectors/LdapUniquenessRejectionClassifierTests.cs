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
/// Rejection classification for the LDAP Connector (Unique Value Generation, #242, release 4, decision 9): which
/// directory rejections mean "this value is already in use", and which attribute the directory named.
/// <para>
/// Every fixture marked "captured" is the exact text a real server returned, provoked against the integration
/// harness's own directories on 2026-10-06 through System.DirectoryServices.Protocols (the same library the
/// Connector uses): Samba AD from the jim-samba-ad image, OpenLDAP from the jim-openldap image with its slapo-unique
/// overlay, 389 Directory Server 3.1 with its Attribute Uniqueness plug-in enabled for mail. The Active Directory
/// fixtures are the documented shapes of Windows Server's messages (the extended error code, DSID, problem and, for
/// a constraint, the "Att" clause). Samba's codes differ from Active Directory's (its account name collision is
/// 00002071, Active Directory's DN collision code), which is why classification reads the message and not the code
/// alone.
/// </para>
/// </summary>
[TestFixture]
public class LdapUniquenessRejectionClassifierTests
{
    private static IEnumerable<TestCaseData> ClassifiedRejections()
    {
        // ---- Active Directory ----
        yield return new TestCaseData(ResultCode.EntryAlreadyExists,
                "00000524: UpdErr: DSID-031A11E2, problem 6005 (ENTRY_EXISTS), data 0\n", "sAMAccountName")
            .SetName("ActiveDirectory_AccountNameInUse_ERROR_USER_EXISTS_IsSAMAccountName");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "000021C8: AtrErr: DSID-03200BBA, #1:\n\t0: 000021C8: DSID-03200BBA, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName)\n",
                "userPrincipalName")
            .SetName("ActiveDirectory_UpnNotUniqueInForest_IsUserPrincipalName");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "000021C8: AtrErr: DSID-03200BBA, problem 1005 (CONSTRAINT_ATT_TYPE), data 0\n", "userPrincipalName")
            .SetName("ActiveDirectory_UpnNotUniqueInForestWithoutAttClause_IsUserPrincipalName");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "000021C7: AtrErr: DSID-03200BBA, #1:\n\t0: 000021C7: DSID-03200BBA, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90303 (servicePrincipalName)\n",
                "servicePrincipalName")
            .SetName("ActiveDirectory_SpnNotUniqueInForest_IsServicePrincipalName");
        yield return new TestCaseData(ResultCode.EntryAlreadyExists,
                "00002071: UpdErr: DSID-030502F1, problem 6005 (ENTRY_EXISTS), data 0\n", "distinguishedName")
            .SetName("ActiveDirectory_ObjectAlreadyExists_IsDistinguishedName");

        // ---- Samba AD (captured) ----
        yield return new TestCaseData(ResultCode.EntryAlreadyExists,
                "00002071: samldb: sAMAccountName 'jimprobe1' already in use!", "sAMAccountName")
            .SetName("SambaAD_AccountNameInUse_Captured_IsSAMAccountNameNotTheDn");
        yield return new TestCaseData(ResultCode.EntryAlreadyExists,
                "00002071: samldb: sAMAccountName 'JIMPROBE1' already in use!", "sAMAccountName")
            .SetName("SambaAD_AccountNameInUseDifferingOnlyInCase_Captured_IsSAMAccountName");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "0000202F: samldb: userPrincipalName 'jimprobe1@panoply.local' is already in use ", "userPrincipalName")
            .SetName("SambaAD_UpnInUse_Captured_IsUserPrincipalName");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "0000202F: samldb: spn[HOST/jimprobe] would cause a conflict", "servicePrincipalName")
            .SetName("SambaAD_SpnConflict_Captured_IsServicePrincipalName");
        yield return new TestCaseData(ResultCode.EntryAlreadyExists,
                "Entry CN=Jim Probe One,CN=Users,DC=panoply,DC=local already exists", "distinguishedName")
            .SetName("SambaAD_EntryExistsOnCreateOrRename_Captured_IsDistinguishedName");

        // ---- OpenLDAP (captured, slapo-unique on mail) ----
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "non-unique attributes found with (|(mail=jim.probe@yellowstone.local))", "mail")
            .SetName("OpenLDAP_UniqueOverlay_Captured_IsTheFilterAttribute");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "non-unique attributes found with (|(mail=a@yellowstone.local)(mail=b@yellowstone.local))", "mail")
            .SetName("OpenLDAP_UniqueOverlayTwoValuesOfOneAttribute_IsThatAttribute");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "non-unique attributes found with (|(mail=a@yellowstone.local)(uid=ada))", null)
            .SetName("OpenLDAP_UniqueOverlayTwoAttributes_IsClassifiedButUnattributed");
        yield return new TestCaseData(ResultCode.ConstraintViolation, "some attributes not unique", null)
            .SetName("OpenLDAP_UniqueOverlayOlderWording_IsClassifiedButUnattributed");
        yield return new TestCaseData(ResultCode.EntryAlreadyExists, "", "distinguishedName")
            .SetName("OpenLDAP_EntryExistsWithNoText_Captured_IsDistinguishedName");

        // ---- 389 Directory Server (captured, Attribute Uniqueness plug-in on mail) ----
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "Another entry with the same attribute value already exists (attribute: \"mail \")", "mail")
            .SetName("DirectoryServer389_AttributeUniqueness_Captured_IsTheNamedAttribute");

        // ---- Any other directory ----
        yield return new TestCaseData(ResultCode.EntryAlreadyExists, null, "distinguishedName")
            .SetName("Generic_EntryAlreadyExistsWithNoMessage_IsDistinguishedName");
        yield return new TestCaseData(ResultCode.EntryAlreadyExists, "The entry could not be added.", "distinguishedName")
            .SetName("Generic_EntryAlreadyExistsWithUnrecognisedText_IsDistinguishedName");
    }

    private static IEnumerable<TestCaseData> UnclassifiedRejections()
    {
        // A bare constraint violation can be a schema or policy refusal; classifying it would have Collision
        // Remediation revise a value that was never in use.
        yield return new TestCaseData(ResultCode.ConstraintViolation, "displayName: multiple values provided")
            .SetName("OpenLDAP_SingleValuedAttributeGivenTwo_Captured_IsNotClassified");
        yield return new TestCaseData(ResultCode.ConstraintViolation, "Password fails quality checking policy")
            .SetName("OpenLDAP_PasswordPolicy_Captured_IsNotClassified");
        yield return new TestCaseData(ResultCode.ConstraintViolation, null)
            .SetName("Generic_ConstraintViolationWithNoMessage_IsNotClassified");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "0000052D: Constraint violation - check_password_restrictions: the password is too short. It should be equal or longer than 7 characters!")
            .SetName("SambaAD_PasswordTooShort_IsNotClassified");
        yield return new TestCaseData(ResultCode.ConstraintViolation,
                "00002082: AtrErr: DSID-03151904, #1:\n\t0: 00002082: DSID-03151904, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 9005a (userAccountControl)\n")
            .SetName("ActiveDirectory_ConstraintOnAnotherAttribute_IsNotClassified");
        yield return new TestCaseData(ResultCode.InvalidAttributeSyntax,
                "0000200B: objectclass_attrs: attribute 'userAccountControl' on entry 'CN=Jim Probe Two,CN=Users,DC=panoply,DC=local' contains at least one invalid value!")
            .SetName("SambaAD_InvalidSyntax_Captured_IsNotClassified");
        yield return new TestCaseData(ResultCode.UnwillingToPerform,
                "error in module samldb: Unwilling to perform during LDB_MODIFY (53)")
            .SetName("SambaAD_UnwillingToPerform_Captured_IsNotClassified");
        yield return new TestCaseData(ResultCode.NamingViolation, "00002037: Naming violation")
            .SetName("SambaAD_NamingViolation_Captured_IsNotClassified");
        yield return new TestCaseData(ResultCode.ObjectClassViolation, "single-valued attribute \"displayName\" has multiple values\n")
            .SetName("DirectoryServer389_SingleValuedAttributeGivenTwo_Captured_IsNotClassified");
        yield return new TestCaseData(ResultCode.InsufficientAccessRights, "no write access to parent")
            .SetName("OpenLDAP_InsufficientAccess_Captured_IsNotClassified");
        // The message wording alone, under a result code that does not mean "in use", is not enough.
        yield return new TestCaseData(ResultCode.Other, "00002071: samldb: sAMAccountName 'jimprobe1' already in use!")
            .SetName("UniquenessWordingUnderAnUnrelatedResultCode_IsNotClassified");
    }

    [TestCaseSource(nameof(ClassifiedRejections))]
    public void TryClassify_UniquenessRejection_IsClassifiedWithTheAttributeTheServerNamed(ResultCode resultCode, string? serverMessage, string? expectedAttribute)
    {
        var classified = LdapUniquenessRejectionClassifier.TryClassify(resultCode, serverMessage, out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.True);
            Assert.That(attributeName, Is.EqualTo(expectedAttribute));
        }
    }

    [TestCaseSource(nameof(UnclassifiedRejections))]
    public void TryClassify_OtherRejection_IsNotClassified(ResultCode resultCode, string? serverMessage)
    {
        var classified = LdapUniquenessRejectionClassifier.TryClassify(resultCode, serverMessage, out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.False);
            Assert.That(attributeName, Is.Null);
        }
    }

    [Test]
    public void Rules_EveryRule_NamesItsServerFamilyAndResultCode()
    {
        // The table is the documentation: an administrator-facing question ("why was this classified?") is answered
        // by reading the rule that matched, so every rule must say whose message it reads.
        Assert.That(LdapUniquenessRejectionClassifier.Rules, Is.Not.Empty);
        foreach (var rule in LdapUniquenessRejectionClassifier.Rules)
            Assert.That(rule.ServerFamily, Is.Not.Empty, $"Rule '{rule.Description}' names no server family.");
    }

    // ---- Through the export path ----

    [Test]
    public async Task ExecuteAsync_DirectoryRejectsAnAccountNameInUse_ReturnsAClassifiedResultCarryingTheServerTextAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(
                LdapTestResponses.Create<AddResponse>(ResultCode.EntryAlreadyExists, "00002071: samldb: sAMAccountName 'jimprobe1' already in use!")));

        var results = await Export(executor, LdapDirectoryType.SambaAD).ExecuteAsync([UserCreate("CN=Jim Probe Three,CN=Users,DC=panoply,DC=local")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(results[0].RejectedAttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(results[0].ErrorMessage, Does.Contain("samldb: sAMAccountName 'jimprobe1' already in use!"));
        }
    }

    [Test]
    public async Task ExecuteAsync_ConstraintViolationThatIsNotUniqueness_StaysAGeneralFailureAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(
                LdapTestResponses.Create<AddResponse>(ResultCode.ConstraintViolation, "displayName: multiple values provided")));

        var results = await Export(executor, LdapDirectoryType.OpenLDAP).ExecuteAsync([UserCreate("uid=ada,ou=People,dc=yellowstone,dc=local")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.General));
            Assert.That(results[0].RejectedAttributeName, Is.Null);
        }
    }

    [Test]
    public async Task ExecuteAsync_RenameOntoAnExistingEntry_IsClassifiedAsTheDistinguishedNameAsync()
    {
        // A rename's failure is raised by the Connector itself (an LdapException carrying the result code) rather
        // than by the library, so classification must read both shapes.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyDNRequest>()))
            .Returns(LdapTestResponses.Create<ModifyDNResponse>(ResultCode.EntryAlreadyExists, ""));

        var results = await Export(executor, LdapDirectoryType.OpenLDAP)
            .ExecuteAsync([UserRename("uid=ada,ou=People,dc=yellowstone,dc=local", "uid=grace,ou=People,dc=yellowstone,dc=local")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(results[0].RejectedAttributeName, Is.EqualTo("distinguishedName"));
        }
    }

    [Test]
    public async Task ExecuteAsync_ConcurrentPath_ClassifiesTheSameWayAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequestAsync(It.IsAny<AddRequest>()))
            .ThrowsAsync(new DirectoryOperationException(
                LdapTestResponses.Create<AddResponse>(ResultCode.ConstraintViolation, "non-unique attributes found with (|(mail=jim.probe@yellowstone.local))")));
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(
                LdapTestResponses.Create<AddResponse>(ResultCode.ConstraintViolation, "non-unique attributes found with (|(mail=jim.probe@yellowstone.local))")));

        var results = await Export(executor, LdapDirectoryType.OpenLDAP, concurrency: 4)
            .ExecuteAsync([UserCreate("uid=ada,ou=People,dc=yellowstone,dc=local"), UserCreate("uid=bob,ou=People,dc=yellowstone,dc=local")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.ErrorType), Is.All.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(results.Select(r => r.RejectedAttributeName), Is.All.EqualTo("mail"));
        }
    }

    [Test]
    public async Task ExecuteAsync_GroupWithPlaceholderRejectedForAMailInUse_IsAUniquenessRejectionNotAPlaceholderOneAsync()
    {
        // OpenLDAP's jimGroup carries mail, so a group create can collide on it. The placeholder handling reads any
        // constraint violation on a group carrying a placeholder member as the placeholder being refused; that
        // would send an administrator to fix a setting that is not wrong.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(
                LdapTestResponses.Create<AddResponse>(ResultCode.ConstraintViolation, "non-unique attributes found with (|(mail=team@yellowstone.local))")));

        var results = await Export(executor, LdapDirectoryType.OpenLDAP).ExecuteAsync([EmptyGroupCreate("cn=team,ou=Groups,dc=yellowstone,dc=local")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(results[0].RejectedAttributeName, Is.EqualTo("mail"));
        }
    }

    private static LdapConnectorExport Export(Mock<ILdapOperationExecutor> executor, LdapDirectoryType directoryType, int concurrency = 1) =>
        new(executor.Object,
            [new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }],
            Log.Logger,
            concurrency,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
            directoryType,
            placeholderMemberDn: null);

    private static PendingExport UserCreate(string dn)
    {
        var type = new ConnectedSystemObjectType { Name = "user" };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = type },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = dn
                },
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "mail", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = "jim.probe@yellowstone.local"
                }
            ]
        };
    }

    private static PendingExport UserRename(string currentDn, string newDn)
    {
        var type = new ConnectedSystemObjectType { Name = "user" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = type };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = type,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = dnAttribute, AttributeId = dnAttribute.Id, StringValue = currentDn }]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange { Attribute = dnAttribute, ChangeType = PendingExportAttributeChangeType.Update, StringValue = newDn }
            ]
        };
    }

    private static PendingExport EmptyGroupCreate(string dn)
    {
        var type = new ConnectedSystemObjectType { Name = "groupOfNames" };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = type },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = dn
                },
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "mail", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = "team@yellowstone.local"
                }
            ]
        };
    }
}

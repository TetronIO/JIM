// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using NUnit.Framework;

namespace JIM.Models.Tests.Exchange;

/// <summary>
/// The rules export staging applies before queuing an Exchange change (#2021): whether a declared recipient type fits
/// the object (PRD requirement 5), whether it changes an existing recipient's type (requirement 16), and the decision
/// the two make together. Pure functions, so every row of the tables is pinned here rather than through an export.
/// </summary>
[TestFixture]
public class ExchangeRecipientRulesTests
{
    // groupType values as Active Directory stores them: the scope bits, with the security bit set for security groups.
    private const long GlobalSecurityGroup = unchecked((int)0x80000002);
    private const long DomainLocalSecurityGroup = unchecked((int)0x80000004);
    private const long UniversalSecurityGroup = unchecked((int)0x80000008);
    private const long UniversalDistributionGroup = 0x8;
    private const long GlobalDistributionGroup = 0x2;

    #region Type fit (requirement 5)

    [TestCase("user", ExchangeRecipientTypes.Mailbox)]
    [TestCase("user", ExchangeRecipientTypes.SharedMailbox)]
    [TestCase("user", ExchangeRecipientTypes.RoomMailbox)]
    [TestCase("user", ExchangeRecipientTypes.EquipmentMailbox)]
    [TestCase("user", ExchangeRecipientTypes.MailUser)]
    [TestCase("User", ExchangeRecipientTypes.Mailbox)]
    [TestCase("contact", ExchangeRecipientTypes.MailContact)]
    [TestCase("CONTACT", ExchangeRecipientTypes.MailContact)]
    public void TypeFitRefusal_TypeThatFitsTheObject_ReturnsNull(string objectTypeName, string recipientType)
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal(objectTypeName, recipientType, groupType: null), Is.Null);
    }

    [TestCase("user")]
    [TestCase("group")]
    [TestCase("contact")]
    [TestCase("computer")]
    public void TypeFitRefusal_None_FitsAnything(string objectTypeName)
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal(objectTypeName, ExchangeRecipientTypes.None, GlobalSecurityGroup), Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    public void TypeFitRefusal_NothingDeclared_FitsAnythingAsNone(string? recipientType)
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal("group", recipientType, GlobalSecurityGroup), Is.Null);
    }

    [TestCase("group", ExchangeRecipientTypes.Mailbox)]
    [TestCase("group", ExchangeRecipientTypes.SharedMailbox)]
    [TestCase("contact", ExchangeRecipientTypes.RoomMailbox)]
    [TestCase("contact", ExchangeRecipientTypes.EquipmentMailbox)]
    [TestCase("group", ExchangeRecipientTypes.MailUser)]
    [TestCase("user", ExchangeRecipientTypes.MailContact)]
    [TestCase("group", ExchangeRecipientTypes.MailContact)]
    [TestCase("user", ExchangeRecipientTypes.MailEnabledGroup)]
    [TestCase("contact", ExchangeRecipientTypes.MailEnabledGroup)]
    [TestCase("computer", ExchangeRecipientTypes.Mailbox)]
    public void TypeFitRefusal_TypeThatDoesNotFitTheObject_ExplainsWhy(string objectTypeName, string recipientType)
    {
        var refusal = ExchangeRecipientRules.TypeFitRefusal(objectTypeName, recipientType, groupType: null);

        Assert.That(refusal, Is.Not.Null.And.Contains($"this object is a {objectTypeName}"));
    }

    [Test]
    public void TypeFitRefusal_MailboxOnAGroup_SaysOnlyAUserCanHaveOne()
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal("group", ExchangeRecipientTypes.Mailbox, groupType: null),
            Is.EqualTo("Only a user can have a mailbox; this object is a group."));
    }

    [Test]
    public void TypeFitRefusal_MailContactOnAUser_SaysOnlyAContactCanBeOne()
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal("user", ExchangeRecipientTypes.MailContact, groupType: null),
            Is.EqualTo("Only a contact can be a mail contact; this object is a user."));
    }

    [TestCase(UniversalSecurityGroup)]
    [TestCase(UniversalDistributionGroup)]
    public void TypeFitRefusal_MailEnabledUniversalGroup_ReturnsNull(long groupType)
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal("group", ExchangeRecipientTypes.MailEnabledGroup, groupType), Is.Null);
    }

    [TestCase(GlobalSecurityGroup)]
    [TestCase(DomainLocalSecurityGroup)]
    [TestCase(GlobalDistributionGroup)]
    public void TypeFitRefusal_MailEnabledGroupThatIsNotUniversal_SaysOnlyUniversalGroupsCanBe(long groupType)
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal("group", ExchangeRecipientTypes.MailEnabledGroup, groupType),
            Is.EqualTo("Only universal groups can be mail-enabled; this group is not universal."));
    }

    /// <summary>
    /// A group whose scope JIM does not know (groupType not imported or flowed) is left to Exchange, which refuses a
    /// group that is not universal itself; refusing here would block every group whose groupType is not selected.
    /// </summary>
    [Test]
    public void TypeFitRefusal_MailEnabledGroupWhoseScopeIsUnknown_ReturnsNull()
    {
        Assert.That(ExchangeRecipientRules.TypeFitRefusal("group", ExchangeRecipientTypes.MailEnabledGroup, groupType: null), Is.Null);
    }

    [TestCase("Mailboxx")]
    [TestCase("mailbox")]
    [TestCase("RemoteMailbox")]
    public void TypeFitRefusal_UnrecognisedRecipientType_NamesTheValueAndTheTypesJimRecognises(string recipientType)
    {
        var refusal = ExchangeRecipientRules.TypeFitRefusal("user", recipientType, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal, Does.Contain($"'{recipientType}'"));
            Assert.That(refusal, Does.Contain(ExchangeRecipientTypes.MailEnabledGroup));
        }
    }

    #endregion

    #region Type change (requirement 16)

    [TestCase(null, ExchangeRecipientTypes.Mailbox)]
    [TestCase("", ExchangeRecipientTypes.MailUser)]
    [TestCase(ExchangeRecipientTypes.None, ExchangeRecipientTypes.SharedMailbox)]
    public void ClassifyChange_FromNoneToAType_IsAnEnable(string? imported, string declared)
    {
        Assert.That(ExchangeRecipientRules.ClassifyChange(imported, declared), Is.EqualTo(ExchangeRecipientChangeKind.Enable));
    }

    [TestCase(ExchangeRecipientTypes.Mailbox)]
    [TestCase(ExchangeRecipientTypes.MailContact)]
    public void ClassifyChange_SameType_IsAnUpdate(string type)
    {
        Assert.That(ExchangeRecipientRules.ClassifyChange(type, type), Is.EqualTo(ExchangeRecipientChangeKind.Update));
    }

    [TestCase(ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.SharedMailbox)]
    [TestCase(ExchangeRecipientTypes.MailUser, ExchangeRecipientTypes.Mailbox)]
    [TestCase(ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.None)]
    [TestCase(ExchangeRecipientTypes.MailContact, null)]
    public void ClassifyChange_AnyOtherChange_IsATypeChange(string imported, string? declared)
    {
        Assert.That(ExchangeRecipientRules.ClassifyChange(imported, declared), Is.EqualTo(ExchangeRecipientChangeKind.TypeChange));
    }

    #endregion

    #region Decision

    [TestCase(null, null)]
    [TestCase(ExchangeRecipientTypes.None, ExchangeRecipientTypes.None)]
    [TestCase(null, "")]
    public void Decide_NoneDeclaredAndNoneImported_IsNotARecipient(string? imported, string? declared)
    {
        var decision = ExchangeRecipientRules.Decide("user", imported, declared, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.NotARecipient));
            Assert.That(decision.Reason, Is.Null);
        }
    }

    [Test]
    public void Decide_TypeThatDoesNotFit_IsRefusedWithTheReason()
    {
        var decision = ExchangeRecipientRules.Decide("group", null, ExchangeRecipientTypes.MailEnabledGroup, GlobalSecurityGroup);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Refused));
            Assert.That(decision.Reason, Is.EqualTo("Only universal groups can be mail-enabled; this group is not universal."));
        }
    }

    /// <summary>
    /// A misfit is refused even on an existing recipient whose type it would also change: the declaration cannot be
    /// right for this object whatever it is in Exchange today, so the refusal is the more useful thing to report.
    /// </summary>
    [Test]
    public void Decide_TypeThatDoesNotFitAndWouldChangeTheType_IsRefused()
    {
        var decision = ExchangeRecipientRules.Decide("user", ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.MailContact, groupType: null);

        Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Refused));
    }

    [Test]
    public void Decide_TypeChange_IsNotSupportedWithASentenceNamingBothTypes()
    {
        var decision = ExchangeRecipientRules.Decide("user", ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.SharedMailbox, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.TypeChangeNotSupported));
            Assert.That(decision.Reason, Does.Contain("not supported in this release"));
            Assert.That(decision.Reason, Does.Contain("a mailbox"));
            Assert.That(decision.Reason, Does.Contain("a shared mailbox"));
        }
    }

    [Test]
    public void Decide_ChangeOfAnExistingRecipientToNone_IsNotSupported()
    {
        var decision = ExchangeRecipientRules.Decide("contact", ExchangeRecipientTypes.MailContact, ExchangeRecipientTypes.None, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.TypeChangeNotSupported));
            Assert.That(decision.Reason, Does.Contain("a mail contact"));
            Assert.That(decision.Reason, Does.Contain("not to be a recipient"));
        }
    }

    [TestCase(null, ExchangeRecipientTypes.Mailbox, ExchangeRecipientChangeKind.Enable)]
    [TestCase(ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.Mailbox, ExchangeRecipientChangeKind.Update)]
    public void Decide_EnableOrUpdateThatFits_IsStaged(string? imported, string declared, ExchangeRecipientChangeKind expectedChange)
    {
        var decision = ExchangeRecipientRules.Decide("user", imported, declared, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Stage));
            Assert.That(decision.Change, Is.EqualTo(expectedChange));
            Assert.That(decision.Reason, Is.Null);
        }
    }

    [Test]
    public void Decide_UniversalGroupMailEnabled_IsStaged()
    {
        var decision = ExchangeRecipientRules.Decide("group", ExchangeRecipientTypes.None, ExchangeRecipientTypes.MailEnabledGroup, UniversalSecurityGroup);

        Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Stage));
    }

    #endregion

    #region Recipients JIM does not manage

    /// <summary>
    /// Unmanaged is what the import reads for a recipient this release does not manage (a linked or remote mailbox, a
    /// dynamic group). It describes Exchange, never a request, so declaring it is refused as a type nobody can ask for.
    /// </summary>
    [TestCase("user")]
    [TestCase("group")]
    [TestCase("contact")]
    public void TypeFitRefusal_UnmanagedDeclared_IsRefusedAsATypeThatCannotBeDeclared(string objectTypeName)
    {
        var refusal = ExchangeRecipientRules.TypeFitRefusal(objectTypeName, ExchangeRecipientTypes.Unmanaged, UniversalSecurityGroup);

        Assert.That(refusal, Is.Not.Null.And.Contains("cannot be declared"));
    }

    [TestCase(ExchangeRecipientTypes.Mailbox)]
    [TestCase(ExchangeRecipientTypes.SharedMailbox)]
    [TestCase(ExchangeRecipientTypes.MailUser)]
    public void Decide_DeclaredTypeOverAnUnmanagedRecipient_IsRefusedSayingJimDoesNotManageIt(string declared)
    {
        var decision = ExchangeRecipientRules.Decide("user", ExchangeRecipientTypes.Unmanaged, declared, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Refused));
            Assert.That(decision.Reason, Does.Contain("a kind of recipient JIM does not manage"));
        }
    }

    /// <summary>
    /// A misfit over an unmanaged recipient is refused for the recipient it already is: whatever was declared, JIM will
    /// not touch it.
    /// </summary>
    [Test]
    public void Decide_MisfitOverAnUnmanagedRecipient_IsRefusedSayingJimDoesNotManageIt()
    {
        var decision = ExchangeRecipientRules.Decide("user", ExchangeRecipientTypes.Unmanaged, ExchangeRecipientTypes.MailContact, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Refused));
            Assert.That(decision.Reason, Does.Contain("a kind of recipient JIM does not manage"));
        }
    }

    [TestCase(ExchangeRecipientTypes.None)]
    [TestCase(null)]
    [TestCase("")]
    public void Decide_NoneDeclaredOverAnUnmanagedRecipient_IsNotARecipient(string? declared)
    {
        var decision = ExchangeRecipientRules.Decide("user", ExchangeRecipientTypes.Unmanaged, declared, groupType: null);

        Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.NotARecipient));
    }

    [Test]
    public void Decide_UnmanagedDeclaredOverNothing_IsRefusedAsATypeThatCannotBeDeclared()
    {
        var decision = ExchangeRecipientRules.Decide("user", null, ExchangeRecipientTypes.Unmanaged, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Refused));
            Assert.That(decision.Reason, Does.Contain("cannot be declared"));
        }
    }

    #endregion
}

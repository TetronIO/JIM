// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Active Directory's ranged retrieval of a multi-valued attribute over MaxValRange (#1853): the range option is
/// recognised and stripped, and the ranges past the first are read from the directory until it answers the last.
/// </summary>
[TestFixture]
public class LdapRangedAttributeTests
{
    private const string GroupDn = "CN=Everyone,OU=Groups,DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestCase("member;range=0-1499", "member", 0, 1499)]
    [TestCase("member;range=1500-*", "member", 1500, null)]
    [TestCase("Member;Range=3000-4499", "Member", 3000, 4499)]
    [TestCase("proxyAddresses;range=0-0", "proxyAddresses", 0, 0)]
    public void TryParse_ARangeQualifiedDescription_GivesTheNameAndTheRange(string description, string expectedName, int expectedLow, int? expectedHigh)
    {
        var parsed = LdapRangedAttribute.TryParse(description, out var name, out var low, out var high);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(name, Is.EqualTo(expectedName));
            Assert.That(low, Is.EqualTo(expectedLow));
            Assert.That(high, Is.EqualTo(expectedHigh));
        });
    }

    [TestCase("member")]
    [TestCase("userCertificate;binary")]
    [TestCase("member;range=")]
    [TestCase("member;range=a-b")]
    [TestCase("member;range=0-1499;binary")]
    public void TryParse_ADescriptionWithoutARangeOption_IsNotARange(string description)
    {
        var parsed = LdapRangedAttribute.TryParse(description, out var name, out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.False);
            Assert.That(name, Is.EqualTo(description), "The description is handed back unchanged.");
        });
    }

    [Test]
    public void ReadAll_FollowsEachRangeTheDirectoryAnswersUntilTheLast()
    {
        var requested = new List<string>();
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Returns((DirectoryRequest request, TimeSpan _) =>
            {
                var search = (SearchRequest)request;
                Assert.Multiple(() =>
                {
                    Assert.That(search.DistinguishedName, Is.EqualTo(GroupDn));
                    Assert.That(search.Scope, Is.EqualTo(SearchScope.Base));
                    Assert.That(search.Attributes, Has.Count.EqualTo(1));
                });
                requested.Add(search.Attributes[0]!);
                return search.Attributes[0] switch
                {
                    // The directory answers with the range it actually returned, not the one asked for.
                    "member;range=2-*" => LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, ("member;range=2-3", ["CN=c", "CN=d"]))),
                    "member;range=4-*" => LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, ("member;range=4-*", ["CN=e"]))),
                    _ => throw new InvalidOperationException($"Unexpected request for {search.Attributes[0]}")
                };
            });
        var entry = LdapTestResponses.EntryWithValues(GroupDn, ("objectClass", ["top", "group"]), ("member;range=0-1", ["CN=a", "CN=b"]));

        var attribute = LdapRangedAttribute.ReadAll(executor.Object, entry, "member;range=0-1", Timeout, Logger);

        Assert.Multiple(() =>
        {
            Assert.That(attribute.Name, Is.EqualTo("member"), "The result carries the plain attribute name.");
            Assert.That(attribute.GetValues(typeof(string)), Is.EqualTo(new object[] { "CN=a", "CN=b", "CN=c", "CN=d", "CN=e" }));
            Assert.That(requested, Is.EqualTo(new[] { "member;range=2-*", "member;range=4-*" }));
        });
    }

    [Test]
    public void ReadAll_WhenTheFirstRangeIsAlreadyTheLast_ReadsNothingMore()
    {
        var executor = new Mock<ILdapOperationExecutor>(MockBehavior.Strict);
        var entry = LdapTestResponses.EntryWithValues(GroupDn, ("member;range=0-*", ["CN=a", "CN=b"]));

        var attribute = LdapRangedAttribute.ReadAll(executor.Object, entry, "member;range=0-*", Timeout, Logger);

        Assert.That(attribute.GetValues(typeof(string)), Is.EqualTo(new object[] { "CN=a", "CN=b" }));
        executor.VerifyNoOtherCalls();
    }

    [Test]
    public void ReadAll_WhenTheEntryIsGoneBeforeTheNextRange_KeepsWhatWasRead()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>(), Timeout)).Returns(LdapTestResponses.EmptySearchResponse());
        var entry = LdapTestResponses.EntryWithValues(GroupDn, ("member;range=0-1", ["CN=a", "CN=b"]));

        var attribute = LdapRangedAttribute.ReadAll(executor.Object, entry, "member;range=0-1", Timeout, Logger);

        Assert.That(attribute.GetValues(typeof(string)), Is.EqualTo(new object[] { "CN=a", "CN=b" }));
    }

    [Test]
    public void ReadAll_WhenTheDirectoryAnswersARangeThatDoesNotAdvance_StopsRatherThanLooping()
    {
        var calls = 0;
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Returns((DirectoryRequest _, TimeSpan _) =>
            {
                calls++;
                return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, ("member;range=0-1", ["CN=a", "CN=b"])));
            });
        var entry = LdapTestResponses.EntryWithValues(GroupDn, ("member;range=0-1", ["CN=a", "CN=b"]));

        var attribute = LdapRangedAttribute.ReadAll(executor.Object, entry, "member;range=0-1", Timeout, Logger);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(attribute.Count, Is.EqualTo(4), "What the directory answered is kept; the reader that consumes it removes duplicates.");
        });
    }

    // A directory may answer a value set over MaxValRange with the plain attribute, carrying no values, beside the
    // range-qualified one (#2041). Read naively, the empty plain attribute either wins (no values at all) or lands
    // beside the ranged one as a second attribute of the same name.

    [Test]
    public void DescriptionsToRead_AnEmptyPlainAttributeBesideItsRangedForm_LeavesThePlainOneOut()
    {
        var entry = LdapTestResponses.EntryWithValues(GroupDn,
            ("objectClass", ["top", "group"]), ("member", []), ("member;range=0-1", ["CN=a", "CN=b"]));

        Assert.That(LdapRangedAttribute.DescriptionsToRead(entry), Is.EquivalentTo(new[] { "objectClass", "member;range=0-1" }).IgnoreCase);
    }

    [Test]
    public void DescriptionsToRead_TheRangedFormInADifferentCase_StillLeavesThePlainOneOut()
    {
        // The overload over descriptions keeps their case, which an entry's own collection does not.
        Assert.That(LdapRangedAttribute.DescriptionsToRead(["Member;Range=0-1", "member"]), Is.EqualTo(new[] { "Member;Range=0-1" }));
    }

    [Test]
    public void FindReturnedRange_ThePlainAttributeListedBeforeTheRange_ReturnsTheRange()
    {
        // Deterministic where the ReadAll test below is not: an entry's attribute collection is hash-ordered, so
        // which of the two it lists first varies from process to process.
        var returned = LdapRangedAttribute.FindReturnedRange(["member", "member;range=2-3"], "member", out var high);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returned, Is.EqualTo("member;range=2-3"));
            Assert.That(high, Is.EqualTo(3));
        }
    }

    [Test]
    public void FindReturnedRange_OnlyThePlainAttribute_ReturnsIt()
    {
        // A directory may answer the rest of the values whole; that is the last of them.
        var returned = LdapRangedAttribute.FindReturnedRange(["objectClass", "member"], "member", out var high);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returned, Is.EqualTo("member"));
            Assert.That(high, Is.Null);
        }
    }

    [Test]
    public void DescriptionsToRead_AnEntryWithNoRangedAttribute_ReturnsEveryDescription()
    {
        var entry = LdapTestResponses.EntryWithValues(GroupDn, ("objectClass", ["top", "group"]), ("member", ["CN=a"]), ("userCertificate;binary", ["x"]));

        Assert.That(LdapRangedAttribute.DescriptionsToRead(entry), Is.EquivalentTo(new[] { "objectClass", "member", "userCertificate;binary" }).IgnoreCase);
    }

    [Test]
    public void ReadAll_AFollowUpAnswerCarryingAnEmptyPlainAttributeBesideTheRange_FollowsTheRange()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Returns((DirectoryRequest request, TimeSpan _) => ((SearchRequest)request).Attributes[0] switch
            {
                "member;range=2-*" => LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, ("member", []), ("member;range=2-3", ["CN=c", "CN=d"]))),
                "member;range=4-*" => LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, ("member", []), ("member;range=4-*", ["CN=e"]))),
                var other => throw new InvalidOperationException($"Unexpected request for {other}")
            });
        var entry = LdapTestResponses.EntryWithValues(GroupDn, ("member", []), ("member;range=0-1", ["CN=a", "CN=b"]));

        var attribute = LdapRangedAttribute.ReadAll(executor.Object, entry, "member;range=0-1", Timeout, Logger);

        Assert.That(attribute.GetValues(typeof(string)), Is.EqualTo(new object[] { "CN=a", "CN=b", "CN=c", "CN=d", "CN=e" }));
    }
}

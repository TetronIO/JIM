// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphPermissionsTests
{
    [Test]
    public void ReadRoles_TokenWithRoles_ReturnsThem()
    {
        var roles = GraphAccessToken.ReadRoles(GraphTestTokens.AccessToken("User.Read.All", "Group.Read.All"));

        Assert.That(roles, Is.EqualTo(new[] { "User.Read.All", "Group.Read.All" }));
    }

    [Test]
    public void ReadRoles_TokenWithoutRoles_ReturnsNone()
    {
        Assert.That(GraphAccessToken.ReadRoles(GraphTestTokens.AccessToken()), Is.Empty);
    }

    [TestCase("")]
    [TestCase("not-a-jwt")]
    [TestCase("a.%%%.c")]
    [TestCase("eyJhbGciOiJub25lIn0.WzEsMl0.c2ln")]
    public void ReadRoles_MalformedToken_ReturnsNoneRatherThanThrowing(string token)
    {
        Assert.That(GraphAccessToken.ReadRoles(token), Is.Empty);
    }

    [Test]
    public void Check_NoRoles_IsAnErrorAboutApplicationPermissionsAndConsent()
    {
        var result = GraphPermissions.Check([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Does.Contain("application permissions"));
            Assert.That(result.Error, Does.Contain("admin consent"));
        }
    }

    [Test]
    public void Check_NoUserOrGroupRead_IsAnErrorNamingTheReadPermissions()
    {
        var result = GraphPermissions.Check(["Mail.Send"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Does.Contain("User.Read.All"));
            Assert.That(result.Error, Does.Contain("Group.Read.All"));
        }
    }

    [Test]
    public void Check_EverythingGranted_HasNoErrorOrWarning()
    {
        var result = GraphPermissions.Check(GraphTestTokens.AllPermissions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Warning, Is.Null);
            Assert.That(result.CanReadUsers, Is.True);
            Assert.That(result.CanReadGroups, Is.True);
        }
    }

    [Test]
    public void Check_DirectoryReadWriteWithSourceOfAuthorityTracked_CoversEverything()
    {
        var result = GraphPermissions.Check(["Directory.ReadWrite.All", "User-OnPremisesSyncBehavior.ReadWrite.All", "Group-OnPremisesSyncBehavior.ReadWrite.All", "Member.Read.Hidden"],
            tracksSourceOfAuthority: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Warning, Is.Null);
        }
    }

    [Test]
    public void Check_ReadOnly_WarnsNamingWhatExportNeeds()
    {
        var result = GraphPermissions.Check(["User.Read.All", "Group.Read.All", "Member.Read.Hidden"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Warning, Does.Contain("User.ReadWrite.All"));
            Assert.That(result.Warning, Does.Contain("Group.ReadWrite.All"));
        }
    }

    [Test]
    public void Check_SourceOfAuthorityNotTracked_NeverAsksForItsPermissions()
    {
        // Tracking is off by default: its permission also allows changing an object's Source of Authority, so it is
        // never asked for unless tracking is turned on.
        var result = GraphPermissions.Check(["User.ReadWrite.All", "Group.ReadWrite.All", "Member.Read.Hidden"]);

        Assert.That(result.Warning, Is.Null);
    }

    [Test]
    public void Check_SourceOfAuthorityTrackedWithoutItsPermissions_WarnsNamingTheReadWritePermissions()
    {
        // Microsoft offers no read-only permission for it.
        var result = GraphPermissions.Check(["User.ReadWrite.All", "Group.ReadWrite.All", "Member.Read.Hidden"], tracksSourceOfAuthority: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Warning, Does.Contain("User-OnPremisesSyncBehavior.ReadWrite.All"));
            Assert.That(result.Warning, Does.Contain("Group-OnPremisesSyncBehavior.ReadWrite.All"));
            Assert.That(result.Warning, Does.Contain("Track Source of Authority Changes"));
        }
    }

    [Test]
    public void Check_UsersOnly_CanReadUsersButNotGroupsAndWarnsAboutGroups()
    {
        var result = GraphPermissions.Check(["User.ReadWrite.All"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.CanReadUsers, Is.True);
            Assert.That(result.CanReadGroups, Is.False);
            Assert.That(result.Warning, Does.Contain("Group.Read.All"));
            Assert.That(result.Warning, Does.Not.Contain("User.ReadWrite.All"));
        }
    }

    [Test]
    public void Check_RoleNames_AreMatchedWhateverTheirCase()
    {
        var result = GraphPermissions.Check(["user.readwrite.all", "GROUP.READWRITE.ALL", "user-onpremisessyncbehavior.readwrite.all", "group-onpremisessyncbehavior.readwrite.all", "member.read.hidden"],
            tracksSourceOfAuthority: true);

        Assert.That(result.Warning, Is.Null);
    }

    [Test]
    public void Check_GroupsWithoutMemberReadHidden_WarnsThatHiddenMembershipCannotBeRead()
    {
        var result = GraphPermissions.Check(["User.ReadWrite.All", "Group.ReadWrite.All"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Warning, Does.Contain("Member.Read.Hidden"));
        }
    }

    [Test]
    public void Check_UsersOnlyWithoutMemberReadHidden_DoesNotAskForIt()
    {
        var result = GraphPermissions.Check(["User.ReadWrite.All"]);

        Assert.That(result.Warning, Does.Not.Contain("Member.Read.Hidden"));
    }

    [Test]
    public void CanReadHiddenMembership_OnlyWithMemberReadHidden()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GraphPermissions.CanReadHiddenMembership(["Member.Read.Hidden"]), Is.True);
            Assert.That(GraphPermissions.CanReadHiddenMembership(["member.read.hidden", "Group.Read.All"]), Is.True);
            Assert.That(GraphPermissions.CanReadHiddenMembership(["Directory.ReadWrite.All", "Group.ReadWrite.All"]), Is.False);
        }
    }
}

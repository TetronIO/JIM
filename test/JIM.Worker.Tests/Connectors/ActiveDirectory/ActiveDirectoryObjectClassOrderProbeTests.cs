// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// The order Active Directory lists an entry's <c>objectClass</c> values in (#1853). The Object Type matcher takes
/// the first structural class it finds among the selected Object Types, on the stated assumption that Active
/// Directory lists the most specific class first; a directory that lists <c>top</c> first would make an entry carrying
/// both a selected <c>person</c> and a selected <c>user</c> resolve to the wrong one.
/// </summary>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectoryObjectClassOrderProbeTests
{
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public void Match_UserEntryClassesAsTheDirectoryListsThem_ResolvesToUserWhenPersonIsAlsoSelected()
    {
        var lab = ActiveDirectoryLab.Require();
        string[] objectClasses;
        using (var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger))
            objectClasses = ActiveDirectoryLab.ReadValues(admin, $"CN=Administrator,CN=Users,{lab.BaseDn}", "objectClass");

        TestContext.Out.WriteLine($"objectClass as listed: {string.Join(", ", objectClasses)}");
        Assert.That(objectClasses, Is.Not.Empty);

        var objectTypes = new List<ConnectedSystemObjectType>
        {
            new() { Id = 1, Name = "user", Selected = true },
            new() { Id = 2, Name = "person", Selected = true }
        };

        var matched = LdapObjectTypeMatcher.Match(objectClasses, objectTypes);
        Assert.That(matched?.Name, Is.EqualTo("user"),
            "An entry that is a user must resolve to user, whatever order the directory lists its classes in.");
    }
}

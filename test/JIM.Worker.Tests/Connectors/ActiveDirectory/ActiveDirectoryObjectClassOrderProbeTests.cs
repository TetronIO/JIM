// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// Which Object Type a real Active Directory entry resolves to when it carries two selected structural classes
/// (#1853, #2043). A user carries <c>top, person, organizationalPerson, user</c>, so with both <c>user</c> and
/// <c>person</c> selected it must resolve to <c>user</c>, the more specific. The Object Type matcher settles that from
/// the inheritance schema discovery records, whatever order the classes come in, and falls back on the order the
/// directory lists them in (top first, most specific last, on Windows Server as on Samba AD) for Object Types
/// discovered before JIM recorded inheritance. Both are checked here against a real domain controller.
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
        var objectClasses = ReadAdministratorObjectClasses();

        // Object Types as a Connected System discovered before JIM recorded inheritance holds them: names only.
        var objectTypes = new List<ConnectedSystemObjectType>
        {
            new() { Id = 1, Name = "user", Selected = true },
            new() { Id = 2, Name = "person", Selected = true }
        };

        var matched = LdapObjectTypeMatcher.Match(objectClasses, objectTypes);
        Assert.That(matched?.Name, Is.EqualTo("user"),
            "An entry that is a user must resolve to user, whatever order the directory lists its classes in.");
    }

    [Test]
    public async Task Match_UserEntryClassesInAnyOrder_ResolvesToUserFromTheDiscoveredSchemaAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        var objectClasses = ReadAdministratorObjectClasses();

        using var connector = ActiveDirectoryLab.NewConnector(lab);
        var schema = await connector.GetSchemaAsync(ActiveDirectoryLab.ConnectorSettings(lab), _logger);
        var objectTypes = new[] { "user", "person" }.Select((name, index) => Selected(schema, name, index + 1)).ToList();

        TestContext.Out.WriteLine($"user inherits from: {string.Join(", ", objectTypes[0].Tags.Where(tag => tag.Key == ObjectTypeTags.Keys.SuperiorClass).Select(tag => tag.Value))}");
        Assert.That(objectTypes[0].InheritsFrom("person"), Is.True, "Schema discovery must record that user inherits from person.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LdapObjectTypeMatcher.Match(objectClasses, objectTypes)?.Name, Is.EqualTo("user").IgnoreCase, "as the directory lists them");
            Assert.That(LdapObjectTypeMatcher.Match(objectClasses.Reverse(), objectTypes)?.Name, Is.EqualTo("user").IgnoreCase, "reversed");
        }
    }

    private string[] ReadAdministratorObjectClasses()
    {
        var lab = ActiveDirectoryLab.Require();
        string[] objectClasses;
        using (var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger))
            objectClasses = ActiveDirectoryLab.ReadValues(admin, $"CN=Administrator,CN=Users,{lab.BaseDn}", "objectClass");

        TestContext.Out.WriteLine($"objectClass as listed: {string.Join(", ", objectClasses)}");
        Assert.That(objectClasses, Is.Not.Empty);
        return objectClasses;
    }

    /// <summary>
    /// A discovered class as a Connected System holds it once selected: its name and its tags.
    /// </summary>
    private static ConnectedSystemObjectType Selected(ConnectorSchema schema, string name, int id)
    {
        var discovered = schema.ObjectTypes.SingleOrDefault(objectType => objectType.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.That(discovered, Is.Not.Null, $"Schema discovery did not return the {name} class.");
        return new ConnectedSystemObjectType
        {
            Id = id,
            Name = discovered!.Name,
            Selected = true,
            Tags = discovered!.Tags.Select(tag => new ConnectedSystemObjectTypeTag { Key = tag.Key, Value = tag.Value }).ToList()
        };
    }
}

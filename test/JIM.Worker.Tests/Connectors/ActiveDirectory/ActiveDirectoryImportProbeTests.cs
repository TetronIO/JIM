// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// A Full Import of a group larger than Active Directory's MaxValRange (#1853).
/// </summary>
/// <remarks>
/// Active Directory returns a multi-valued attribute over MaxValRange (1,500 by default) as
/// <c>member;range=0-1499</c> and omits plain <c>member</c>; the rest is read by asking for
/// <c>member;range=1500-*</c> and so on until the directory answers with a range ending in <c>*</c>. Samba AD returns
/// every value, so the integration lab never met a ranged attribute.
/// </remarks>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectoryImportProbeTests
{
    private const int MemberCount = 1600;
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public async Task FullImport_GroupOverMaxValRange_ImportsEveryMemberAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        string groupDn;
        int membersInDirectory;
        using (var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger))
        {
            var contacts = ActiveDirectoryLab.EnsureContacts(admin, lab, MemberCount);
            groupDn = ActiveDirectoryLab.EnsureGroup(admin, lab, "JIM Probe Large Group", "jim-probe-large-group", contacts);
            membersInDirectory = ActiveDirectoryLab.ReadAllValues(admin, groupDn, "member").Count;
        }
        Assert.That(membersInDirectory, Is.GreaterThanOrEqualTo(MemberCount), "The fixture group is smaller than the probe needs.");

        var objectType = ActiveDirectoryLab.GroupObjectType();
        var connectedSystem = ActiveDirectoryLab.NewConnectedSystem(lab, objectType, lab.ProbeOu, "JIM Probes");
        var runProfile = new ConnectedSystemRunProfile { Name = "Probe Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = 500 };

        using var connector = ActiveDirectoryLab.NewConnector(lab);
        connector.OpenImportConnection(connectedSystem.SettingValues, null, _logger);
        ConnectedSystemImportResult result;
        try
        {
            result = await connector.ImportAsync(connectedSystem, runProfile, [], null, _logger, CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseImportConnection();
        }

        var group = result.ImportObjects.SingleOrDefault(importObject => importObject.Attributes.Any(attribute =>
            attribute.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase) &&
            attribute.StringValues.Any(value => value.Equals(groupDn, StringComparison.OrdinalIgnoreCase))));
        Assert.That(group, Is.Not.Null, $"{groupDn} was not imported. Errors: {string.Join("; ", result.ImportObjects.Where(o => o.ErrorType != null).Select(o => o.ErrorMessage))}");
        Assert.That(group!.ErrorType, Is.Null, group.ErrorMessage);

        var member = group.Attributes.SingleOrDefault(attribute => attribute.Name.Equals("member", StringComparison.OrdinalIgnoreCase));
        Assert.That(member, Is.Not.Null, "The group came back without a member attribute: a ranged attribute was not recognised.");
        Assert.That(member!.ReferenceValues, Has.Count.EqualTo(membersInDirectory),
            "Every member must be imported; a count of exactly MaxValRange means the ranged retrieval stopped after the first range.");
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// Container enumeration in a domain holding more organisational units than the directory answers in one unpaged
/// search (#1853). Large estates routinely have thousands of OUs.
/// </summary>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectoryPartitionProbeTests
{
    private const int ContainerCount = 1100;
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public async Task GetPartitionsAsync_OverOneThousandContainers_EnumeratesEveryOneAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        var parentDn = $"OU=JIM Probe Containers,{lab.ProbeOu}";
        using (var admin = ActiveDirectoryLab.OpenAdminConnection(lab, _logger))
        {
            ActiveDirectoryLab.EnsureProbeOu(admin, lab);
            ActiveDirectoryLab.EnsureChildContainers(admin, parentDn, "JIM Probe Containers", ContainerCount);
        }

        using var connector = ActiveDirectoryLab.NewConnector(lab);
        List<ConnectorPartition>? partitions = null;
        Assert.That(
            async () => partitions = await connector.GetPartitionsAsync(ActiveDirectoryLab.ConnectorSettings(lab), _logger),
            Throws.Nothing,
            "Container enumeration must page: an unpaged subtree search over more containers than MaxPageSize is refused with sizeLimitExceeded.");

        var domain = partitions!.SingleOrDefault(partition => partition.Id.Equals(lab.BaseDn, StringComparison.OrdinalIgnoreCase));
        Assert.That(domain, Is.Not.Null, $"The domain partition {lab.BaseDn} was not returned.");

        var parent = Find(domain!.Containers, parentDn);
        Assert.That(parent, Is.Not.Null, $"{parentDn} was not in the hierarchy.");
        Assert.That(parent!.ChildContainers, Has.Count.GreaterThanOrEqualTo(ContainerCount));
    }

    private static ConnectorContainer? Find(IEnumerable<ConnectorContainer> containers, string dn)
    {
        foreach (var container in containers)
        {
            if (container.Id.Equals(dn, StringComparison.OrdinalIgnoreCase))
                return container;

            var found = Find(container.ChildContainers, dn);
            if (found != null)
                return found;
        }

        return null;
    }
}

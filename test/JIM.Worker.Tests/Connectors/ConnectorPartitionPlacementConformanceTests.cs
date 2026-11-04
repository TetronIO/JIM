// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Every Connector with Partitions says which Partition each of its objects is in (#2723). One that did not would have
/// every object it creates recorded against the Partition of the Run Profile that read it, which is nothing for an
/// unscoped Run Profile, and a Partition-scoped Full Import would then never see those objects to detect their deletion.
/// </summary>
[TestFixture]
public class ConnectorPartitionPlacementConformanceTests
{
    /// <summary>Every concrete Connector JIM.Connectors ships, built-in or test-only.</summary>
    private static List<Type> ConnectorTypes() => typeof(LdapConnector).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IConnectorCapabilities).IsAssignableFrom(type))
        .OrderBy(type => type.FullName, StringComparer.Ordinal)
        .ToList();

    [Test]
    public void EveryConnectorWithPartitions_ImplementsPartitionPlacement()
    {
        var connectorTypes = ConnectorTypes();
        Assert.That(connectorTypes, Does.Contain(typeof(LdapConnector)), "precondition: the enumeration finds the Connectors");

        var problems = connectorTypes
            .Where(type => type.GetConstructor(Type.EmptyTypes) == null)
            .Select(type => $"{type.Name}: has no parameterless constructor, so this test cannot ask whether it supports Partitions")
            .Concat(connectorTypes
                .Where(type => type.GetConstructor(Type.EmptyTypes) != null)
                .Where(SupportsPartitionsWithoutPlacement)
                .Select(type => $"{type.Name}: supports Partitions but does not implement {nameof(IConnectorPartitionPlacement)}"))
            .ToList();

        Assert.That(problems, Is.Empty, string.Join("\n", problems));
    }

    private static bool SupportsPartitionsWithoutPlacement(Type connectorType)
    {
        var connector = (IConnectorCapabilities)Activator.CreateInstance(connectorType)!;
        try
        {
            return connector.SupportsPartitions && connector is not IConnectorPartitionPlacement;
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }
}

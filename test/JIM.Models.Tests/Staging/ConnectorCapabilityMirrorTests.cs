// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Guards the completeness of capability mirroring.
/// <para>
/// A Connector declares its capabilities on <see cref="IConnectorCapabilities"/>; those flags are persisted on a
/// <see cref="ConnectorDefinition"/> and drive what the portal offers an administrator. Before these tests existed
/// the copy was hand-written in two places, so adding a flag and forgetting one of them left the capability
/// permanently false in the database with nothing failing: the Connector would advertise a feature the rest of JIM
/// could not see. These tests reflect over the interface so a new flag is covered the moment it is declared,
/// rather than when somebody remembers to extend a list.
/// </para>
/// </summary>
[TestFixture]
public class ConnectorCapabilityMirrorTests
{
    private static IReadOnlyList<PropertyInfo> CapabilityProperties =>
        typeof(IConnectorCapabilities).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// Every capability is a bool. The mirror and its tests assume this, so assert it rather than let a future
    /// non-bool capability be silently skipped by the round-trip tests below.
    /// </summary>
    [Test]
    public void IConnectorCapabilities_EveryCapability_IsABoolean()
    {
        Assert.That(CapabilityProperties, Is.Not.Empty);

        foreach (var property in CapabilityProperties)
            Assert.That(property.PropertyType, Is.EqualTo(typeof(bool)),
                $"Capability '{property.Name}' is not a bool. ConnectorCapabilityMirror copies capabilities generically and assumes they are all booleans.");
    }

    /// <summary>
    /// ConnectorDefinition implements IConnectorCapabilities, so this cannot fail to compile, but it can fail to
    /// expose a setter, which would make the capability unwritable and therefore unmirrorable.
    /// </summary>
    [Test]
    public void ConnectorDefinition_EveryCapability_IsSettable()
    {
        foreach (var property in CapabilityProperties)
        {
            var definitionProperty = typeof(ConnectorDefinition).GetProperty(property.Name);

            Assert.That(definitionProperty, Is.Not.Null, $"ConnectorDefinition is missing capability '{property.Name}'.");
            Assert.That(definitionProperty!.CanWrite, Is.True, $"ConnectorDefinition capability '{property.Name}' has no setter, so it can never be mirrored from the Connector.");
        }
    }

    [Test]
    public void CopyTo_EveryCapabilityEnabled_CopiesEveryCapability()
    {
        var source = CapabilityStub.WithAll(true);
        var target = new ConnectorDefinition { Name = "Test Connector" };

        ConnectorCapabilityMirror.CopyTo(source, target);

        foreach (var property in CapabilityProperties)
            Assert.That(ReadCapability(target, property.Name), Is.True,
                $"Capability '{property.Name}' was not copied onto the Connector Definition. Every capability on IConnectorCapabilities must be mirrored.");
    }

    /// <summary>
    /// The reverse direction matters just as much: a capability that a Connector has dropped must be cleared on
    /// the Connector Definition, otherwise the portal keeps offering a feature the Connector no longer supports.
    /// </summary>
    [Test]
    public void CopyTo_EveryCapabilityDisabled_ClearsEveryCapability()
    {
        var target = new ConnectorDefinition { Name = "Test Connector" };
        ConnectorCapabilityMirror.CopyTo(CapabilityStub.WithAll(true), target);

        ConnectorCapabilityMirror.CopyTo(CapabilityStub.WithAll(false), target);

        foreach (var property in CapabilityProperties)
            Assert.That(ReadCapability(target, property.Name), Is.False,
                $"Capability '{property.Name}' was not cleared on the Connector Definition when the Connector stopped declaring it.");
    }

    /// <summary>
    /// The strong one. Differs() gates whether the mirrored flags are written back at all, so a capability missing
    /// from that comparison is never persisted no matter how correct CopyTo is. Flipping one capability at a time
    /// proves every single one is compared.
    /// </summary>
    [Test]
    public void Differs_WithASingleCapabilityChanged_DetectsEveryCapabilityIndividually()
    {
        foreach (var property in CapabilityProperties)
        {
            var target = new ConnectorDefinition { Name = "Test Connector" };
            ConnectorCapabilityMirror.CopyTo(CapabilityStub.WithAll(false), target);

            var source = CapabilityStub.WithAll(false);
            source.Set(property.Name, true);

            Assert.That(ConnectorCapabilityMirror.Differs(source, target), Is.True,
                $"Capability '{property.Name}' changing was not detected as a difference, so it would never be persisted to the Connector Definition.");
        }
    }

    [Test]
    public void Differs_WithIdenticalCapabilities_ReportsNoDifference()
    {
        var target = new ConnectorDefinition { Name = "Test Connector" };
        var source = CapabilityStub.WithAll(true);
        ConnectorCapabilityMirror.CopyTo(source, target);

        Assert.That(ConnectorCapabilityMirror.Differs(source, target), Is.False);
    }

    private static bool ReadCapability(ConnectorDefinition definition, string name)
    {
        var property = typeof(ConnectorDefinition).GetProperty(name)
            ?? throw new InvalidOperationException($"ConnectorDefinition has no property '{name}'.");

        return (bool)property.GetValue(definition)!;
    }

    /// <summary>
    /// A capabilities source whose flags are set by name, so the tests never have to enumerate them by hand.
    /// </summary>
    private sealed class CapabilityStub : IConnectorCapabilities
    {
        private readonly Dictionary<string, bool> _values = new();

        public static CapabilityStub WithAll(bool value)
        {
            var stub = new CapabilityStub();
            foreach (var property in CapabilityProperties)
                stub._values[property.Name] = value;

            return stub;
        }

        public void Set(string name, bool value) => _values[name] = value;

        private bool Get(string name) => _values[name];

        public bool SupportsFullImport => Get(nameof(SupportsFullImport));

        public bool SupportsDeltaImport => Get(nameof(SupportsDeltaImport));

        public bool SupportsExport => Get(nameof(SupportsExport));

        public bool SupportsPartitions => Get(nameof(SupportsPartitions));

        public bool SupportsPartitionContainers => Get(nameof(SupportsPartitionContainers));

        public bool SupportsSecondaryExternalId => Get(nameof(SupportsSecondaryExternalId));

        public bool SupportsUserSelectedExternalId => Get(nameof(SupportsUserSelectedExternalId));

        public bool SupportsUserSelectedAttributeTypes => Get(nameof(SupportsUserSelectedAttributeTypes));

        public bool SupportsAutoConfirmExport => Get(nameof(SupportsAutoConfirmExport));

        public bool SupportsParallelExport => Get(nameof(SupportsParallelExport));

        public bool SupportsPaging => Get(nameof(SupportsPaging));

        public bool SupportsFilePaths => Get(nameof(SupportsFilePaths));

        public bool SupportsPasswordSet => Get(nameof(SupportsPasswordSet));

        public bool SupportsPasswordPolicyDiscovery => Get(nameof(SupportsPasswordPolicyDiscovery));
    }
}

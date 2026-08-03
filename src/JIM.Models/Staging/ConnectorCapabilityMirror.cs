// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Interfaces;
namespace JIM.Models.Staging;

/// <summary>
/// Copies a Connector's declared capabilities onto the <see cref="ConnectorDefinition"/> that persists them.
/// <para>
/// This is driven off the shape of <see cref="IConnectorCapabilities"/> rather than a hand-written list of flags.
/// The copy and the change-detection used to be written out longhand in two places, which meant adding a
/// capability and missing one of them left the flag permanently false in the database with nothing failing:
/// the Connector advertised a feature the rest of JIM could not see. Declaring a capability on the interface is
/// now the only step required.
/// </para>
/// </summary>
public static class ConnectorCapabilityMirror
{
    /// <summary>
    /// The capability flags declared by <see cref="IConnectorCapabilities"/>, paired with their settable
    /// counterparts on <see cref="ConnectorDefinition"/>. Resolved once; the interface cannot change at runtime.
    /// </summary>
    private static readonly (PropertyInfo Source, PropertyInfo Target)[] Capabilities = BuildCapabilityMap();

    /// <summary>
    /// Copies every capability flag from the Connector onto the Connector Definition.
    /// </summary>
    public static void CopyTo(IConnectorCapabilities source, ConnectorDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        foreach (var (sourceProperty, targetProperty) in Capabilities)
            targetProperty.SetValue(target, sourceProperty.GetValue(source));
    }

    /// <summary>
    /// Whether any capability flag on the Connector differs from the one persisted on the Connector Definition.
    /// </summary>
    public static bool Differs(IConnectorCapabilities source, ConnectorDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return Capabilities.Any(capability =>
            !Equals(capability.Source.GetValue(source), capability.Target.GetValue(target)));
    }

    /// <summary>
    /// Names the capabilities whose values differ, for logging what a Connector Definition update actually changed.
    /// </summary>
    public static IReadOnlyList<string> GetDifferences(IConnectorCapabilities source, ConnectorDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return Capabilities
            .Where(capability => !Equals(capability.Source.GetValue(source), capability.Target.GetValue(target)))
            .Select(capability => capability.Source.Name)
            .ToList();
    }

    private static (PropertyInfo Source, PropertyInfo Target)[] BuildCapabilityMap()
    {
        return typeof(IConnectorCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(sourceProperty =>
            {
                // ConnectorDefinition implements IConnectorCapabilities, so both of these are compile-time
                // guarantees rather than runtime possibilities. They are asserted because a silently skipped
                // capability is exactly the failure this class exists to prevent.
                var targetProperty = typeof(ConnectorDefinition).GetProperty(sourceProperty.Name)
                    ?? throw new InvalidOperationException(
                        $"ConnectorDefinition has no '{sourceProperty.Name}' property to mirror the Connector capability onto.");

                if (!targetProperty.CanWrite)
                    throw new InvalidOperationException(
                        $"ConnectorDefinition property '{sourceProperty.Name}' has no setter, so the Connector capability can never be mirrored onto it.");

                return (sourceProperty, targetProperty);
            })
            .ToArray();
    }
}

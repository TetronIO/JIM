// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The conformance suite every Connector that declares phases must pass (#454). Derive from it,
/// supply the Connector and a Connected System, and the rules a declaration has to satisfy are
/// enforced rather than merely documented.
/// </summary>
/// <remarks>
/// <para>
/// The rules exist because JIM reads a declaration once, before the run starts, and turns it into
/// the steps an administrator watches. A declaration that varies per call, throws on a run type the
/// Connector does not support, or reuses a key would produce a stepper that contradicts itself.
/// </para>
/// <para>
/// Derived fixtures should also assert that every phase the Connector actually enters was declared
/// (see FileConnectorSubPhaseProgressTests), which needs the Connector to be exercised and so
/// cannot live here.
/// </para>
/// </remarks>
public abstract class ConnectorPhaseConformanceTests
{
    /// <summary>
    /// A Connector instance to interrogate. Declaring phases must not need a connection, so this
    /// should be a plain instance with no directory or file behind it.
    /// </summary>
    protected abstract IConnectorPhases CreateConnector();

    /// <summary>
    /// A Connected System configured the way this Connector expects. Declarations may legitimately
    /// vary with configuration, so this is the configuration whose declaration is being asserted.
    /// </summary>
    protected abstract ConnectedSystem CreateConnectedSystem();

    /// <summary>
    /// The run types to interrogate. Every executable run type by default: a Connector must answer
    /// for run types it does not support (with an empty list) rather than throwing.
    /// </summary>
    protected virtual IEnumerable<ConnectedSystemRunType> RunTypes =>
        Enum.GetValues<ConnectedSystemRunType>().Where(t => t != ConnectedSystemRunType.NotSet);

    private static ConnectedSystemRunProfile RunProfile(ConnectedSystemRunType runType) =>
        new() { Name = runType.ToString(), RunType = runType };

    [Test]
    public void GetPhases_ForEveryRunType_DoesNotThrow()
    {
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in RunTypes)
        {
            Assert.DoesNotThrow(() => connector.GetPhases(connectedSystem, RunProfile(runType)),
                $"Declaring phases for {runType} threw. JIM survives it, but the Connector's steps are lost for that run type.");
        }
    }

    [Test]
    public void GetPhases_ForEveryRunType_ReturnsUniqueKeys()
    {
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in RunTypes)
        {
            var keys = connector.GetPhases(connectedSystem, RunProfile(runType)).Select(p => p.Key).ToList();
            Assert.That(keys, Is.Unique, $"{runType} declares a duplicate phase key; phases are entered by key, so duplicates are ambiguous.");
        }
    }

    [Test]
    public void GetPhases_EveryDeclaredPhase_HasAKeyAndAName()
    {
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in RunTypes)
        {
            foreach (var phase in connector.GetPhases(connectedSystem, RunProfile(runType)))
            {
                Assert.That(phase.Key, Is.Not.Null.And.Not.Empty, $"{runType} declares a phase with no key.");
                Assert.That(phase.Name, Is.Not.Null.And.Not.Empty, $"{runType} phase '{phase.Key}' has no administrator-facing name.");
            }
        }
    }

    [Test]
    public void GetPhases_EveryDeclaredName_ReadsAsAStepLabel()
    {
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in RunTypes)
        {
            foreach (var phase in connector.GetPhases(connectedSystem, RunProfile(runType)))
            {
                Assert.That(phase.Name, Does.Not.Contain("—"), $"{runType} phase '{phase.Key}' uses an em dash; house style forbids them.");
                Assert.That(phase.Name, Does.Not.Contain("..."),
                    $"{runType} phase '{phase.Key}' reads as narration. A step's name is what it is; the running commentary belongs in the message.");
                Assert.That(phase.Name.Trim(), Is.EqualTo(phase.Name), $"{runType} phase '{phase.Key}' has leading or trailing whitespace.");
            }
        }
    }

    [Test]
    public void GetPhases_EveryDeclaredKey_IsStableAndInternal()
    {
        // Keys are persisted against historic Activities, so they are part of the data model rather
        // than display text. Anything that reads as a sentence is a name in the wrong field.
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in RunTypes)
        {
            foreach (var phase in connector.GetPhases(connectedSystem, RunProfile(runType)))
            {
                Assert.That(phase.Key.Trim(), Is.EqualTo(phase.Key), $"{runType} phase key '{phase.Key}' has leading or trailing whitespace.");
                Assert.That(phase.Key, Does.Not.Contain(" "), $"{runType} phase key '{phase.Key}' contains spaces; a key is an identifier, not a label.");
            }
        }
    }

    [Test]
    public void GetPhases_CalledTwice_DeclaresTheSamePhases()
    {
        // JIM reads the declaration once, at the start of the run. A declaration that varies per
        // call would produce a stepper that disagrees with what the Connector then does.
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in RunTypes)
        {
            var first = connector.GetPhases(connectedSystem, RunProfile(runType)).Select(p => (p.Key, p.Name)).ToList();
            var second = connector.GetPhases(connectedSystem, RunProfile(runType)).Select(p => (p.Key, p.Name)).ToList();
            Assert.That(second, Is.EqualTo(first), $"{runType} declared different phases on a second call.");
        }
    }

    [Test]
    public void GetPhases_ForSynchronisationRunTypes_DeclaresNothing()
    {
        // Synchronisation never calls a Connector, so anything declared there could never be entered
        // and would sit in the stepper as work that never happens.
        var connector = CreateConnector();
        var connectedSystem = CreateConnectedSystem();

        foreach (var runType in new[] { ConnectedSystemRunType.FullSynchronisation, ConnectedSystemRunType.DeltaSynchronisation })
        {
            Assert.That(connector.GetPhases(connectedSystem, RunProfile(runType)), Is.Empty,
                $"{runType} does not call a Connector, so declaring phases for it cannot be honoured.");
        }
    }
}

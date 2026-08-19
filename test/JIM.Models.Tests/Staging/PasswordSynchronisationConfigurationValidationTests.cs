// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Validation of a proposed Password Synchronisation configuration (#1119), shared by the portal and the REST
/// API and PowerShell so that all three accept and refuse exactly the same settings.
/// <para>
/// Worth testing on its own because every problem it catches is one that would otherwise surface as queued
/// password changes that can never be delivered: a Connector with no password channel, or a target Object Type
/// that holds no accounts. Both look configured and do nothing.
/// </para>
/// </summary>
[TestFixture]
public class PasswordSynchronisationConfigurationValidationTests
{
    private static ConnectedSystem SystemWithPasswordCapableConnector() => new()
    {
        Id = 3,
        Name = "Corporate AD",
        ConnectorDefinitionId = 4,
        ConnectorDefinition = new ConnectorDefinition { Id = 4, Name = "JIM LDAP Connector", SupportsPasswordSet = true },
        ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 7, Name = "user", Selected = true },
            new ConnectedSystemObjectType { Id = 8, Name = "group", Selected = false }
        ]
    };

    private static ConnectedSystemPasswordSynchronisation ValidConfiguration() => new()
    {
        ConnectedSystemId = 3,
        Enabled = true,
        TargetObjectTypeId = 7
    };

    [Test]
    public void Validate_WithAValidConfiguration_ReportsNoProblems()
    {
        var problems = ValidConfiguration().Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void Validate_WhenTheConnectorCannotSetPasswords_IsRefused()
    {
        // Requirement 4 hides the option in the portal, but the API and PowerShell are reachable without it, and
        // a configuration stored against a Connector with no password channel queues changes nothing can deliver.
        var connectedSystem = SystemWithPasswordCapableConnector();
        connectedSystem.ConnectorDefinition!.SupportsPasswordSet = false;
        connectedSystem.ConnectorDefinition.Name = "JIM File Connector";

        var problems = ValidConfiguration().Validate(connectedSystem);

        Assert.That(problems.Single(), Does.Contain("JIM File Connector").And.Contain("cannot set passwords"));
    }

    [Test]
    public void Validate_WithNoTargetObjectType_IsRefused()
    {
        var configuration = ValidConfiguration();
        configuration.TargetObjectTypeId = 0;

        var problems = configuration.Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems.Single(), Does.Contain("Object Type"));
    }

    [Test]
    public void Validate_WithAnObjectTypeFromAnotherSystem_IsRefused()
    {
        var configuration = ValidConfiguration();
        configuration.TargetObjectTypeId = 999;

        var problems = configuration.Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems.Single(), Does.Contain("Object Type"));
    }

    [Test]
    public void Validate_WithAnUnselectedObjectType_IsRefused()
    {
        // An unselected Object Type is not synchronised, so it holds no Connected System Objects for fan-out to
        // find: every queued change would sit waiting for an account that will never appear.
        var configuration = ValidConfiguration();
        configuration.TargetObjectTypeId = 8;

        var problems = configuration.Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems.Single(), Does.Contain("group").And.Contain("not selected"));
    }

    [Test]
    public void Validate_WithAnImplausibleRetryCount_IsRefused()
    {
        var configuration = ValidConfiguration();
        configuration.MaxRetries = 5000;

        var problems = configuration.Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems.Single(), Does.Contain("between 0 and 100"));
    }

    [Test]
    public void Validate_WithZeroRetries_IsAccepted()
    {
        // Zero means "unconfigured, use JIM's default", which the effective-value fallback resolves. Refusing it
        // would make an untouched configuration invalid.
        var configuration = ValidConfiguration();
        configuration.MaxRetries = 0;

        var problems = configuration.Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void Validate_WithAnAbsurdBackoffBase_IsRefused()
    {
        var configuration = ValidConfiguration();
        configuration.RetryBackoffBase = TimeSpan.FromDays(30);

        var problems = configuration.Validate(SystemWithPasswordCapableConnector());

        Assert.That(problems.Single(), Does.Contain("one day"));
    }

    [Test]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        // An administrator fixing one problem at a time, learning of the next only after saving, is the
        // experience this list exists to avoid.
        var connectedSystem = SystemWithPasswordCapableConnector();
        connectedSystem.ConnectorDefinition!.SupportsPasswordSet = false;

        var configuration = ValidConfiguration();
        configuration.TargetObjectTypeId = 0;
        configuration.MaxRetries = 5000;

        var problems = configuration.Validate(connectedSystem);

        Assert.That(problems, Has.Count.EqualTo(3));
    }
}

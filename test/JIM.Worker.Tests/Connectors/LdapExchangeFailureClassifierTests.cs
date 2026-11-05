// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Transactional;
using NUnit.Framework;
using Tetron.PowerShellRemoting;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How a failed Exchange command is classified (#2021, requirement 26), which decides whether the delivery lane retries
/// it or parks it for a person. Exchange's role-based access hides a command or a parameter the account may not use
/// rather than refusing it, so "not recognised" means insufficient permissions, not a typo.
/// </summary>
[TestFixture]
public class LdapExchangeFailureClassifierTests
{
    [TestCase(RemoteFailureKind.Connection, ExchangeChangeFailureKind.Unreachable)]
    [TestCase(RemoteFailureKind.Timeout, ExchangeChangeFailureKind.Unreachable)]
    [TestCase(RemoteFailureKind.Protocol, ExchangeChangeFailureKind.Unreachable)]
    [TestCase(RemoteFailureKind.Certificate, ExchangeChangeFailureKind.Unreachable)]
    [TestCase(RemoteFailureKind.Authentication, ExchangeChangeFailureKind.AuthenticationFailed)]
    public void Classify_TransportOrSignInFailure_IsItsKind(RemoteFailureKind kind, ExchangeChangeFailureKind expected)
    {
        Assert.That(Classify(Failure(kind, "The connection was closed.")).Kind, Is.EqualTo(expected));
    }

    [Test]
    public void Classify_ObjectExchangeCannotFind_IsObjectNotFound()
    {
        // Usually replication: the account was written to one domain controller and Exchange read another.
        var failure = Failure(RemoteFailureKind.Command, "The operation couldn't be performed because object 'CN=Alice' couldn't be found on 'dc2.panoply.local'.")
            with { FailureCategory = "Cmdlet-ManagementObjectNotFoundException" };

        Assert.That(Classify(failure).Kind, Is.EqualTo(ExchangeChangeFailureKind.ObjectNotFound));
    }

    [Test]
    public void Classify_DeclaredDatabaseExchangeCannotFind_IsRefusedNotObjectNotFound()
    {
        // Exchange reports a missing database under the same category as a missing object, but a database that does not
        // exist will not appear by retrying: it is a person's to fix (requirement 20).
        var failure = Failure(RemoteFailureKind.Command, "Couldn't find database \"DB99\". Make sure you have typed it correctly.")
            with { FailureCategory = "Cmdlet-ManagementObjectNotFoundException" };

        var result = LdapExchangeFailureClassifier.Classify("Enable-Mailbox", failure, database: "DB99");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Kind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(result.Message, Does.Contain("DB99"));
        }
    }

    [TestCase("The term 'Enable-Mailbox' is not recognized as the name of a cmdlet, function, script file, or operable program.")]
    [TestCase("A parameter cannot be found that matches parameter name 'Database'.")]
    public void Classify_CommandOrParameterTheAccountCannotSee_IsInsufficientPermissions(string message)
    {
        var result = Classify(Failure(RemoteFailureKind.Command, message));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Kind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions));
            Assert.That(result.Message, Does.Contain("Recipient Management"), "The administrator is told which role group grants it.");
        }
    }

    [Test]
    public void Classify_CommandNotFoundByReason_IsInsufficientPermissions()
    {
        var failure = Failure(RemoteFailureKind.Command, "Something about a command.") with { Reason = "CommandNotFoundException" };

        Assert.That(Classify(failure).Kind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions));
    }

    [Test]
    public void Classify_ExchangeCannotReachTheDomainController_IsUnreachable()
    {
        var failure = Failure(RemoteFailureKind.Command, "Active Directory server dc1.panoply.local is not available. Error message: The LDAP server is unavailable.")
            with { FailureCategory = "Cmdlet-ADServerUnavailableException" };

        Assert.That(Classify(failure).Kind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
    }

    [Test]
    public void Classify_AnyOtherCommandFailure_IsRefusedInExchangesOwnWords()
    {
        var failure = Failure(RemoteFailureKind.Command, "The alias 'alice' is already being used by another recipient.")
            with { FailureCategory = "Cmdlet-ProxyAddressExistsException" };

        var result = Classify(failure);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Kind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(result.Message, Does.Contain("already being used by another recipient"));
        }
    }

    [Test]
    public void Classify_Always_NamesTheCommandThatFailed()
    {
        var result = Classify(Failure(RemoteFailureKind.Command, "Nope."));

        Assert.That(result.Message, Does.StartWith("Set-Mailbox"));
    }

    private static (ExchangeChangeFailureKind Kind, string Message) Classify(RemoteFailure failure) =>
        LdapExchangeFailureClassifier.Classify("Set-Mailbox", failure);

    private static RemoteFailure Failure(RemoteFailureKind kind, string message) => new() { Kind = kind, Message = message };
}

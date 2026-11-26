// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An export refused because the Connector cannot disable an account carries JIM's own wording as a message, so the
/// portal reads it in the viewer's language; the English is what the refusal always said (#3094).
/// </summary>
[TestFixture]
public class ExportRefusalMessageTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static ConnectedSystem SystemWith(string? connectorName) => new()
    {
        Name = "Corporate AD",
        ConnectorDefinition = connectorName == null ? null! : new ConnectorDefinition { Name = connectorName, SupportsDisable = false }
    };

    [TestCase("JIM LDAP Connector", "This object was to be disabled, but the JIM LDAP Connector that 'Corporate AD' uses cannot disable an account, so nothing was sent. Change the export Synchronisation Rule's Deprovisioning Action to Delete or Disconnect.")]
    [TestCase(null, "This object was to be disabled, but the Connector that 'Corporate AD' uses cannot disable an account, so nothing was sent. Change the export Synchronisation Rule's Deprovisioning Action to Delete or Disconnect.")]
    public void UnsupportedDisableRefusal_ConnectorCannotDisable_RefusesWithTheEnglishItAlwaysSaid(string? connectorName, string expected)
    {
        var refusal = ExportExecutionServer.UnsupportedDisableRefusal(new PendingExport { ChangeType = PendingExportChangeType.Disable }, SystemWith(connectorName))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal.Success, Is.False);
            Assert.That(refusal.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
            Assert.That(refusal.ErrorMessage, Is.EqualTo(expected));
            Assert.That(refusal.ErrorMessageDetail!.ToEnglish(), Is.EqualTo(expected));
        }
    }

    [Test]
    public void UnsupportedDisableRefusal_InFrenchAndGerman_ReadsAsWholeSentences()
    {
        var refusal = ExportExecutionServer.UnsupportedDisableRefusal(new PendingExport { ChangeType = PendingExportChangeType.Disable }, SystemWith("JIM LDAP Connector"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageRenderer.Render(refusal.ErrorMessageDetail!, French, French), Does.StartWith("Cet objet devait être désactivé, mais le JIM LDAP Connector qu’utilise « Corporate AD » ne peut pas désactiver un compte"));
            Assert.That(MessageRenderer.Render(refusal.ErrorMessageDetail!, German, German), Does.StartWith("Dieses Objekt sollte deaktiviert werden, aber der von „Corporate AD“ verwendete JIM LDAP Connector kann kein Konto deaktivieren"));
        }
    }

    [Test]
    public void UnsupportedDisableRefusal_ConnectorCanDisable_DoesNotRefuse()
    {
        var system = new ConnectedSystem { Name = "Corporate AD", ConnectorDefinition = new ConnectorDefinition { Name = "x", SupportsDisable = true } };

        Assert.That(ExportExecutionServer.UnsupportedDisableRefusal(new PendingExport { ChangeType = PendingExportChangeType.Disable }, system), Is.Null);
    }
}

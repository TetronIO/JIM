// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using JIM.Connectors.Graph;
using JIM.Connectors.LDAP;
using JIM.Models.Connectors;
using JIM.Models.Localisation;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What the Connectors tell an administrator is held as a message (#3030): the British English every other reader
/// (the REST API, PowerShell, Activities, logs) gets is exactly what it was, and the portal can render the same
/// message in another language.
/// </summary>
[TestFixture]
public class ConnectorMessageLocalisationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Test]
    public void EntraTokenFailure_DescribeMessage_IsTheEnglishDescribeReturns_AndRendersInFrench()
    {
        const string body = """{"error":"invalid_client","error_codes":[7000215]}""";

        var message = EntraTokenFailure.DescribeMessage(HttpStatusCode.BadRequest, body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(EntraTokenFailure.Describe(HttpStatusCode.BadRequest, body)));
            Assert.That(MessageRenderer.Render(message, French, French), Does.StartWith("Microsoft Entra ID a refusé la demande de jeton (AADSTS7000215)."));
        }
    }

    [Test]
    public void ExchangeDirectoryTypeRefusal_IsTheEnglishItWas_AndRendersInFrench()
    {
        var refusal = LdapExchangeSettings.DirectoryTypeRefusalMessage(true, LdapDirectoryType.OpenLDAP)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LdapExchangeSettings.DirectoryTypeRefusal(true, LdapDirectoryType.OpenLDAP), Is.EqualTo(
                "Exchange recipients can be managed only on Active Directory, and this directory is OpenLDAP. Turn off Manage Exchange recipients, or connect to an Active Directory domain controller."));
            Assert.That(MessageRenderer.Render(refusal, French, French), Is.EqualTo(
                "Les destinataires Exchange ne peuvent être gérés que sur Active Directory, et cet annuaire est OpenLDAP. Désactivez Manage Exchange recipients, ou connectez-vous à un contrôleur de domaine Active Directory."));
            Assert.That(LdapExchangeSettings.DirectoryTypeRefusalMessage(true, LdapDirectoryType.ActiveDirectory), Is.Null);
        }
    }

    [Test]
    public void ExchangeCommandCoverage_ListsTheMissingCommands_WithAnOrBeforeTheLast()
    {
        var visible = new HashSet<string>(LdapExchangeCommandCoverage.RequiredCommands.Skip(3), StringComparer.OrdinalIgnoreCase);

        var warning = LdapExchangeCommandCoverage.WarningMessageFor("svc-jim@corp.local", visible)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.ToEnglish(), Is.EqualTo(LdapExchangeCommandCoverage.WarningFor("svc-jim@corp.local", visible)));
            Assert.That(warning.ToEnglish(), Does.Contain("cannot run Get-Recipient, Get-User or Enable-Mailbox in Exchange"));
            Assert.That(MessageRenderer.Render(warning, French, French), Does.Contain("ne peut pas exécuter Get-Recipient, Get-User ou Enable-Mailbox dans Exchange"));
        }
    }

    [Test]
    public void ServerCertificateDiagnostic_RemediationDetail_SurvivesBeingStoredAsJson()
    {
        var diagnostic = new ServerCertificateDiagnostic { Host = "dc1", Port = 636 };
        diagnostic.SetRemediation(JimMessage.Create(JIM.Connectors.ConnectorMessageCatalogues.Connectors, "CertExpired",
            JimMessage.Create(JIM.Connectors.ConnectorMessageCatalogues.Connectors, "CertServerDirectoryServer")));

        var stored = JsonSerializer.Serialize(diagnostic);
        var read = JsonSerializer.Deserialize<ServerCertificateDiagnostic>(stored)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostic.Remediation, Is.EqualTo("The certificate expired. Renew it on the directory server; trusting its issuer does not waive the expiry date."));
            Assert.That(read.Remediation, Is.EqualTo(diagnostic.Remediation));
            Assert.That(read.RemediationDetail, Is.Not.Null);
            Assert.That(MessageRenderer.Render(read.RemediationDetail!, French, French),
                Is.EqualTo("Le certificat a expiré. Renouvelez-le sur le serveur d’annuaire ; faire confiance à son émetteur n’annule pas la date d’expiration."));
        }
    }
}

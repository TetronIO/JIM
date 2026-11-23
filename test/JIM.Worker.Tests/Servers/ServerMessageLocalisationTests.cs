// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Exceptions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Localisation;
using JIM.Models.Operations;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What the application servers refuse or report to a person is a message that renders in the viewer's language (#3030),
/// while <see cref="Exception.Message"/> and the REST-facing strings stay the British English they always were.
/// </summary>
[TestFixture]
public class ServerMessageLocalisationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static string InFrench(JimMessage message) => MessageRenderer.Render(message, French, French);

    [Test]
    public void FeatureDisabledException_KeepsItsEnglishMessage_AndRendersInFrench()
    {
        var definition = new FeatureFlagDefinition("x", "Sample Feature", "d", FeatureFlagTier.Preview, 1);

        var exception = new FeatureDisabledException(definition);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo($"The '{definition.DisplayName}' feature is currently disabled."));
            Assert.That(InFrench(exception.Detail), Is.EqualTo($"La fonctionnalité « {definition.DisplayName} » est actuellement désactivée."));
        }
    }

    [Test]
    public void MetaverseAttributeNameConflictException_StaysAnInvalidOperation_WithFrenchDetail()
    {
        var exception = new MetaverseAttributeNameConflictException("Mail");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception, Is.InstanceOf<InvalidOperationException>());
            Assert.That(exception.Message, Is.EqualTo("A Metaverse Attribute named 'Mail' already exists (names are compared case-insensitively)."));
            Assert.That(InFrench(exception.Detail), Does.StartWith("Un attribut Metaverse nommé « Mail » existe déjà"));
        }
    }

    [Test]
    public void UnsupportedAttributePluralityException_MultiValuedBoolean_SaysTheSharedRule()
    {
        var exception = new UnsupportedAttributePluralityException(AttributeDataType.Boolean, AttributePlurality.MultiValued);

        Assert.That(exception.Message, Is.EqualTo(AttributeDataTypeCapabilities.MultiValuedBooleanRule));
    }

    [Test]
    public void Argument_WithParameterName_KeepsTheFrameworkSuffixInMessageOnly()
    {
        var detail = JimMessage.Create(JIM.Application.Localisation.ApplicationMessageCatalogues.Servers, "AttributeNotFound", "42");

        var exception = JimExceptions.Argument(detail, "attributeId");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception, Is.TypeOf<ArgumentException>());
            Assert.That(exception.Message, Is.EqualTo("Metaverse Attribute 42 not found. (Parameter 'attributeId')"));
            Assert.That(detail.ToEnglish(), Is.EqualTo("Metaverse Attribute 42 not found."));
            Assert.That(InFrench(detail), Is.EqualTo("L’attribut Metaverse 42 est introuvable."));
            Assert.That(exception.DetailOf(), Is.EqualTo(detail));
        }
    }

    [Test]
    public void SystemHealthDerive_ReasonIsTheSameEnglish_AndRendersInFrench()
    {
        var asOf = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var heartbeat = new ServiceHeartbeat
        {
            Service = JimService.WorkerSync,
            InstanceId = "worker-1",
            HostName = "worker",
            Version = "1.0.0",
            StartedAt = asOf.AddHours(-2),
            LastSeenAt = asOf.AddSeconds(-3)
        };

        var healthy = SystemHealthServer.Derive(JimService.WorkerSync, heartbeat, asOf);
        var neverStarted = SystemHealthServer.Derive(JimService.Scheduler, null, asOf);
        heartbeat.LastSeenAt = asOf.AddMinutes(-4);
        var silent = SystemHealthServer.Derive(JimService.WorkerSync, heartbeat, asOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(healthy.Reason, Is.EqualTo("Heartbeat 3 seconds ago"));
            Assert.That(InFrench(healthy.ReasonDetail!), Is.EqualTo("Signal il y a 3 secondes"));
            Assert.That(neverStarted.Reason, Is.EqualTo("Never started"));
            Assert.That(InFrench(neverStarted.ReasonDetail!), Is.EqualTo("Jamais démarré"));
            Assert.That(silent.Reason, Is.EqualTo("No heartbeat for 4 minutes"));
            Assert.That(InFrench(silent.ReasonDetail!), Is.EqualTo("Aucun signal depuis 4 minutes"));
        }
    }
}

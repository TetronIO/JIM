// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Globalization;
using System.Linq;
using JIM.Models.Exchange;
using JIM.Models.Localisation;
using NUnit.Framework;

namespace JIM.Models.Tests.Exchange;

/// <summary>
/// What export staging says about a declared Exchange recipient type is held as a message, one whole sentence per
/// recipient type, so French and German can give each type its own article, gender and case (#3084). The English is
/// exactly what the rules wrote before.
/// </summary>
[TestFixture]
public class ExchangeRecipientMessageTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static readonly string[] ObjectTypes = ["user", "group", "contact", "computer"];

    // Every value a declaration or an import can carry: the types a rule may declare, None, Unmanaged and values JIM
    // does not recognise.
    private static readonly string?[] RecipientTypes =
        [.. ExchangeRecipientTypes.All, ExchangeRecipientTypes.None, ExchangeRecipientTypes.Unmanaged, null, "", "Mailboxx", "RemoteMailbox"];

    private static readonly long?[] GroupTypes = [null, 0x8, 0x2];

    [Test]
    public void Decide_EveryCombination_ReasonIsTheEnglishTheRulesWroteBefore()
    {
        var checkedReasons = 0;
        using (Assert.EnterMultipleScope())
        {
            foreach (var objectType in ObjectTypes)
            foreach (var imported in RecipientTypes)
            foreach (var declared in RecipientTypes)
            foreach (var groupType in GroupTypes)
            {
                var decision = ExchangeRecipientRules.Decide(objectType, imported, declared, groupType);
                var expected = Legacy.Decide(objectType, imported, declared, groupType);
                Assert.That(decision.Reason, Is.EqualTo(expected), $"{objectType}, imported {imported ?? "null"}, declared {declared ?? "null"}, groupType {groupType}");
                Assert.That(decision.ReasonDetail?.ToEnglish(), Is.EqualTo(decision.Reason));
                if (expected != null)
                    checkedReasons++;
            }
        }

        Assert.That(checkedReasons, Is.GreaterThan(100), "the combinations cover the refusals and type changes");
    }

    [TestCase(ExchangeRecipientTypes.Mailbox, "Seul un utilisateur peut avoir une boîte aux lettres ; cet objet est de type group.")]
    [TestCase(ExchangeRecipientTypes.EquipmentMailbox, "Seul un utilisateur peut avoir une boîte aux lettres d’équipement ; cet objet est de type group.")]
    [TestCase(ExchangeRecipientTypes.MailContact, "Seul un contact peut être un contact de messagerie ; cet objet est de type group.")]
    public void TypeFitRefusal_InFrench_IsOneWholeSentence(string declared, string expected)
    {
        var refusal = ExchangeRecipientRules.TypeFitRefusalMessage("group", declared, groupType: null)!;

        Assert.That(MessageRenderer.Render(refusal, French, French), Is.EqualTo(expected));
    }

    [Test]
    public void Decide_TypeChange_InGerman_NamesBothTypesWithoutAnArticleToAgree()
    {
        var decision = ExchangeRecipientRules.Decide("user", ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.MailUser, groupType: null);

        Assert.That(MessageRenderer.Render(decision.ReasonDetail!, German, German), Is.EqualTo(
            "Das Ändern des Typs eines Empfängers wird in dieser Version nicht unterstützt: Dieses Objekt ist in Exchange vom Typ Postfach " +
            "und ist jetzt als Typ E-Mail-Benutzer deklariert. In Exchange wurde nichts geändert."));
    }

    [Test]
    public void Decide_UnmanagedRecipient_InGerman_PutsTheTypeInTheDative()
    {
        var decision = ExchangeRecipientRules.Decide("group", ExchangeRecipientTypes.Unmanaged, ExchangeRecipientTypes.MailEnabledGroup, groupType: null);

        Assert.That(MessageRenderer.Render(decision.ReasonDetail!, German, German), Does.Contain("nicht zu einer E-Mail-aktivierten Gruppe gemacht werden"));
    }

    [Test]
    public void Decide_Stage_HasNoReason()
    {
        var decision = ExchangeRecipientRules.Decide("user", null, ExchangeRecipientTypes.Mailbox, groupType: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Kind, Is.EqualTo(ExchangeRecipientDecisionKind.Stage));
            Assert.That(decision.ReasonDetail, Is.Null);
            Assert.That(decision.Reason, Is.Null);
        }
    }

    /// <summary>The rules' English as they composed it before #3084, kept here as the reference the messages must match.</summary>
    private static class Legacy
    {
        public static string? Decide(string objectTypeName, string? importedRecipientType, string? declaredRecipientType, long? groupType)
        {
            var imported = Normalise(importedRecipientType);
            var declared = Normalise(declaredRecipientType);

            if ((imported is ExchangeRecipientTypes.None or ExchangeRecipientTypes.Unmanaged) && declared == ExchangeRecipientTypes.None)
                return null;

            if (imported == ExchangeRecipientTypes.Unmanaged && ExchangeRecipientTypes.All.Contains(declared))
                return $"This object is a kind of recipient JIM does not manage (for example a linked or remote mailbox), so it cannot be made {Describe(declared)}. " +
                       "Nothing was changed in Exchange.";

            if (TypeFitRefusal(objectTypeName, declared, groupType) is { } refusal)
                return refusal;

            if (string.Equals(imported, declared, StringComparison.Ordinal) || imported == ExchangeRecipientTypes.None)
                return null;

            return $"Changing a recipient's type is not supported in this release: this object is {Describe(imported)} in Exchange, " +
                   $"and is now declared {(declared == ExchangeRecipientTypes.None ? "not to be a recipient" : Describe(declared))}. " +
                   "Nothing was changed in Exchange.";
        }

        private static string? TypeFitRefusal(string objectTypeName, string declared, long? groupType) => declared switch
        {
            ExchangeRecipientTypes.None => null,
            ExchangeRecipientTypes.Unmanaged =>
                $"'{ExchangeRecipientTypes.Unmanaged}' cannot be declared: it is what JIM reads for a recipient it does not manage. " +
                $"Use one of {string.Join(", ", ExchangeRecipientTypes.All)}.",
            ExchangeRecipientTypes.Mailbox or ExchangeRecipientTypes.SharedMailbox or ExchangeRecipientTypes.RoomMailbox
                or ExchangeRecipientTypes.EquipmentMailbox => Is(objectTypeName, "user")
                    ? null
                    : $"Only a user can have {Describe(declared)}; this object is a {objectTypeName}.",
            ExchangeRecipientTypes.MailUser => Is(objectTypeName, "user")
                ? null
                : $"Only a user can be a mail user; this object is a {objectTypeName}.",
            ExchangeRecipientTypes.MailContact => Is(objectTypeName, "contact")
                ? null
                : $"Only a contact can be a mail contact; this object is a {objectTypeName}.",
            ExchangeRecipientTypes.MailEnabledGroup => !Is(objectTypeName, "group")
                ? $"Only a group can be mail-enabled; this object is a {objectTypeName}."
                : groupType is { } scope && (scope & 0x8) == 0
                    ? "Only universal groups can be mail-enabled; this group is not universal."
                    : null,
            _ => $"'{declared}' is not an Exchange recipient type; use one of {string.Join(", ", ExchangeRecipientTypes.All)}."
        };

        private static string Describe(string recipientType) => recipientType switch
        {
            ExchangeRecipientTypes.None => "not a recipient",
            ExchangeRecipientTypes.Unmanaged => "a recipient JIM does not manage",
            ExchangeRecipientTypes.Mailbox => "a mailbox",
            ExchangeRecipientTypes.SharedMailbox => "a shared mailbox",
            ExchangeRecipientTypes.RoomMailbox => "a room mailbox",
            ExchangeRecipientTypes.EquipmentMailbox => "an equipment mailbox",
            ExchangeRecipientTypes.MailUser => "a mail user",
            ExchangeRecipientTypes.MailContact => "a mail contact",
            ExchangeRecipientTypes.MailEnabledGroup => "a mail-enabled group",
            var other => $"'{other}'"
        };

        private static string Normalise(string? recipientType) =>
            string.IsNullOrEmpty(recipientType) ? ExchangeRecipientTypes.None : recipientType;

        private static bool Is(string objectTypeName, string expected) =>
            string.Equals(objectTypeName, expected, StringComparison.OrdinalIgnoreCase);
    }
}

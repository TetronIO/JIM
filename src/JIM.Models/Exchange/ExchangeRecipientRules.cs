// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// The rules export staging applies to an object's declared Exchange recipient state before anything is queued
/// (#2021, decision D9): whether the declared recipient type fits the object (PRD requirement 5), and whether it would
/// change an existing recipient's type, which the first release does not make (requirement 16). Pure functions of what
/// the export already holds, so they are tested on their own.
/// </summary>
/// <remarks>
/// Recipient types are compared ordinally, as the queue, the import derivation and export confirmation compare them: a
/// value in another case is reported as unrecognised rather than quietly accepted, because it would otherwise be queued
/// as one string and confirmed against another.
/// </remarks>
public static class ExchangeRecipientRules
{
    /// <summary>
    /// The scope bit of Active Directory's <c>groupType</c> that makes a group universal, the only scope Exchange
    /// mail-enables.
    /// </summary>
    public const long UniversalGroupScope = 0x8;

    private const string User = "user";
    private const string Group = "group";
    private const string Contact = "contact";

    /// <summary>
    /// Why <paramref name="declaredRecipientType"/> cannot be made on an object of <paramref name="objectTypeName"/>, or
    /// null when it fits.
    /// </summary>
    /// <param name="objectTypeName">The Connected System Object Type's name (<c>user</c>, <c>group</c>, <c>contact</c>),
    /// compared case-insensitively.</param>
    /// <param name="declaredRecipientType">The declared recipient type; null or empty means None, which fits
    /// anything.</param>
    /// <param name="groupType">The group's <c>groupType</c>, or null when JIM does not know it (not imported and not
    /// flowed). An unknown scope is left to Exchange, which refuses a group that is not universal itself: refusing here
    /// would block every group whose <c>groupType</c> is not selected.</param>
    public static string? TypeFitRefusal(string objectTypeName, string? declaredRecipientType, long? groupType)
    {
        ArgumentNullException.ThrowIfNull(objectTypeName);
        var declared = Normalise(declaredRecipientType);

        return declared switch
        {
            ExchangeRecipientTypes.None => null,
            ExchangeRecipientTypes.Unmanaged =>
                $"'{ExchangeRecipientTypes.Unmanaged}' cannot be declared: it is what JIM reads for a recipient it does not manage. " +
                $"Use one of {string.Join(", ", ExchangeRecipientTypes.All)}.",
            ExchangeRecipientTypes.Mailbox or ExchangeRecipientTypes.SharedMailbox or ExchangeRecipientTypes.RoomMailbox
                or ExchangeRecipientTypes.EquipmentMailbox => Is(objectTypeName, User)
                    ? null
                    : $"Only a user can have {Describe(declared)}; this object is a {objectTypeName}.",
            ExchangeRecipientTypes.MailUser => Is(objectTypeName, User)
                ? null
                : $"Only a user can be a mail user; this object is a {objectTypeName}.",
            ExchangeRecipientTypes.MailContact => Is(objectTypeName, Contact)
                ? null
                : $"Only a contact can be a mail contact; this object is a {objectTypeName}.",
            ExchangeRecipientTypes.MailEnabledGroup => !Is(objectTypeName, Group)
                ? $"Only a group can be mail-enabled; this object is a {objectTypeName}."
                : groupType is { } scope && (scope & UniversalGroupScope) == 0
                    ? "Only universal groups can be mail-enabled; this group is not universal."
                    : null,
            _ => $"'{declared}' is not an Exchange recipient type; use one of {string.Join(", ", ExchangeRecipientTypes.All)}."
        };
    }

    /// <summary>
    /// What the declared recipient type does to the recipient an import last read back: a change from None to a type is
    /// an enable, the same type an update, and anything else, including a change to None, a type change.
    /// </summary>
    /// <param name="importedRecipientType">The recipient type the last import read back; null or empty means None (the
    /// object has not been imported with one).</param>
    /// <param name="declaredRecipientType">The declared recipient type; null or empty means None.</param>
    public static ExchangeRecipientChangeKind ClassifyChange(string? importedRecipientType, string? declaredRecipientType)
    {
        var imported = Normalise(importedRecipientType);
        var declared = Normalise(declaredRecipientType);

        if (string.Equals(imported, declared, StringComparison.Ordinal))
            return ExchangeRecipientChangeKind.Update;

        return imported == ExchangeRecipientTypes.None ? ExchangeRecipientChangeKind.Enable : ExchangeRecipientChangeKind.TypeChange;
    }

    /// <summary>
    /// What export staging does with an object's declared recipient state. A type that does not fit the object is
    /// refused before a type change is considered, because the declaration cannot be right for this object whatever it
    /// is in Exchange today.
    /// <para>
    /// A recipient the import read as <see cref="ExchangeRecipientTypes.Unmanaged"/> (a kind this release does not
    /// manage) is left as it is: declared None there is nothing to do, and any type JIM recognises is refused with a
    /// sentence saying why, rather than attempted as an enable or reported as a type change. A declaration JIM does not
    /// recognise, Unmanaged itself included, is refused as the type it is.
    /// </para>
    /// </summary>
    public static ExchangeRecipientDecision Decide(string objectTypeName, string? importedRecipientType, string? declaredRecipientType, long? groupType)
    {
        var imported = Normalise(importedRecipientType);
        var declared = Normalise(declaredRecipientType);

        if ((imported is ExchangeRecipientTypes.None or ExchangeRecipientTypes.Unmanaged) && declared == ExchangeRecipientTypes.None)
            return new ExchangeRecipientDecision { Kind = ExchangeRecipientDecisionKind.NotARecipient };

        if (imported == ExchangeRecipientTypes.Unmanaged && ExchangeRecipientTypes.All.Contains(declared))
        {
            return new ExchangeRecipientDecision
            {
                Kind = ExchangeRecipientDecisionKind.Refused,
                Reason = $"This object is a kind of recipient JIM does not manage (for example a linked or remote mailbox), so it cannot be made {Describe(declared)}. " +
                         "Nothing was changed in Exchange."
            };
        }

        if (TypeFitRefusal(objectTypeName, declared, groupType) is { } refusal)
            return new ExchangeRecipientDecision { Kind = ExchangeRecipientDecisionKind.Refused, Reason = refusal };

        var change = ClassifyChange(imported, declared);
        if (change == ExchangeRecipientChangeKind.TypeChange)
        {
            return new ExchangeRecipientDecision
            {
                Kind = ExchangeRecipientDecisionKind.TypeChangeNotSupported,
                Reason = $"Changing a recipient's type is not supported in this release: this object is {Describe(imported)} in Exchange, " +
                         $"and is now declared {(declared == ExchangeRecipientTypes.None ? "not to be a recipient" : Describe(declared))}. " +
                         "Nothing was changed in Exchange."
            };
        }

        return new ExchangeRecipientDecision { Kind = ExchangeRecipientDecisionKind.Stage, Change = change };
    }

    /// <summary>
    /// A recipient type in a sentence, with its article ("a shared mailbox", "an equipment mailbox"). None reads "not a
    /// recipient"; an unrecognised type is quoted as it was declared.
    /// </summary>
    public static string Describe(string? recipientType) => Normalise(recipientType) switch
    {
        ExchangeRecipientTypes.None => "not a recipient",
        ExchangeRecipientTypes.Unmanaged => "a recipient JIM does not manage",
        ExchangeRecipientTypes.EquipmentMailbox => $"an {Noun(recipientType)}",
        var type when ExchangeRecipientTypes.All.Contains(type) => $"a {Noun(type)}",
        var other => $"'{other}'"
    };

    /// <summary>
    /// A recipient type as a noun in a sentence ("shared mailbox", "mail-enabled group"). None reads "recipient"; an
    /// unrecognised type is quoted as it was declared.
    /// </summary>
    public static string Noun(string? recipientType) => Normalise(recipientType) switch
    {
        ExchangeRecipientTypes.None => "recipient",
        ExchangeRecipientTypes.Mailbox => "mailbox",
        ExchangeRecipientTypes.SharedMailbox => "shared mailbox",
        ExchangeRecipientTypes.RoomMailbox => "room mailbox",
        ExchangeRecipientTypes.EquipmentMailbox => "equipment mailbox",
        ExchangeRecipientTypes.MailUser => "mail user",
        ExchangeRecipientTypes.MailContact => "mail contact",
        ExchangeRecipientTypes.MailEnabledGroup => "mail-enabled group",
        ExchangeRecipientTypes.Unmanaged => "recipient JIM does not manage",
        var other => $"'{other}'"
    };

    /// <summary>Nothing declared and nothing imported both mean None.</summary>
    private static string Normalise(string? recipientType) =>
        string.IsNullOrEmpty(recipientType) ? ExchangeRecipientTypes.None : recipientType;

    private static bool Is(string objectTypeName, string expected) =>
        string.Equals(objectTypeName, expected, StringComparison.OrdinalIgnoreCase);
}

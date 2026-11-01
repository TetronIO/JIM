// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// The values the Exchange Recipient Type attribute takes (#2021, PRD requirement 4): what kind of Exchange recipient an
/// object should be. Held as text, as the attribute flows it, so a value JIM does not recognise is recorded and
/// reported exactly as it was declared.
/// </summary>
public static class ExchangeRecipientTypes
{
    /// <summary>Not an Exchange recipient.</summary>
    public const string None = "None";

    /// <summary>A user mailbox.</summary>
    public const string Mailbox = "Mailbox";

    /// <summary>A mailbox several people use, on a disabled directory account.</summary>
    public const string SharedMailbox = "SharedMailbox";

    /// <summary>A room's mailbox, on a disabled directory account.</summary>
    public const string RoomMailbox = "RoomMailbox";

    /// <summary>A piece of equipment's mailbox, on a disabled directory account.</summary>
    public const string EquipmentMailbox = "EquipmentMailbox";

    /// <summary>A directory user whose mail is delivered to an external address.</summary>
    public const string MailUser = "MailUser";

    /// <summary>A directory contact whose mail is delivered to an external address.</summary>
    public const string MailContact = "MailContact";

    /// <summary>A universal group that receives mail.</summary>
    public const string MailEnabledGroup = "MailEnabledGroup";

    /// <summary>Every recipient type, None included.</summary>
    public static IReadOnlyList<string> All { get; } =
        [None, Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact, MailEnabledGroup];
}

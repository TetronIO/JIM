// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Web.Models.Api;

/// <summary>
/// The five Exchange recipient values of an account: its recipient type, alias, mailbox database, primary SMTP address
/// and external address. Each is null where nothing is declared or read.
/// </summary>
public class ExchangeRecipientValuesDto
{
    /// <summary>
    /// None, Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact or MailEnabledGroup.
    /// </summary>
    public string? RecipientType { get; set; }

    /// <summary>The Exchange alias.</summary>
    public string? Alias { get; set; }

    /// <summary>The mailbox database, by name.</summary>
    public string? Database { get; set; }

    /// <summary>The primary SMTP address.</summary>
    public string? PrimarySmtpAddress { get; set; }

    /// <summary>The external address of a mail user or mail contact.</summary>
    public string? ExternalAddress { get; set; }

    /// <summary>
    /// Creates the DTO from the five values.
    /// </summary>
    public static ExchangeRecipientValuesDto FromModel(ExchangeDeclaredState state) => new()
    {
        RecipientType = state.RecipientType,
        Alias = state.Alias,
        Database = state.Database,
        PrimarySmtpAddress = state.PrimarySmtpAddress,
        ExternalAddress = state.ExternalAddress
    };
}

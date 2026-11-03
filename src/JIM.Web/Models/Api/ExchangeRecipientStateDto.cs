// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// An account's Exchange recipient: what its Synchronisation Rules declare, what the last import read back, and the
/// change still owed to Exchange, if any. JIM treats an account as a recipient only once an import shows it.
/// </summary>
public class ExchangeRecipientStateDto
{
    /// <summary>
    /// What is declared now: the imported values with any changes still awaiting Exchange laid over.
    /// </summary>
    public ExchangeRecipientValuesDto Declared { get; set; } = new();

    /// <summary>
    /// What the last import read back.
    /// </summary>
    public ExchangeRecipientValuesDto Imported { get; set; } = new();

    /// <summary>
    /// Whether what is declared is what the import read back, with nothing still being worked on.
    /// </summary>
    public bool IsInStep { get; set; }

    /// <summary>
    /// The change on the Exchange queue for the account, with where it stands; null where nothing is queued.
    /// </summary>
    public ExchangeChangeOutcome? QueuedChange { get; set; }

    /// <summary>
    /// Creates the DTO from the account's state.
    /// </summary>
    public static ExchangeRecipientStateDto FromModel(ExchangeRecipientState state) => new()
    {
        Declared = ExchangeRecipientValuesDto.FromModel(state.Declared),
        Imported = ExchangeRecipientValuesDto.FromModel(state.Imported),
        IsInStep = state.IsInStep,
        QueuedChange = state.QueuedChange
    };
}

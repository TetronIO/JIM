// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;

namespace JIM.Models.Exchange;

/// <summary>
/// One account's Exchange recipient as JIM sees it (#2021, Phase 6): what its Synchronisation Rules declare, what the
/// last import read back from the directory, and the change still owed to Exchange, if any. JIM believes Exchange only
/// once an import shows it, so an account whose mailbox is still being made is not yet a recipient.
/// </summary>
public sealed class ExchangeRecipientState
{
    public required Guid ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The state declared now: the imported values with the changes the account's Pending Export still holds for
    /// Exchange laid over.
    /// </summary>
    public required ExchangeDeclaredState Declared { get; init; }

    /// <summary>
    /// The five Exchange attributes as the last import read them.
    /// </summary>
    public required ExchangeDeclaredState Imported { get; init; }

    /// <summary>
    /// The change on the Exchange queue for the account, with where it stands; null where nothing is queued.
    /// </summary>
    public ExchangeChangeOutcome? QueuedChange { get; init; }

    /// <summary>
    /// Whether what is declared is what the import read back, and nothing is still being worked on.
    /// </summary>
    public bool IsInStep => SameValues(Declared, Imported) && QueuedChange is not { IsSettled: false };

    private static bool SameValues(ExchangeDeclaredState a, ExchangeDeclaredState b) =>
        Same(a.RecipientType, b.RecipientType) && Same(a.Alias, b.Alias) && Same(a.Database, b.Database)
        && Same(a.PrimarySmtpAddress, b.PrimarySmtpAddress) && Same(a.ExternalAddress, b.ExternalAddress);

    private static bool Same(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
}

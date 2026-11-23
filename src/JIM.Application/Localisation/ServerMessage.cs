// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Application.Localisation;

/// <summary>
/// Builds messages from the <see cref="ApplicationMessageCatalogues.Servers"/> catalogue, so a refusal reads
/// <c>ServerMessage.Create(nameof(ServerMessages.AttributeNotFound), id.ToString())</c>. Pass identifiers as text:
/// a number argument is shown with the reader's digit grouping, which an id must not have.
/// </summary>
internal static class ServerMessage
{
    /// <summary>A message with no count.</summary>
    public static JimMessage Create(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.Servers, key, arguments);

    /// <summary>A counted message; <paramref name="oneKey"/> is the singular's key.</summary>
    public static JimMessage Counted(string oneKey, long count, params MessageArgument[] arguments) =>
        JimMessage.Counted(ApplicationMessageCatalogues.Servers, oneKey, count, arguments);
}

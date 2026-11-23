// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Localisation;

namespace JIM.Application.Localisation;

/// <summary>
/// Builds the messages in <c>ConnectedSystemMessages.resx</c> (#3030), so a call site writes
/// <c>ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.Key), arguments)</c> and the catalogue's name is held once.
/// </summary>
public static class ConnectedSystemMessage
{
    /// <summary>A message with no count.</summary>
    public static JimMessage Create(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.ConnectedSystems, key, arguments);

    /// <summary>A counted message; pass the singular's key (ending <c>One</c>).</summary>
    public static JimMessage Counted(string oneKey, long count, params MessageArgument[] arguments) =>
        JimMessage.Counted(ApplicationMessageCatalogues.ConnectedSystems, oneKey, count, arguments);

    /// <summary>
    /// Several items as one comma-separated list in the reader's language (items nest, so each may itself be a message).
    /// One item is itself; the list must not be empty.
    /// </summary>
    public static JimMessage List(IEnumerable<JimMessage> items) => Fold(items, nameof(ConnectedSystemMessages.ListPair));

    /// <summary>Several items as one semicolon-separated list in the reader's language.</summary>
    public static JimMessage Semicolons(IEnumerable<JimMessage> items) => Fold(items, nameof(ConnectedSystemMessages.SemicolonPair));

    /// <summary>Several sentences joined by a space, in the reader's language.</summary>
    public static JimMessage Sentences(IEnumerable<JimMessage> sentences) => Fold(sentences, nameof(ConnectedSystemMessages.MessagePair));

    private static JimMessage Fold(IEnumerable<JimMessage> items, string pairKey)
    {
        JimMessage? joined = null;
        foreach (var item in items)
            joined = joined == null ? item : Create(pairKey, joined, item);

        return joined ?? throw new ArgumentException("There is nothing to join.", nameof(items));
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// A message JIM shows to a person, held as a key into a message catalogue plus its arguments rather than as rendered
/// English (#3030, #3039). The application layer, the Worker and the Connectors produce these; the portal renders one
/// in the viewer's language, while the REST API, the logs and anything stored for those use its British English
/// (<see cref="MessageRenderer.ToEnglish"/>), so they never change with anyone's language.
/// </summary>
/// <param name="Catalogue">
/// The catalogue holding the text: <c>"&lt;assembly&gt;:&lt;resource file&gt;"</c>, for instance
/// <c>"JIM.Application:ConnectedSystemMessages"</c> for <c>src/JIM.Application/Resources/ConnectedSystemMessages.resx</c>.
/// Each project declares its catalogues as constants rather than writing the string at each call.
/// </param>
/// <param name="Key">
/// The text's name in the catalogue, always written <c>nameof(ConnectedSystemMessages.NotFound)</c> so a mistyped or
/// removed key fails the build. For a counted message it is the stem: the catalogue holds <c>KeyOne</c> and
/// <c>KeyOther</c>, chosen by <paramref name="PluralCount"/> under the reader's language's own rule.
/// </param>
/// <param name="Arguments">The values for the text's <c>{0}</c>, <c>{1}</c> placeholders, in order.</param>
/// <param name="PluralCount">For a counted message, the count that picks the singular or plural form.</param>
public sealed record JimMessage(string Catalogue, string Key, IReadOnlyList<MessageArgument> Arguments, long? PluralCount = null)
{
    /// <summary>A message with no count.</summary>
    public static JimMessage Create(string catalogue, string key, params MessageArgument[] arguments) =>
        new(catalogue, key, arguments);

    /// <summary>
    /// A counted message: the catalogue holds <c>XOne</c> and <c>XOther</c>, and <paramref name="count"/> picks between
    /// them in the reader's language. Pass the singular's key, <c>nameof(Messages.ObjectsDeletedOne)</c>, so the
    /// compiler checks it; <c>LocalisationResourceTests</c> checks every <c>One</c> has its <c>Other</c>. The count is
    /// usually one of the arguments as well.
    /// </summary>
    public static JimMessage Counted(string catalogue, string oneKey, long count, params MessageArgument[] arguments)
    {
        if (!oneKey.EndsWith("One", StringComparison.Ordinal))
            throw new ArgumentException($"A counted message is named by its singular key, ending \"One\"; got \"{oneKey}\".", nameof(oneKey));

        return new JimMessage(catalogue, oneKey[..^"One".Length], arguments, count);
    }

    /// <summary>The message's British English, for logs, the REST API and anything stored.</summary>
    public override string ToString() => this.ToEnglish();
}

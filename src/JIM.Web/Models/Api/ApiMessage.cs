// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Web.Models.Api;

/// <summary>
/// Turns a stored message into the key, arguments and count the REST API returns beside its English (#3039).
/// </summary>
/// <remarks>
/// A key is the catalogue's name and the message's key joined by a full stop (<c>ActivityMessages.RoleMemberAdded</c>):
/// the catalogue's assembly is an implementation detail and is left out. A Connector's catalogue keeps its whole name
/// (<c>connector:jim.ldap/Messages.PermissionMissing</c>), since that is what says which Connector the message is from. Every key that can reach the API is pinned by
/// <c>PublishedMessageKeyTests</c>, because renaming one would break a client that renders it.
/// </remarks>
public static class ApiMessage
{
    /// <summary>The message's published key, or null where there is no message.</summary>
    public static string? KeyOf(JimMessage? message) =>
        message == null ? null : CatalogueName(message.Catalogue) + "." + message.Key;

    /// <summary>The message's arguments, typed, or null where there is no message.</summary>
    public static List<MessageArgumentDto>? ArgumentsOf(JimMessage? message) =>
        message?.Arguments.Select(ToDto).ToList();

    /// <summary>The count that chooses a counted message's form, or null for any other message.</summary>
    public static long? CountOf(JimMessage? message) => message?.PluralCount;

    private static MessageArgumentDto ToDto(MessageArgument argument) => new()
    {
        Text = argument.Text,
        Number = argument.Number,
        Date = argument.Utc,
        Message = argument.Message == null
            ? null
            : new MessageDto
            {
                Key = KeyOf(argument.Message)!,
                Arguments = ArgumentsOf(argument.Message)!,
                Count = CountOf(argument.Message)
            }
    };

    private static string CatalogueName(string catalogue)
    {
        if (ConnectorCatalogueNames.IsConnectorCatalogue(catalogue))
            return catalogue;

        var separator = catalogue.IndexOf(':');
        return separator < 0 ? catalogue : catalogue[(separator + 1)..];
    }
}

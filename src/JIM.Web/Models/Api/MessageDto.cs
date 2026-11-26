// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Web.Models.Api;

/// <summary>
/// A message placed inside another message: its key, its arguments and, for a counted message, the count.
/// </summary>
public class MessageDto
{
    /// <summary>
    /// The message's key, as the catalogue name and the key joined by a full stop, such as
    /// <c>ActivityMessages.RoleMemberAdded</c>. For a counted message this is the key without its One or Other ending.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The values placed into the message, in order: the first fills <c>{0}</c>, the second <c>{1}</c>, and so on.
    /// </summary>
    public List<MessageArgumentDto> Arguments { get; set; } = [];

    /// <summary>
    /// For a counted message, the count that chooses its singular or plural form; absent otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }
}

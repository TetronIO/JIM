// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Localisation;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a change to an attribute is refused because configuration reads it (#2265): renaming a Metaverse
/// Attribute an Expression reads by name, or deselecting a Connected System attribute a Scoping Criterion reads.
/// The message names every reader, so the administrator knows what to change first; nothing is changed.
/// </summary>
public class AttributeReadByConfigurationException : InvalidOperationException, IJimMessageException
{
    /// <summary>
    /// The configuration that reads the attribute.
    /// </summary>
    public IReadOnlyList<AttributeReader> Readers { get; }

    /// <summary>
    /// The refusal, for rendering in the reader's language; null where the exception was built from English text.
    /// </summary>
    public JimMessage? Detail { get; }

    /// <summary>
    /// A refusal that carries its message, readers included, so the portal can show it in the reader's language.
    /// </summary>
    public AttributeReadByConfigurationException(JimMessage detail, IReadOnlyList<AttributeReader> readers)
        : base(detail.ToEnglish())
    {
        Detail = detail;
        Readers = readers;
    }

    public AttributeReadByConfigurationException(string refusal, IReadOnlyList<AttributeReader> readers)
        : base($"{refusal} {string.Join("; ", readers.Select(r => r.Description))}.")
    {
        Readers = readers;
    }
}

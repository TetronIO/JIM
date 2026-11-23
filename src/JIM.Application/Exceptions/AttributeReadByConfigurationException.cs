// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core.DTOs;
using JIM.Models.Localisation;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a change to an attribute is refused because configuration reads it (#2265): renaming a Metaverse
/// Attribute an Expression reads by name, or deselecting a Connected System attribute a Scoping Criterion reads.
/// The message names every reader, so the administrator knows what to change first; nothing is changed.
/// </summary>
public class AttributeReadByConfigurationException : InvalidOperationException, IJimMessageCarrier
{
    /// <summary>
    /// The configuration that reads the attribute.
    /// </summary>
    public IReadOnlyList<AttributeReader> Readers { get; }

    /// <inheritdoc />
    public JimMessage? Detail { get; }

    public AttributeReadByConfigurationException(string refusal, IReadOnlyList<AttributeReader> readers)
        : base($"{refusal} {string.Join("; ", readers.Select(r => r.Description))}.")
    {
        Readers = readers;
    }

    /// <summary>
    /// A refusal the portal shows in the viewer's language (#3030); <see cref="Exception.Message"/> is its British English.
    /// </summary>
    /// <param name="refusal">The sentence saying what was refused and why.</param>
    /// <param name="readers">The configuration reading the attribute, each with its description.</param>
    public AttributeReadByConfigurationException(JimMessage refusal, IReadOnlyList<AttributeReader> readers)
        : this(refusal, readers, Describe(refusal, readers))
    {
    }

    private AttributeReadByConfigurationException(JimMessage refusal, IReadOnlyList<AttributeReader> readers, JimMessage detail)
        : base(detail.ToEnglish())
    {
        Readers = readers;
        Detail = detail;
    }

    private static JimMessage Describe(JimMessage refusal, IReadOnlyList<AttributeReader> readers) =>
        ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.AttributeReadersRefusal), refusal,
            ConnectedSystemMessage.Semicolons(readers.Select(reader => reader.DescriptionDetail
                ?? ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.PassThroughText), reader.Description))));
}

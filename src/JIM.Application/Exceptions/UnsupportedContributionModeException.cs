// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Localisation;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a Metaverse Attribute would be created or changed to a contribution mode its data type or plurality
/// cannot use (#3059): any mode other than Priority on a single-valued attribute, or Accumulate on a Reference attribute.
/// Nothing is created or changed. The REST API maps it to 400 Bad Request carrying <see cref="System.Exception.Message"/>,
/// which names the rule.
/// </summary>
public class UnsupportedContributionModeException : InvalidOperationException, IJimMessageCarrier
{
    /// <summary>
    /// The contribution mode that was requested.
    /// </summary>
    public AttributeContributionMode ContributionMode { get; }

    /// <summary>
    /// The data type the attribute would have.
    /// </summary>
    public AttributeDataType DataType { get; }

    /// <summary>
    /// The plurality the attribute would have.
    /// </summary>
    public AttributePlurality Plurality { get; }

    /// <summary>
    /// The refusal, for rendering in the reader's language.
    /// </summary>
    public JimMessage Detail { get; }

    public UnsupportedContributionModeException(AttributeContributionMode contributionMode, AttributeDataType dataType, AttributePlurality plurality, JimMessage detail)
        : base(detail.ToEnglish())
    {
        ContributionMode = contributionMode;
        DataType = dataType;
        Plurality = plurality;
        Detail = detail;
    }
}

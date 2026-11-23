// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a feature flag key does not match any entry in <c>FeatureFlagCatalogue</c>. REST maps this to a
/// 404 response.
/// </summary>
public class FeatureFlagNotFoundException : KeyNotFoundException, IJimMessageCarrier
{
    /// <summary>
    /// The key that was not found in the catalogue.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// The refusal, for rendering in the reader's language.
    /// </summary>
    public JimMessage Detail { get; }

    public FeatureFlagNotFoundException(string key)
        : this(key, ServerMessage.Create(nameof(ServerMessages.FeatureFlagNotFound), key))
    {
    }

    private FeatureFlagNotFoundException(string key, JimMessage detail)
        : base(detail.ToEnglish())
    {
        Key = key;
        Detail = detail;
    }
}

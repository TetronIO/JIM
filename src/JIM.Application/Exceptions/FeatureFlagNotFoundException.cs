// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a feature flag key does not match any entry in <c>FeatureFlagCatalogue</c>. REST maps this to a
/// 404 response.
/// </summary>
public class FeatureFlagNotFoundException : KeyNotFoundException
{
    /// <summary>
    /// The key that was not found in the catalogue.
    /// </summary>
    public string Key { get; }

    public FeatureFlagNotFoundException(string key)
        : base($"No feature flag with key '{key}' exists.")
    {
        Key = key;
    }
}

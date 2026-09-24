// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown by <c>FeatureFlagServer.EnsureEnabledAsync</c> when a caller reaches an entry point that is gated on a
/// feature flag that is currently off. Consumers gate at entry points (a portal control, a REST endpoint, a
/// server-side configuration write) rather than deep in the engine; this exception names the feature so the
/// gate's message is self-explanatory wherever it surfaces. REST maps this to a 400 response.
/// </summary>
public class FeatureDisabledException : InvalidOperationException
{
    /// <summary>
    /// The definition of the disabled feature.
    /// </summary>
    public FeatureFlagDefinition Definition { get; }

    public FeatureDisabledException(FeatureFlagDefinition definition)
        : base($"The '{definition.DisplayName}' feature is currently disabled.")
    {
        Definition = definition;
    }
}

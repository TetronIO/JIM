// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core;
using JIM.Models.Localisation;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown by <c>FeatureFlagServer.EnsureEnabledAsync</c> when a caller reaches an entry point that is gated on a
/// feature flag that is currently off. Consumers gate at entry points (a portal control, a REST endpoint, a
/// server-side configuration write) rather than deep in the engine; this exception names the feature so the
/// gate's message is self-explanatory wherever it surfaces. REST maps this to a 400 response.
/// </summary>
public class FeatureDisabledException : InvalidOperationException, IJimMessageCarrier
{
    /// <summary>
    /// The definition of the disabled feature.
    /// </summary>
    public FeatureFlagDefinition Definition { get; }

    /// <summary>
    /// The refusal, for rendering in the reader's language.
    /// </summary>
    public JimMessage Detail { get; }

    public FeatureDisabledException(FeatureFlagDefinition definition)
        : this(definition, ServerMessage.Create(nameof(ServerMessages.FeatureDisabled), definition.DisplayName))
    {
    }

    private FeatureDisabledException(FeatureFlagDefinition definition, JimMessage detail)
        : base(detail.ToEnglish())
    {
        Definition = definition;
        Detail = detail;
    }
}

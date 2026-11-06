// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Who made one or more of the configuration changes in a <see cref="ConfigurationChangeGroup"/>.
/// </summary>
/// <param name="InitiatedByType">The kind of principal, or null once the Activity that recorded the change has gone.</param>
/// <param name="InitiatedById">The principal's identifier.</param>
/// <param name="InitiatedByName">The principal's name as it was when the change was made.</param>
public record ConfigurationChangeInitiator(ActivityInitiatorType? InitiatedByType, Guid? InitiatedById, string? InitiatedByName);

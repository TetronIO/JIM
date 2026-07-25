// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A throughput estimate for an in-progress Activity, derived from successive progress samples
/// (#202). Both members are null until enough samples exist to compute a rate.
/// </summary>
/// <param name="ObjectsPerSecond">Objects processed per second over the recent sample window, or
/// null when no rate can be computed yet.</param>
/// <param name="EstimatedSecondsRemaining">Estimated seconds until the current counting window
/// completes, or null when the rate is zero or the total is indeterminate.</param>
public readonly record struct ActivityEtaEstimate(double? ObjectsPerSecond, double? EstimatedSecondsRemaining);

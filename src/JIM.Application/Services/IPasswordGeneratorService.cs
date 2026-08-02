// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// Generates initial passwords, and tells an administrator in advance what a given configuration would produce.
/// </summary>
public interface IPasswordGeneratorService
{
    /// <summary>
    /// Generates one password satisfying the configuration.
    /// <para>
    /// Compliant by construction: the requirements are placed first and the result shuffled, rather than
    /// generating a candidate and re-rolling until one passes. Re-rolling makes the running time depend on the
    /// configuration, and quietly biases the output towards whatever is easiest to satisfy.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The configuration cannot be satisfied, for example because the per-category minimums add up to more than
    /// the length. Call <see cref="Assess"/> first to present the reason rather than catching this.
    /// </exception>
    string Generate(PasswordGenerationPolicy policy);

    /// <summary>
    /// Works out what the configuration would produce and whether the Connected System would accept it, without
    /// generating anything.
    /// </summary>
    /// <param name="targetPolicy">
    /// What the Connected System demands, where JIM discovered it. Null means nothing was discovered, in which
    /// case the assessment still reports what the configuration produces and simply has nothing to check it
    /// against.
    /// </param>
    PasswordGenerationAssessment Assess(PasswordGenerationPolicy policy, ConnectedSystemPasswordPolicy? targetPolicy);

    /// <summary>
    /// Produces a sensible starting configuration for a Connected System, so an administrator does not have to
    /// retype rules the target already published.
    /// <para>
    /// Only ever stricter than the default, never looser: a target willing to accept eight characters is not a
    /// reason for JIM to generate eight.
    /// </para>
    /// </summary>
    PasswordGenerationPolicy DeriveFrom(ConnectedSystemPasswordPolicy? targetPolicy);
}

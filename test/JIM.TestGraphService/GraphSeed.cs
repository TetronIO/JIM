// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.TestGraphService;

/// <summary>
/// A population for <see cref="MockGraphTenant.Seed"/>: the same shape Scenario 039 provisions from its HR source, so the
/// two scale scenarios measure the same tenant.
/// </summary>
/// <param name="Prefix">Starts every user's and group's display name and mail nickname.</param>
/// <param name="Domain">The domain of every user principal name.</param>
/// <param name="Users">How many users.</param>
/// <param name="Groups">How many groups: All Staff, which holds everyone, and teams.</param>
/// <param name="AverageMemberships">How many groups each user is in, All Staff included.</param>
public sealed record GraphSeed(string Prefix, string Domain, int Users, int Groups, int AverageMemberships);

/// <summary>What <see cref="MockGraphTenant.Seed"/> made.</summary>
public sealed record GraphSeedResult(int Users, int Groups, int Memberships);

/// <summary>What <see cref="MockGraphTenant.ApplyChanges"/> changed.</summary>
public sealed record GraphChangeResult(int Retitled, int Moved, int Deleted);

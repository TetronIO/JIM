// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json.Nodes;

namespace JIM.TestGraphService;

/// <summary>
/// A population made in process, for the scale scenario: a million users cannot be created through Microsoft Graph
/// requests in any time worth waiting, and the scenario measures JIM reading them, not anything writing them.
/// </summary>
public partial class MockGraphTenant
{
    private readonly List<string> _seededUserIds = [];
    private readonly List<string> _seededGroupIds = [];

    /// <summary>
    /// Adds the population: users named <c>{Prefix}u{number}</c>, each managed by the user whose number is a tenth of
    /// theirs; All Staff, holding everyone; and teams, each user in the next few from an offset of their own. The
    /// same shape, and the same names, as Scenario 039 provisions.
    /// </summary>
    public GraphSeedResult Seed(GraphSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentOutOfRangeException.ThrowIfLessThan(seed.Users, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(seed.Groups, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(seed.AverageMemberships, 1);

        lock (_gate)
        {
            var width = Math.Max(5, seed.Users.ToString(CultureInfo.InvariantCulture).Length);
            for (var i = 0; i < seed.Users; i++)
            {
                var number = i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                var accountName = $"{seed.Prefix}u{number}";
                var id = SeedId(1, i);
                var user = new JsonObject
                {
                    ["id"] = id,
                    ["displayName"] = $"{seed.Prefix}Scale User {number}",
                    ["givenName"] = "Scale",
                    ["surname"] = $"User {number}",
                    ["userPrincipalName"] = $"{accountName}@{seed.Domain}",
                    ["mailNickname"] = accountName,
                    ["department"] = $"Department {i % 20}",
                    ["jobTitle"] = "Analyst",
                    ["employeeId"] = $"E{number}",
                    ["accountEnabled"] = true,
                    ["userType"] = "Member",
                    ["onPremisesSyncEnabled"] = null
                };
                if (i > 0)
                {
                    var managerId = _seededUserIds[i / 10];
                    user["managerId"] = managerId;
                    (_reports.TryGetValue(managerId, out var reports) ? reports : _reports[managerId] = []).Add(id);
                }

                Users.Add(user);
                IndexUserPrincipalName(user);
                _seededUserIds.Add(id);
            }

            var groupWidth = Math.Max(3, seed.Groups.ToString(CultureInfo.InvariantCulture).Length);
            for (var g = 0; g < seed.Groups; g++)
            {
                var number = g.ToString(CultureInfo.InvariantCulture).PadLeft(groupWidth, '0');
                var id = SeedId(2, g);
                Groups.Add(new JsonObject
                {
                    ["id"] = id,
                    ["displayName"] = $"{seed.Prefix}{(g == 0 ? "All Staff" : $"Team {number}")}",
                    ["mailNickname"] = $"{seed.Prefix}g{number}",
                    ["securityEnabled"] = true,
                    ["mailEnabled"] = false,
                    ["onPremisesSyncEnabled"] = null
                });
                Members[id] = [];
                Owners[id] = [];
                _seededGroupIds.Add(id);
            }

            var memberships = 0;
            var perPerson = Math.Min(seed.AverageMemberships, seed.Groups);
            for (var i = 0; i < seed.Users; i++)
            {
                Members[_seededGroupIds[0]].Add(("user", _seededUserIds[i]));
                memberships++;
                for (var m = 1; m < perPerson; m++)
                {
                    Members[_seededGroupIds[1 + ((i + m - 1) % (seed.Groups - 1))]].Add(("user", _seededUserIds[i]));
                    memberships++;
                }
            }

            return new GraphSeedResult(seed.Users, seed.Groups, memberships);
        }
    }

    /// <summary>
    /// Changes the seeded population as Scenario 039's changes and leavers do, recording each change for the next delta
    /// round: every tenth user from the eighth gets a new job title, every tenth from the fourth moves from one team to
    /// another, and every hundredth from the sixth is deleted, leaving their groups and their reports' manager.
    /// </summary>
    public GraphChangeResult ApplyChanges()
    {
        lock (_gate)
        {
            int retitled = 0, moved = 0, deleted = 0;
            var teams = _seededGroupIds.Skip(1).ToList();
            for (var i = 0; i < _seededUserIds.Count; i++)
            {
                var id = _seededUserIds[i];
                if (!Users.TryGet(id, out var user))
                    continue;

                if (i % 10 == 7)
                {
                    user["jobTitle"] = "Senior Analyst";
                    RecordChange("users", id);
                    retitled++;
                }
                else if (i % 10 == 3 && teams.Count > 1)
                {
                    var from = teams.FirstOrDefault(t => Members[t].Contains(id));
                    var to = teams.FirstOrDefault(t => !Members[t].Contains(id));
                    if (from != null && to != null)
                    {
                        Members[from].Remove(id);
                        Members[to].Add(("user", id));
                        RecordChange("groups", from);
                        RecordChange("groups", to);
                        moved++;
                    }
                }

                if (i % 100 == 5)
                {
                    Users.Remove(id);
                    RemoveEverywhere("users", id);
                    deleted++;
                }
            }

            return new GraphChangeResult(retitled, moved, deleted);
        }
    }

    /// <summary>An id shaped as Graph's are, from a kind (1 users, 2 groups) and a number, so a population's ids repeat run to run.</summary>
    private static string SeedId(short kind, int number) => new Guid(number, kind, 0, new byte[8]).ToString();
}

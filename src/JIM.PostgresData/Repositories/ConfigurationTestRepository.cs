// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Logic;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public class ConfigurationTestRepository : IConfigurationTestRepository
{
    private PostgresDataRepository Repository { get; }

    internal ConfigurationTestRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    private IQueryable<ConfigurationTestCase> TestCases => Repository.Database.ConfigurationTestCases.AsNoTracking();

    public async Task<ConfigurationTestCase?> GetTestCaseAsync(int id) =>
        await TestCases.SingleOrDefaultAsync(c => c.Id == id);

    public async Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesAsync(IReadOnlyCollection<int> ids) =>
        await TestCases.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Id).ToListAsync();

    public async Task<IReadOnlyList<ConfigurationTestCase>> GetAllTestCasesAsync() =>
        await TestCases.OrderBy(c => c.Id).ToListAsync();

    public async Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesForSyncRuleAsync(int syncRuleId) =>
        await TestCases
            .Where(c => c.SyncRuleId == syncRuleId ||
                        c.SyncRuleMapping!.SyncRuleId == syncRuleId ||
                        c.ObjectMatchingRule!.SyncRuleId == syncRuleId)
            .OrderBy(c => c.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<ConfigurationTestCase>> GetSimpleModeMatchingTestCasesAsync(int connectedSystemId) =>
        await TestCases
            .Where(c => c.ObjectMatchingRule!.SyncRuleId == null &&
                        c.ObjectMatchingRule.ConnectedSystemObjectType!.ConnectedSystemId == connectedSystemId)
            .OrderBy(c => c.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesForConnectedSystemAsync(int connectedSystemId) =>
        await TestCases
            .Where(c => c.SyncRule!.ConnectedSystemId == connectedSystemId ||
                        c.SyncRuleMapping!.SyncRule!.ConnectedSystemId == connectedSystemId ||
                        c.ObjectMatchingRule!.SyncRule!.ConnectedSystemId == connectedSystemId ||
                        (c.ObjectMatchingRule.SyncRuleId == null &&
                         c.ObjectMatchingRule.ConnectedSystemObjectType!.ConnectedSystemId == connectedSystemId))
            .OrderBy(c => c.Id)
            .ToListAsync();

    public async Task CreateTestCaseAsync(ConfigurationTestCase testCase)
    {
        Repository.Database.ConfigurationTestCases.Add(testCase);
        await Repository.Database.SaveChangesAsync();
        Repository.Database.Entry(testCase).State = EntityState.Detached;
    }

    public async Task UpdateTestCaseAsync(ConfigurationTestCase testCase)
    {
        // The case was read untracked, so it is attached for this update only. Its navigations are never loaded, and
        // Update marks only the case itself, so nothing it covers is written.
        Repository.Database.ConfigurationTestCases.Update(testCase);
        await Repository.Database.SaveChangesAsync();
        Repository.Database.Entry(testCase).State = EntityState.Detached;
    }

    public async Task DeleteTestCaseAsync(int id) =>
        await Repository.Database.ConfigurationTestCases.Where(c => c.Id == id).ExecuteDeleteAsync();
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using Microsoft.EntityFrameworkCore.Design;

namespace JIM.PostgresData;

/// <summary>
/// Design-time factory for creating JimDbContext instances during migrations.
/// This allows EF Core tools to create migrations without requiring actual database connection.
/// </summary>
public class JimDbContextFactory : IDesignTimeDbContextFactory<JimDbContext>
{
    public JimDbContext CreateDbContext(string[] args)
    {
        // Set dummy environment variables for migration creation, but only when they are not already set.
        // These are only used at design-time and won't affect runtime.
        SetIfMissing(Constants.Config.DatabaseHostname, "localhost");
        SetIfMissing(Constants.Config.DatabaseName, "jim_design");
        SetIfMissing(Constants.Config.DatabaseUsername, "postgres");
        SetIfMissing(Constants.Config.DatabasePassword, "password");
        SetIfMissing(Constants.Config.DatabaseLogSensitiveInformation, "false");

        return new JimDbContext();
    }

    private static void SetIfMissing(string variableName, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variableName)))
            Environment.SetEnvironmentVariable(variableName, value);
    }
}
// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Repositories;

public class BulkSqlColumnConventionTests
{
    [Test]
    public void RepositoryInsertsAndCopies_UseConstantColumnLists()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "JIM.sln")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null, "Could not locate repository sources for the SQL convention sweep.");
        var failures = new List<string>();
        var statementCount = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(root!.FullName, "src/JIM.PostgresData/Repositories"), "*.cs"))
        {
            var source = File.ReadAllText(file).Replace("\\\"", "\"").Replace("\"\"", "\"");
            foreach (Match match in Regex.Matches(source, "(?:COPY|INSERT\\s+INTO)\\s+\"[^\"]+\"\\s*\\((?<columns>.*?)\\)\\s*(?:FROM|VALUES|SELECT|ON|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                statementCount++;
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                var precedingLines = string.Join('\n', source[..match.Index].Split('\n').TakeLast(4));
                if (precedingLines.Contains("bulk-sql-column-exemption:", StringComparison.Ordinal))
                    continue;
                if (!Regex.IsMatch(match.Groups["columns"].Value, @"^\s*\{BulkSqlHelpers\.ToQuotedList\([^{}]+\)\}\s*$"))
                    failures.Add($"{Path.GetFileName(file)}:{line}: use BulkSqlHelpers.ToQuotedList with a column constant");
            }
        }
        Assert.That(statementCount, Is.GreaterThan(0), "The sweep must find repository write statements.");
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }
}

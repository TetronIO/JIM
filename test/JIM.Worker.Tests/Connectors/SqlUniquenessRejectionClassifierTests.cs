// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Rejection classification for the SQL Connector (Unique Value Generation, #242, release 4, decision 9).
/// <para>
/// The SQL Server and PostgreSQL fixtures are captured: SQL Server 2022 (mcr.microsoft.com/mssql/server:2022-latest)
/// through Microsoft.Data.SqlClient, PostgreSQL 18 through Npgsql, both on 2026-10-06. SQL Server names the
/// constraint or index and never the column, so its rejections are classified but unattributed. PostgreSQL names the
/// column in the detail when the connection includes error detail, and redacts it otherwise. The Oracle fixtures are
/// the documented ORA-00001 shapes (the column clause is Oracle Database 23ai's addition).
/// </para>
/// </summary>
/// <remarks>The database type travels as an int because <see cref="SqlDatabaseType"/> is internal and NUnit test
/// methods are public.</remarks>
[TestFixture]
public class SqlUniquenessRejectionClassifierTests
{
    private static IEnumerable<TestCaseData> ClassifiedRejections()
    {
        yield return new TestCaseData((int)SqlDatabaseType.SqlServer, 2627, null,
                "Violation of UNIQUE KEY constraint 'UQ_Users_Email'. Cannot insert duplicate key in object 'dbo.#u'. The duplicate key value is (a@x.com).\nThe statement has been terminated.",
                null)
            .SetName("SqlServer_2627UniqueKey_Captured_IsClassifiedButUnattributed");
        yield return new TestCaseData((int)SqlDatabaseType.SqlServer, 2627, null,
                "Violation of PRIMARY KEY constraint 'PK__#u________3213E83F16EB35A8'. Cannot insert duplicate key in object 'dbo.#u'. The duplicate key value is (1).\nThe statement has been terminated.",
                null)
            .SetName("SqlServer_2627PrimaryKey_Captured_IsClassifiedButUnattributed");
        yield return new TestCaseData((int)SqlDatabaseType.SqlServer, 2601, null,
                "Cannot insert duplicate key row in object 'dbo.#u' with unique index 'IX_Users_Upn'. The duplicate key value is (a@x.com).\nThe statement has been terminated.",
                null)
            .SetName("SqlServer_2601UniqueIndex_Captured_IsClassifiedButUnattributed");
        yield return new TestCaseData((int)SqlDatabaseType.NotSet, null, "23505",
                "23505: duplicate key value violates unique constraint \"t_email_key\"\n\nDETAIL: Key (email)=(x@y.z) already exists.",
                "email")
            .SetName("PostgreSql_23505WithDetail_Captured_IsTheColumn");
        yield return new TestCaseData((int)SqlDatabaseType.NotSet, null, "23505",
                "23505: duplicate key value violates unique constraint \"t_a_b_key\"\n\nDETAIL: Key (a, b)=(p, q) already exists.",
                null)
            .SetName("PostgreSql_23505OnTwoColumns_Captured_IsClassifiedButUnattributed");
        yield return new TestCaseData((int)SqlDatabaseType.NotSet, null, "23505",
                "23505: duplicate key value violates unique constraint \"t_email_key\"\n\nDETAIL: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.",
                null)
            .SetName("PostgreSql_23505WithDetailRedacted_Captured_IsClassifiedButUnattributed");
        yield return new TestCaseData((int)SqlDatabaseType.Oracle, 1, null,
                "ORA-00001: unique constraint (JIM.UQ_USERS_EMAIL) violated", null)
            .SetName("Oracle_00001_IsClassifiedButUnattributed");
        yield return new TestCaseData((int)SqlDatabaseType.Oracle, 1, null,
                "ORA-00001: unique constraint (JIM.UQ_USERS_EMAIL) violated on table JIM.USERS columns (EMAIL)", "EMAIL")
            .SetName("Oracle_00001WithColumnClause_IsTheColumn");
        yield return new TestCaseData((int)SqlDatabaseType.Oracle, 1, null,
                "ORA-00001: unique constraint (JIM.UQ_USERS_NAME) violated on table JIM.USERS columns (FIRST_NAME, LAST_NAME)", null)
            .SetName("Oracle_00001OnTwoColumns_IsClassifiedButUnattributed");
    }

    private static IEnumerable<TestCaseData> UnclassifiedRejections()
    {
        yield return new TestCaseData((int)SqlDatabaseType.SqlServer, 2628, null,
                "String or binary data would be truncated in table 'tempdb.dbo.#u', column 'email'. Truncated value: 'xxxx'.\nThe statement has been terminated.")
            .SetName("SqlServer_Truncation_Captured_IsNotClassified");
        yield return new TestCaseData((int)SqlDatabaseType.SqlServer, 547, null,
                "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Users_Department\".")
            .SetName("SqlServer_ForeignKey_IsNotClassified");
        yield return new TestCaseData((int)SqlDatabaseType.NotSet, null, "23503",
                "23503: insert or update on table \"t\" violates foreign key constraint \"t_dept_fkey\"")
            .SetName("PostgreSql_ForeignKey_IsNotClassified");
        yield return new TestCaseData((int)SqlDatabaseType.Oracle, 1400, null,
                "ORA-01400: cannot insert NULL into (\"JIM\".\"USERS\".\"EMAIL\")")
            .SetName("Oracle_NotNull_IsNotClassified");
        // The numbers are provider-specific: SQL Server's 2627 under Oracle, or Oracle's 1 under SQL Server, is
        // some other error entirely.
        yield return new TestCaseData((int)SqlDatabaseType.Oracle, 2627, null, "ORA-02627: something else")
            .SetName("Oracle_SqlServerNumber_IsNotClassified");
        yield return new TestCaseData((int)SqlDatabaseType.SqlServer, 1, null, "Some SQL Server message number 1")
            .SetName("SqlServer_OracleNumber_IsNotClassified");
    }

    [TestCaseSource(nameof(ClassifiedRejections))]
    public void TryClassify_UniquenessRejection_IsClassifiedWithTheColumnTheDatabaseNamed(int provider, int? errorNumber, string? sqlState, string message, string? expectedAttribute)
    {
        var classified = SqlUniquenessRejectionClassifier.TryClassify((SqlDatabaseType)provider, errorNumber, sqlState, message, out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.True);
            Assert.That(attributeName, Is.EqualTo(expectedAttribute));
        }
    }

    [TestCaseSource(nameof(UnclassifiedRejections))]
    public void TryClassify_OtherRejection_IsNotClassified(int provider, int? errorNumber, string? sqlState, string message)
    {
        var classified = SqlUniquenessRejectionClassifier.TryClassify((SqlDatabaseType)provider, errorNumber, sqlState, message, out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.False);
            Assert.That(attributeName, Is.Null);
        }
    }

    [Test]
    public void TryClassify_ExceptionThatIsNotADatabaseError_IsNotClassified()
    {
        var classified = SqlUniquenessRejectionClassifier.TryClassify(new InvalidOperationException("23505 duplicate key"), out var attributeName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.False);
            Assert.That(attributeName, Is.Null);
        }
    }

    [Test]
    public void Rules_EveryRule_NamesItsServerFamily()
    {
        Assert.That(SqlUniquenessRejectionClassifier.Rules, Is.Not.Empty);
        foreach (var rule in SqlUniquenessRejectionClassifier.Rules)
            Assert.That(rule.ServerFamily, Is.Not.Empty, $"Rule '{rule.Description}' names no server family.");
    }
}

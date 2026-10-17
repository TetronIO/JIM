// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Export refusal classification for the SQL Connector (#2118): a database that considered a change and said no is
/// reported as Access Denied or Export Rejected, never as an Unhandled Error. The codes are the documented ones:
/// PostgreSQL's SQLSTATEs (Appendix A), SQL Server's error numbers (sys.messages) and Oracle's ORA- numbers.
/// </summary>
/// <remarks>The database type travels as an int because <see cref="SqlDatabaseType"/> is internal and NUnit test
/// methods are public.</remarks>
[TestFixture]
public class SqlExportRefusalClassifierTests
{
    private static IEnumerable<TestCaseData> Refusals()
    {
        // PostgreSQL, by SQLSTATE, whatever the provider.
        yield return Case(SqlDatabaseType.NotSet, null, "42501", ConnectedSystemExportErrorType.AccessDenied, "PostgreSql_42501InsufficientPrivilege_IsAccessDenied");
        yield return Case(SqlDatabaseType.NotSet, null, "23503", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_23503ForeignKey_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "23502", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_23502NotNull_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "23514", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_23514Check_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "23P01", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_23P01Exclusion_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "22001", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_22001Truncation_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "22P02", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_22P02InvalidText_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "42703", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_42703UndefinedColumn_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "42P01", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_42P01UndefinedTable_IsRejected");
        yield return Case(SqlDatabaseType.NotSet, null, "P0001", ConnectedSystemExportErrorType.RejectedByConnectedSystem, "PostgreSql_P0001RaisedByATrigger_IsRejected");

        // SQL Server, by error number.
        yield return Case(SqlDatabaseType.SqlServer, 229, null, ConnectedSystemExportErrorType.AccessDenied, "SqlServer_229ObjectPermission_IsAccessDenied");
        yield return Case(SqlDatabaseType.SqlServer, 230, null, ConnectedSystemExportErrorType.AccessDenied, "SqlServer_230ColumnPermission_IsAccessDenied");
        yield return Case(SqlDatabaseType.SqlServer, 547, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "SqlServer_547ForeignKeyOrCheck_IsRejected");
        yield return Case(SqlDatabaseType.SqlServer, 515, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "SqlServer_515NotNull_IsRejected");
        yield return Case(SqlDatabaseType.SqlServer, 2628, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "SqlServer_2628Truncation_IsRejected");
        yield return Case(SqlDatabaseType.SqlServer, 207, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "SqlServer_207InvalidColumn_IsRejected");
        yield return Case(SqlDatabaseType.SqlServer, 50000, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "SqlServer_50000RaisedByATrigger_IsRejected");

        // Oracle, by ORA- number.
        yield return Case(SqlDatabaseType.Oracle, 1031, null, ConnectedSystemExportErrorType.AccessDenied, "Oracle_01031InsufficientPrivileges_IsAccessDenied");
        yield return Case(SqlDatabaseType.Oracle, 2291, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "Oracle_02291ParentKeyNotFound_IsRejected");
        yield return Case(SqlDatabaseType.Oracle, 1400, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "Oracle_01400NotNull_IsRejected");
        yield return Case(SqlDatabaseType.Oracle, 2290, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "Oracle_02290Check_IsRejected");
        yield return Case(SqlDatabaseType.Oracle, 12899, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "Oracle_12899ValueTooLarge_IsRejected");
        yield return Case(SqlDatabaseType.Oracle, 20001, null, ConnectedSystemExportErrorType.RejectedByConnectedSystem, "Oracle_20001RaisedByATrigger_IsRejected");
    }

    private static IEnumerable<TestCaseData> NotRefusals()
    {
        // The database never considered the change: it was unreachable, out of time, deadlocked or interrupted.
        yield return NotACase(SqlDatabaseType.NotSet, null, "08006", "PostgreSql_08006ConnectionFailure_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.NotSet, null, "57014", "PostgreSql_57014QueryCancelled_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.NotSet, null, "40P01", "PostgreSql_40P01Deadlock_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.NotSet, null, "53300", "PostgreSql_53300TooManyConnections_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.NotSet, null, "XX000", "PostgreSql_XX000InternalError_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.NotSet, null, null, "NoSqlState_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.SqlServer, -2, null, "SqlServer_Timeout_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.SqlServer, 1205, null, "SqlServer_1205Deadlock_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.SqlServer, 4060, null, "SqlServer_4060CannotOpenDatabase_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.Oracle, 3113, null, "Oracle_03113EndOfFileOnChannel_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.Oracle, 60, null, "Oracle_00060Deadlock_IsNotARefusal");

        // The numbers are provider-specific: SQL Server's 547 under Oracle, or Oracle's 1031 under SQL Server, is
        // some other error entirely.
        yield return NotACase(SqlDatabaseType.Oracle, 547, null, "Oracle_SqlServerNumber_IsNotARefusal");
        yield return NotACase(SqlDatabaseType.SqlServer, 1031, null, "SqlServer_OracleNumber_IsNotARefusal");
    }

    [TestCaseSource(nameof(Refusals))]
    public void TryClassify_ARefusal_IsClassified(int provider, int? errorNumber, string? sqlState, ConnectedSystemExportErrorType expected)
    {
        var classified = SqlExportRefusalClassifier.TryClassify((SqlDatabaseType)provider, errorNumber, sqlState, out var errorType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classified, Is.True);
            Assert.That(errorType, Is.EqualTo(expected));
        }
    }

    [TestCaseSource(nameof(NotRefusals))]
    public void TryClassify_NotARefusal_IsNotClassified(int provider, int? errorNumber, string? sqlState)
    {
        var classified = SqlExportRefusalClassifier.TryClassify((SqlDatabaseType)provider, errorNumber, sqlState, out _);

        Assert.That(classified, Is.False);
    }

    [Test]
    public void FailureFor_ExceptionThatIsNotADatabaseError_IsNull()
    {
        Assert.That(SqlExportRefusalClassifier.FailureFor(new InvalidOperationException("42501 permission denied")), Is.Null);
    }

    [Test]
    public void FailureFor_ADatabaseRefusal_KeepsTheDatabasesOwnMessage()
    {
        var result = SqlExportRefusalClassifier.FailureFor(new FakeDbException("23514: new row for relation \"person\" violates check constraint \"person_age_check\"", "23514"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
            Assert.That(result.ErrorMessage, Does.Contain("violates check constraint \"person_age_check\""));
        }
    }

    [Test]
    public void Rules_EveryRule_NamesItsServerFamilyAndAClassification()
    {
        Assert.That(SqlExportRefusalClassifier.Rules, Is.Not.Empty);
        foreach (var rule in SqlExportRefusalClassifier.Rules)
        {
            Assert.That(rule.ServerFamily, Is.Not.Empty, $"Rule '{rule.Description}' names no server family.");
            Assert.That(rule.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied).Or.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem),
                $"Rule '{rule.Description}' classifies as something other than a refusal.");
        }
    }

    private static TestCaseData Case(SqlDatabaseType provider, int? errorNumber, string? sqlState, ConnectedSystemExportErrorType expected, string name) =>
        new TestCaseData((int)provider, errorNumber, sqlState, expected).SetName(name);

    private static TestCaseData NotACase(SqlDatabaseType provider, int? errorNumber, string? sqlState, string name) =>
        new TestCaseData((int)provider, errorNumber, sqlState).SetName(name);
}

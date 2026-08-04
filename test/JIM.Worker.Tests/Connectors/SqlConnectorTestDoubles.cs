// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A stand-in for a database server behind <see cref="ISqlProvider"/>, so the JIM SQL Connector's
/// connection handling can be exercised without one. The Connector never touches a driver type
/// directly, which is what makes this substitution possible at all.
/// </summary>
internal sealed class FakeSqlProvider : SqlProviderBase
{
    /// <summary>
    /// Thrown by <see cref="DbConnection.Open"/> instead of connecting, which is how an unreachable
    /// server or a refused login is expressed here.
    /// </summary>
    internal Exception? OpenFailure { get; init; }

    /// <summary>
    /// When set, a connection succeeds only once it has been built with a pinned server certificate,
    /// which is how a driver that refuses a certificate the operating system's bundle does not vouch
    /// for, and accepts it once told to, is expressed here.
    /// </summary>
    internal bool SucceedsOnlyWithAPinnedCertificate { get; init; }

    /// <summary>
    /// Whether this dialect's driver can be told to accept one specific server certificate. Settable so
    /// a test can stand in for Oracle Database, whose driver cannot.
    /// </summary>
    internal bool CanPinServerCertificate { get; init; } = true;

    /// <summary>
    /// What the connectivity query returns when the connection opens.
    /// </summary>
    internal object? ConnectivityTestResult { get; init; } = 1;

    /// <summary>
    /// The command text every command created through this provider was given, in order, so a test can
    /// assert that the Connector ran the dialect's own connectivity query rather than one of its own.
    /// </summary>
    internal List<string> ExecutedCommandTexts { get; } = [];

    /// <summary>
    /// Every connection string built through this provider, so a test can assert what the Connector
    /// asked for without a driver parsing it first.
    /// </summary>
    internal List<SqlConnectionSettings> BuiltConnectionSettings { get; } = [];

    public override SqlDatabaseType DatabaseType => SqlDatabaseType.SqlServer;

    public override string DisplayName => "Fake Database";

    public override string ParameterPrefix => "@";

    public override string ConnectivityTestCommandText => "SELECT 1";

    public override SqlGeneratedKeyRetrieval GeneratedKeyRetrieval => SqlGeneratedKeyRetrieval.ResultSet;

    public override bool SupportsPinnedServerCertificate => CanPinServerCertificate;

    public override int GetDefaultPort(bool useTls) => 1433;

    protected override char OpenQuote => '[';

    protected override char CloseQuote => ']';

    public override DbParameter CreateParameter(string parameterName, object? value) => throw new NotSupportedException();

    public override DbParameter? CreateGeneratedKeyParameter(string parameterName, AttributeDataType keyType) => throw new NotSupportedException();

    public override string BuildConnectionString(SqlConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BuiltConnectionSettings.Add(settings);
        return $"Fake;Host={settings.Host}";
    }

    public override DbConnection CreateConnection(string connectionString) => new FakeDbConnection(this, connectionString);

    public override string BuildKeysetPageCommandText(SqlKeysetPageRequest request) => throw new NotSupportedException();

    public override string BuildInsertReturningGeneratedKeyCommandText(SqlInsertCommand command) => throw new NotSupportedException();

    public override Guid ConvertToGuid(object value) => throw new NotSupportedException();

    public override object ConvertFromGuid(Guid value) => throw new NotSupportedException();

    public override string TablesCommandText => throw new NotSupportedException();

    public override string ViewsCommandText => throw new NotSupportedException();

    public override string ColumnsCommandText => throw new NotSupportedException();

    public override string PrimaryKeyColumnsCommandText => throw new NotSupportedException();

    public override string ForeignKeyColumnsCommandText => throw new NotSupportedException();
}

/// <summary>
/// The connection a <see cref="FakeSqlProvider"/> hands out. It opens, or fails the way a driver does.
/// </summary>
internal sealed class FakeDbConnection : DbConnection
{
    private readonly FakeSqlProvider _provider;
    private ConnectionState _state = ConnectionState.Closed;

    internal FakeDbConnection(FakeSqlProvider provider, string connectionString)
    {
        _provider = provider;
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString { get; set; }

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open()
    {
        // The settings this connection was built from are the ones most recently handed to the provider,
        // which is what lets a pinned certificate change the outcome.
        var pinned = _provider.BuiltConnectionSettings.LastOrDefault()?.PinnedServerCertificatePath != null;

        if (_provider.SucceedsOnlyWithAPinnedCertificate && !pinned)
            throw _provider.OpenFailure ?? new FakeDbException("The server's certificate is not trusted.");

        if (!_provider.SucceedsOnlyWithAPinnedCertificate && _provider.OpenFailure != null)
            throw _provider.OpenFailure;

        _state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeDbCommand(_provider);
}

/// <summary>
/// The command a <see cref="FakeSqlProvider"/> hands out. It records what it was asked to run and
/// answers with the provider's canned result.
/// </summary>
internal sealed class FakeDbCommand : DbCommand
{
    private readonly FakeSqlProvider _provider;

    internal FakeDbCommand(FakeSqlProvider provider)
    {
        _provider = provider;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } = new FakeDbParameterCollection();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        _provider.ExecutedCommandTexts.Add(CommandText);
        return 0;
    }

    public override object? ExecuteScalar()
    {
        _provider.ExecutedCommandTexts.Add(CommandText);
        return _provider.ConnectivityTestResult;
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
}

/// <summary>
/// The empty parameter collection a <see cref="FakeDbCommand"/> carries. Nothing in the connectivity
/// path binds a parameter, so this exists only to satisfy the base class.
/// </summary>
internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<object> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _parameters.Add(value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values) => throw new NotSupportedException();

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains(value);

    public override bool Contains(string value) => false;

    public override void CopyTo(Array array, int index) => throw new NotSupportedException();

    public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf(value);

    public override int IndexOf(string parameterName) => -1;

    public override void Insert(int index, object value) => _parameters.Insert(index, value);

    public override void Remove(object value) => _parameters.Remove(value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => throw new NotSupportedException();

    protected override DbParameter GetParameter(int index) => throw new NotSupportedException();

    protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();

    protected override void SetParameter(int index, DbParameter value) => throw new NotSupportedException();

    protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
}

/// <summary>
/// A driver-shaped failure. Both priority 1 drivers report connection failures as a
/// <see cref="DbException"/>, so this is what an unreachable server or a refused login looks like to
/// the Connector.
/// </summary>
internal sealed class FakeDbException : DbException
{
    internal FakeDbException(string message) : base(message)
    {
    }
}

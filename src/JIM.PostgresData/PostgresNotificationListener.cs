// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Data;
using Npgsql;
using Serilog;

namespace JIM.PostgresData;

/// <summary>
/// PostgreSQL LISTEN/NOTIFY implementation of <see cref="IDatabaseNotificationListener"/> (issue #307).
/// Opens a dedicated, non-pooled connection (required for LISTEN; see
/// <see cref="JimDbContext.BuildListenerConnectionString"/>), waits for notifications and dispatches them
/// to the supplied handler. Reconnects with exponential backoff when the connection drops; consumers use
/// <see cref="IsConnected"/>/<see cref="ConnectionStateChanged"/> to activate their polling fallback while
/// disconnected.
/// </summary>
public sealed partial class PostgresNotificationListener : IDatabaseNotificationListener
{
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(60);

    private readonly string _connectionString;

    public PostgresNotificationListener(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public event Action<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public async Task ListenAsync(
        IReadOnlyCollection<string> channelNames,
        Func<string, string, CancellationToken, Task> onNotificationAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelNames);
        ArgumentNullException.ThrowIfNull(onNotificationAsync);
        if (channelNames.Count == 0)
            throw new ArgumentException("At least one channel name is required.", nameof(channelNames));
        foreach (var channelName in channelNames)
            ValidateChannelName(channelName);

        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnConnectionAsync(channelNames, onNotificationAsync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // resilience boundary: any connection-level failure (network drop, database restart,
                // failover) must not take the listener down; we back off and reconnect instead.
                reconnectAttempt++;
                var delay = GetReconnectDelay(reconnectAttempt);
                Log.Warning(ex, "PostgresNotificationListener: connection failed (attempt {Attempt}); reconnecting in {Delay}",
                    reconnectAttempt, delay);
                SetConnected(false);

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }
            finally
            {
                SetConnected(false);
            }

            // ListenOnConnectionAsync only returns without throwing when cancellation was requested.
            break;
        }
    }

    private async Task ListenOnConnectionAsync(
        IReadOnlyCollection<string> channelNames,
        Func<string, string, CancellationToken, Task> onNotificationAsync,
        CancellationToken cancellationToken)
    {
        var pendingNotifications = new Queue<(string Channel, string Payload)>();

        await using var connection = new NpgsqlConnection(_connectionString);
        connection.Notification += (_, args) => pendingNotifications.Enqueue((args.Channel, args.Payload));
        await connection.OpenAsync(cancellationToken);

        foreach (var channelName in channelNames)
        {
            // channel names are validated constants (see ValidateChannelName), never user input.
            await using var listenCommand = new NpgsqlCommand($"LISTEN \"{channelName}\"", connection);
            await listenCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        Log.Information("PostgresNotificationListener: listening on channels: {Channels}", string.Join(", ", channelNames));
        SetConnected(true);

        while (!cancellationToken.IsCancellationRequested)
        {
            await connection.WaitAsync(cancellationToken);
            while (pendingNotifications.Count > 0)
            {
                var (channel, payload) = pendingNotifications.Dequeue();
                try
                {
                    await onNotificationAsync(channel, payload, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // resilience boundary: a faulty handler must not take down the listener loop;
                    // the notification is a hint and the consumer's polling fallback still applies.
                    Log.Error(ex, "PostgresNotificationListener: notification handler failed for channel {Channel}", channel);
                }
            }
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
            return;

        IsConnected = connected;
        ConnectionStateChanged?.Invoke(connected);
    }

    /// <summary>
    /// Returns the delay before the given reconnection attempt (1-based): exponential backoff starting
    /// at one second and capped at sixty seconds.
    /// </summary>
    internal static TimeSpan GetReconnectDelay(int attempt)
    {
        if (attempt <= 1)
            return TimeSpan.FromSeconds(1);

        // cap the exponent before shifting to avoid overflow for large attempt counts.
        var cappedAttempt = Math.Min(attempt, 7);
        var seconds = Math.Min(1 << (cappedAttempt - 1), (int)MaximumReconnectDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Ensures a channel name is one of our lowercase snake_case constants, never arbitrary input,
    /// as channel names are embedded in the LISTEN statement and cannot be parameterised.
    /// </summary>
    internal static void ValidateChannelName(string channelName)
    {
        if (string.IsNullOrEmpty(channelName) || !ChannelNameRegex().IsMatch(channelName))
            throw new ArgumentException($"Invalid notification channel name: '{channelName}'. " +
                                        "Channel names must be lowercase snake_case.", nameof(channelName));
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex ChannelNameRegex();
}

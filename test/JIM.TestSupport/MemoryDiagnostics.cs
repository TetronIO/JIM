// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace JIM.TestSupport;

/// <summary>
/// Opt-in per-test memory and process snapshot logger for NUnit test assemblies.
/// Activated by setting the <c>JIM_TEST_MEMORY_LOG</c> environment variable to a file path;
/// unset is a no-op so normal test runs incur no overhead.
///
/// The log is written as CSV with one row per test-phase event (TestSession, BeforeTest,
/// AfterTest) and flushed to disk after every row so an OOM loses at most the last row.
///
/// Wire into a test assembly by referencing this project and adding:
/// <c>[assembly: JIM.TestSupport.MemoryLogging]</c>
/// </summary>
public static class MemoryDiagnostics
{
    private const string LogPathEnvVar = "JIM_TEST_MEMORY_LOG";
    private static readonly object WriterLock = new();
    private static StreamWriter? _writer;
    private static bool _initialised;
    private static bool _exitHookRegistered;

    /// <summary>
    /// Called by <see cref="MemoryLoggingAttribute"/> per test and optionally once per
    /// assembly at session start. Records a single CSV row with current memory stats.
    /// No-op when <c>JIM_TEST_MEMORY_LOG</c> is unset.
    /// </summary>
    public static void WriteSnapshot(string assemblyName, string? testFullName, string phase)
    {
        EnsureInitialised();
        if (_writer == null)
            return;

        var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        var workingSetBytes = Environment.WorkingSet;
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{assemblyName},{Escape(testFullName)},{phase},{managedBytes / 1024d / 1024d:F1},{workingSetBytes / 1024d / 1024d:F1},{gen0},{gen1},{gen2}");

        lock (WriterLock)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private static void EnsureInitialised()
    {
        if (_initialised)
            return;

        lock (WriterLock)
        {
            if (_initialised)
                return;
            _initialised = true;

            var path = Environment.GetEnvironmentVariable(LogPathEnvVar);
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // FileOptions.WriteThrough bypasses the OS write cache so a Flush() reaches
                // disk before returning — essential for surviving an OOM mid-run.
                var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough);

                _writer = new StreamWriter(stream) { AutoFlush = false };

                // CSV header only if we created a new file
                if (stream.Position == 0)
                {
                    _writer.WriteLine(
                        "timestamp_utc,assembly,test,phase,managed_mb,working_set_mb,gen0_count,gen1_count,gen2_count");
                    _writer.Flush();
                }

                if (!_exitHookRegistered)
                {
                    _exitHookRegistered = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseWriter();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                FailOpen(path, ex);
            }
            catch (IOException ex)
            {
                FailOpen(path, ex);
            }
            catch (ArgumentException ex)
            {
                FailOpen(path, ex);
            }
            catch (NotSupportedException ex)
            {
                FailOpen(path, ex);
            }
            catch (System.Security.SecurityException ex)
            {
                FailOpen(path, ex);
            }
        }
    }

    private static void FailOpen(string path, Exception ex)
    {
        // Diagnostic should never break tests. Fall back to no-op and surface via stderr.
        Console.Error.WriteLine($"[MemoryDiagnostics] Failed to open log at '{path}': {ex.Message}");
        _writer = null;
    }

    private static void CloseWriter()
    {
        lock (WriterLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        // Quote if it contains comma, quote, or newline.
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    internal static string GetEntryAssemblyName()
    {
        return Assembly.GetEntryAssembly()?.GetName().Name
            ?? Assembly.GetCallingAssembly().GetName().Name
            ?? "unknown";
    }
}

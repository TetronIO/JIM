---
title: Logs
description: "Read and search the logs of JIM's services from the portal, filtered by service, day and level, without access to the host."
type: object
audience: operator
---

# Logs

The **Logs** page (Administration, then **Logs**) shows the log entries JIM's services write, so you can follow up an error or watch a run without a shell on the host. Each entry shows its time, level, service and message, and opens to its full detail: properties, any exception, and the raw entry.

```jim-diagram
archetype: flow
title: From a service's log file to one entry's detail
desc: "JIM's services, Web, Worker, Scheduler and Database, write log files to the shared log directory, JIM_LOG_PATH. On the Logs page you choose a Service, a Date and the Levels, then select Refresh; the filters apply only then. Search messages narrows the list as you type, matching the message or its exception. Selecting an entry opens Log Entry Details, with its properties, any exception and the raw entry."
in: {label: JIM's services, note: 'Web, Worker, Scheduler, Database'}
steps:
- {id: files, label: Log files, sub: '`JIM_LOG_PATH`'}
- {id: filter, label: Filter, sub: 'Service, Date, Level', note: 'applied on Refresh'}
- {id: search, label: Search messages, sub: message or exception}
- {id: detail, label: Log Entry Details, sub: 'properties, exception, raw'}
```

## :material-filter-outline: Which logs it shows

| Service | What it logs |
|---|---|
| Web | The portal and the REST API |
| Worker | Synchronisation runs and every other background task |
| Scheduler | Schedules starting and their steps |
| Database | The bundled PostgreSQL database |

The page reads the files the services write to the shared log directory, `JIM_LOG_PATH`, which the standard deployment mounts at `/var/log/jim`. A JIM that connects to an external database has no Database entries. See [Logging](configuration.md#logging) for the settings.

## :material-clipboard-text-clock-outline: What gets logged, and for how long

`JIM_LOG_LEVEL` sets the least serious level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. Raise it to `Debug` to investigate a problem, and set it back afterwards: at `Debug` and `Verbose` the logs grow quickly.

The Web, Worker and Scheduler services each start a new file every day, and another whenever a file reaches 50 MB, and keep their 100 most recent files. On a quiet system that is about 100 days; a day that fills several files shortens it.

## :material-magnify: Find a log entry

1. Choose a **Service**, a **Date** and the **Level(s)** to show, or leave them at **All Services**, **Today** and **All Levels**.
2. Select **Refresh**. The filters apply only when you do.
3. Type in **Search messages...** to narrow the list as you type. It matches the message or its exception, ignoring case.
4. Select an entry to open **Log Entry Details**. **Copy** beside the exception or the raw entry copies it, ready to attach to a support request.

The list shows one day at a time, newest first, and loads more as you scroll. Turn on **Auto-refresh (5s)** to keep it current while you watch a run.

## :material-account-lock-outline: Who can use it

Only administrators can open the page, and the REST API behind it requires the Administrator role too.

## :material-console: Read logs from a script

`Get-JIMLogEntry` returns the same entries, `Watch-JIMLog` follows new ones as they are written, and `Get-JIMLogFile` lists the files; see [Logs](../powershell/logs.md). The REST API serves them from `GET /api/v1/logs`.

## :material-book-open-variant-outline: Related

- [Troubleshooting](troubleshooting.md): what to look for when something fails
- [Activities](../configuration/activities.md): the record of each run, often the faster place to start
- [Configuration Reference](configuration.md#logging): the logging settings

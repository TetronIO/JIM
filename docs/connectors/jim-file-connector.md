# JIM File Connector

## Overview

The JIM File Connector enables bi-directional synchronisation of identity data with CSV and delimited text files. It is ideal for integrating with systems that produce flat-file extracts, such as HR exports, batch feeds, or data migration files.

**Capabilities:** Full Import, Export

## Supported Features

- **Configurable delimiters**<br /> Comma (default), tab, pipe, semicolon, or any custom character
- **Multi-valued attributes**<br /> Supported via duplicate column names in the header row, or via a configurable in-field delimiter (default: pipe `|`)
- **Schema auto-discovery**<br /> Column headers are read automatically to build the schema
- **Type detection**<br /> Attribute data types are inferred by inspecting up to 50 rows of data. Supported types: Text, Number, Boolean, Guid, DateTime
- **Culture-aware parsing**<br /> An optional culture setting controls how numbers, dates, and other locale-sensitive values are parsed
- **Object type support**<br /> Objects can be typed via a dedicated column in the file, or by specifying a fixed object type for the entire file
- **Operational modes**<br /> Import Only, Export Only, or Bidirectional (export then confirming import from the same file)
- **Auto-confirm export**<br /> In Export Only mode, exported changes are written directly and automatically confirmed
- **Stop on first error**<br /> Optionally halt processing on the first error encountered, useful for debugging data quality issues

## Settings

| Setting | Description | Default | Example |
|---------|-------------|---------|---------|
| File Path | Path to the CSV file inside the container. Used for import, export, or both depending on mode. | *(required)* | `/connector-files/Users.csv` |
| Mode | Operational mode: Import Only, Export Only, or Bidirectional. | `Import Only` | `Bidirectional` |
| Object Type Column | Column in the file that contains the object type. Use when the file contains multiple object types. | *(optional)* | `Type` |
| Object Type | Fixed object type name. Use when the file contains a single type of object. | *(optional)* | `User` |
| Delimiter | Field delimiter character. | `,` | `\t` (tab) |
| Multi-Value Delimiter | Character used to separate multiple values within a single field. | `\|` | `;` |
| Culture | Culture code for parsing locale-sensitive values. Uses invariant culture if not specified. | *(invariant)* | `en-gb` |
| Stop On First Error | Stop processing when the first error is encountered. Useful for debugging. | `false` | `true` |

!!! note "Object type configuration"
    You must specify either **Object Type Column** or **Object Type** (but not both). If the file contains multiple object types in different rows, use Object Type Column to point to the column that identifies each row's type. If every row represents the same type, use Object Type to set a fixed value.

## File access

JIM ships with a formal Docker volume — `jim-connector-files-volume` — mounted inside both the JIM Web and JIM Worker containers at `/connector-files`. The File Connector reads from and writes to paths under this directory. **You will configure all File Connector File Path settings as `/connector-files/<some-path>`.**

There are two patterns for getting files into and out of `/connector-files`. Most deployments use both at the same time, depending on the integration.

### 1. Default: write to the named volume

This is the simplest pattern and the recommended starting point. The volume is created and managed by Docker; ownership is set automatically so the JIM container's runtime user can read and write to it without any host-side permission tweaking.

You don't need to configure anything in your `docker-compose.yml` — the volume is part of the bundled JIM compose files. To put a file into the volume, stream it through the worker container so the resulting file is owned by the JIM runtime user:

```bash
docker exec -i -u app jim.worker sh -c 'cat > /connector-files/Users.csv' < ./Users.csv
```

The `-u app` flag is important: it runs the shell inside the container as the JIM runtime user (UID 1654), so the file lands with the correct ownership. This matters when JIM will later rewrite the same file — for example in **Export Only** or **Bidirectional** mode, or whenever schema discovery is triggered on an existing file.

!!! warning "Don't use `docker cp` to push files JIM will rewrite"
    `docker cp ./file jim.worker:/path` is tempting but preserves your host UID/GID. The resulting file is not writable by the JIM runtime user (UID 1654), so any subsequent JIM export against that file will fail with an "Access to the path … is denied" error. Use the `docker exec … cat >` form above instead.

    Reading *out* of the volume with `docker cp` is fine — the file on the host takes your local UID, which is what you usually want:

    ```bash
    docker cp jim.worker:/connector-files/Exports.csv ./Exports.csv
    ```

When to use this pattern:

- **Exports**<br /> JIM writes the file; a downstream process or human pulls it out via `docker cp`, an SFTP sidecar, or similar.
- **Manual imports**<br /> Admin pushes a file in once, JIM imports it.
- **Sidecar-orchestrated drops**<br /> A helper service (cron job, file-transfer container) runs alongside JIM, drops a file into the volume, and JIM picks it up on the next scheduled import.

### 2. Integration: bind-mount over a subdirectory

Use this pattern when an external system writes files to a fixed location you cannot change — typically a network share (SMB/CIFS or NFS) the customer's HR system writes to nightly.

You bind-mount the host path over a *subdirectory* of `/connector-files`. JIM still sees a unified `/connector-files` filesystem; only the specific subdirectory's contents come from the host.

```yaml
# docker-compose.override.yml or your production overlay
services:
  jim.worker:
    volumes:
      # Mount your network share (already mounted on the host) at a subdirectory
      - /mnt/hr-extracts:/connector-files/hr-input
  jim.web:
    volumes:
      - /mnt/hr-extracts:/connector-files/hr-input
```

Then configure the File Connector with `File Path = /connector-files/hr-input/employees.csv`.

!!! warning "Permissions on bind-mounted paths"
    The JIM container runs as a non-root user (UID 1654, named `app`). The default named volume is owned by this user, so writes work out of the box. Bind-mounted host paths preserve host UID/GID — so if your HR system writes files owned by a different UID, the JIM worker may not have read access (for imports) or write access (for exports). Either:

    - Make the host directory readable/writable by UID 1654 (`chown 1654:1654 /mnt/hr-extracts`), or
    - Adjust your network share's mount options (`uid=1654,gid=1654` for CIFS, or apply NFS UID-mapping) so files appear to be owned by UID 1654 inside the container.

    Permission errors during sync show up as RPEIs on the failing import or export Activity, with a clear "Access to the path … is denied" message.

### Mounting both at once

The patterns combine naturally. A typical deployment looks like this:

```yaml
services:
  jim.worker:
    volumes:
      # Default volume is already wired up — JIM uses it for exports and ad-hoc imports.
      # No line needed here; comes from the base docker-compose.yml.

      # Pull HR extracts directly from the network share they're written to.
      - /mnt/hr-extracts:/connector-files/hr-input

      # Push payroll exports to a different network share that the payroll team picks up.
      - /mnt/payroll-drop:/connector-files/payroll-output
```

JIM's File Connector configurations would then point at:

- `/connector-files/Users.csv`<br /> Admin-pushed file in the named volume
- `/connector-files/hr-input/employees.csv`<br /> Read from the HR share
- `/connector-files/payroll-output/payroll.csv`<br /> Written to the payroll share

## Schema Discovery

When you configure a File Connector connected system and trigger schema discovery, JIM performs the following steps:

1. **Reads the header row**<br /> Column names become attribute names in the schema.
2. **Detects multi-valued attributes**<br /> If the same column name appears more than once in the header, the attribute is marked as multi-valued.
3. **Infers data types**<br /> JIM reads up to 50 data rows and attempts to parse each column's values as Number, Boolean, Guid, or DateTime. If none of these match, the attribute defaults to Text.
4. **Discovers object types**<br /> If an Object Type Column is configured, JIM reads through the file to find all unique object type values. Otherwise, it uses the fixed Object Type setting.

In **Export Only** mode where no file exists yet, schema discovery creates a minimal schema with just the specified object type. Attributes are defined later by synchronisation rules.

## Troubleshooting

### File not found

The error `File not found: '/connector-files/Users.csv'` indicates the file is not accessible at the specified path inside the container.

- For named-volume files: confirm the file is in the volume — `docker exec jim.worker ls /connector-files`.
- For bind-mounted paths: verify the host path exists and is mounted in `docker-compose.yml` (`docker compose config | grep connector-files`).
- Paths are case-sensitive on Linux. Make sure the File Path in JIM matches the actual filename exactly.

### Access denied (permission error)

The error `Access to the path '/connector-files/...' is denied` means the JIM runtime user (UID 1654) does not have write permission on the target file or its parent directory. This happens in two common situations:

**Bind-mounted host directories:** host files preserve host UID/GID, which may not match UID 1654.

- Fix the ownership: `chown -R 1654:1654 /mnt/your-mount-point` on the host.
- For CIFS/SMB mounts, add `uid=1654,gid=1654` to the mount options.
- For NFS, use UID mapping or ensure the file owner UID matches.

**Files pushed into the named volume with `docker cp`:** `docker cp` preserves your host UID, so the file is owned by you, not by UID 1654. A subsequent JIM export or schema refresh against that file will fail. Either re-push the file with the `docker exec … cat >` form shown under [File access](#file-access), or fix the ownership directly:

```bash
docker exec -u 0 jim.worker chown app:app /connector-files/Users.csv
```

### Column count mismatch

The error `Row X has Y columns but the header defines Z columns` means a data row has fewer or more fields than the header row.

- Open the file and inspect the row number mentioned in the error.
- Check for missing delimiters, extra delimiters, or unquoted fields that contain the delimiter character.
- Fields containing the delimiter character should be enclosed in double quotes.

### Object type column not found

The error `Object type column 'Type' not found in file headers` means the column name specified in Object Type Column does not match any column in the file.

- Check the spelling and case of the column name -- the match is case-insensitive, but the column must exist in the header row.
- If the file does not have an object type column, use the Object Type setting instead.

### Encoding issues

If imported data contains garbled characters or unexpected symbols:

- Set the **Culture** setting to match the file's locale (e.g. `en-gb`, `de-de`).
- Ensure the file is saved in UTF-8 encoding.
- If the file was exported from another system, check whether that system uses a different character encoding.

### Multi-valued attribute not detected

If attributes that should be multi-valued are appearing as single-valued:

- **Duplicate column approach**<br /> Ensure the column name appears more than once in the header row. The names must match exactly (case-insensitive).
- **In-field delimiter approach**<br /> Check that the Multi-Value Delimiter setting matches the delimiter used within the field values.

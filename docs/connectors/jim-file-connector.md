---
title: JIM File Connector
description: Synchronise identities with CSV and delimited text files, where JIM finds them, and the settings the File Connector takes.
type: object
audience: administrator
---

# JIM File Connector

The JIM File Connector enables bi-directional synchronisation of identity data with CSV and delimited text files. It is ideal for integrating with systems that produce flat-file extracts, such as HR exports, batch feeds, or data migration files.

**Capabilities:** Full Import, Export

```jim-diagram
archetype: comparison
title: The File Connector's three modes compared
desc: "Import Only, the default, reads the file on each Full Import and never writes it; the file must exist first, and a change made to it outside JIM is read at the next import. Export Only writes the file on each export, confirmed as soon as it is written, and never reads it back; the file need not exist, because an export creates it, but the Object Type must then be set as a fixed value. A change made to the file outside JIM goes unseen, because JIM compares with the values it last wrote. Bidirectional writes the file on each export and then reads it in a confirming import; the file must exist first, and a change made outside JIM is read by the confirming import. Every mode reads or writes one file, at a path under /connector-files."
columns:
  - { label: Import Only, sub: the default }
  - { label: Export Only, sub: "writes, never reads back" }
  - { label: Bidirectional, sub: "export, then a confirming import" }
rows:
  - label: Reads the file
    cells:
      - { text: on each Full Import }
      - { text: never }
      - { text: after each export, sub: in a confirming import }
  - label: Writes the file
    cells:
      - { text: never }
      - { text: on each export, sub: confirmed as soon as it is written }
      - { text: on each export, sub: confirmed as soon as it is written }
  - label: "Must the file exist?"
    cells:
      - { text: "yes, before you configure it" }
      - { text: "no: an export creates it", sub: then set a fixed Object Type }
      - { text: "yes, before you configure it" }
  - label: Edits made outside JIM
    cells:
      - { text: read at the next import }
      - { text: go unseen, sub: JIM compares with what it last wrote }
      - { text: read by the confirming import }
footer: "Every mode reads or writes one file, at a path under `/connector-files`"
```

## :material-format-list-checks: Supported Features

- **Configurable delimiters**<br /> Comma (default), tab, pipe, semicolon, or any custom character
- **Multi-valued attributes**<br /> Held in one column, the values separated by a configurable Multi-Value Delimiter (default: pipe `|`); you mark the attribute multi-valued on the Schema tab (see [Multi-valued attributes](#multi-valued-attributes))
- **Schema auto-discovery**<br /> Column headers are read automatically to build the schema
- **Type detection**<br /> Attribute data types are inferred by inspecting up to 50 rows of data. Supported types: Text, Number, Long Number, Decimal, Boolean, Guid, DateTime
- **Culture-aware parsing**<br /> An optional culture setting controls how numbers, dates, and other locale-sensitive values are parsed. A date with no time zone is read as UTC, and one with an offset is converted to UTC
- **Object type support**<br /> Objects can be typed via a dedicated column in the file, or by specifying a fixed object type for the entire file
- **Operational modes**<br /> Import Only, Export Only, or Bidirectional (export then confirming import from the same file)
- **Auto-confirm export**<br /> An export counts as confirmed as soon as the file is written, and JIM records the values it wrote, so it knows what the file holds without reading it back (see [Export Only mode](#export-only-mode))
- **Stop on first error**<br /> Optionally halt processing on the first error encountered, useful for debugging data quality issues

## :material-folder-outline: File access

JIM ships with a named volume (`jim-connector-files-volume`) mounted inside both the JIM Web and JIM Worker containers at `/connector-files`, on Docker and on Podman. The File Connector reads from and writes to paths under this directory. **You will configure all File Connector File Path settings as `/connector-files/<some-path>`.**

There are two patterns for getting files into and out of `/connector-files`. Most deployments use both at the same time, depending on the integration.

### 1. Default: write to the named volume

This is the simplest pattern and the recommended starting point. The volume is created and managed by Docker; ownership is set automatically so the JIM container's runtime user can read and write to it without any host-side permission tweaking.

You don't need to configure anything in your `docker-compose.yml`; the volume is part of the bundled JIM compose files. To put a file into the volume, stream it through the worker container so the resulting file is owned by the JIM runtime user:

```bash
docker exec -i -u app jim.worker sh -c 'cat > /connector-files/Users.csv' < ./Users.csv
```

The `-u app` flag is important: it runs the shell inside the container as the JIM runtime user (UID 1654), so the file lands with the correct ownership. This matters when JIM will later rewrite the same file, for example in **Export Only** or **Bidirectional** mode, or whenever schema discovery is triggered on an existing file.

On Podman, the worker container is `jim-worker`, and its default user is already `app` (for a rootless installation, see [Rootless commands](../administration/podman.md#rootless-commands)):

```bash
sudo podman exec -i jim-worker sh -c 'cat > /connector-files/Users.csv' < ./Users.csv
sudo podman cp jim-worker:/connector-files/Exports.csv - | tar -xf -   # pull an export out
```

!!! warning "Don't use `docker cp` to push files JIM will rewrite"
    `docker cp ./file jim.worker:/path` is tempting but preserves your host UID/GID. The resulting file is not writable by the JIM runtime user (UID 1654), so any subsequent JIM export against that file will fail with an "Access to the path … is denied" error. Use the `docker exec … cat >` form above instead.

    Reading *out* of the volume with `docker cp` is fine; the file on the host takes your local UID, which is what you usually want:

    ```bash
    docker cp jim.worker:/connector-files/Exports.csv ./Exports.csv
    ```

When to use this pattern:

- **Exports**<br /> JIM writes the file; a downstream process or human pulls it out via `docker cp`, an SFTP sidecar, or similar.
- **Manual imports**<br /> Admin pushes a file in once, JIM imports it.
- **Sidecar-orchestrated drops**<br /> A helper service (cron job, file-transfer container) runs alongside JIM, drops a file into the volume, and JIM picks it up on the next scheduled import.

### 2. Integration: bind-mount over a subdirectory

Use this pattern when an external system writes files to a fixed location you cannot change, typically a network share (SMB/CIFS or NFS) the customer's HR system writes to nightly.

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
    The JIM container runs as a non-root user (UID 1654, named `app`). The default named volume is owned by this user, so writes work out of the box. Bind-mounted host paths preserve host UID/GID, so if your HR system writes files owned by a different UID, the JIM worker may not have read access (for imports) or write access (for exports). Either:

    - Make the host directory readable/writable by UID 1654 (`chown 1654:1654 /mnt/hr-extracts`), or
    - Adjust your network share's mount options (`uid=1654,gid=1654` for CIFS, or apply NFS UID-mapping) so files appear to be owned by UID 1654 inside the container.

    Permission errors during a run show up as execution item errors on the failing import or export Activity, with a clear "Access to the path … is denied" message.

#### On Podman

On Podman, add the host folder to the pod file, `/opt/jim/jim.yaml`: a `hostPath` volume, mounted in the `worker` container (and the `web` container, for the file browser):

```yaml
      volumeMounts:
        - { name: hr-extracts, mountPath: /connector-files/hr-input }
  # ...
  volumes:
    - name: hr-extracts
      hostPath: { path: /mnt/hr-extracts, type: Directory }
```

Then restart JIM: `sudo systemctl restart jim.service`, or for a rootless installation, `jim-systemctl restart jim.service` (see [Rootless commands](../administration/podman.md#rootless-commands)). An upgrade replaces `jim.yaml`, so carry the addition into each new release's copy.

Two things can differ from Docker:

- **Ownership, when rootless**<br /> A rootful JIM, the default, needs the folder owned by UID 1654, as on Docker. A rootless JIM runs as the `jim` account, and its container user, UID 1654, is a *subordinate* ID of that account on the host rather than UID 1654. Hand the folder to the container user from within the account's own user namespace, which picks the right host ID:

    ```bash
    chown jim: /mnt/hr-extracts
    jim-podman unshare chown 1654:1654 /mnt/hr-extracts
    ```

    On the host, the folder then belongs to the account's first subordinate ID plus 1653 (for example `101653` when `/etc/subuid` gives `jim` the range from `100000`). For a network share, mount it with that ID as the owner (`uid=101653,gid=101653` for CIFS).

- **SELinux**<br /> On a host with SELinux enforcing, as RHEL is by default, a container may read and write only files labelled for containers. Label the folder, persistently:

    ```bash
    semanage fcontext -a -t container_file_t '/mnt/hr-extracts(/.*)?'
    restorecon -R /mnt/hr-extracts
    ```

    For an NFS or CIFS mount, set the label as a mount option instead: `context="system_u:object_r:container_file_t:s0"`.

Where a network share cannot be mounted on the host with a suitable owner or label, a rootful installation can mount it as a Podman volume instead, which Podman mounts itself: `sudo podman volume create --driver local --opt type=cifs --opt device=//fileserver/hr-extracts --opt o=username=jim-svc,uid=1654,gid=1654 hr-extracts`, then use `persistentVolumeClaim: { claimName: hr-extracts }` in place of `hostPath`. Mounting a share needs root, so this does not work rootless.

### Mounting both at once

The patterns combine naturally. A typical deployment looks like this:

```yaml
services:
  jim.worker:
    volumes:
      # Default volume is already wired up; JIM uses it for exports and ad-hoc imports.
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

## :material-file-search-outline: Schema Discovery

When you configure a File Connector Connected System and trigger schema discovery, JIM performs the following steps:

1. **Reads the header row**<br /> Column names become attribute names in the schema. On import a column is matched to its attribute ignoring case, so a file whose header later reads `employeeid` still fills `EmployeeId`. Each attribute has one column: a header that names a column twice, or two columns differing only in case, is refused.
2. **Discovers every attribute as single-valued**<br /> A delimiter in a cell could as easily be part of one value, so you mark the multi-valued attributes yourself (see [Multi-valued attributes](#multi-valued-attributes)).
3. **Infers data types**<br /> JIM reads up to 50 data rows and attempts to parse each column's values as Number, Long Number, Decimal, Boolean, Guid, or DateTime, in that order (narrowest numeric type first). Whole numbers within 32-bit range infer Number, larger whole numbers infer Long Number, and fractional or exponent-notation values (for example `1.5` or `1.5E3`) infer Decimal. If none of these match, the attribute defaults to Text.
4. **Discovers object types**<br /> If an Object Type Column is configured, JIM reads through the file to find all unique object type values. Otherwise, it uses the fixed Object Type setting.

In **Export Only** mode where no file exists yet, schema discovery creates a minimal schema with just the specified object type. Attributes are defined later by Synchronisation Rules.

## :material-format-list-bulleted: Multi-valued attributes

A multi-valued attribute is one column whose cells hold several values, separated by the **Multi-Value Delimiter** setting (pipe `|` by default):

```text
employeeId,displayName,member
E1001,Joe Bloggs,CN=Engineering|CN=London Office
E1002,Ann Smith,CN=Finance
```

Schema discovery cannot tell a delimited cell from a single value that happens to contain the delimiter, so every column is discovered single-valued. After retrieving the schema, open each multi-valued attribute's **Edit** control on the Connected System's Schema tab, set **Values per object** to **Multi-Valued**, and save the schema; [Connected System Attributes](../configuration/connected-system-attributes.md#plurality) covers the rules and the PowerShell and REST API equivalents. Do this before a Synchronisation Rule uses the attribute or an import fills it, because the setting is locked after that. A schema refresh keeps it.

On import, each value in the cell is read separately. On export, JIM writes the whole set: the values the cell already holds (or, for a row not yet in the file, the values JIM holds for the object), less the values removed, plus the values added, each once.

- **Choose a delimiter no value contains.** A value containing the Multi-Value Delimiter would read back as two values, so JIM fails that object's export with an error naming the attribute instead of writing it. Avoid `,` and `;` for attributes holding distinguished names, which contain commas and may contain semicolons; the default `|` is the safe choice.
- **Large sets stay in one cell.** A group with thousands of members is a long cell. The file is still valid, but spreadsheet applications may not show all of it (Excel shows at most 32,767 characters in a cell), so edit such files with a text editor or a script.
- **A Boolean holds one value**, so it cannot be made multi-valued.

## :material-file-delimited-outline: Export Cell Formats

When JIM writes the export file, each attribute value is rendered as text in its cell:

- **Text, Number, Long Number, Guid**<br /> Written as-is, using invariant culture.
- **Decimal**<br /> Canonical invariant form: plain notation, no trailing zeros, never exponent notation.
- **DateTime**<br /> ISO 8601 round-trip format (for example `2025-06-15T10:30:00.0000000Z`).
- **Boolean**<br /> Lowercase `true` or `false`.
- **Reference**<br /> The referenced object's External ID value (for example the manager's employee ID). This is the same string form the import side parses, so a reference exported by JIM resolves back to the same object if the file is re-imported.
- **Binary**<br /> Base64-encoded, as a CSV cell cannot carry raw bytes.

!!! note "Binary is export-only"
    The File Connector cannot import Binary attribute values: schema discovery never infers a Binary type, and a schema-declared Binary attribute fails to parse on import. Binary cells written by an export are therefore one-way; do not use Bidirectional mode for a schema that includes Binary attributes.

## :material-export: Export Only mode

In Export Only mode JIM writes the file and never reads it back. It knows what each row holds because every export records the values it wrote against the row's Connected System Object. That is what lets JIM clear a value in the file when it is cleared in the Metaverse: it compares the new value with what it last wrote.

Three consequences are worth knowing:

- **Changes made to the file outside JIM go unseen.** JIM compares against what it wrote, not against the file. If another process edits the file, use Bidirectional mode, so a confirming import reads the file after each export.
- **Exported objects stay Pending Provisioning in the Connector Space.** Only an import moves an object to Normal, and Export Only never imports. This does not hold anything up: changes and deletions are exported as usual, and an object JIM deletes from the file is removed from the Connector Space at the same time.
- **Nothing is lost if JIM cannot record what it wrote.** The export is left unconfirmed, the next export run writes the same rows again (which changes nothing in the file) and records them, and the export's Activity finishes with a warning saying so.

### Bringing JIM up to date with an existing file

JIM records values only when it writes them. Point a new Export Only Connected System at a file that already has rows, or upgrade from a JIM release that did not record exported values, and JIM does not know what those rows hold: a value cleared in the Metaverse is not cleared from them until the value has changed at least once since. To have JIM learn the whole file at once:

1. Set the Connected System's **Mode** to **Bidirectional**.
2. Run a **Full Import** for it, then a **Full Synchronisation**, creating those Run Profiles if it has none.
3. Set **Mode** back to **Export Only**.

The import reads every row into the Connector Space, and moves the objects it finds to Normal. Rows JIM did not write, and objects JIM expected but the file does not have, are reported by that import like any other.

## :material-tune-variant: Settings

| Setting | Description | Default | Example |
|---------|-------------|---------|---------|
| File Path | Path to the CSV file inside the container. Used for import, export, or both depending on mode. | *(required)* | `/connector-files/Users.csv` |
| Mode | Operational mode: Import Only, Export Only, or Bidirectional. | `Import Only` | `Bidirectional` |
| Object Type Column | Column in the file that contains the object type. Use when the file contains multiple object types. | *(one of these two is required)* | `Type` |
| Object Type | Fixed object type name. Use when the file contains a single type of object. | *(one of these two is required)* | `User` |
| Delimiter | Field delimiter character. | `,` | `\t` (tab) |
| Multi-Value Delimiter | Character used to separate multiple values within a single field. | `\|` | `;` |
| Culture | Culture code for parsing locale-sensitive values. Uses invariant culture if not specified. | *(invariant)* | `en-gb` |
| Stop On First Error | Stop processing when the first error is encountered. Useful for debugging. | `false` | `true` |

!!! note "Object type configuration"
    You must specify exactly one of **Object Type Column** or **Object Type**; JIM rejects the settings if you supply neither or both. If the file contains multiple object types in different rows, use Object Type Column to point to the column that identifies each row's type. If every row represents the same type, use Object Type to set a fixed value.

## :material-wrench-outline: Troubleshooting

### File not found

The error `File not found: '/connector-files/Users.csv'` indicates the file is not accessible at the specified path inside the container.

- For named-volume files: confirm the file is in the volume: `docker exec jim.worker ls /connector-files`.
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

- Check the spelling and case of the column name; the match is case-insensitive, but the column must exist in the header row.
- If the file does not have an object type column, use the Object Type setting instead.

### Column missing from the file

An import fails with an error naming the columns when the file's header lacks a column that a selected Object Type reads: a selected attribute, or its External ID or Secondary External ID. JIM stops rather than reading the column as empty, which would clear that attribute on every object. This includes a Connected System whose file has never had the column: its imports fail until you add the column to the file or deselect the attribute.

- Check the header row; the match ignores case, but the column must be there.
- If the column was removed on purpose, deselect the attribute on the Connected System's Schema tab, or refresh the schema.

The same error is raised when two columns' names differ only in case, such as `Name` and `name`, because JIM cannot tell which one an attribute means. Rename or remove one of them.

### Encoding issues

If imported data contains garbled characters or unexpected symbols:

- Ensure the file is saved in UTF-8 encoding. JIM reads the file as UTF-8 (or as UTF-16 or UTF-32 when the file starts with a byte order mark), and no setting changes this; the **Culture** setting only affects how numbers and dates are parsed.
- If the file was exported from another system, check whether that system uses a different character encoding.

### Export refuses to rewrite the file

An export rewrites the whole file, and every row it was not asked to change must come back as it was. When JIM cannot be sure of that, every export in the run fails with an error naming the file, and the file is left exactly as it was:

- **The file could not be read.**<br /> It is malformed (for example a stray quotation mark), locked by another process, or not readable by JIM. The error includes what the reader reported; correct the file or the permission (see [Access denied](#access-denied-permission-error)).
- **The file has rows but no External ID column.**<br /> JIM matches each row to its object by the External ID attribute's column, so without it every row would be lost. Add the column, or move the file aside so JIM starts a new one.
- **Two rows have the same External ID.**<br /> JIM cannot tell which to update. The error names the External ID and both row numbers; remove the duplicate.

The exports are retried on the next run once the file is corrected. A row with no External ID at all is not JIM's to change: it is written back unchanged, after the rows JIM manages. JIM writes the new file beside the old one and then replaces it, so an export that fails part-way through writing leaves the previous file whole.

### Repeated column

Schema discovery, an import and an export all fail with an error naming the column when the file's header names a column twice, such as `member,member`. Each attribute has one column: put the values in that one column, separated by the Multi-Value Delimiter, and mark the attribute multi-valued on the Schema tab (see [Multi-valued attributes](#multi-valued-attributes)). An export refuses such a file before writing anything, so the file is left as it was.

### Multi-valued attribute imports as one value

If a cell such as `CN=Alice|CN=Bob` imports as a single value:

- **The attribute is single-valued.**<br /> Every column is discovered single-valued; mark it **Multi-Valued** on the Schema tab, as described under [Multi-valued attributes](#multi-valued-attributes).
- **The delimiter does not match.**<br /> Check that the Multi-Value Delimiter setting matches the character separating the values in the file.

## :material-book-open-variant-outline: Related

- [Connectors](index.md)
- [Connected Systems](../configuration/connected-systems.md)
- [Your First Synchronisation](../getting-started/first-synchronisation.md): two File Connector Connected Systems, end to end.

# Import ChangeType Design

- **Status:** Done
>
> Design decisions for handling ObjectChangeType during import operations

## Background

When connectors import objects into JIM, each `ConnectedSystemImportObject` has a `ChangeType` property that indicates what the connector believes should happen to the object:
- `NotSet` - No hint provided
- `Create` - Connector believes this is a new object
- `Update` - Connector believes this is an existing object
- `Delete` - Connector wants this object removed
- `Obsolete` - Connector wants to mark this object as obsolete

## Design Decision

### Who determines Create vs Update?

**The import processor (JIM) determines whether an object should be Created or Updated**, not the connector. This design choice was made because:

1. **JIM has authoritative knowledge** of what objects exist in the Connected System Object space
2. **Connectors may not have reliable state** - especially file-based connectors that process flat data
3. **Idempotency** - importing the same data multiple times should produce consistent results
4. **Error resilience** - if a connector incorrectly flags an object as Create when it already exists (or vice versa), JIM handles it gracefully

### When the connector's ChangeType is ignored

| Connector Says | JIM Finds | JIM Does | Logged |
|----------------|-----------|----------|--------|
| Create | Object exists | Updates instead | Warning |
| Update | Object missing | Creates instead | Warning |
| NotSet | - | Auto-determines | - |

### When the connector's ChangeType is honoured

**Delete is the only ChangeType that is honoured from the connector.**

| Connector Says | JIM Finds | JIM Does |
|----------------|-----------|----------|
| Delete | Object exists | Marks as Obsolete |
| Delete | Object missing | Ignores (nothing to delete) |

This is critical for **delta import** scenarios (e.g., LDAP changelog) where the connector has knowledge of deletions that JIM cannot infer from the data alone.

## Implementation

### Full Import Deletions

During a full import, deletions are determined by **absence** - objects that exist in JIM but were not present in the import results are marked as Obsolete. This is handled in `ProcessConnectedSystemObjectDeletionsAsync()`.

### Delta Import Deletions (connector-driven)

During delta imports, connectors explicitly set `ChangeType = ObjectChangeType.Delete` for removed objects. This is handled in `ProcessImportObjectsAsync()`:

```csharp
if (importObject.ChangeType == ObjectChangeType.Delete)
{
    if (connectedSystemObject != null)
    {
        connectedSystemObject.Status = ConnectedSystemObjectStatus.Obsolete;
        // ... persist changes
    }
    continue; // Skip further processing
}
```

## Impact on Custom Connector Development

When developing custom connectors:

1. **Create/Update hints are optional** - JIM will determine the correct action based on existing data
2. **Delete must be explicit** - If your connector can detect deletions (e.g., via a changelog or tombstone query), set `ChangeType = ObjectChangeType.Delete`
3. **Full imports don't require Delete** - JIM automatically detects deletions by comparing import results to existing objects
4. **Delta imports require Delete** - Without explicit Delete change types, objects removed from the source won't be detected

### LDAP Connector Deletion Detection

The built-in LDAP connector supports delta import deletion detection for Active Directory:

- **AD Tombstones**: When objects are deleted in AD, they're moved to `CN=Deleted Objects,<partition>` with `isDeleted=TRUE`
- **Show Deleted Control**: The connector uses `LDAP_SERVER_SHOW_DELETED_OID` (1.2.840.113556.1.4.417) to query tombstones
- **USN-based filtering**: Only tombstones with `uSNChanged` greater than the previous watermark are returned
- **Object matching**: Uses `objectGUID` (preserved on tombstones) to match deleted objects to existing CSOs

This enables delta imports to detect deletions without requiring a full import.

### Example: Delta Import with Deletions

```csharp
// Connector detects a deleted entry (e.g., from LDAP changelog)
var deletedObject = new ConnectedSystemImportObject
{
    ChangeType = ObjectChangeType.Delete,
    ObjectType = "user",
    Attributes = new List<ConnectedSystemImportObjectAttribute>
    {
        new() { Name = "uid", StringValues = ["jsmith"] }
    }
};
result.ImportObjects.Add(deletedObject);
```

## Related Issues

- GitHub Issue #88: ObjectChangeType from connectors not fully honoured (Closed)

## Future Considerations

If there's a need to allow connectors to enforce Create (fail if exists) or Update (fail if missing), this could be implemented as optional connector capability flags. However, the current graceful handling has proven more robust for real-world scenarios where data consistency between systems cannot be guaranteed.

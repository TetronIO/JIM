# Synchronisation Integrity Requirements

> Synchronisation operations are the core of JIM. Data integrity and reliability are paramount. Customers depend on JIM to synchronise their identity data accurately without corruption or data loss.

## Error Handling Philosophy

1. **Fast/Hard Failures**: Better to stop and report an error than continue with corrupted state
2. **Comprehensive Reporting**: ALL errors must be reported via RPEIs/Activities - no silent failures
3. **Defensive Programming**: Anticipate edge cases (duplicates, missing data, type mismatches) and handle explicitly
4. **Trust and Confidence**: Customers must be able to trust that JIM won't silently corrupt their data

## Detailed Requirements

### 1. Query Operations Must Be Explicit About Multiplicity

- NEVER use `First()` or `FirstOrDefault()` when you expect exactly one result and would not know what to do with multiple matches
- NEVER use `Single()` or `SingleOrDefault()` in sync operations without a try-catch that logs and fails the operation
- If a query might return multiple results, you MUST explicitly handle that case:
  - Either validate that only one result exists before calling Single/SingleOrDefault
  - Or use First/FirstOrDefault and log a warning about unexpected duplicates
  - Or catch the exception and fail the activity with detailed error information
- Example: `GetConnectedSystemObjectByAttributeAsync()` should have caught and logged the "multiple matches" scenario

### 2. All Sync Operation Code Must Be Wrapped in Try-Catch

- Import, sync, and export operations must catch ALL exceptions
- Exceptions must be logged to RPEI.ErrorType and RPEI.ErrorMessage
- After catching, evaluate: should this fail the entire activity or just mark this object as errored?
- When in doubt, fail fast rather than continue with unknown state

### 3. Data Integrity Checks Before Operations

- Before creating/updating CSOs, verify no duplicates exist for the same external ID
- Before creating/updating MVOs, verify the connector space is in expected state
- Before exporting, verify reference resolution succeeded
- Log findings - silence is the enemy of debugging

### 4. Activity Completion Logic

- Activities with any UnhandledError RPEI items should fail the entire activity
- Do not treat UnhandledError the same as other error types - it indicates code/logic problems
- When processing multiple objects, continue collecting errors for all objects, then fail if any UnhandledErrors occurred
- Never silently skip objects due to exceptions - always fail the activity

### 5. Logging for Sync Operations

- Log summary statistics at the end of every batch operation (imports, syncs, exports)
- Include: Total objects, successfully processed, errored, and categorise error types
- For integrity issues (duplicates, mismatches), log CSO/MVO IDs so admins can investigate
- Use appropriate log levels: Debug for normal flow, Warning for unexpected but handled cases, Error for failures

### 6. Testing Edge Cases

- Unit tests MUST cover: normal case, empty results, single result, multiple results
- Unit tests MUST cover: null values, type mismatches, corrupt data states
- Integration tests MUST verify error reporting when edge cases occur
- Never assume data will always be in expected state

### 7. Code Review Focus for Sync Code

- Pay special attention to database queries and their cardinality assumptions
- Look for unhandled exceptions in sync loops
- Verify error handling logs include enough context for debugging
- Question whether a catch-all exception handler should fail the operation or continue
- Ask: "Could a customer's data be corrupted if this exception occurs?"

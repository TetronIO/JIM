# Prompt: Write API Documentation for a JIM Resource Section

Use this prompt to create API documentation for a new resource section. Start a fresh conversation, paste this prompt, and specify which resource to document.

---

## Task

Write complete API documentation for the **[RESOURCE NAME]** section of the JIM API. This includes:

1. A resource index page (`docs/api/[resource-slug]/index.md`)
2. Individual endpoint pages for each API endpoint
3. A single nav entry in `mkdocs.yml`
4. Any missing PowerShell cmdlets needed for example parity

## Before You Start

1. Read the gold-standard Connected Systems section to understand the exact patterns:
   - `docs/api/connected-systems/index.md` (resource index)
   - `docs/api/connected-systems/create.md` (POST with request body)
   - `docs/api/connected-systems/list.md` (GET with pagination)
   - `docs/api/connected-systems/retrieve.md` (GET single)
   - `docs/api/connected-systems/update.md` (PUT with body)
   - `docs/api/connected-systems/delete.md` (DELETE)
   - `docs/api/connected-systems/object-types.md` (multi-endpoint grouped page)

2. Read the API landing page for shared conventions: `docs/api/index.md`

3. Read the relevant controller(s) in `src/JIM.Web/Controllers/Api/` to get exact routes, parameters, and return types.

4. Read the request/response DTOs in `src/JIM.Web/Models/Api/` and entity models in `src/JIM.Models/`.

5. Read the existing PowerShell cmdlets in `src/JIM.PowerShell/Public/` to check which cmdlets exist for this resource.

## Resource Index Page Template

The index page (`docs/api/[resource-slug]/index.md`) must include, in this order:

```markdown
---
title: [Resource Name]
---

# [Resource Name]

[1-2 paragraph description of what this resource represents and its role in JIM.]

## Common Workflows

[Step-by-step recipes showing how endpoints chain together for common tasks.
Link to endpoint pages that exist within this section.]

## The [Resource Name] Object

[Example JSON response of the primary object, followed by an attributes table.]

## Endpoints

[Tables of endpoints grouped by sub-domain, each row linking to its detail page.
Use anchor links for endpoints that share a page (e.g. `page.md#anchor`).]
```

## Endpoint Page Template

Each endpoint page must include, in this order:

```markdown
---
title: [Action] a [Resource]
---

# [Action] a [Resource]

[1-2 sentence description of what this endpoint does and when to use it.]

\```
[HTTP METHOD] /api/v1/[path]
\```

## Path Parameters  (if applicable)
## Query Parameters  (if applicable)
## Request Body      (if applicable)

## Examples

=== "curl"

    \```bash
    [curl example(s)]
    \```

=== "PowerShell"

    \```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    [PowerShell cmdlet example(s)]
    \```

## Response

[Status code, then example JSON response, then attribute table if the response
object hasn't been documented on the index page.]

## Errors

[Table with Status, Code, Description columns.]
```

## Grouping Rules

- **CRUD endpoints** (List, Create, Retrieve, Update, Delete): one page each
- **Simple sub-resources** with 2-3 closely related endpoints: group on a single page with `---` horizontal rule separators and `## Heading` per endpoint
- **Endpoint ordering** in the index table and nav: workflow order (CRUD first, then operations, then sub-resources). Keep related endpoints adjacent (e.g. Delete next to Deletion Preview).

## Style Rules (CRITICAL)

- **British English everywhere**: "synchronisation", "authorisation", "behaviour", "colour"
- **No em dashes** (`—`): use colons, semicolons, or commas instead
- **No emojis** in API reference content
- **Two example tabs only**: "curl" and "PowerShell" (the JIM PowerShell module, NOT raw Invoke-RestMethod)
- **curl is always the first tab** (making it the default)
- **Example base URL**: `https://jim.example.com`
- **Example API key**: `jim_xxxxxxxxxxxx`
- **Example GUIDs**: use the pattern `a1b2c3d4-e5f6-7890-abcd-ef1234567890` (increment letters for multiple)
- **Admonitions**: use `!!! note`, `!!! tip`, `!!! warning` sparingly for contextual guidance
- **Error codes**: use the standard set from `ApiErrorResponse` (VALIDATION_ERROR, NOT_FOUND, UNAUTHORISED, FORBIDDEN, CONFLICT, BAD_REQUEST, INTERNAL_ERROR, SERVICE_UNAVAILABLE)
- **Pagination**: don't re-document pagination conventions on every list page; just show the query parameters table and link to the API landing page conventions if needed
- **Cross-links**: only link to pages that exist. Use plain text for pages not yet written.

## PowerShell Parity (CRITICAL)

**Every curl example MUST have a matching PowerShell module example.** No exceptions.

Before writing docs:
1. List all PowerShell cmdlets in `src/JIM.PowerShell/Public/` for this resource area
2. Cross-reference against the API endpoints you're documenting
3. If a cmdlet is missing, **create it** before writing the docs. Follow the patterns in existing cmdlets (read 2-3 examples first). Place new cmdlets in the appropriate subdirectory under `src/JIM.PowerShell/Public/`.
4. If an existing cmdlet handles multiple endpoints via parameter sets (like `Get-JIMPendingExport` handles list, detail, and attribute changes), use the correct parameter set in each example.

## Navigation

Add a single entry to `mkdocs.yml` under the API section:

```yaml
- [Resource Name]: api/[resource-slug]/index.md
```

Do NOT list individual endpoint pages in the nav. They are reached via links on the index page.

Also update the resource table in `docs/api/index.md` to link to the new section (change the plain text entry to a link).

## Verification Checklist

Before committing:

- [ ] `mkdocs build --strict` passes with no warnings (ignore the CLAUDE.md INFO)
- [ ] Every curl example has a matching PowerShell example
- [ ] All PowerShell cmdlet names match actual files in `src/JIM.PowerShell/Public/`
- [ ] All API routes match the actual controller route attributes
- [ ] All request/response fields match the actual DTOs
- [ ] All cross-links point to pages that exist
- [ ] No em dashes anywhere
- [ ] British English throughout
- [ ] Index page has: description, workflows, object example, endpoint tables
- [ ] Each endpoint page has: description, method/path, parameters, examples, response, errors

## Resource Priority Order

1. ~~Connected Systems~~ (done)
2. Schedules + Schedule Executions
3. Run Profiles
4. Activities
5. Sync Rules (large: includes mappings, scoping criteria, object matching rules)
6. Metaverse (Object Types, Attributes, Objects, Pending Deletions)
7. API Keys, Certificates, Service Settings, Security (small: single page each)
8. Health, Auth Config, User Info, Logs, History (small: single page each)

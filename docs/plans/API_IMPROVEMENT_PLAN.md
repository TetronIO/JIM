# JIM API Improvement Plan

> **Goal**: Build a standards-compliant RESTful API suitable for PowerShell module consumption.
>
> **Branch**: `feature/api-authentication`
>
> **Last Updated**: 2025-12-07

---

## Phase 1: Quick Wins & Bug Fixes

### 1.1 Fix Known Bugs
- [x] Fix parameter naming inconsistency (`csid` → `connectedSystemId`) in SynchronisationController

### 1.2 Standardise Route Declarations
- [x] Convert all absolute routes to relative routes (e.g., `/metaverse/object-types` → `object-types`)
- [x] Add `api/` prefix to base route: `[Route("api/[controller]")]`
- [x] Ensure consistent kebab-case throughout

### 1.3 Fix Return Types
- [x] Add explicit `IActionResult` return type to `DataGenerationController.ExecuteTemplateAsync`
- [x] Return `202 Accepted` for async operations (template execution)
- [x] Return `202 Accepted` for queued deletions (SynchronisationController)
- [x] Return `204 No Content` for void operations (already implemented in CertificatesController)

---

## Phase 2: Error Handling & Response Consistency

### 2.1 Create Standardised Error Response
- [x] Create `ApiErrorResponse` DTO in `JIM.Web/Models/Api/`:
  ```csharp
  public class ApiErrorResponse
  {
      public string Code { get; set; }
      public string Message { get; set; }
      public Dictionary<string, string[]>? ValidationErrors { get; set; }
      public DateTime Timestamp { get; set; }
  }
  ```
- [x] Define standard error codes (e.g., `VALIDATION_ERROR`, `NOT_FOUND`, `UNAUTHORISED`, `CONFLICT`)

### 2.2 Add Global Exception Handler
- [x] Create exception handling middleware
- [x] Return consistent `ApiErrorResponse` for all errors
- [x] Log exceptions with correlation IDs
- [ ] Document 500 responses on all endpoints (deferred to Phase 5)

### 2.3 Standardise Error Returns in Controllers
- [x] Replace string error messages with `ApiErrorResponse`
- [x] Use `BadRequest(new ApiErrorResponse { ... })` pattern consistently
- [x] Add `[ProducesResponseType(typeof(ApiErrorResponse), 400/404)]` to all endpoints

---

## Phase 3: Response DTOs ✅

### 3.1 Create DTOs for MetaverseController
- [x] `MetaverseObjectTypeDto` / `MetaverseObjectTypeDetailDto`
- [x] `MetaverseObjectDto`
- [x] `MetaverseAttributeDto` / `MetaverseAttributeDetailDto`

### 3.2 Create DTOs for SynchronisationController
- [x] `ConnectedSystemDto` / `ConnectedSystemDetailDto`
- [x] `ConnectedSystemObjectTypeDto`
- [x] `ConnectedSystemObjectDto`

### 3.3 Create DTOs for DataGenerationController
- [x] `DataGenerationTemplateHeader` (uses existing DTO from JIM.Models)
- [x] `ExampleDataSetHeader` (uses existing DTO from JIM.Models)

### 3.4 Create DTOs for SecurityController
- [x] `RoleDto`

### 3.5 Update CertificatesController
- [x] Create `TrustedCertificateDetailDto` (without raw certificate bytes)
- [x] Add separate endpoint for certificate data download if needed

### 3.6 Refactor Controllers to Use DTOs
- [x] MetaverseController - map all responses to DTOs
- [x] SynchronisationController - map all responses to DTOs
- [x] DataGenerationController - map all responses to DTOs
- [x] SecurityController - map all responses to DTOs
- [x] CertificatesController - use detail DTO for GetById

### 3.7 JWT Authentication & Swagger OAuth
- [x] Configure JWT Bearer authentication with OIDC discovery
- [x] Configure Swagger UI OAuth2 with PKCE (SPA public client)
- [x] Auto-detect Entra ID and configure v1/v2 issuer formats
- [x] Extract API audience from scope for proper token validation
- [x] Document SSO configuration in `.env.example`

---

## Phase 4: Pagination & Filtering ✅

### 4.1 Create Pagination Infrastructure
- [x] Create `PaginationRequest` model (page, pageSize, sortBy, sortDirection, filter)
- [x] Create `PaginatedResponse<T>` wrapper with metadata (totalCount, totalPages, hasNextPage, hasPreviousPage)
- [x] Add pagination extension methods to IQueryable (`ToPaginatedResponse`, `ApplySort`, `ApplyFilter`)

### 4.2 Add Pagination to List Endpoints
- [x] `GET /api/certificates` - add pagination
- [x] `GET /api/metaverse/object-types` - add pagination
- [x] `GET /api/metaverse/attributes` - add pagination
- [x] `GET /api/synchronisation/connected-systems` - add pagination
- [x] `GET /api/synchronisation/sync-rules` - add pagination
- [x] `GET /api/data-generation/templates` - add pagination
- [x] `GET /api/data-generation/example-data-sets` - add pagination

### 4.3 Add Filtering Support
- [x] Define filter query parameter format: `?filter=property:operator:value`
- [x] Supported operators: `eq`, `ne`, `contains`, `startswith`, `endswith` (strings); `eq`, `ne`, `gt`, `gte`, `lt`, `lte` (numbers)
- [x] Implement expression-based filtering in `QueryableExtensions.ApplyFilter`

### 4.4 Add Sorting Support
- [x] Add `sortBy` and `sortDirection` query parameters
- [x] Implement dynamic sorting via reflection in `QueryableExtensions.ApplySort`

### 4.5 Implementation Notes
- Pagination is applied at the API layer (in-memory) for configuration endpoints (Connected Systems, Sync Rules, Object Types, Attributes) which have small, bounded datasets
- Database-level pagination already exists for large datasets (CSOs and MVOs) via `PagedResultSet<T>` in the repository layer
- Default page size: 25, max: 100

---

## Phase 5: Documentation ✅

### 5.1 Add XML Documentation to All Endpoints
- [x] MetaverseController - add `<summary>`, `<param>`, `<returns>` to all methods
- [x] SynchronisationController - add XML docs
- [x] DataGenerationController - add XML docs
- [x] SecurityController - add XML docs
- [x] CertificatesController - enhance existing docs
- [x] HealthController - add XML docs

### 5.2 Add ProducesResponseType Attributes
- [x] All endpoints: `[ProducesResponseType(200)]` with specific type
- [x] All endpoints: `[ProducesResponseType(typeof(ApiErrorResponse), 400)]`
- [x] All endpoints: `[ProducesResponseType(401)]`
- [ ] All endpoints: `[ProducesResponseType(typeof(ApiErrorResponse), 500)]` (deferred - handled by global exception handler)
- [x] Applicable endpoints: `[ProducesResponseType(404)]`, `[ProducesResponseType(204)]`

### 5.3 Add Operation IDs for PowerShell Generation
- [x] Add `Name = "OperationName"` to all HTTP method attributes
- [x] Follow naming convention: `Get{Resource}`, `Create{Resource}`, `Update{Resource}`, `Delete{Resource}`

### 5.4 Configure Swagger for Better Documentation
- [x] Enable XML comment inclusion in Swagger
- [x] Add API description and contact info
- [x] Group endpoints by controller/tag (automatic via controller-based routing)

---

## Phase 6: Security Enhancements

### 6.1 Input Validation ✅
- [x] Add `[Required]` and validation attributes to all request DTOs
- [x] Add size limits to string parameters (`[StringLength]`)
- [x] Add range validation for numeric parameters (`[Range]`)
- [x] Add regex validation for constrained strings (`[RegularExpression]`)
- Note: ASP.NET Core model validation automatically validates DTOs via data annotations

### 6.2 Role-Based Authorisation (MVP) ✅
- [x] Add middleware to look up JIM roles from database for authenticated API users (`JimRoleEnrichmentMiddleware`)
- [x] Add `[Authorize]` to all controllers (require authentication) - already in place
- [x] Add `[Authorize(Roles = "Administrators")]` to admin-only endpoints (all current endpoints are admin-only)
- Note: NPE/service account access is out of scope for MVP
- Note: Fine-grained resource-level permissions deferred to future RBAC implementation

### 6.3 Sensitive Data Protection ✅
- [x] Remove certificate bytes from standard GET response (POST responses now use `TrustedCertificateDetailDto`)
- [x] Create separate download endpoint for certificate data (`GET /api/certificates/{id}/download`)
- [x] Review MetaverseObject attributes for sensitive data exposure (no sensitive data exposed via DTOs)
- [ ] Add field selection parameter to limit returned data (deferred - nice-to-have for future)

### 6.4 Rate Limiting (Optional)
- [ ] Add rate limiting middleware
- [ ] Configure per-endpoint rate limits
- [ ] Document rate limits in Swagger

---

## Phase 7: API Versioning ✅

### 7.1 Implement Versioning Strategy ✅
- [x] Choose versioning approach: **URL path** (`/api/v1/...`)
- [x] Add `Asp.Versioning.Mvc` and `Asp.Versioning.Mvc.ApiExplorer` NuGet packages
- [x] Configure default version (1.0) with `AssumeDefaultVersionWhenUnspecified`
- [x] Add version to Swagger documentation via `AddApiExplorer`

### 7.2 Version Existing Endpoints ✅
- [x] Mark current API as v1 (`[ApiVersion("1.0")]` on all controllers)
- [x] Update routes to include version segment (`api/v{version:apiVersion}/...`)
- [x] Document versioning strategy in Developer Guide
- [ ] Plan deprecation policy (deferred - define when v2 is needed)

---

## Phase 8: Testing & Validation

### 8.1 Integration Tests (Deferred)
- **Status**: Deferred to backlog - tracked in [GitHub Issue #137](https://github.com/TetronIO/JIM/issues/137)
- **Reason**: Not required for MVP; implement when setting up CI/CD pipeline or when API needs regression protection
- [ ] Create API integration test project
- [ ] Test authentication flows
- [ ] Test pagination
- [ ] Test error responses
- [ ] Test all CRUD operations

### 8.2 PowerShell Module Validation (Deferred)
- **Status**: Deferred to backlog - tracked in [GitHub Issue #138](https://github.com/TetronIO/JIM/issues/138)
- **Reason**: Not required for MVP; implement when API is stable and PowerShell automation is needed
- [ ] Generate PowerShell module from OpenAPI spec
- [ ] Test all cmdlets work correctly
- [ ] Validate error handling in PowerShell
- [ ] Validate pagination handling

---

## Reference: CertificatesController Pattern

The `CertificatesController` should be used as the template for all other controllers. Key patterns:

```csharp
/// <summary>
/// Retrieves all trusted certificates.
/// </summary>
/// <returns>A list of certificate headers.</returns>
[HttpGet]
[ProducesResponseType(typeof(IEnumerable<TrustedCertificateHeader>), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
public async Task<IActionResult> GetAllAsync()
{
    var certificates = await _application.Certificates.GetAllAsync();
    var headers = certificates.Select(TrustedCertificateHeader.FromEntity);
    return Ok(headers);
}
```

---

## Notes

- **Priority**: Phases 1-3 are required before PowerShell module development
- **Breaking Changes**: All changes in this plan may break existing API consumers (none currently)
- **Testing**: Build and test after each phase completion
- **MVP Status**: Phases 1-7 complete; Phase 6.4 (Rate Limiting), Phase 8.1 (Integration Tests), and Phase 8.2 (PowerShell Module) deferred to backlog

# Service Name and Service ID Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a user-editable Service Name and a read-only auto-generated Service ID so administrators can identify individual JIM instances at a glance, via the portal chrome (sidebar, browser tab, footer), the Web API, and the PowerShell module. Resolves [#583](https://github.com/TetronIO/JIM/issues/583).

**Architecture:** Two new service settings following existing patterns. Service ID uses a new `Guid` value type and is seeded exactly once via a new `SeedSettingOnceAsync` helper (distinct from `CreateOrUpdateSettingAsync`, which re-applies values on every startup for env-var-backed settings). Read-only enforcement for Service ID is already free from the existing `PrepareUpdateAsync` guard. Portal chrome gains three small Service Name surfaces (drawer header, browser tab title, footer); Service ID is only rendered on `/admin/settings`.

**Tech Stack:** C# / .NET 10, EF Core, PostgreSQL, Blazor Server, MudBlazor, NUnit + Moq, PowerShell.

**Spec:** [`docs/superpowers/specs/2026-04-18-service-name-and-id-design.md`](../specs/2026-04-18-service-name-and-id-design.md)

---

## File Structure

### Modified
- `src/JIM.Models/Core/CoreEnums.cs` — add `Instance = 6` to `ServiceSettingCategory`, add `Guid = 6` to `ServiceSettingValueType`
- `src/JIM.Models/Core/Constants.cs` — add `ServiceName` and `ServiceId` to `SettingKeys`
- `src/JIM.Application/Servers/SeedingServer.cs` — seed both settings, add `SeedSettingOnceAsync` helper
- `src/JIM.Application/Servers/ServiceSettingsServer.cs` — add `Guid` branch to `ConvertSettingValue<T>`
- `src/JIM.Web/Pages/Admin/Settings.razor` — Instance category label, Guid value rendering with copy-to-clipboard button
- `src/JIM.Web/Shared/MainLayout.razor` — load Service Name, render in drawer header + browser title + footer
- `src/JIM.Web/Shared/EditSettingDialog.razor` — unchanged (default String branch handles Service Name)
- `CHANGELOG.md` — entry under `[Unreleased] → Added`

### New test files
- `test/JIM.Worker.Tests/Servers/ServiceSettingsServerGuidConversionTests.cs`
- `test/JIM.Worker.Tests/Servers/SeedingServerInstanceSettingsTests.cs`

### Modified test files
- `test/JIM.Web.Api.Tests/ServiceSettingsControllerTests.cs` — add tests for Service Name CRUD and Service ID read-only rejection

### No migration required
`ValueType` and `Category` are stored as `integer` columns; adding enum values is a code-only change.

---

## Task 1: Add enum values and setting key constants

No tests required; this is pure data that later tasks depend on. Compile-check only.

**Files:**
- Modify: `src/JIM.Models/Core/CoreEnums.cs`
- Modify: `src/JIM.Models/Core/Constants.cs`

- [ ] **Step 1: Add `Instance` to `ServiceSettingCategory`**

Edit `src/JIM.Models/Core/CoreEnums.cs`. Find the `ServiceSettingCategory` enum (around line 160) and add after `UI = 5`:

```csharp
    /// <summary>
    /// User interface settings.
    /// </summary>
    UI = 5,

    /// <summary>
    /// Instance identity settings (Service Name, Service ID).
    /// Used by administrators to tell JIM instances apart.
    /// </summary>
    Instance = 6
}
```

- [ ] **Step 2: Add `Guid` to `ServiceSettingValueType`**

In the same file, find `ServiceSettingValueType` (around line 196) and add after `StringEncrypted = 5`:

```csharp
    /// <summary>
    /// Encrypted string value for secrets (passwords, API keys, etc.).
    /// Values are encrypted at rest using ASP.NET Core Data Protection.
    /// </summary>
    StringEncrypted = 5,

    /// <summary>
    /// GUID value. Stored as a string in standard GUID format.
    /// Typically used for read-only, auto-generated identifiers.
    /// </summary>
    Guid = 6
}
```

- [ ] **Step 3: Add setting key constants**

Edit `src/JIM.Models/Core/Constants.cs`. Inside `public static class SettingKeys`, add at the end (after `ProgressUpdateInterval`):

```csharp
        // Instance Settings
        /// <summary>
        /// A friendly, editable name for this JIM instance.
        /// Appears in the sidebar, browser tab title, and footer.
        /// </summary>
        public const string ServiceName = "Instance.Name";

        /// <summary>
        /// A stable, immutable GUID identifier for this JIM instance.
        /// Generated exactly once on first startup; never changes thereafter.
        /// </summary>
        public const string ServiceId = "Instance.Id";
```

- [ ] **Step 4: Build affected projects**

```bash
dotnet build src/JIM.Models/
```

Expected: build succeeds with zero errors, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/JIM.Models/Core/CoreEnums.cs src/JIM.Models/Core/Constants.cs
git commit -m "feat: add Instance category, Guid value type, and Service Name/ID keys (#583)

Adds ServiceSettingCategory.Instance, ServiceSettingValueType.Guid, and
SettingKeys.ServiceName / ServiceId. No migration required — enum values
are stored as integers and are backwards compatible.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Add Guid branch to `ConvertSettingValue<T>` (TDD)

**Files:**
- Modify: `src/JIM.Application/Servers/ServiceSettingsServer.cs:331-359`
- Create: `test/JIM.Worker.Tests/Servers/ServiceSettingsServerGuidConversionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/JIM.Worker.Tests/Servers/ServiceSettingsServerGuidConversionTests.cs`:

```csharp
// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ServiceSettingsServerGuidConversionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetSettingValueAsync_GuidValueType_ReturnsParsedGuidAsync()
    {
        var expected = Guid.NewGuid();
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Instance.Id"))
            .ReturnsAsync(new ServiceSetting
            {
                Key = "Instance.Id",
                DisplayName = "Service ID",
                Category = ServiceSettingCategory.Instance,
                ValueType = ServiceSettingValueType.Guid,
                Value = expected.ToString(),
                IsReadOnly = true
            });

        var result = await _application.ServiceSettings.GetSettingValueAsync<Guid>("Instance.Id");

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetSettingValueAsync_GuidValueType_MalformedString_ReturnsDefaultAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Instance.Id"))
            .ReturnsAsync(new ServiceSetting
            {
                Key = "Instance.Id",
                DisplayName = "Service ID",
                Category = ServiceSettingCategory.Instance,
                ValueType = ServiceSettingValueType.Guid,
                Value = "not-a-guid",
                IsReadOnly = true
            });

        var result = await _application.ServiceSettings.GetSettingValueAsync<Guid>("Instance.Id");

        Assert.That(result, Is.EqualTo(Guid.Empty));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test test/JIM.Worker.Tests/ --filter "FullyQualifiedName~ServiceSettingsServerGuidConversionTests"
```

Expected: both tests FAIL. The first fails because `ConvertSettingValue<Guid>` returns `default(Guid)` (i.e. `Guid.Empty`) instead of the parsed value, because the method doesn't handle `Guid`.

- [ ] **Step 3: Add the Guid branch**

Edit `src/JIM.Application/Servers/ServiceSettingsServer.cs`. Find `ConvertSettingValue<T>` (around line 331) and add a Guid branch before the `if (underlyingType.IsEnum)` line:

```csharp
                if (underlyingType == typeof(TimeSpan))
                    return (T)(object)TimeSpan.Parse(value);

                if (underlyingType == typeof(Guid))
                    return (T)(object)Guid.Parse(value);

                if (underlyingType.IsEnum)
                    return (T)Enum.Parse(underlyingType, value);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/JIM.Worker.Tests/ --filter "FullyQualifiedName~ServiceSettingsServerGuidConversionTests"
```

Expected: both tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/JIM.Application/Servers/ServiceSettingsServer.cs \
        test/JIM.Worker.Tests/Servers/ServiceSettingsServerGuidConversionTests.cs
git commit -m "feat: support Guid value type in ConvertSettingValue (#583)

Adds Guid.Parse branch to the generic converter so GetSettingValueAsync<Guid>
returns a parsed GUID for Instance.Id and other future Guid-typed settings.
Malformed values fall through to the existing catch-and-default path.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Add `SeedSettingOnceAsync` helper and seed Instance settings (TDD)

**Files:**
- Modify: `src/JIM.Application/Servers/SeedingServer.cs`
- Create: `test/JIM.Worker.Tests/Servers/SeedingServerInstanceSettingsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/JIM.Worker.Tests/Servers/SeedingServerInstanceSettingsTests.cs`:

```csharp
// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class SeedingServerInstanceSettingsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task SyncServiceSettings_FirstRun_SeedsServiceNameWithNullValueAsync()
    {
        // First run: no existing settings
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceName)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);

        // Stub the other seed calls so the method completes
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key != Constants.SettingKeys.ServiceName)))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Instance));
        Assert.That(captured.ValueType, Is.EqualTo(ServiceSettingValueType.String));
        Assert.That(captured.DefaultValue, Is.Null);
        Assert.That(captured.Value, Is.Null);
        Assert.That(captured.IsReadOnly, Is.False);
    }

    [Test]
    public async Task SyncServiceSettings_FirstRun_GeneratesServiceIdGuidAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceId)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key != Constants.SettingKeys.ServiceId)))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Instance));
        Assert.That(captured.ValueType, Is.EqualTo(ServiceSettingValueType.Guid));
        Assert.That(captured.IsReadOnly, Is.True);
        Assert.That(Guid.TryParse(captured.Value, out var parsed), Is.True,
            "Service ID value must be a valid GUID");
        Assert.That(parsed, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task SyncServiceSettings_SecondRun_PreservesExistingServiceIdAsync()
    {
        // Second run: Service ID already exists
        var existingId = Guid.NewGuid().ToString();
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(true);
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ServiceId,
                DisplayName = "Service ID",
                Category = ServiceSettingCategory.Instance,
                ValueType = ServiceSettingValueType.Guid,
                Value = existingId,
                IsReadOnly = true
            });
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        // Verify: CreateSettingAsync was NOT called for Service ID on the second run
        _mockServiceSettingsRepo.Verify(
            r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceId)),
            Times.Never,
            "Service ID must not be recreated when it already exists");

        // And UpdateSettingAsync was NOT called for Service ID either
        _mockServiceSettingsRepo.Verify(
            r => r.UpdateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceId)),
            Times.Never,
            "Service ID must not be updated when it already exists");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test test/JIM.Worker.Tests/ --filter "FullyQualifiedName~SeedingServerInstanceSettingsTests"
```

Expected: all three tests FAIL — `CreateSettingAsync` is never called for `Instance.Name` or `Instance.Id` because the seed calls don't exist yet.

- [ ] **Step 3: Add `SeedSettingOnceAsync` helper**

Edit `src/JIM.Application/Servers/SeedingServer.cs`. Find `SeedSettingAsync` (around line 927) and add immediately below it:

```csharp
    /// <summary>
    /// Seeds a single service setting exactly once. Creates the setting with a generated value
    /// on first run; on subsequent runs, leaves the existing setting completely untouched.
    /// Use for identifiers that must never be regenerated (e.g. Service ID).
    /// </summary>
    private async Task SeedSettingOnceAsync(ServiceSetting template, Func<string> valueFactory)
    {
        if (await Application.ServiceSettings.SettingExistsAsync(template.Key))
        {
            Log.Verbose($"SeedSettingOnceAsync: '{template.Key}' already exists; preserving existing value.");
            return;
        }

        template.Value = valueFactory();
        await Application.ServiceSettings.CreateSettingAsync(template);
        Log.Information($"SeedSettingOnceAsync: Generated '{template.Key}' with value '{template.Value}'.");
    }
```

- [ ] **Step 4: Add Instance-category seed calls**

In the same file, inside `SyncServiceSettingsAsync`, add immediately before the `stopwatch.Stop();` line (around line 920):

```csharp
        // Instance Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceName,
            DisplayName = "Service Name",
            Description = "A friendly, editable name for this JIM instance. Appears in the sidebar, browser tab title, and footer so you can tell instances apart.",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = null,
            IsReadOnly = false
        });

        await SeedSettingOnceAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Description = "A stable, immutable identifier generated once when this JIM instance was created. Used by tooling, logs, and telemetry to identify this instance. Cannot be changed.",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            DefaultValue = null,
            IsReadOnly = true
        }, () => Guid.NewGuid().ToString());

        stopwatch.Stop();
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test test/JIM.Worker.Tests/ --filter "FullyQualifiedName~SeedingServerInstanceSettingsTests"
```

Expected: all three tests PASS.

- [ ] **Step 6: Run the full worker test project to catch regressions**

```bash
dotnet test test/JIM.Worker.Tests/
```

Expected: all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/JIM.Application/Servers/SeedingServer.cs \
        test/JIM.Worker.Tests/Servers/SeedingServerInstanceSettingsTests.cs
git commit -m "feat: seed Service Name and Service ID with once-only semantics (#583)

Adds SeedSettingOnceAsync helper distinct from CreateOrUpdateSettingAsync.
Service Name is seeded editable with null default. Service ID is generated
as a new GUID on first run and never touched again, even across restarts.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: API tests for Service Name CRUD and Service ID read-only rejection (TDD verification)

No production code changes required — the existing `PrepareUpdateAsync` already enforces `IsReadOnly`. Tests prove the behaviour is correct for the new settings.

**Files:**
- Modify: `test/JIM.Web.Api.Tests/ServiceSettingsControllerTests.cs`

- [ ] **Step 1: Add tests at the end of the existing test class**

Edit `test/JIM.Web.Api.Tests/ServiceSettingsControllerTests.cs`. Add these tests just before the closing brace of the class (after the last existing `#endregion`, mirror existing patterns for mock setup):

```csharp
    #region Instance settings tests (#583)

    [Test]
    public async Task UpdateAsync_ServiceName_UpdatesSuccessfullyAsync()
    {
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceName,
            DisplayName = "Service Name",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.String,
            Value = null,
            IsReadOnly = false
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceName))
            .ReturnsAsync(setting);
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>()))
            .Returns(Task.CompletedTask);

        var request = new ServiceSettingUpdateRequestDto { Value = "HQ-Production" };
        var result = await _controller.UpdateAsync(Constants.SettingKeys.ServiceName, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockServiceSettingsRepo.Verify(r => r.UpdateSettingAsync(
            It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceName && s.Value == "HQ-Production")),
            Times.Once);
    }

    [Test]
    public async Task UpdateAsync_ServiceId_ReturnsBadRequestAsync()
    {
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            Value = Guid.NewGuid().ToString(),
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(setting);

        var request = new ServiceSettingUpdateRequestDto { Value = Guid.NewGuid().ToString() };
        var result = await _controller.UpdateAsync(Constants.SettingKeys.ServiceId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var body = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Message, Does.Contain("read-only"));

        // Verify update was NOT called
        _mockServiceSettingsRepo.Verify(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()),
            Times.Never);
    }

    [Test]
    public async Task RevertAsync_ServiceId_ReturnsBadRequestAsync()
    {
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            Value = Guid.NewGuid().ToString(),
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(setting);

        var result = await _controller.RevertAsync(Constants.SettingKeys.ServiceId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockServiceSettingsRepo.Verify(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()),
            Times.Never);
    }

    [Test]
    public async Task GetByKeyAsync_ServiceId_ReturnsGuidStringAsync()
    {
        var id = Guid.NewGuid();
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            Value = id.ToString(),
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(setting);

        var result = await _controller.GetByKeyAsync(Constants.SettingKeys.ServiceId) as OkObjectResult;
        var dto = result?.Value as ServiceSettingDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.ValueType, Is.EqualTo("Guid"));
        Assert.That(dto.EffectiveValue, Is.EqualTo(id.ToString()));
        Assert.That(dto.IsReadOnly, Is.True);
    }

    #endregion
```

- [ ] **Step 2: Run new tests**

```bash
dotnet test test/JIM.Web.Api.Tests/ --filter "FullyQualifiedName~ServiceSettingsControllerTests"
```

Expected: all tests PASS (the behaviour is already in place from Task 3 + existing PrepareUpdateAsync guard).

Note: If the Activity repository mock signatures do not match the actual interface (e.g. method names or parameters have evolved), copy the setup pattern from an existing `UpdateAsync_*` test in the file — don't invent new mock shapes.

- [ ] **Step 3: Commit**

```bash
git add test/JIM.Web.Api.Tests/ServiceSettingsControllerTests.cs
git commit -m "test: cover Service Name CRUD and Service ID read-only rejection (#583)

Confirms PUT /Instance.Name succeeds, PUT /Instance.Id returns 400,
DELETE /Instance.Id returns 400, and the GET response carries the
Guid value type and read-only flag.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Render Service ID on `/admin/settings` with copy-to-clipboard

No unit tests (Blazor pages have no automated tests in JIM). Manual verification in Step 5.

**Files:**
- Modify: `src/JIM.Web/Pages/Admin/Settings.razor`

- [ ] **Step 1: Add Instance category display name**

Edit `src/JIM.Web/Pages/Admin/Settings.razor`. Find `GetCategoryDisplayName` (around line 215) and add the `Instance` case:

```csharp
    private string GetCategoryDisplayName(object? categoryValue)
    {
        var categoryString = categoryValue?.ToString();
        return categoryString switch
        {
            "SSO" => "Single Sign-On (SSO)",
            "Synchronisation" => "Synchronisation",
            "Maintenance" => "Maintenance",
            "History" => "History",
            "Security" => "Security",
            "Instance" => "Instance",
            _ => categoryString ?? "Unknown"
        };
    }
```

- [ ] **Step 2: Inject the JS runtime for clipboard**

At the top of the same file, add `@inject IJSRuntime JSRuntime` alongside the other `@inject` directives (after `@inject ICredentialProtectionService CredentialProtection`, and add the `using` to top of file):

```razor
@inject IJimApplicationFactory JimFactory
@inject ISnackbar Snackbar
@inject IDialogService DialogService
@inject ICredentialProtectionService CredentialProtection
@inject IJSRuntime JSRuntime
```

- [ ] **Step 3: Add Guid value rendering with copy button**

In the value `<MudTd>` cell (around line 71), add a new branch before the `else` fallback. The existing structure looks like:

```razor
<MudTd DataLabel="Value" Class="@GetRowClass(settingContext)">
    @if (settingContext.ValueType == ServiceSettingValueType.StringEncrypted)
    {
        @* ... existing encrypted rendering ... *@
    }
    else
    {
        <span class="jim-text-code">@(settingContext.GetEffectiveValue() ?? "(not set)")</span>
    }
</MudTd>
```

Change it to:

```razor
<MudTd DataLabel="Value" Class="@GetRowClass(settingContext)">
    @if (settingContext.ValueType == ServiceSettingValueType.StringEncrypted)
    {
        @if (_revealedSecrets.Contains(settingContext.Key) && _decryptedCache.TryGetValue(settingContext.Key, out var decrypted))
        {
            <span class="jim-text-code">@(decrypted.Value ?? "(not set)")</span>
            <MudIconButton Icon="@Icons.Material.Outlined.VisibilityOff" Variant="Variant.Filled" Size="Size.Small" OnClick="() => HideSecret(settingContext.Key)" />
        }
        else
        {
            <span class="jim-text-code">********</span>
            <MudIconButton Icon="@Icons.Material.Outlined.Visibility" Variant="Variant.Filled" Size="Size.Small" OnClick="() => RevealSecretAsync(settingContext.Key)" />
        }
    }
    else if (settingContext.ValueType == ServiceSettingValueType.Guid)
    {
        var guidValue = settingContext.GetEffectiveValue();
        <span class="jim-text-code">@(guidValue ?? "(not set)")</span>
        @if (!string.IsNullOrEmpty(guidValue))
        {
            <MudTooltip Text="Copy to clipboard" Arrow="true" Placement="Placement.Top">
                <MudIconButton Icon="@Icons.Material.Outlined.ContentCopy" Variant="Variant.Filled" Size="Size.Small"
                               OnClick="() => CopyToClipboardAsync(guidValue!)" />
            </MudTooltip>
        }
    }
    else
    {
        <span class="jim-text-code">@(settingContext.GetEffectiveValue() ?? "(not set)")</span>
    }
</MudTd>
```

(Retain the *complete* existing `StringEncrypted` block; the snippet above shows the full structure to avoid ambiguity.)

- [ ] **Step 4: Add the clipboard helper method**

In the `@code` block at the bottom of the file, add this method alongside the other helpers (e.g. after `HandleRevertAsync`):

```csharp
    private async Task CopyToClipboardAsync(string value)
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", value);
            Snackbar.Add("Copied to clipboard.", Severity.Success);
        }
        catch (JSException)
        {
            Snackbar.Add("Failed to copy to clipboard.", Severity.Error);
        }
    }
```

- [ ] **Step 5: Build and manually verify**

```bash
dotnet build src/JIM.Web/
```

Expected: build succeeds with zero errors, zero warnings.

Manual verification (from repo root):
1. Start the stack: `jim-build-light`
2. Sign in, navigate to `/admin/settings`.
3. Expand the "Instance" group.
4. Confirm:
   - Service Name row: empty/default, Edit icon enabled.
   - Service ID row: monospace GUID visible, copy icon enabled, **Read-only** chip shown, **no Edit icon**.
5. Click the copy icon → snackbar "Copied to clipboard." → paste into a text field to confirm.
6. Click Edit on Service Name → enter `HQ-Production` → Save → row updates, **Modified** chip appears.

- [ ] **Step 6: Commit**

```bash
git add src/JIM.Web/Pages/Admin/Settings.razor
git commit -m "feat: render Service ID with copy-to-clipboard on settings page (#583)

Adds the Instance category label, Guid value rendering with a monospace
string + copy icon, and a CopyToClipboardAsync helper. Service Name uses
the default String editing path so no dialog changes are needed.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Display Service Name in drawer header

**Files:**
- Modify: `src/JIM.Web/Shared/MainLayout.razor`

- [ ] **Step 1: Inject the application factory**

Edit `src/JIM.Web/Shared/MainLayout.razor`. Add below the existing `@inject` directives (after `@inject NavigationManager NavigationManager`):

```razor
@inject IJimApplicationFactory JimFactory
@inject JIM.Web.Models.ThemeSettings ThemeSettings
@inject NavigationManager NavigationManager
```

(If `IJimApplicationFactory` already is available, skip. Check for existing `@using JIM.Application` — NavMenu already uses this pattern.)

- [ ] **Step 2: Add Service Name loading in code-behind**

In the `@code { }` block, add a field near the other fields and load in `OnInitializedAsync`:

```csharp
    private string? _serviceName;

    protected override async Task OnInitializedAsync()
    {
        using var jim = JimFactory.Create();
        _serviceName = await jim.ServiceSettings.GetSettingValueAsync<string>(
            JIM.Models.Core.Constants.SettingKeys.ServiceName);

        NavigationManager.LocationChanged += OnLocationChanged;
    }
```

Merge this with the existing `OnInitialized`/`OnInitializedAsync`. The existing code has a synchronous `OnInitialized` that subscribes to `NavigationManager.LocationChanged`. Replace it with the async version above so both concerns are handled, and **delete** the old `OnInitialized`.

- [ ] **Step 3: Render Service Name under the "JIM" wordmark**

Find the drawer logo block (around line 23):

```razor
<div class="jim-drawer-logo">
    <MudImage Src="/images/jim-logo.png" Width="28" Class="mt-4 ms-4 mb-3" Alt="JIM Logo"></MudImage>
    @if (IsDrawerOpen)
    {
        <MudText Inline="true">JIM</MudText>
    }
</div>
```

Replace with:

```razor
<div class="jim-drawer-logo">
    <MudImage Src="/images/jim-logo.png" Width="28" Class="mt-4 ms-4 mb-3" Alt="JIM Logo"></MudImage>
    @if (IsDrawerOpen)
    {
        <div class="d-flex flex-column">
            <MudText Inline="true">JIM</MudText>
            @if (!string.IsNullOrEmpty(_serviceName))
            {
                <MudText Typo="Typo.caption" Class="mud-text-secondary jim-text-code">@_serviceName</MudText>
            }
        </div>
    }
    else if (!string.IsNullOrEmpty(_serviceName))
    {
        @* Collapsed drawer: no visible Service Name, but logo tooltip surfaces it on hover. *@
    }
</div>

@if (!IsDrawerOpen && !string.IsNullOrEmpty(_serviceName))
{
    @* Tooltip wrapper is applied at the logo level only when collapsed, to avoid interfering with expanded layout. *@
}
```

Because MudBlazor tooltips are easier to attach at the image level, use an alternative: wrap the `<MudImage>` in `<MudTooltip>` conditional on collapsed + ServiceName set. Simplest approach:

```razor
<div class="jim-drawer-logo">
    @if (!IsDrawerOpen && !string.IsNullOrEmpty(_serviceName))
    {
        <MudTooltip Text="@_serviceName" Arrow="true" Placement="Placement.Right">
            <MudImage Src="/images/jim-logo.png" Width="28" Class="mt-4 ms-4 mb-3" Alt="JIM Logo"></MudImage>
        </MudTooltip>
    }
    else
    {
        <MudImage Src="/images/jim-logo.png" Width="28" Class="mt-4 ms-4 mb-3" Alt="JIM Logo"></MudImage>
    }
    @if (IsDrawerOpen)
    {
        <div class="d-flex flex-column">
            <MudText Inline="true">JIM</MudText>
            @if (!string.IsNullOrEmpty(_serviceName))
            {
                <MudText Typo="Typo.caption" Class="mud-text-secondary jim-text-code">@_serviceName</MudText>
            }
        </div>
    }
</div>
```

- [ ] **Step 4: Build and manually verify**

```bash
dotnet build src/JIM.Web/
```

Expected: build succeeds.

Manual verification:
1. With Service Name unset: drawer (expanded and collapsed) looks identical to today — "JIM" text only, no tooltip.
2. Set Service Name to `HQ-Production` via `/admin/settings`. Hard-refresh the page.
3. Expanded drawer: `JIM` on the first line, `HQ-Production` in monospace secondary text beneath.
4. Collapsed drawer (unpin, mouse away): hovering the logo shows a tooltip `HQ-Production`.

- [ ] **Step 5: Commit**

```bash
git add src/JIM.Web/Shared/MainLayout.razor
git commit -m "feat: show Service Name under JIM wordmark in drawer (#583)

When configured, the drawer renders the Service Name in monospace style
beneath the JIM wordmark. Collapsed drawer keeps the logo-only silhouette
but surfaces the Service Name in a hover tooltip.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Append Service Name to browser tab title

**Files:**
- Modify: `src/JIM.Web/Shared/MainLayout.razor`

- [ ] **Step 1: Update the `<PageTitle>` block**

Find line 15 (`<PageTitle>JIM</PageTitle>`) and replace with:

```razor
@if (!string.IsNullOrEmpty(_serviceName))
{
    <PageTitle>JIM — @_serviceName</PageTitle>
}
else
{
    <PageTitle>JIM</PageTitle>
}
```

Note: Individual pages that define their own `<PageTitle>` will continue to override the layout-level one — that's the Blazor cascading behaviour. The layout-level title covers pages that don't set one. A follow-up can extend per-page titles if needed.

- [ ] **Step 2: Build and manually verify**

```bash
dotnet build src/JIM.Web/
```

Manual verification:
1. With Service Name unset: browser tab shows `JIM` on pages without a custom `<PageTitle>`.
2. With Service Name set to `HQ-Production`: same pages show `JIM — HQ-Production`.
3. `/admin/settings` shows `Service Settings` (the page's own title still overrides — expected).

- [ ] **Step 3: Commit**

```bash
git add src/JIM.Web/Shared/MainLayout.razor
git commit -m "feat: append Service Name to browser tab title (#583)

The layout-level PageTitle becomes 'JIM — {ServiceName}' when Service Name
is set, helping administrators differentiate tabs when running several JIM
instances side-by-side. Pages with their own PageTitle still override.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Append Service Name to the footer

**Files:**
- Modify: `src/JIM.Web/Shared/MainLayout.razor`

- [ ] **Step 1: Update the footer block**

Find the footer block (around line 55) and add the Service Name span between the version and the GitHub link:

```razor
<div class="jim-page-footer mt-5">
    <MudText Typo="Typo.body2" Class="mud-text-secondary jim-page-footer-text">
        <span>&copy; @DateTime.UtcNow.Year</span>
        <MudLink Href="https://tetron.io" Target="_blank"
                 Color="Color.Inherit" Underline="Underline.Hover">
            Tetron
        </MudLink>
        <span class="jim-page-footer-sep">|</span>
        <span>All rights reserved</span>
        <span class="jim-page-footer-sep">|</span>
        <span>v@(AppVersion)</span>
        @if (!string.IsNullOrEmpty(_serviceName))
        {
            <span class="jim-page-footer-sep">|</span>
            <span>@_serviceName</span>
        }
        <span class="jim-page-footer-sep">|</span>
        <MudLink Href="https://github.com/TetronIO/JIM" Target="_blank"
                 Color="Color.Inherit" Underline="Underline.Hover" Class="jim-page-footer-github">
            <MudIcon Icon="@Icons.Custom.Brands.GitHub" Size="Size.Small" Class="jim-page-footer-github-icon" />
            <span>GitHub</span>
        </MudLink>
    </MudText>
</div>
```

- [ ] **Step 2: Build and manually verify**

```bash
dotnet build src/JIM.Web/
```

Manual verification:
1. Service Name unset: footer unchanged (`© 2026 Tetron | All rights reserved | v0.8.x | GitHub`).
2. Service Name set to `HQ-Production`: footer shows `© 2026 Tetron | All rights reserved | v0.8.x | HQ-Production | GitHub`.

- [ ] **Step 3: Commit**

```bash
git add src/JIM.Web/Shared/MainLayout.razor
git commit -m "feat: show Service Name in footer beside version (#583)

When configured, Service Name appears between the version and the GitHub
link in the page footer — a natural neighbour for instance reference info.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Add changelog entry

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add the entry**

Edit `CHANGELOG.md`. Under `## [Unreleased]` → `### Added`, add as the first bullet (top of the list):

```markdown
- ✨ Added a Service Name and Service ID so you can tell JIM instances apart at a glance. Set a friendly name per instance on the Service Settings page and see it under "JIM" in the sidebar, in the browser tab title, and in the footer. The Service ID is generated once per instance and never changes — useful for tooling, logs, and telemetry (#583)
```

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: add changelog entry for Service Name / Service ID (#583)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Final pre-PR verification

- [ ] **Step 1: Full solution build**

```bash
dotnet build JIM.sln
```

Expected: zero errors, zero warnings.

- [ ] **Step 2: Full solution tests**

```bash
dotnet test JIM.sln
```

Expected: all tests PASS.

- [ ] **Step 3: Manual smoke test against a freshly-seeded database**

```bash
# From repo root:
jim-stack  # or jim-build-light
```

1. Reset the DB volume if needed (`docker compose down -v`) then start fresh so the seeder runs on an empty database.
2. Sign in, go to `/admin/settings`, expand **Instance**:
   - `Service Name` present, editable, empty by default.
   - `Service ID` present, GUID visible, monospace, copy icon, **Read-only** chip.
3. **Restart** the worker + web containers. Reload `/admin/settings`. Confirm the same Service ID GUID is present (not regenerated).
4. Set Service Name to `Smoke-Test-Instance`. Reload page. Confirm:
   - Drawer expanded: "JIM" with `Smoke-Test-Instance` in monospace beneath.
   - Drawer collapsed: hover tooltip shows `Smoke-Test-Instance`.
   - Browser tab: `JIM — Smoke-Test-Instance` (on pages that don't set their own title — e.g. `/`).
   - Footer: `| Smoke-Test-Instance |` between version and GitHub.
5. Try PowerShell:
   ```powershell
   Connect-JIM -Url http://localhost -ApiKey "<key>"
   Get-JIMServiceSetting -Key Instance.Name  # shows value
   Get-JIMServiceSetting -Key Instance.Id    # shows GUID
   Set-JIMServiceSetting -Key Instance.Id -Value (New-Guid).Guid  # must fail with "read-only"
   Set-JIMServiceSetting -Key Instance.Name -Value "Renamed"       # succeeds
   ```

- [ ] **Step 4: Confirm acceptance criteria**

Re-read [`docs/superpowers/specs/2026-04-18-service-name-and-id-design.md`](../specs/2026-04-18-service-name-and-id-design.md) Acceptance section. Tick off each item.

- [ ] **Step 5: Push and open PR (only on explicit user instruction)**

```bash
git push -u origin claude/thirsty-tereshkova-101f67
gh pr create --title "Add Service Name and Service ID for instance identification (#583)" --body "$(cat <<'EOF'
## Summary
- Adds a user-editable `Service Name` and a read-only auto-generated `Service ID` (GUID) to identify individual JIM instances at a glance.
- Service Name is surfaced in the drawer header (under "JIM"), browser tab title, and footer. Service ID is shown on `/admin/settings` only, with copy-to-clipboard.
- Both are read/writable via the existing `/api/v1/service-settings/{key}` endpoint and the existing `Get/Set/Reset-JIMServiceSetting` cmdlets. Read-only enforcement on Service ID is provided by the existing `PrepareUpdateAsync` guard.

Resolves #583.

## Test plan
- [x] `dotnet build JIM.sln` — zero errors, zero warnings
- [x] `dotnet test JIM.sln` — all pass
- [x] Fresh DB: Service ID generated once; preserved across restart
- [x] PUT/DELETE `/api/v1/service-settings/Instance.Id` returns 400
- [x] `Set-JIMServiceSetting -Key Instance.Id` fails with read-only error
- [x] Service Name shown in drawer (expanded + collapsed tooltip), browser tab, footer
EOF
)"
```

---

## Self-review notes

Spec coverage check:
- ServiceSettingCategory.Instance: Task 1
- ServiceSettingValueType.Guid: Task 1
- SettingKeys.ServiceName / ServiceId: Task 1
- SeedSettingOnceAsync helper + Service ID once-only seeding: Task 3
- Service Name seeding: Task 3
- Guid branch in ConvertSettingValue<T>: Task 2
- Drawer header display: Task 6
- Browser tab title: Task 7
- Footer display: Task 8
- /admin/settings Instance category label + Guid rendering + copy: Task 5
- API read-only enforcement tests: Task 4
- PowerShell read-only enforcement: covered transitively (cmdlet calls the API; Task 4 test confirms API returns 400; manual verification in Task 10 Step 3 confirms the cmdlet surfaces the error)
- Test plan (seeding, preservation, CRUD, read-only, conversion): Tasks 2–4
- Changelog: Task 9
- No migration: confirmed — `ValueType` column type is `integer`

All type names consistent: `ServiceSetting`, `ServiceSettingCategory.Instance`, `ServiceSettingValueType.Guid`, `Constants.SettingKeys.ServiceName`, `Constants.SettingKeys.ServiceId`, `SeedSettingOnceAsync`, `CopyToClipboardAsync`.

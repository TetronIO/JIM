# CSO Attribute Table Redesign

**Status:** Planned
**Milestone:** Post-MVP
**Created:** 2026-01-23

## Overview

Redesign the Connected System Object (CSO) detail page attribute display from a grouped accordion table to a compact, professional two-column table layout.

## Current Problems

1. **Redundant "Group:" prefix** - Every attribute shows "Group:" which adds visual noise
2. **Excessive vertical space** - Each attribute takes multiple lines even for simple single values
3. **Accordion overkill** - Expandable sections for single-valued attributes is unnecessary
4. **"Total Values: 1" clutter** - Showing count for single-value attributes adds no value
5. **Inconsistent information density** - Reference values show rich detail while text shows just the value
6. **No visual hierarchy** - All attributes appear equally important

## Proposed Design

### Compact Table Layout

A clean two-column table with:
- **Left column:** Attribute name with type indicator
- **Right column:** Value(s) with appropriate formatting

### Design Principles

1. **Single-valued attributes**: One row, no expansion needed
2. **Multi-valued attributes**: Show first value with "(+N more)" indicator, expandable inline
3. **Reference values**: Compact chip with type + clickable link
4. **Type indicators**: Small, subtle icons or badges showing data type
5. **Consistent row height**: Maintain visual rhythm

### Visual Mockup (ASCII)

```
+--------------------------------------------------------------------+
| Attributes                                                         |
+--------------------------------------------------------------------+
| cn                    | Dept-Engineering                           |
| description           | Department group for Engineering           |
| displayName           | Dept-Engineering                           |
| distinguishedName     | CN=Dept-Engineering,OU=Entitlements,...    |
| groupType             | -2147483640                                |
| mail                  | dept-engineering@sourcedomain.local        |
| managedBy             | [User] Oliver Smith (distinguishedName:..) |
| member                | [User] Jack Pearson (+1 more)        [v]   |
| objectClass           | group, top                                 |
| objectGUID            | c9d951da-dd81-48d5-810c-a7aad49b80ba       |
| sAMAccountName        | Dept-Engineering                           |
+--------------------------------------------------------------------+
```

### Expanded Multi-Value View

When user clicks "(+1 more)" or the expand icon:

```
| member                | [User] Jack Pearson                   [^] |
|                       | [User] Jacob Howell                       |
```

## Implementation

### Phase 1: Core Table Structure

**File:** `src/JIM.Web/Pages/Admin/ConnectedSystemObjectDetail.razor`

Replace the grouped MudTable (lines 159-211) with:

```razor
<MudSimpleTable Dense="true" Hover="true" Elevation="0" Class="jim-attribute-table">
    <thead>
        <tr>
            <th style="width: 200px;">Attribute</th>
            <th>Value</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var group in GetGroupedAttributes())
        {
            <tr class="@(group.IsMultiValued && group.IsExpanded ? "jim-attr-row-expanded" : "")">
                <td class="jim-attr-name">
                    <MudText Typo="Typo.body2" Class="jim-text-code">@group.AttributeName</MudText>
                </td>
                <td class="jim-attr-value">
                    @if (group.Values.Count == 1)
                    {
                        <CsoAttributeValue Value="@group.Values[0]" />
                    }
                    else if (!group.IsExpanded)
                    {
                        <CsoAttributeValue Value="@group.Values[0]" />
                        <MudButton Variant="Variant.Text" Size="Size.Small"
                                   OnClick="@(() => ToggleExpand(group.AttributeName))">
                            (+@(group.Values.Count - 1) more)
                        </MudButton>
                    }
                    else
                    {
                        <MudStack Spacing="1">
                            @foreach (var val in group.Values)
                            {
                                <div><CsoAttributeValue Value="@val" /></div>
                            }
                        </MudStack>
                        <MudButton Variant="Variant.Text" Size="Size.Small"
                                   OnClick="@(() => ToggleExpand(group.AttributeName))">
                            (collapse)
                        </MudButton>
                    }
                </td>
            </tr>
        }
    </tbody>
</MudSimpleTable>
```

### Phase 2: Reusable Attribute Value Component

**New File:** `src/JIM.Web/Shared/CsoAttributeValue.razor`

```razor
@using JIM.Models.Staging
@using JIM.Models.Core
@using JIM.Utilities

@if (Value.ReferenceValue != null)
{
    <span class="jim-attr-reference">
        <MudChip T="string" Color="Color.Default" Size="Size.Small" Class="mr-1">
            @Value.ReferenceValue.Type.Name
        </MudChip>
        <MudLink Href="@Utilities.GetConnectedSystemObjectHref(Value.ReferenceValue)">
            @Value.ReferenceValue.DisplayNameOrId
        </MudLink>
        @if (Value.ReferenceValue.SecondaryExternalIdAttributeValue != null)
        {
            <span class="mud-text-secondary ml-1">
                (@Value.ReferenceValue.SecondaryExternalIdAttributeValue.ToStringNoName())
            </span>
        }
    </span>
}
else if (Value.UnresolvedReferenceValue != null)
{
    <span class="jim-attr-unresolved">
        <MudIcon Icon="@Icons.Material.Filled.LinkOff" Size="Size.Small" Class="mr-1" />
        <span class="mud-text-secondary">@Value.UnresolvedReferenceValue</span>
    </span>
}
else if (Value.Attribute.Type == AttributeDataType.DateTime && Value.DateTimeValue.HasValue)
{
    <MudTooltip Text="@Value.DateTimeValue.Value.ToFriendlyDate()">
        <span>@Value.DateTimeValue.Value.ToRelativeTime()</span>
    </MudTooltip>
}
else if (Value.Attribute.Type == AttributeDataType.Boolean && Value.BoolValue.HasValue)
{
    <MudIcon Icon="@(Value.BoolValue.Value ? Icons.Material.Filled.Check : Icons.Material.Filled.Close)"
             Size="Size.Small"
             Color="@(Value.BoolValue.Value ? Color.Success : Color.Default)" />
}
else if (Value.Attribute.Type == AttributeDataType.Binary && Value.ByteValue != null)
{
    <span class="mud-text-secondary">[Binary: @Value.ByteValue.Length bytes]</span>
}
else
{
    @Value.ToStringNoName()
}

@code {
    [Parameter]
    public ConnectedSystemObjectAttributeValue Value { get; set; } = null!;
}
```

### Phase 3: CSS Styling

**File:** `src/JIM.Web/wwwroot/css/site.css`

Add new styles:

```css
/* CSO Attribute Table Styles */
.jim-attribute-table {
    border: 1px solid var(--mud-palette-lines-default);
    border-radius: 8px;
}

.jim-attribute-table th {
    background-color: var(--mud-palette-background-grey);
    font-weight: 600;
    padding: 12px 16px;
}

.jim-attribute-table td {
    padding: 8px 16px;
    vertical-align: top;
}

.jim-attr-name {
    background-color: var(--mud-palette-background);
    border-right: 1px solid var(--mud-palette-lines-default);
    white-space: nowrap;
}

.jim-attr-value {
    word-break: break-word;
}

.jim-attr-row-expanded {
    background-color: var(--mud-palette-background-grey);
}

.jim-attr-reference {
    display: inline-flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 4px;
}

.jim-attr-unresolved {
    display: inline-flex;
    align-items: center;
    color: var(--mud-palette-warning);
}
```

### Phase 4: Code-Behind Logic

**File:** `src/JIM.Web/Pages/Admin/ConnectedSystemObjectDetail.razor` (code section)

Add helper methods:

```csharp
private HashSet<string> _expandedAttributes = new();

private record AttributeGroup(
    string AttributeName,
    List<ConnectedSystemObjectAttributeValue> Values,
    bool IsMultiValued)
{
    public bool IsExpanded { get; set; }
}

private List<AttributeGroup> GetGroupedAttributes()
{
    if (ConnectedSystemObject == null)
        return new List<AttributeGroup>();

    return ConnectedSystemObject.AttributeValues
        .GroupBy(v => v.Attribute.Name)
        .OrderBy(g => g.Key)
        .Select(g => new AttributeGroup(
            g.Key,
            g.ToList(),
            g.First().Attribute.AttributePlurality == AttributePlurality.MultiValued)
        {
            IsExpanded = _expandedAttributes.Contains(g.Key)
        })
        .ToList();
}

private void ToggleExpand(string attributeName)
{
    if (_expandedAttributes.Contains(attributeName))
        _expandedAttributes.Remove(attributeName);
    else
        _expandedAttributes.Add(attributeName);
}
```

## Benefits

1. **Reduced visual noise** - No "Group:" prefix, no "Total Values: 1" footer
2. **Better information density** - More attributes visible without scrolling
3. **Consistent row heights** - Easier to scan
4. **Progressive disclosure** - Multi-valued attributes expand only when needed
5. **Reusable component** - `CsoAttributeValue` can be used elsewhere
6. **Professional appearance** - Clean, modern table design
7. **Accessibility** - Clear visual hierarchy, keyboard navigable

## Success Criteria

- [ ] All attribute types render correctly (Text, Number, DateTime, Boolean, Binary, Reference, Guid, LongNumber)
- [ ] Multi-valued attributes show expand/collapse functionality
- [ ] Reference values link to the referenced CSO
- [ ] Unresolved references show warning indicator
- [ ] Table is responsive on smaller screens
- [ ] Consistent with MVO detail page styling
- [ ] No regression in functionality

## Testing

1. **Visual testing**: Compare before/after screenshots
2. **Functional testing**: Verify all attribute types display correctly
3. **Multi-value testing**: Test expand/collapse with 2+ values
4. **Reference testing**: Verify links navigate correctly
5. **Responsive testing**: Check on mobile/tablet viewports

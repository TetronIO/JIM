# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Publishes a long-form release announcement to GitHub Discussions.

.DESCRIPTION
    Creates a discussion in the target category (default: Announcements) from a
    reviewed markdown body file, using the GitHub GraphQL API (createDiscussion).

    This is the mechanical "post the approved file" step. It does NOT write the
    announcement: the /release skill drafts the body, a human reviews and edits
    it, and only then is this script run. It performs no confirmation of its own
    beyond -WhatIf, so callers must ensure the body has been approved first.

    Requires gh to be authenticated with permission to create discussions, and
    Discussions to be enabled on the repository with the target category present.

.PARAMETER Title
    The discussion title (e.g. "JIM v0.11.0").

.PARAMETER BodyFile
    Path to the reviewed markdown body for the announcement.

.PARAMETER Category
    Discussion category name. Defaults to "Announcements".

.PARAMETER Owner
    Repository owner. Defaults to "TetronIO".

.PARAMETER Repo
    Repository name. Defaults to "JIM".

.EXAMPLE
    ./scripts/Publish-ReleaseDiscussion.ps1 -Title "JIM v0.11.0" -BodyFile /tmp/release-discussion-v0.11.0.md

.EXAMPLE
    ./scripts/Publish-ReleaseDiscussion.ps1 -Title "JIM v0.11.0" -BodyFile /tmp/body.md -WhatIf
    Resolves the repository and category IDs and validates inputs without posting.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Title,
    [Parameter(Mandatory)][string]$BodyFile,
    [string]$Category = 'Announcements',
    [string]$Owner = 'TetronIO',
    [string]$Repo = 'JIM'
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message, [int]$Code = 1) {
    [Console]::Error.WriteLine("ERROR: $Message")
    exit $Code
}

if (-not (Test-Path -LiteralPath $BodyFile)) { Fail "Body file not found: $BodyFile" 2 }
$body = Get-Content -Raw -LiteralPath $BodyFile
if ([string]::IsNullOrWhiteSpace($body)) { Fail "Body file is empty: $BodyFile" 2 }

# Resolve the repository node ID and the target category ID.
$query = 'query($owner:String!,$repo:String!){repository(owner:$owner,name:$repo){id discussionCategories(first:25){nodes{id name}}}}'
$lookupJson = gh api graphql -f "query=$query" -f "owner=$Owner" -f "repo=$Repo"
if ($LASTEXITCODE -ne 0) { Fail "Failed to query repository discussion categories (gh exit $LASTEXITCODE)." }
$lookup = $lookupJson | ConvertFrom-Json

$repoId = $lookup.data.repository.id
if (-not $repoId) { Fail "Could not resolve repository $Owner/$Repo." }

$categoryNode = $lookup.data.repository.discussionCategories.nodes | Where-Object { $_.name -eq $Category } | Select-Object -First 1
if (-not $categoryNode) {
    $available = ($lookup.data.repository.discussionCategories.nodes.name) -join ', '
    Fail "Discussion category '$Category' not found. Available: $available"
}
$categoryId = $categoryNode.id

if (-not $PSCmdlet.ShouldProcess("$Owner/$Repo discussions [$Category]", "Create discussion '$Title'")) {
    Write-Host "WhatIf: would create discussion '$Title' in '$Category' (repoId=$repoId, categoryId=$categoryId)."
    return
}

# Create the discussion.
$mutation = 'mutation($repositoryId:ID!,$categoryId:ID!,$title:String!,$body:String!){createDiscussion(input:{repositoryId:$repositoryId,categoryId:$categoryId,title:$title,body:$body}){discussion{url}}}'
$resultJson = gh api graphql -f "query=$mutation" -f "repositoryId=$repoId" -f "categoryId=$categoryId" -f "title=$Title" -f "body=$body"
if ($LASTEXITCODE -ne 0) { Fail "Failed to create discussion (gh exit $LASTEXITCODE)." }
$result = $resultJson | ConvertFrom-Json

$url = $result.data.createDiscussion.discussion.url
if (-not $url) { Fail "Discussion creation returned no URL. Response: $($result | ConvertTo-Json -Depth 8)" }

Write-Host "Discussion published: $url"

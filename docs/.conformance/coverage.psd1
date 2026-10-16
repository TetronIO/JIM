# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Configuration for scripts/Test-DocsCoverage.ps1: the coverage checks of the documentation
# standard (engineering/DOCUMENTATION_STANDARD.md, "Coverage"; #1454). Each inventory is read
# from source and compared with the docs both ways. Paths are relative to the repository root.
#
# This folder starts with a dot, so MkDocs never publishes it.
@{
    # COV-1: the module manifest's FunctionsToExport, and the pages that document them.
    ModuleManifest      = 'src/JIM.PowerShell/JIM.psd1'
    CmdletDocs          = 'docs/powershell'

    # COV-2: where the product and installer read their environment variables.
    Constants           = 'src/JIM.Models/Core/Constants.cs'
    CodeSources         = @('src')
    PowerShellSources   = @('src/JIM.PowerShell/Public', 'src/JIM.PowerShell/Private')
    ComposeFiles        = @('docker-compose.yml', 'deploy/docker-compose.production.yml')
    EnvExample          = '.env.example'
    Installer           = 'deploy/setup.sh'

    # COV-6: the functions the expression evaluator registers, and their reference.
    ExpressionEvaluator = 'src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs'
    ExpressionDocs      = 'docs/concepts/expressions.md'

    # LNK-3: where the product links into the docs.
    LinkSources         = @('src', 'deploy', 'scripts/Build-ReleaseBundle.ps1', 'docker-compose.yml')
    DocsBaseUrl         = 'https://docs.junctional.io'

    # ACC-3 and COV-4: the API overview whose Resource groups table names every OpenAPI tag. The routes
    # themselves are checked against the OpenAPI document build-and-test generates.
    ApiIndex            = 'docs/api/index.md'

    # COV-5: the portal's Razor components. Every routable page names its docs page from its PageInfo.
    PortalSources       = @('src/JIM.Web')

    # The docs the checks read: the public site, less what leaves it.
    DocsPath            = 'docs'
    DocsExclude         = @('CLAUDE.md', 'reference/changelog.md')

    # Deliberate exceptions, each with the reason it is not documented. An entry that no longer
    # applies (the thing is gone, or now documented) fails the check, so remove it then.
    Allow               = @{
        Cmdlets              = @{}
        EnvironmentVariables = @{
            'JIM_BENCH_API_KEY'          = 'The integration test runner streams its metrics with it; an installation never reads it.'
            'JIM_BENCH_API_URL'          = 'The integration test runner streams its metrics to it; an installation never reads it.'
            'JIM_BUILD_EXTRA_CA_BASE64'  = 'A build argument for building the images from source; an installation never reads it.'
            'JIM_BUILD_HTTPS_PROXY'      = 'A build argument for building the images from source behind a proxy; an installation never reads it.'
            'JIM_BUILD_HTTP_PROXY'       = 'A build argument for building the images from source behind a proxy; an installation never reads it.'
            'JIM_BUILD_NETWORK'          = 'A build argument for building the images from source; an installation never reads it.'
            'JIM_BUILD_NO_PROXY'         = 'A build argument for building the images from source behind a proxy; an installation never reads it.'
            'JIM_OPENAPI_GENERATE'       = 'Build time only: JIM.Web writes its OpenAPI document and exits, for the docs build and the jim.web image.'
            'JIM_OPENAPI_OUTPUT_PATH'    = 'Build time only: where JIM.Web writes its OpenAPI document under JIM_OPENAPI_GENERATE.'
            'JIM_RUNPROFILE_ABORT_SENTINEL' = 'A test harness hook: the integration runner sets it so Start-JIMRunProfile stops waiting when the runner sees an error in the logs.'
        }
        ExpressionFunctions  = @{}
        DocsLinks            = @{}
        ApiRoutes            = @{}
        ApiResourceGroups    = @{}
        # Keyed by the page's path. A page that has a PageInfo but no docs page to name still describes itself.
        PortalPages          = @{
            'src/JIM.Web/Pages/Index.razor'                       = 'The home page: it orients a first-time visitor and leads to pages that each name their own docs page.'
            'src/JIM.Web/Pages/Admin/AdminIndex.razor'            = 'The Administration landing page: it orients a first-time visitor and leads to pages that each name their own docs page.'
            'src/JIM.Web/Pages/Admin/ThemePreview.razor'          = 'A design specimen page for developers, not a feature an administrator uses.'
            'src/JIM.Web/Pages/Admin/LoadingPreview.razor'        = 'A design specimen page for developers, showing the loading states; not a feature an administrator uses.'
            'src/JIM.Web/Pages/Dev/ErrorPagePreview.razor'        = 'A design specimen page for developers, previewing the error pages; not a feature an administrator uses.'
        }
        # Read by the NUnit coverage tests in test/JIM.Worker.Tests/Docs (COV-3 and the Connector settings of
        # COV-6), which need the compiled product to take their inventories. A Connector setting is keyed
        # 'Connector name: setting name'.
        ServiceSettings      = @{}
        ConnectorSettings    = @{}
    }
}

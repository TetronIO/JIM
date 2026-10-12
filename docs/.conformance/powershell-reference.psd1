# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Configuration for scripts/Update-PowerShellReference.ps1, which generates each cmdlet's Syntax,
# Parameters and Output on the docs/powershell/ pages from the module (ACC-5; #1083).
#
# This folder starts with a dot, so MkDocs never publishes it.
@{
    # A cmdlet declares what it returns with [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')], and
    # the script lists that schema's properties from the OpenAPI document. A cmdlet that builds its own
    # object returns a type the API does not have; list it here with the reason, and describe its
    # properties in the cmdlet's .OUTPUTS help instead. An entry no cmdlet declares fails the check.
    NonApiOutputTypes = @{
        'JIM.ConnectionStatus' = 'Test-JIMConnection builds it from the module''s connection and the health endpoint'
    }
}

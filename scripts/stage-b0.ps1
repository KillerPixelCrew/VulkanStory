<#
.SYNOPSIS
Rejects the retired observation-only B0 staging entry.
.DESCRIPTION
Always throws before staging. The current bootstrap needs the complete runtime; use stage-runtime.ps1 with native, shader, and license inputs.
.PARAMETER Configuration
Retained legacy argument; unused because this entry refuses execution.
.PARAMETER OutputDirectory
Retained legacy destination argument; no output directory is created.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
throw 'Observation-only B0 staging is retired: the current bootstrap prepares the full runtime. Use stage-runtime.ps1 with the complete native, shader and license inputs.'

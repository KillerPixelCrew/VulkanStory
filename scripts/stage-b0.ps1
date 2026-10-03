[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
throw 'Observation-only B0 staging is retired: the current bootstrap prepares the full runtime. Use stage-runtime.ps1 with the complete native, shader and license inputs.'

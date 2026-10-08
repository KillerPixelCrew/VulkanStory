<#
.SYNOPSIS
Rejects the retired observation-only B0 validation entry.
.DESCRIPTION
Always throws before builds, tests, staging, deployment, or game launch. Use the current runtime build/stage flow and isolated headless harness.
.PARAMETER Configuration
Retained legacy build configuration; unused by this retired entry.
.PARAMETER RunId
Retained legacy validated run identifier; no validation run is created.
.PARAMETER VintageStoryPath
Retained legacy official-reference path; not read.
.PARAMETER Generator
Retained legacy CMake generator; no native build runs.
.PARAMETER Deploy
Retained legacy deployment switch; no deployment occurs.
.PARAMETER GameDirectory
Retained legacy deployment target; not modified.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId = ('b0-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')),
    [string]$VintageStoryPath,
    [string]$Generator = 'Ninja',
    [switch]$Deploy,
    [string]$GameDirectory
)

$ErrorActionPreference = 'Stop'
throw 'Observation-only B0 validation is retired. Use the current runtime build/stage flow and isolated headless harness.'

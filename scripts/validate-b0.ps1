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

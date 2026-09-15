# =====================================================================
# PrivGvard - Release Pipeline Orchestrator Forwarder
# =====================================================================

param (
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$SkipMsix,

    [switch]$MsixOnly,

    [switch]$SignMsix,

    [string]$CertificateThumbprint
)

$ErrorActionPreference = "Stop"
$RootScript = Join-Path $PSScriptRoot "..\build-installer.ps1"

if (-not (Test-Path -LiteralPath $RootScript)) {
    throw "Root build-installer.ps1 was not found at: $RootScript"
}

& $RootScript @PSBoundParameters
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

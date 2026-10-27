# Starts DiskLens; builds it first (build.ps1) when there is no Release build yet.
# Keep this file ASCII-only (see build.ps1).
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'src\DiskLens\bin\Release\net10.0-windows\DiskLens.exe'
try {
    if (!(Test-Path -LiteralPath $exe)) {
        Write-Host 'First run: building DiskLens, this takes a few minutes...'
        & (Join-Path $PSScriptRoot 'build.ps1')
    }
    Start-Process -FilePath $exe
} catch {
    Write-Host "DiskLens could not start: $_" -ForegroundColor Red
    # Started from Explorer, the window would close before the error can be read
    if (-not $MyInvocation.PSCommandPath) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

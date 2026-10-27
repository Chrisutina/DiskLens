param([switch]$Check)
# Builds the web UI and the Release desktop app. -Check also runs the backend checks and the real WebView2 check.
# Keep this file ASCII-only: Windows PowerShell 5.1 reads a script without a BOM in the system code page,
# where non-ASCII text can break parsing (web/tests/scripts.test.ts checks this).
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'src\DiskLens\bin\Release\net10.0-windows\DiskLens.exe'
# Started on its own from Explorer ("Run with PowerShell"): the console closes when the script ends, so wait first
$ownWindow = -not $MyInvocation.PSCommandPath -and
    (Get-Process -Id (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId -ErrorAction SilentlyContinue).ProcessName -eq 'explorer'
Push-Location $PSScriptRoot
try {
    & pnpm --dir web install --frozen-lockfile
    if ($LASTEXITCODE) { throw 'Installing the web dependencies failed' }
    & pnpm --dir web check
    if ($LASTEXITCODE) { throw 'The web type check failed' }
    & pnpm --dir web test
    if ($LASTEXITCODE) { throw 'The web tests failed' }
    & pnpm --dir web build
    if ($LASTEXITCODE) { throw 'The web build failed' }
    & dotnet build DiskLens.slnx -c Release --nologo
    if ($LASTEXITCODE) { throw 'The desktop build failed' }
    if ($Check) {
        & dotnet run --project tools/DiskLens.Check -c Release --no-build -- all
        if ($LASTEXITCODE) { throw 'The backend checks failed' }
        & dotnet run --project tools/DiskLens.UiCheck -c Release --no-build
        if ($LASTEXITCODE) { throw 'The desktop UI check failed' }
    }
    Write-Output $exe
    if ($ownWindow) { Read-Host 'Build finished. Press Enter to close' | Out-Null }
} catch {
    if ($ownWindow) {
        Write-Host $_ -ForegroundColor Red
        Read-Host 'Build failed. Press Enter to close' | Out-Null
    }
    throw
} finally {
    Pop-Location
}

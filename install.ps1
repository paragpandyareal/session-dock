# Session Dock installer.
#
#   irm https://raw.githubusercontent.com/paragpandyareal/session-dock/main/install.ps1 | iex
#
# Downloads the latest SessionDock.exe from GitHub Releases, checks it against the
# release's SHA256SUMS.txt, installs Node.js with winget if it's missing, then runs
# "SessionDock.exe --install". That copies the exe to %LOCALAPPDATA%\Programs\SessionDock,
# connects it to Claude Code, adds Start menu and startup shortcuts, and starts it.
# Session Dock itself needs no admin rights. Installing Node.js may show a Windows admin prompt.
#
# Everything runs inside a script block, so "| iex" leaves your PowerShell session as it was.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar
    # GitHub needs TLS 1.2. "SystemDefault" (0) already lets Windows pick it, so only add TLS 1.2
    # when the session is pinned to an explicit list without it. This setting is process-wide.
    $proto = [Net.ServicePointManager]::SecurityProtocol
    if ([int]$proto -ne 0 -and -not ($proto -band [Net.SecurityProtocolType]::Tls12)) {
        [Net.ServicePointManager]::SecurityProtocol = $proto -bor [Net.SecurityProtocolType]::Tls12
    }

    $base = 'https://github.com/paragpandyareal/session-dock/releases/latest/download'
    $installedExe = Join-Path $env:LOCALAPPDATA 'Programs\SessionDock\SessionDock.exe'

    function Step($text) { Write-Host "  > $text" -ForegroundColor Cyan }

    function Test-Node {
        (Get-Command node.exe -ErrorAction SilentlyContinue) -or
        (Test-Path "$env:ProgramFiles\nodejs\node.exe") -or
        (Test-Path "$env:LOCALAPPDATA\Programs\nodejs\node.exe")
    }

    Write-Host ''
    Write-Host '  Session Dock installer' -ForegroundColor Yellow
    Write-Host ''

    # 1. Node.js runs the tiny hook script that Claude Code calls.
    if (-not (Test-Node)) {
        if (Get-Command winget.exe -ErrorAction SilentlyContinue) {
            Step 'Installing Node.js, which the Claude Code hook needs (Windows may ask for permission)...'
            winget install --id OpenJS.NodeJS.LTS --exact --silent --accept-package-agreements --accept-source-agreements | Out-Host
            if (-not (Test-Node)) {
                Write-Host ''
                Write-Host '  Node.js did not install. Install it from https://nodejs.org, then run this again.' -ForegroundColor Red
                return
            }
        } else {
            Write-Host '  Node.js is missing and winget is not available.' -ForegroundColor Red
            Write-Host '  Install Node.js from https://nodejs.org, then run this again.'
            return
        }
    }

    # 2. Download and verify.
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("SessionDock-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $exe = Join-Path $temp 'SessionDock.exe'
        Step 'Downloading the latest release...'
        Invoke-WebRequest "$base/SessionDock.exe" -OutFile $exe -UseBasicParsing
        $sums = (Invoke-WebRequest "$base/SHA256SUMS.txt" -UseBasicParsing).Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }

        $line = $sums -split "`n" | Where-Object { $_ -match '^\s*[0-9a-fA-F]{64}\s+\*?SessionDock\.exe\s*$' } | Select-Object -First 1
        $expected = if ($line) { ($line.Trim() -split '\s+')[0].ToLowerInvariant() } else { '' }
        $actual = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            throw "Checksum mismatch for SessionDock.exe (expected '$expected', got '$actual'). Nothing was installed."
        }
        Step "Checksum OK ($($actual.Substring(0, 16))...)"

        # 3. Let the app install itself. It quits a running copy first, so this also upgrades.
        Step 'Installing...'
        # Not -Wait: in Windows PowerShell that also waits for the dock this launches.
        $p = Start-Process -FilePath $exe -ArgumentList '--install' -PassThru -NoNewWindow
        $null = $p.Handle   # keeps ExitCode readable after exit on Windows PowerShell 5.1
        $p.WaitForExit()
        Write-Host ''
        switch ($p.ExitCode) {
            0 {
                Write-Host '  Done. Session Dock is docked on the right edge of your screen.' -ForegroundColor Green
                Write-Host '  Restart any Claude Code sessions that were already open so they show up.'
            }
            2 {
                Write-Host '  Could not put Session Dock in place (see above). Nothing else was changed.' -ForegroundColor Red
                return
            }
            3 {
                Write-Host '  Session Dock is installed but did not start. Start it from the Start menu.' -ForegroundColor Yellow
            }
            default {
                Write-Host '  Session Dock is installed, but connecting it to Claude Code needs attention (see above).' -ForegroundColor Yellow
                Write-Host '  Once fixed, click "Not connected" at the bottom of the dock.'
            }
        }
        Write-Host "  Uninstall any time with: & `"$installedExe`" --uninstall"
        Write-Host ''
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

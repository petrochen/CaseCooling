$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'bin\CaseCooling.Tray.exe'
$monitor = Join-Path $env:ProgramFiles 'CaseCooling\CaseCooling.Monitor.exe'
if (!(Test-Path -LiteralPath $monitor -PathType Leaf)) { throw 'Install the CaseCooling monitor first.' }
$dir = Join-Path $env:LOCALAPPDATA 'CaseCooling'
$null = New-Item -ItemType Directory -Force -Path $dir
$target = Join-Path $dir 'CaseCooling.Tray.exe'
$session = (Get-Process -Id $PID).SessionId
if (!(Test-Path -LiteralPath $target) -or (Get-FileHash $target).Hash -ne (Get-FileHash $source).Hash) {
    Get-Process -Name 'CaseCooling.Tray' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $session -and $_.Path -eq $target } | Stop-Process
    Copy-Item -LiteralPath $source -Destination $target -Force
}
$startup = [Environment]::GetFolderPath('Startup')
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $startup 'CaseCooling.lnk'))
$link.TargetPath = $target
$link.WorkingDirectory = $dir
$link.Save()
Start-Process -FilePath $target -WorkingDirectory $dir -WindowStyle Hidden

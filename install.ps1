#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
if (Get-Service -Name CaseCooling -ErrorAction SilentlyContinue) {
    throw 'CaseCooling already registered. Stop and uninstall the existing version first.'
}
$dest = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CaseCooling'
if (Test-Path -LiteralPath $dest) { throw "Installation folder already exists: $dest" }
$runtime = Join-Path $PSScriptRoot 'bin'
$files = @('CaseCooling.exe','CaseCooling.exe.config','CaseCooling.Monitor.exe','LibreHardwareMonitorLib.dll','HidSharp.dll',
    'BlackSharp.Core.dll','RAMSPDToolkit-NDD.dll','DiskInfoToolkit.dll','System.Memory.dll',
    'System.Buffers.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
    'System.Threading.Tasks.Extensions.dll','Microsoft.Bcl.HashCode.dll','third-party-notices.txt',
    'Fonts\SourceSans3-Regular.otf','Fonts\SourceSans3-Semibold.otf','Fonts\LICENSE.md')
foreach ($name in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $runtime $name) -PathType Leaf)) { throw "Missing $name" }
}
$registered = $false
$null = New-Item -ItemType Directory -Path $dest
try {
    $null=New-Item -ItemType Directory -Path "$dest\Fonts"
    foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination (Join-Path $dest $name) }
    [IO.File]::WriteAllText((Join-Path $dest 'dashboard-data.txt'),$PSScriptRoot)
    $command = '"' + (Join-Path $dest 'CaseCooling.exe') + '" service'
    New-Service -Name CaseCooling -BinaryPathName $command -StartupType Manual -DisplayName 'Case cooling (X570 I)' | Out-Null
    $registered = $true
    & sc.exe failure CaseCooling reset= 86400 actions= restart/1000/restart/2000/restart/5000
    if ($LASTEXITCODE) { throw 'Could not configure service recovery.' }
    & sc.exe failureflag CaseCooling 1
    if ($LASTEXITCODE) { throw 'Could not configure failure handling.' }
} catch {
    if ($registered) { & sc.exe delete CaseCooling | Out-Null }
    foreach ($name in @($files)+@('dashboard-data.txt')) {
        $file = Join-Path $dest $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    if ((Test-Path -LiteralPath "$dest\Fonts") -and !(Get-ChildItem -LiteralPath "$dest\Fonts" -Force)) {Remove-Item -LiteralPath "$dest\Fonts"}
    if (!(Get-ChildItem -LiteralPath $dest -Force)) { Remove-Item -LiteralPath $dest }
    throw
}
Write-Output 'Installed in manual-start mode. The service is stopped.'

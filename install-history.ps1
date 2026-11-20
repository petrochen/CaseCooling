#Requires -RunAsAdministrator
$ErrorActionPreference='Stop'
$bin=Join-Path $PSScriptRoot 'bin'
$dest=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CaseCooling'
$controller=Get-Service CaseCooling
if (!(Test-Path -LiteralPath "$dest\CaseCooling.exe")) { throw 'Install CaseCooling first.' }
function Copy-BuildFile([string]$name) {
    $source=Join-Path $bin $name
    $target=Join-Path $dest $name
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash $target).Hash -eq (Get-FileHash $source).Hash) { return }
    $temp=Join-Path $dest ('.install-'+[guid]::NewGuid().ToString('N'))
    try {
        Copy-Item -LiteralPath $source -Destination $temp
        if (Test-Path -LiteralPath $target) {[IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value)}
        else {[IO.File]::Move($temp,$target)}
    } finally {if (Test-Path -LiteralPath $temp) {Remove-Item -LiteralPath $temp}}
}
$history=Get-Service CaseCoolingHistory -ErrorAction SilentlyContinue
if ($history -and $history.Status -ne 'Stopped') {Stop-Service CaseCoolingHistory}
if ($controller.Status -eq 'Stopped') {Copy-BuildFile 'CaseCooling.exe'}
$null=New-Item -ItemType Directory "$dest\Fonts" -Force
foreach($name in 'CaseCooling.History.exe','CaseCooling.History.exe.config','CaseCooling.Monitor.exe','streamcam_raw.exe',
        'Fonts\SourceSans3-Regular.otf','Fonts\SourceSans3-Semibold.otf','Fonts\LICENSE.md') {Copy-BuildFile $name}
[IO.File]::WriteAllText("$dest\dashboard-data.txt",$PSScriptRoot)
if (!(Test-Path -LiteralPath "$dest\history-1s.bin")) {
    $sessions=Join-Path (Split-Path $PSScriptRoot) 'cooling-logger\sessions'
    $logs=@(Get-ChildItem "$sessions\*\metrics.csv" -ErrorAction SilentlyContinue | Where-Object LastWriteTimeUtc -gt ([datetime]::UtcNow.AddDays(-1)) | Sort-Object FullName | ForEach-Object FullName)
    if ($logs.Count) {& "$dest\CaseCooling.History.exe" seed @logs; if ($LASTEXITCODE) {throw 'History import failed.'}}
}
if (!$history) {
    $null=New-Service -Name CaseCoolingHistory -BinaryPathName ('"'+$dest+'\CaseCooling.History.exe"') -StartupType Automatic -DisplayName 'CaseCooling sensor history'
}
& sc.exe failure CaseCoolingHistory reset= 86400 actions= restart/3000/restart/5000/restart/15000
if ($LASTEXITCODE) {throw 'Service recovery failed.'}
Start-Service CaseCoolingHistory
Get-Service CaseCoolingHistory

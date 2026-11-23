$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\bin\CaseCooling.exe" self-test
if ($LASTEXITCODE) { throw "Controller self-test failed ($LASTEXITCODE)." }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$check = Join-Path $PSScriptRoot '.build\RingCheck.exe'
& $compiler /nologo "/out:$check" "$PSScriptRoot\history\Ring.cs" "$PSScriptRoot\history\Sample.cs" "$PSScriptRoot\tests\RingCheck.cs"
if ($LASTEXITCODE) { throw "History test compilation failed ($LASTEXITCODE)." }
& $check
if ($LASTEXITCODE) { throw "History test failed ($LASTEXITCODE)." }
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$plotCheck = Join-Path $PSScriptRoot '.build\PlotCheck.exe'
$ui = (Get-ChildItem "$PSScriptRoot\dashboard" -Filter '*.cs').FullName
& $compiler /nologo /platform:x64 /main:PlotCheck "/out:$plotCheck" /r:System.Xaml.dll /r:System.ServiceProcess.dll "/r:$wpf\WindowsBase.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" $ui "$PSScriptRoot\history\Ring.cs" "$PSScriptRoot\history\Sample.cs" "$PSScriptRoot\tests\PlotCheck.cs"
if ($LASTEXITCODE) { throw "Plot test compilation failed ($LASTEXITCODE)." }
& $plotCheck
if ($LASTEXITCODE) { throw "Plot test failed ($LASTEXITCODE)." }

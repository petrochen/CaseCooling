$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\bin\CaseCooling.exe" self-test
if ($LASTEXITCODE) { throw "Controller self-test failed ($LASTEXITCODE)." }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$check = Join-Path $PSScriptRoot '.build\RingCheck.exe'
& $compiler /nologo "/out:$check" "$PSScriptRoot\history\Ring.cs" "$PSScriptRoot\history\Sample.cs" "$PSScriptRoot\tests\RingCheck.cs"
if ($LASTEXITCODE) { throw "History test compilation failed ($LASTEXITCODE)." }
& $check
if ($LASTEXITCODE) { throw "History test failed ($LASTEXITCODE)." }

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$bin = Join-Path $PSScriptRoot 'bin'
$cache = Join-Path $PSScriptRoot '.build'
$null = New-Item -ItemType Directory -Force -Path $bin,$cache
# Use the exact .NET Framework hardware-library distribution used for calibration.
$zip = Join-Path $cache 'FanControl_281_net_4_8.zip'
$hash = '2958D45E1B43A298B0251EF9FDEFAC35236F7B5A8293CC49556E64878D7B40A1'
if (!(Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip).Hash -ne $hash) {
    Invoke-WebRequest -UseBasicParsing 'https://github.com/Rem0o/FanControl.Releases/releases/download/V281/FanControl_281_net_4_8.zip' -OutFile $zip
}
if ((Get-FileHash -LiteralPath $zip).Hash -ne $hash) { throw 'Hardware dependency download failed SHA-256 verification.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in 'LibreHardwareMonitorLib.dll','HidSharp.dll','BlackSharp.Core.dll','DiskInfoToolkit.dll',
        'RAMSPDToolkit-NDD.dll','System.Memory.dll','System.Buffers.dll','System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll','Microsoft.Bcl.HashCode.dll') {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($name),(Join-Path $bin $name),$true)
    }
    $notice = $archive.Entries | Where-Object Name -eq 'EULA-Licenses.txt' | Select-Object -First 1
    [IO.Compression.ZipFileExtensions]::ExtractToFile($notice,(Join-Path $bin 'third-party-notices.txt'),$true)
} finally { $archive.Dispose() }
Copy-Item -LiteralPath "$PSScriptRoot\assets\CaseCooling.exe.config" -Destination $bin -Force
$fonts = New-Item -ItemType Directory -Force -Path "$bin\Fonts"
Get-ChildItem "$PSScriptRoot\assets\Fonts" -File | Copy-Item -Destination $fonts.FullName -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /optimize+ /platform:x64 /target:winexe "/out:$bin\CaseCooling.Tray.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\tray\Program.cs"
if ($LASTEXITCODE) { throw "Tray compiler exit $LASTEXITCODE" }
$files = (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName
& $compiler /nologo /optimize+ /platform:x64 /target:exe "/out:$bin\CaseCooling.exe" /r:System.Management.dll /r:System.ServiceProcess.dll "/r:$bin\LibreHardwareMonitorLib.dll" "/r:$bin\HidSharp.dll" $files
if ($LASTEXITCODE) { throw "Compiler exit $LASTEXITCODE" }
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$ui = (Get-ChildItem (Join-Path $PSScriptRoot 'dashboard') -Filter '*.cs').FullName
$shared=@("$PSScriptRoot\history\Ring.cs","$PSScriptRoot\history\Sample.cs")
& $compiler /nologo /optimize+ /platform:x64 /target:winexe "/out:$bin\CaseCooling.Monitor.exe" /r:System.Xaml.dll /r:System.ServiceProcess.dll "/r:$wpf\WindowsBase.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" $ui $shared
if ($LASTEXITCODE) { throw "Dashboard compiler exit $LASTEXITCODE" }
& $compiler /nologo /optimize+ /platform:x64 "/out:$bin\CaseCooling.History.exe" /r:System.Management.dll /r:System.ServiceProcess.dll "/r:$bin\LibreHardwareMonitorLib.dll" "/r:$bin\HidSharp.dll" "$PSScriptRoot\history\Recorder.cs" "$PSScriptRoot\dashboard\Telemetry.cs" "$PSScriptRoot\Hardware.cs" "$PSScriptRoot\Policy.cs" $shared
if ($LASTEXITCODE) { throw "History compiler exit $LASTEXITCODE" }
Copy-Item -LiteralPath "$bin\CaseCooling.exe.config" -Destination "$bin\CaseCooling.History.exe.config" -Force
& "$PSScriptRoot\native\build.cmd"
if ($LASTEXITCODE) { throw "StreamCam compiler exit $LASTEXITCODE" }

@echo off
setlocal
if defined VCToolsInstallDir goto compile
set "locator=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%locator%" goto missing
for /f "usebackq delims=" %%i in (`"%locator%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "vsRoot=%%i"
if not defined vsRoot goto missing
call "%vsRoot%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
:compile
pushd "%~dp0.."
cl /nologo /std:c++17 /O2 /EHsc /MT /W4 /Fo".build\streamcam_raw.obj" /Fe"bin\streamcam_raw.exe" native\streamcam_raw.cpp ole32.lib uuid.lib
set "result=%errorlevel%"
popd
exit /b %result%
:missing
echo Install Visual Studio C++ Build Tools with the Windows SDK or run from an x64 Native Tools prompt.
exit /b 1

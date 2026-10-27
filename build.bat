@echo off
setlocal

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "OUT=%ROOT%bin"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "PF86=%ProgramFiles(x86)%"
set "FAC=%PF86%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\System.Runtime.dll"
set "NET48=%PF86%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
set "WRT=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll"
set "WRTI=%PF86%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\System.Runtime.InteropServices.WindowsRuntime.dll"
set "WINMD="

for /d %%D in ("%PF86%\Windows Kits\10\UnionMetadata\10.*") do set "WINMD=%%D\Windows.winmd"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Install .NET Framework 4.8.
    exit /b 1
)
if not exist "%WRT%" (
    echo [ERROR] System.Runtime.WindowsRuntime.dll not found.
    exit /b 1
)
if not exist "%WINMD%" (
    echo [ERROR] Windows.winmd not found. Install Windows SDK.
    exit /b 1
)
if not exist "%FAC%" (
    echo [ERROR] .NET Framework 4.8 Facades\System.Runtime.dll not found.
    exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:"%OUT%\PeripheralBattery.exe" /win32manifest:"%SRC%\app.manifest" /win32icon:"%SRC%\app.ico" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%NET48%\WindowsBase.dll" /r:"%NET48%\PresentationCore.dll" /r:"%NET48%\PresentationFramework.dll" /r:"%NET48%\System.Xaml.dll" /r:"%WRT%" /r:"%WRTI%" /r:"%FAC%" /r:"%WINMD%" "%SRC%\*.cs"

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo [OK] %OUT%\PeripheralBattery.exe

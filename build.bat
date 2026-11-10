@echo off
rem Rebuilds NetWatch from the source files in the "src" folder.
rem Needs nothing extra: it uses the C# compiler that is part of every Windows 10/11 installation.
rem Result:  bin\NetWatch.exe   (the program itself)
rem          NetWatchSetup.exe  (the installer, which contains the program)

setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo The .NET Framework 4 compiler was not found at %CSC%
    exit /b 1
)
if not exist bin mkdir bin

echo Building bin\NetWatch.exe ...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /unsafe /warn:4 ^
    /out:bin\NetWatch.exe ^
    /win32icon:assets\NetWatch.ico /win32manifest:src\App\app.manifest ^
    /resource:assets\NetWatch.ico,NetWatch.ico ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:Microsoft.CSharp.dll ^
    src\App\*.cs src\Shared\*.cs
if errorlevel 1 exit /b 1

echo Building NetWatchSetup.exe ...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
    /out:NetWatchSetup.exe ^
    /win32icon:assets\NetWatch.ico /win32manifest:src\Setup\setup.manifest ^
    /resource:assets\NetWatch.ico,NetWatch.ico ^
    /resource:bin\NetWatch.exe,NetWatch.Payload.exe ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll ^
    src\Setup\*.cs src\Shared\*.cs
if errorlevel 1 exit /b 1

echo Done.
endlocal

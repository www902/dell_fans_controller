@echo off
rem Build dell_fans_controller.exe with the .NET Framework csc.exe (no Visual Studio needed).
rem Output: dell_fans_controller.exe in this folder. Copy ipmitool.exe next to it before running.
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - install .NET Framework 4.x
    exit /b 1
)

echo [1/3] tools\mkres.exe
"%CSC%" /nologo /target:exe /codepage:65001 /out:tools\mkres.exe tools\mkres.cs || exit /b 1

echo [2/3] FormMain.resources
tools\mkres.exe dell_fans_controller\dell_fans_controller\FormMain.resx dell_fans_controller\dell_fans_controller\FormMain.resources || exit /b 1

echo [3/3] dell_fans_controller.exe
"%CSC%" "@build.rsp" || exit /b 1

echo Done: dell_fans_controller.exe
echo Remember: copy ipmitool.exe and its cygwin dlls into the same folder as the exe.
endlocal

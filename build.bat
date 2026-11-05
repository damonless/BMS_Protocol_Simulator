@echo off
echo ===================================================================
echo   Compiling BMS Protocol Simulator (Standalone Executable)
echo ===================================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% (
    set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

set ICON_FLAG=
if exist "%~dp0app.ico" (
    set ICON_FLAG=/win32icon:"%~dp0app.ico"
)

%CSC% /target:winexe /platform:anycpu /optimize+ %ICON_FLAG% /out:"%~dp0BMS_Protocol_Simulator.exe" "%~dp0BmsDataModel.cs" "%~dp0BmsProtocols.cs" "%~dp0SerialCommManager.cs" "%~dp0MainForm.cs"

if %ERRORLEVEL% equ 0 (
    echo [SUCCESS] Build completed! Output: %~dp0BMS_Protocol_Simulator.exe
) else (
    echo [ERROR] Build failed!
)

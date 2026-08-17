@echo off
echo ===================================================================
echo   Compiling BMS Protocol Simulator (Standalone Executable)
echo ===================================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% (
    set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

%CSC% /target:winexe /platform:anycpu /optimize+ /out:"%~dp0BMS_Protocol_Simulator.exe" "%~dp0BmsDataModel.cs" "%~dp0BmsProtocols.cs" "%~dp0SerialCommManager.cs" "%~dp0MainForm.cs"

if %ERRORLEVEL% equ 0 (
    echo [SUCCESS] Build completed! Output: %~dp0BMS_Protocol_Simulator.exe
    copy /Y "%~dp0BMS_Protocol_Simulator.exe" "%~dp0..\BMS_Protocol_Simulator.exe" >nul
) else (
    echo [ERROR] Build failed!
)

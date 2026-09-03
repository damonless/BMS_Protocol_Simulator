@echo off
echo ===================================================================
echo   Compiling and Running CAN Protocol and Driver Regression Tests
echo ===================================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% (
    set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

%CSC% /nologo /target:exe /out:"%~dp0CanProtocolTest.exe" "%~dp0CanProtocolTest.cs" "%~dp0BmsCanProtocol.cs" "%~dp0CanCommManager.cs" "%~dp0BmsDataModel.cs"

if %ERRORLEVEL% equ 0 (
    echo [RUN] Executing Test Suite...
    "%~dp0CanProtocolTest.exe"
) else (
    echo [ERROR] Test compilation failed!
)

@echo off
cd /d "%~dp0"
set "cncExe=artifacts\app\VirtualCnc.App.exe"
if exist "VirtualCNC.exe" set "cncExe=VirtualCNC.exe"
if "%~1"=="--port" goto console
if "%~1"=="--tcp" goto console
if "%~1"=="--stdio" goto console
if "%~1"=="--help" goto console
if "%~1"=="--list-ports" goto console
if exist "%cncExe%" (
    start "" "%cncExe%" %*
) else (
    dotnet run --project "src\VirtualCnc.App" -c Release -- %*
)
exit /b
:console
if exist "%cncExe%" (
    "%cncExe%" %*
) else (
    dotnet run --project "src\VirtualCnc.App" -c Release -- %*
)

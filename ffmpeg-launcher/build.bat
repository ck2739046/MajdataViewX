@echo off
setlocal

REM Builds FFmpegLauncher.dll and copies it into the Unity project.
REM Requires CMake and a MSVC toolchain (run from a Visual Studio Developer prompt
REM or any shell where cl.exe is on PATH).

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "BUILD=%ROOT%\build"
set "OUT=%ROOT%\..\Assets\Plugins\x86_64"

cmake -S "%ROOT%" -B "%BUILD%" -A x64
if errorlevel 1 exit /b 1

cmake --build "%BUILD%" --config Release
if errorlevel 1 exit /b 1

set "DLL=%BUILD%\Release\FFmpegLauncher.dll"
if not exist "%DLL%" (
    echo FFmpegLauncher.dll not found under build\Release
    exit /b 1
)

copy /Y "%DLL%" "%OUT%\FFmpegLauncher.dll"
if errorlevel 1 exit /b 1

echo.
echo FFmpegLauncher.dll copied to %OUT%
endlocal

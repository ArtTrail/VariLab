@echo off
echo.
echo  VariLab -- building release...
echo.
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  ERROR: .NET SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)
dotnet publish "%~dp0VariLab.csproj" -c Release -r win-x64 --self-contained true --verbosity minimal
if %errorlevel% neq 0 (
    echo.
    echo  Build failed -- see errors above.
    pause
    exit /b 1
)
echo.
echo  Done! Distributable output is in:
echo    %~dp0bin\Release\net8.0\win-x64\publish\
echo.
echo  Copy that folder to any Windows x64 machine and run VariLab.exe
echo  No .NET installation required on the target machine.
echo.
echo  Includes the bundled PSF-fit engine (PsfEngine\) -- Python itself still
echo  needs to be set up once per machine via the in-app PSF Engine Setup.
echo.
start "" "%~dp0bin\Release\net8.0\win-x64\publish\"
pause

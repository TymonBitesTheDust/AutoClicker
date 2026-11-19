@echo off
rem Rebuilds AutoClicker.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the .NET Framework C# compiler on this PC.
    pause
    exit /b 1
)

set "ICON="
if exist app.ico set "ICON=/win32icon:app.ico"

"%CSC%" /nologo /target:winexe /optimize+ %ICON% /out:AutoClicker.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll AutoClicker.cs
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo Built AutoClicker.exe
pause

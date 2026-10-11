@echo off
setlocal
cd /d "%~dp0"
set RC=0

rem Uses the C# compiler that ships with Windows (.NET Framework 4.x). Nothing to install.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

if not exist dist mkdir dist
if exist dist\HomeCloudConnect.exe del /q dist\HomeCloudConnect.exe
if exist dist\HomeCloudConnect-Setup.exe del /q dist\HomeCloudConnect-Setup.exe

echo [1/2] Building HomeCloudConnect.exe ...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:dist\HomeCloudConnect.exe /win32icon:src\app.ico /win32manifest:src\app.manifest /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll src\HomeCloudConnect.cs
if errorlevel 1 goto failed

echo [2/2] Building HomeCloudConnect-Setup.exe ...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:dist\HomeCloudConnect-Setup.exe /win32icon:src\app.ico /win32manifest:src\app.manifest /resource:dist\HomeCloudConnect.exe,app.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\Installer.cs
if errorlevel 1 goto failed

echo.
echo DONE. Files are in the "dist" folder:
echo   dist\HomeCloudConnect.exe        - the program itself (portable, no install needed)
echo   dist\HomeCloudConnect-Setup.exe  - the installer
goto end

:nocsc
echo ERROR: csc.exe was not found. It is part of .NET Framework 4.x, which is built into Windows 10 and 11.
echo Enable it in: Control Panel - Programs - Turn Windows features on or off - .NET Framework 4.8 Advanced Services.
set RC=1
goto end

:failed
echo.
echo BUILD FAILED. Read the error messages above - they name the file and line number.
set RC=1
goto end

:end
if /i not "%~1"=="nopause" pause
exit /b %RC%

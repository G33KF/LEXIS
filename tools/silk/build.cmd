@echo off
rem Builds silk.dll (the SILK SDK decoder, x64) from silk\src, and silk_encoder.exe (tests only: makes the SILK fixture).
rem Source: github.com/kn007/silk-v3-decoder at 507be6bca8ce1fb977a061481f1d79e8c610e309 (MIT; SILK SDK (c) Skype Limited, BSD).
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VS=%%i
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /O2 /LD /DNDEBUG /Isilk\interface /Isilk\src silk\src\*.c /Feout\silk.dll /Foout\ /link /DEF:silk.def || exit /b 1
cl /nologo /O2 /DNDEBUG /Isilk\interface /Isilk\src silk\test\Encoder.c silk\src\*.c /Feout\silk_encoder.exe /Foout\ || exit /b 1
del out\*.obj out\*.exp 2>nul
copy /y out\silk.dll ..\..\src\AleappReader.Core\native\silk.dll >nul

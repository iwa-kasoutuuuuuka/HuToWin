@echo off
setlocal
cd /d "%~dp0"
set CSC="C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
if not exist build mkdir build
%CSC% -nologo -target:winexe -out:build\AlienCrushPC.exe -optimize+ -platform:anycpu -langversion:latest ^
  -r:%FW%\System.dll -r:%FW%\System.Core.dll -r:%FW%\System.Drawing.dll -r:%FW%\System.Windows.Forms.dll ^
  src\Core\Cpu.cs src\Core\Vdc.cs src\Core\Psg.cs src\Core\Machine.cs ^
  src\App\Program.cs src\App\WaveOut.cs src\App\GameForm.cs src\App\Gamepad.cs src\App\ConverterForm.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK: build\AlienCrushPC.exe

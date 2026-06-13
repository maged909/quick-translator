@echo off
rem Rebuild QuickTranslator.exe with the in-box .NET Framework compiler (no SDK needed).
rem OCR uses the offline Windows OCR engine via the per-namespace WinRT metadata.
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set WM=C:\Windows\System32\WinMetadata
set SPEECH=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll
"%CSC%" /target:winexe /out:"%~dp0QuickTranslator.exe" /win32icon:"%~dp0Translator.ico" /codepage:65001 /nologo /optimize+ ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:"%SPEECH%" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Graphics.winmd" /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Globalization.winmd" ^
  "%~dp0QuickTranslator.cs"
echo Exit %ERRORLEVEL%
pause

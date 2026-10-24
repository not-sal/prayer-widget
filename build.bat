@echo off
rem Builds PrayerWidget.exe with the C# compiler that ships with Windows (.NET Framework 4.8). Nothing to install.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ ^
  /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:PrayerWidget.exe PrayerWidget.cs
if errorlevel 1 (pause & exit /b 1)
echo Built PrayerWidget.exe

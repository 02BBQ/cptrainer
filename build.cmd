@echo off
rem Builds gdpt-helper.exe with the C# compiler that ships with Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /target:exe /out:"%~dp0gdpt-helper.exe" "%~dp0helper\gdpt-helper.cs"

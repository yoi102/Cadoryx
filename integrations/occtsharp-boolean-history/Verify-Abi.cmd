@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /std:c11 /Zs /TC /MD /I"C:\Users\yoiri\source\repos\OcctSharp\OcctSharp\src\OcctSharp.Native\include" "%~dp0boolean-abi.c"
if errorlevel 1 exit /b 1
cl /nologo /std:c11 /Zs /TC /MDd /D_DEBUG /I"C:\Users\yoiri\source\repos\OcctSharp\OcctSharp\src\OcctSharp.Native\include" "%~dp0boolean-abi.c"
exit /b %errorlevel%

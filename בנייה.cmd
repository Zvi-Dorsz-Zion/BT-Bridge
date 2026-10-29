@echo off
chcp 65001 >nul
rem בונה את שני הצדדים ומוציא לתיקייה הזו: BtBridge.exe (Windows) ו-BtBridge.apk (Android).
rem Android לא בונה מנתיב עם עברית - מעתיקים לנתיב באנגלית, בונים, ומחזירים את ה-APK לכאן.
rem לפני כל גרסת אנדרואיד חדשה: להגדיל את versionCode בקובץ android\app\build.gradle.kts
set "ROOT=%~dp0"
set "DST=C:\src\btbridge-build"

echo === Windows ===
dotnet publish "%ROOT%windows\BtBridge\BtBridge.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%ROOT%windows\publish" -nologo -v q
if errorlevel 1 (echo בניית Windows נכשלה & exit /b 1)
copy /Y "%ROOT%windows\publish\BtBridge.exe" "%ROOT%BtBridge.exe" >nul
if errorlevel 1 (echo BtBridge.exe פתוח עכשיו ולכן לא הוחלף. סגור אותו: ליד השעון - יציאה, ואז הרץ שוב. הגרסה החדשה נמצאת ב-windows\publish & exit /b 1)

echo === Android ===
robocopy "%ROOT%android\app" "%DST%\app" /MIR /XD build /NFL /NDL /NJH /NJS >nul
robocopy "%ROOT%android\signing" "%DST%\signing" /MIR /NFL /NDL /NJH /NJS >nul
for %%f in (build.gradle.kts settings.gradle.kts gradle.properties local.properties) do copy /Y "%ROOT%android\%%f" "%DST%\%%f" >nul
rem ה-Java של Android Studio, ותיקייה זמנית קצרה (אחרת Java בווינדוס נכשל ב-loopback)
set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
set "JAVA_TOOL_OPTIONS=-Djdk.net.unixdomain.tmpdir=C:\Windows\Temp"
call "%USERPROFILE%\.gradle\wrapper\dists\gradle-8.13-bin\5xuhj0ry160q40clulazy9h7d\gradle-8.13\bin\gradle.bat" -p "%DST%" assembleRelease %*
if errorlevel 1 (echo בניית Android נכשלה & exit /b 1)
copy /Y "%DST%\app\build\outputs\apk\release\app-release.apk" "%ROOT%BtBridge.apk" >nul

echo.
echo מוכן:
echo   %ROOT%BtBridge.exe
echo   %ROOT%BtBridge.apk

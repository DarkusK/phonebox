# Builds the locally patched PhoneBox (tablet APK + PC agent) from C:\Users\darku\src\phonebox and installs both.
# Usage: powershell -ExecutionPolicy Bypass -File deploy-phonebox.ps1 [-SkipApk] [-SkipAgent]
param([switch]$SkipApk, [switch]$SkipAgent)
# not 'Stop': Windows PowerShell turns any stderr line of gradle/dotnet/adb (warnings too) into an error; failures are
# checked through $LASTEXITCODE instead
$ErrorActionPreference = 'Continue'
$src = "$env:USERPROFILE\src\phonebox"
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.21.102-hotspot'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:Path = "$env:JAVA_HOME\bin;$env:Path"

if (-not $SkipApk) {
    Push-Location "$src\android"
    & .\gradlew.bat assembleRelease --no-daemon -q 2>&1 | Where-Object { $_ -notmatch 'SDK XML' }
    if ($LASTEXITCODE) { throw "APK build failed ($LASTEXITCODE)" }
    Pop-Location
}
if (-not $SkipAgent) {
    & 'C:\Program Files\dotnet\dotnet.exe' publish "$src\agent\PhoneBox.Agent" -c Release -f net10.0-windows10.0.19041.0 -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -o "$env:USERPROFILE\src\phonebox-agent-build" --nologo -v q --disable-build-servers
    if ($LASTEXITCODE) { throw "agent build failed ($LASTEXITCODE)" }

    # the agent must be fully gone before its exe can be replaced
    $app = "$env:LOCALAPPDATA\PhoneBox\app"
    Stop-Process -Name PhoneBoxAgent -Force -ErrorAction SilentlyContinue
    Wait-Process -Name PhoneBoxAgent -Timeout 20 -ErrorAction SilentlyContinue
    $copied = $false
    for ($i = 0; $i -lt 15 -and -not $copied; $i++) {
        try { Copy-Item "$env:USERPROFILE\src\phonebox-agent-build\PhoneBoxAgent.exe", "$env:USERPROFILE\src\phonebox-agent-build\PhoneBoxAgent.pdb" $app -Force -ErrorAction Stop; $copied = $true }
        catch { Start-Sleep 1 }
    }
    Start-Process "$app\PhoneBoxAgent.exe" -WorkingDirectory $app
    if (-not $copied) { throw 'could not replace PhoneBoxAgent.exe; the previous agent was restarted' }
    'agent installed'
}
# The agent works with adb as it starts, which can leave the adb server stuck (the panel then says the PC is not
# answering): after any install, start adb fresh. Not Start-Process -Wait: that waits for the adb daemon it starts
# too, which keeps running, so it never returns.
Get-Process adb -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$srv = Start-Job { adb start-server 2>&1 }
Wait-Job $srv -Timeout 20 | Out-Null
Start-Sleep 6
'adb restarted'
if (-not $SkipApk) {
    $apk = "$src\android\app\build\outputs\apk\release\app-release.apk"
    # the install gets a time limit
    $job = Start-Job { adb install -r $using:apk 2>&1 }
    if (Wait-Job $job -Timeout 120) { Receive-Job $job } else { Stop-Job $job; throw 'adb install timed out (try: adb kill-server)' }
    adb shell monkey -p net.oxog.phonebox.deskpanel -c android.intent.category.LAUNCHER 1 | Out-Null
    'APK installed'
}

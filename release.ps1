# Builds the downloads of a HeMiDock release - the same on a PC and in the GitHub workflow (.github/workflows/release.yml):
#   build\dist\HeMiDock-Setup.exe              the installer (Inno Setup 6): x64, and ARM64 on Windows on ARM; it gets
#                                              the .NET and Windows App runtimes the app needs
#   build\dist\HeMiDock-Portable.zip           everything inside, runs from any folder (x64)
#   build\dist\HeMiDock-Portable-arm64.zip     the same for Windows on ARM
# Needs: the .NET 10 SDK and Inno Setup 6. The version comes from <Version> in src\HeMiDock.csproj.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content src\HeMiDock.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in src\HeMiDock.csproj.' }
$iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup).' }

$dist = Join-Path $root 'build\dist'
$work = Join-Path $root 'build\pack'
Remove-Item $dist, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist, $work | Out-Null
Write-Host "HeMiDock $version" -ForegroundColor Cyan

# the apps for the installer, x64 and ARM64 (they need the .NET 10 Runtime and the Windows App Runtime 2.6 of their
# processor - the installer gets them); one installer puts the right one on the PC
foreach ($rid in 'win-x64', 'win-arm64') {
    dotnet publish src\HeMiDock.csproj -c Release -r $rid -o "$work\app-$rid" -v q -nologo
    if ($LASTEXITCODE) { throw "The build of the app failed ($rid)." }
}
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$work\app-win-x64" "/DSourceDirArm=$work\app-win-arm64" "/DOutputDir=$dist" (Join-Path $root 'installer\HeMiDock.iss')
if ($LASTEXITCODE) { throw 'The installer could not be made.' }

# the portable apps: .NET and the Windows App Runtime inside; the files right in the zip, no folder around them
foreach ($rid in 'win-x64', 'win-arm64') {
    dotnet publish src\HeMiDock.csproj -c Release -r $rid -p:Portable=true -o "$work\portable-$rid" -v q -nologo
    if ($LASTEXITCODE) { throw "The portable build failed ($rid)." }
    $zip = if ($rid -eq 'win-x64') { "$dist\HeMiDock-Portable.zip" } else { "$dist\HeMiDock-Portable-arm64.zip" }
    Compress-Archive -Path "$work\portable-$rid\*" -DestinationPath $zip
}
Remove-Item $work -Recurse -Force
Get-ChildItem $dist | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

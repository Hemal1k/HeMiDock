# Builds the downloads of a HeMiDock release – the same on a PC and in the GitHub workflow (.github/workflows/release.yml):
#   build\dist\HeMiDock-Setup.exe      the installer (Inno Setup 6), for the app that needs the .NET and Windows App runtimes
#   build\dist\HeMiDock-Portable.zip   everything inside, runs from any folder
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

# the app for the installer (it needs the .NET 10 Runtime and the Windows App Runtime 2.6 – the installer gets them)
dotnet publish src\HeMiDock.csproj -c Release -r win-x64 -o "$work\app" -v q -nologo
if ($LASTEXITCODE) { throw 'The build of the app failed.' }
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$work\app" "/DOutputDir=$dist" (Join-Path $root 'installer\HeMiDock.iss')
if ($LASTEXITCODE) { throw 'The installer could not be made.' }

# the portable app: .NET and the Windows App Runtime inside, one folder "HeMiDock Portable" in the zip
dotnet publish src\HeMiDock.csproj -c Release -r win-x64 -p:Portable=true -o "$work\HeMiDock Portable" -v q -nologo
if ($LASTEXITCODE) { throw 'The portable build failed.' }
Compress-Archive "$work\HeMiDock Portable" -DestinationPath "$dist\HeMiDock-Portable.zip"

Remove-Item $work -Recurse -Force
Get-ChildItem $dist | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

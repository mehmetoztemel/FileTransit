<#
  FileTransit yayın betiği. Proje kökünde:  powershell -ExecutionPolicy Bypass -File build\publish.ps1
  Üretir (artifacts\release\<sürüm>\):
    FileTransit-<sürüm>-win-x64.exe   kurulumsuz tek dosya (.NET içinde)
    FileTransit-<sürüm>-Kurulum.zip   ClickOnce kurulumu (setup.exe), kullanıcıya özel, yönetici izni yok
    KURULUM.txt, SHA256SUMS.txt
  Yeni sürüm dağıtmadan önce FileTransit.csproj içindeki <Version> artırılmalıdır; aynı sürüm mevcut kurulumu güncellemez.
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$project = Get-Content (Join-Path $root 'FileTransit.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw 'FileTransit.csproj içinde <Version> bulunamadı.' }
Write-Host "FileTransit $version yayımlanıyor" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '1/4 Sunucusuz testler' -ForegroundColor Cyan
    dotnet run --project Tests\FileTransit.Tests.csproj -c Release -- (Join-Path $root 'artifacts') --ui-only | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'Testler başarısız; yayın durduruldu.' }
}

$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host '2/4 Tek EXE (TekExe profili)' -ForegroundColor Cyan
dotnet publish FileTransit.csproj -c Release -p:PublishProfile=TekExe -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Tek EXE yayını başarısız.' }

Write-Host '3/4 ClickOnce kurulumu (Kurulum profili)' -ForegroundColor Cyan
# ClickOnce needs Visual Studio's MSBuild; SQL Server Management Studio also ships one, so only Visual Studio paths are accepted.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = if (Test-Path $vswhere) { & $vswhere -all -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Where-Object { $_ -like '*\Microsoft Visual Studio\*' } | Select-Object -First 1 }
if (-not $msbuild) { throw 'Visual Studio MSBuild bulunamadı. ClickOnce kurulumu için Visual Studio (.NET masaüstü iş yükü) gerekir.' }
& $msbuild FileTransit.csproj /restore /t:Publish /p:Configuration=Release /p:PublishProfile=Kurulum /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'ClickOnce yayını başarısız.' }
# Windows refuses to start a ClickOnce app whose manifest has two requestedPrivileges (SideBySide error 75); see app.manifest.
foreach ($manifest in Get-ChildItem (Join-Path $publish 'kurulum\Application Files') -Recurse -Filter '*.manifest') {
    $count = [regex]::Matches((Get-Content $manifest.FullName -Raw), '<requestedPrivileges').Count
    if ($count -gt 1) { throw "ClickOnce bildiriminde $count requestedPrivileges var ($($manifest.Name)); kurulan uygulama açılmaz. app.manifest trustInfo asm.v2 olmalı." }
}

Write-Host '4/4 Paketleme' -ForegroundColor Cyan
$forbidden = Get-ChildItem $publish -Recurse -File | Where-Object { $_.Name -match '^workspace\.json$|\.pfx$|\.pdb$|^servers\.py$' -or $_.FullName -match '\\test-(results|server|libs)\\' }
if ($forbidden) { throw "Pakete girmemesi gereken dosyalar bulundu: $($forbidden.FullName -join ', ')" }

$release = Join-Path $root "artifacts\release\$version"
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Path $release | Out-Null
$exe = Join-Path $release "FileTransit-$version-win-x64.exe"
$zip = Join-Path $release "FileTransit-$version-Kurulum.zip"
Copy-Item (Join-Path $publish 'tek-exe\FileTransit.exe') $exe
Compress-Archive -Path (Join-Path $publish 'kurulum\*') -DestinationPath $zip
Copy-Item (Join-Path $PSScriptRoot 'KURULUM.txt') $release
$sums = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content -Path (Join-Path $release 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

Write-Host "Hazır: $release" -ForegroundColor Green
Get-ChildItem $release -File | ForEach-Object { '  {0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }

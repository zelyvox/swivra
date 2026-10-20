<#
.SYNOPSIS
  Builds the self-contained Windows release of Swivra and zips it.

.DESCRIPTION
  Output: publish/Swivra-<version>-win-x64.zip, with a Swivra-<version>-win-x64 folder inside.
  The app is published as a plain folder (not single-file) and is not trimmed.
  The user needs no .NET installation. WebView2 Runtime is still required (preinstalled on Windows 11).

.EXAMPLE
  pwsh scripts/publish.ps1
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Swivra.App/Swivra.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }

$name = "Swivra-$version-win-x64"
$out = Join-Path $root 'publish'
$app = Join-Path $out $name
$zip = Join-Path $out "$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $app | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en `
    -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The native SkiaSharp package ships its 89 MB debug symbols, and WebView2 ships XML docs. Neither is needed at run time.
# ONNX Runtime also drops its import libraries (.lib); they are only for native linking.
Get-ChildItem $app -Recurse -Include *.pdb, *.xml, *.lib | Remove-Item -Force

# License texts that must travel with the binaries (see THIRD-PARTY-NOTICES.md).
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$licenses = Join-Path $app 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $app
$licenseFiles = @(
    @{ From = 'microsoft.web.webview2/1.0.4258.31/LICENSE.txt'; To = 'WebView2-LICENSE.txt' },
    @{ From = 'microsoft.web.webview2/1.0.4258.31/NOTICE.txt'; To = 'WebView2-NOTICE.txt' },
    @{ From = 'skiasharp/4.150.1/LICENSE.txt'; To = 'SkiaSharp-LICENSE.txt' },
    @{ From = 'skiasharp.nativeassets.win32/4.150.1/THIRD-PARTY-NOTICES.txt'; To = 'SkiaSharp-native-THIRD-PARTY-NOTICES.txt' },
    @{ From = 'microsoft.extensions.logging.abstractions/8.0.3/LICENSE.TXT'; To = 'Microsoft.Extensions-LICENSE.txt' },
    @{ From = 'microsoft.extensions.dependencyinjection.abstractions/8.0.2/LICENSE.TXT'; To = 'Microsoft.Extensions.DependencyInjection-LICENSE.txt' },
    @{ From = 'microsoft.ml.onnxruntime/1.31.0/LICENSE'; To = 'OnnxRuntime-LICENSE.txt' },
    @{ From = 'microsoft.ml.onnxruntime/1.31.0/ThirdPartyNotices.txt'; To = 'OnnxRuntime-ThirdPartyNotices.txt' },
    @{ From = 'qrcoder/1.8.1/LICENSE.txt'; To = 'QRCoder-LICENSE.txt' }
)
foreach ($f in $licenseFiles) {
    $src = Join-Path $nuget $f.From
    if (Test-Path $src) { Copy-Item $src (Join-Path $licenses $f.To) } else { Write-Warning "License file not found: $src" }
}
# Texts that have no file inside the NuGet packages (PdfPig, PDFium and its bundled code, PDFsharp, model weights, ...) are kept in the repo.
$thirdParty = Join-Path $root 'third-party-licenses'
if (-not (Test-Path $thirdParty)) { throw "third-party-licenses/ is missing: $thirdParty" }
Copy-Item (Join-Path $thirdParty '*') $licenses -Recurse -Force
foreach ($f in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $src = Join-Path $dotnetRoot $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $licenses "dotnet-$f") } else { Write-Warning "License file not found: $src" }
}

if (-not (Test-Path (Join-Path $app 'Swivra.exe'))) { throw 'Swivra.exe is missing from the publish output' }
if (-not (Test-Path (Join-Path $app 'web/index.html'))) { throw 'web/ is missing from the publish output' }

Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal

$folderMb = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$zipMb = (Get-Item $zip).Length / 1MB
'{0}: folder {1:N1} MB, zip {2:N1} MB' -f $name, $folderMb, $zipMb
Write-Host $zip

# FireVPN for Windows: build FireVPN.exe + AmazTool.exe (updater) and pack D:\firevpn\win-release\FireVPN-windows-64.zip
# Cores (xray, sing-box, geo files) come from the official v2rayN release unpacked in D:\firevpn\win-official
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = 'D:\firevpn\dotnet'
$env:Path = "D:\firevpn\dotnet;$env:Path"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = 'D:\firevpn\nuget'

$src = 'D:\firevpn\firevpn-windows\v2rayN'
$stage = 'D:\firevpn\win-release\FireVPN'
$zip = 'D:\firevpn\win-release\FireVPN-windows-64.zip'
$cores = 'D:\firevpn\win-official\x\v2rayN-windows-64\bin'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

Push-Location $src
dotnet publish ./v2rayN/v2rayN.csproj -c Release -r win-x64 -p:SelfContained=true -o $stage *> 'D:\firevpn\win-build.log'
$code1 = $LASTEXITCODE
dotnet publish ./AmazTool/AmazTool.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishTrimmed=true -o $stage *>> 'D:\firevpn\win-build.log'
$code2 = $LASTEXITCODE
Pop-Location
"exit=$code1/$code2"
Select-String -Path 'D:\firevpn\win-build.log' -Pattern ' error ' | Select-Object -First 15 | ForEach-Object { $_.Line }
if ($code1 -ne 0 -or $code2 -ne 0) { exit 1 }

Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force
# cores: xray and sing-box only (mihomo is not needed for VLESS Reality)
New-Item -ItemType Directory -Force "$stage\bin" | Out-Null
Get-ChildItem $cores | Where-Object { $_.Name -ne 'mihomo' } | ForEach-Object { Copy-Item -Recurse -Force $_.FullName "$stage\bin\" }

if (Test-Path $zip) { Remove-Item -Force $zip }
# entries as FireVPN/... with forward slashes: AmazTool (the updater) splits paths by '/'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$base = Split-Path $stage -Parent
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $name = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
$archive.Dispose()
"{0} {1:N0} MB" -f $zip, ((Get-Item $zip).Length / 1MB)

# single-file installer FireVPN-<version>-setup.exe (Inno Setup in D:\firevpn\innosetup)
$iscc = 'D:\firevpn\innosetup\ISCC.exe'
$iss = 'D:\firevpn\firevpn-windows\scripts\firevpn-setup.iss'
$ver = ([regex]::Match((Get-Content -Raw "$src\ServiceLib\Common\FireVpn.cs"), 'AppVersion = "([0-9.]+)"')).Groups[1].Value
if (Test-Path $iscc) {
    Get-ChildItem 'D:\firevpn\win-release' -Filter 'FireVPN-*-setup.exe' | Remove-Item -Force
    & $iscc /Q "/DAppVersion=$ver" "/DSourceDir=$stage" '/DOutputDir=D:\firevpn\win-release' $iss
    if ($LASTEXITCODE -ne 0) { "installer build failed: $LASTEXITCODE"; exit 1 }
    $setup = Get-Item "D:\firevpn\win-release\FireVPN-$ver-setup.exe"
    "{0} {1:N0} MB" -f $setup.FullName, ($setup.Length / 1MB)
} else {
    "Inno Setup not found ($iscc) - installer skipped"
}

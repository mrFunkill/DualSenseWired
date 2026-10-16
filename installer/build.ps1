# Build the installer: publish the app (self-contained, single file), then compile the Inno Setup script.
# The dependency installers must be in deps\ (not in git):
#   USBip-0.9.8.1-x64.exe     https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1
#   HidHide_1.5.230_x64.exe   https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

dotnet publish "$root\DualSenseWired\DualSenseWired.csproj" -nologo -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o "$root\publish"
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
& $iscc /Q "$PSScriptRoot\DualSenseWired.iss"
exit $LASTEXITCODE

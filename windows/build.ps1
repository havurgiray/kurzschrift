# Builds Kurzschrift for Windows and its installers, on Windows with the .NET 10 SDK and Inno Setup 6.
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Arch x64,arm64] [-NoInstaller]
# Output: windows\dist\Kurzschrift-<version>-windows-<arch>-setup.exe
param([string[]]$Arch = @("x64", "arm64"), [switch]$NoInstaller)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
[xml]$proj = Get-Content Kurzschrift\Kurzschrift.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force dist | Out-Null
foreach ($a in $Arch) {
  $out = "publish\win-$a"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  dotnet publish Kurzschrift\Kurzschrift.csproj -c Release -r "win-$a" --self-contained true -o $out -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "publish $a failed" }
  if ($NoInstaller) { continue }
  if (-not $iscc) { throw "Inno Setup 6 not found" }
  & $iscc /Q "/DArch=$a" "/DVersion=$version" "/DSource=..\$out" installer\Kurzschrift.iss
  if ($LASTEXITCODE -ne 0) { throw "installer $a failed" }
}
Get-ChildItem dist\*.exe | ForEach-Object { "{0}  {1:N1} MB  {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_).Hash }

# Compiles the blur passes for cs_5_0 into Shaders/Compiled
param(
	[string]$Fxc = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" | Sort-Object FullName | Select-Object -Last 1).FullName
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'Compiled'
New-Item $out -ItemType Directory -Force | Out-Null
foreach ($name in @('downsample', 'gaussian', 'composite')) {
	& $Fxc /nologo /T cs_5_0 /E CS /O3 /Fo (Join-Path $out "$name.cso") (Join-Path $PSScriptRoot "$name.hlsl") | Where-Object { $_ -match 'error' }
	if ($LASTEXITCODE -ne 0) { throw "fxc failed for $name" }
	Write-Host "$name : built"
}

# Compiles the blur passes (cs_5_0) and the layer composite (vs_5_0, ps_5_0) into Shaders/Compiled
param(
	[string]$Fxc = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" | Sort-Object FullName | Select-Object -Last 1).FullName
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'Compiled'
New-Item $out -ItemType Directory -Force | Out-Null
$shaders = @(
	@('downsample', 'downsample', 'cs_5_0', 'CS'),
	@('gaussian', 'gaussian', 'cs_5_0', 'CS'),
	@('composite', 'composite', 'cs_5_0', 'CS'),
	@('layer_vs', 'layer', 'vs_5_0', 'VS'),
	@('layer_ps', 'layer', 'ps_5_0', 'PS')
)
foreach ($shader in $shaders) {
	$name, $source, $model, $entry = $shader
	& $Fxc /nologo /T $model /E $entry /O3 /Fo (Join-Path $out "$name.cso") (Join-Path $PSScriptRoot "$source.hlsl") | Where-Object { $_ -match 'error' }
	if ($LASTEXITCODE -ne 0) { throw "fxc failed for $name" }
	Write-Host "$name : built"
}

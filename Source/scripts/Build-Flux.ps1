param(
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory
)
$ErrorActionPreference = 'Stop'
$fluxRoot = Split-Path $PSScriptRoot -Parent
$fluxCompiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$fluxRelease = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$fluxBin = Join-Path $fluxRoot 'build-flux'
$fluxWpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
New-Item -ItemType Directory -Force $fluxBin | Out-Null
Copy-Item -LiteralPath (Join-Path $fluxRelease 'wrapper.dll'), (Join-Path $fluxRelease 'Newtonsoft.Json.dll') -Destination $fluxBin
$fluxRefs = @('System.dll','System.Core.dll','System.Xml.dll','System.Net.Http.dll','System.Xaml.dll','System.Security.dll') | ForEach-Object { '/r:' + $_ }
foreach ($fluxRef in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $fluxRefs += '/r:' + (Join-Path $fluxWpf $fluxRef) }
$fluxRefs += '/r:' + (Join-Path $fluxBin 'wrapper.dll')
$fluxRefs += '/r:' + (Join-Path $fluxBin 'Newtonsoft.Json.dll')
$fluxSources = @(Get-ChildItem -LiteralPath (Join-Path $fluxRoot 'flux') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
foreach ($fluxSource in @('TuningProposal.cs','WriterBridge.cs')) { $fluxSources += Join-Path $fluxRoot ('grapher\AI\' + $fluxSource) }
$fluxResources = @(
    '/resource:' + (Join-Path $fluxRoot 'flux\Shell.xaml') + ',KaidenFlux.Shell.xaml'
    '/resource:' + (Join-Path $fluxRoot 'setup\x64\driver\rawaccel.sys') + ',KaidenAccel.Setup.rawaccel.sys'
)
& $fluxCompiler /nologo /langversion:7.3 /target:winexe /platform:x64 /optimize+ /main:KaidenFlux.Entry ('/out:' + (Join-Path $fluxBin 'AimKnight.exe')) ('/win32icon:' + (Join-Path $fluxRoot 'grapher\aimknight.ico')) @fluxRefs @fluxResources @fluxSources
if ($LASTEXITCODE -ne 0) { throw 'AimKnight compilation failed' }
$fluxWriterRefs = @('/r:System.dll','/r:System.Core.dll','/r:System.Windows.Forms.dll',('/r:' + (Join-Path $fluxBin 'wrapper.dll')),('/r:' + (Join-Path $fluxBin 'Newtonsoft.Json.dll')))
& $fluxCompiler /nologo /langversion:7.3 /target:exe /platform:x64 /optimize+ ('/out:' + (Join-Path $fluxBin 'writer.exe')) @fluxWriterRefs (Join-Path $fluxRoot 'writer\Program.cs') (Join-Path $fluxRoot 'writer\Properties\AssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Writer compilation failed' }
Copy-Item -LiteralPath (Join-Path $fluxRoot 'grapher\App.config') -Destination (Join-Path $fluxBin 'AimKnight.exe.config')
Copy-Item -LiteralPath (Join-Path $fluxRoot 'writer\App.config') -Destination (Join-Path $fluxBin 'writer.exe.config')
Copy-Item -LiteralPath (Join-Path $fluxRoot 'LICENSE'), (Join-Path $fluxRoot 'LICENSE.Newtonsoft.Json.md') -Destination $fluxBin



$fluxNotices = "AimKnight includes components from Raw Accel, copyright (c) 2020 a1xd, MIT licensed.`r`nOriginal contributors include simon, _m00se_, Sidiouth, TauntyArmordillo, and Kovaak.`r`nThe original signed driver is included unchanged. This is an independent project.`r`nSee LICENSE for Raw Accel and LICENSE.Newtonsoft.Json.md for Newtonsoft.Json.`r`n"
Set-Content -LiteralPath (Join-Path $fluxBin 'LICENSES.txt') -Value $fluxNotices
Write-Output "AimKnight built: $fluxBin"

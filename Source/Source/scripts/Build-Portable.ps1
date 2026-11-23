<#
.SYNOPSIS
Build the x64 managed apps against the official Raw Accel v1.7.1 release DLLs.
.PARAMETER CompilerPath
Full path to Roslyn csc.exe (tested: Microsoft.Net.Compilers.Toolset 4.8.0/tasks/net472/csc.exe).
.PARAMETER ReleaseDirectory
Extracted official v1.7.1 RawAccel folder containing wrapper.dll and Newtonsoft.Json.dll.
.EXAMPLE
.\scripts\Build-Portable.ps1 -CompilerPath C:\tools\roslyn\tasks\net472\csc.exe -ReleaseDirectory C:\tools\RawAccel
#>
param(
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory
)
$ErrorActionPreference = 'Stop'
$kaidenRoot = Split-Path $PSScriptRoot -Parent
$kaidenCompiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$kaidenRelease = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$kaidenBin = Join-Path $kaidenRoot 'build'
$kaidenIntermediate = Join-Path $kaidenRoot 'scripts\obj'
New-Item -ItemType Directory -Force $kaidenBin, $kaidenIntermediate | Out-Null
Copy-Item -LiteralPath (Join-Path $kaidenRelease 'wrapper.dll'), (Join-Path $kaidenRelease 'Newtonsoft.Json.dll') -Destination $kaidenBin
& $kaidenCompiler /nologo /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/out:' + (Join-Path $kaidenIntermediate 'BuildAssets.exe')) (Join-Path $PSScriptRoot 'BuildAssets.cs')
if ($LASTEXITCODE -ne 0) { throw 'Asset helper compilation failed' }
& (Join-Path $kaidenIntermediate 'BuildAssets.exe') $kaidenRoot $kaidenIntermediate
if ($LASTEXITCODE -ne 0) { throw 'Resource generation failed' }
[xml]$kaidenProject = Get-Content -LiteralPath (Join-Path $kaidenRoot 'grapher\grapher.csproj')
$kaidenSources = @($kaidenProject.Project.ItemGroup.Compile | Where-Object { $_.Include } | ForEach-Object { Join-Path $kaidenRoot ('grapher\' + $_.Include) })
$kaidenRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Windows.Forms.DataVisualization.dll','System.Xml.dll','System.Xml.Linq.dll','System.Data.dll','System.Deployment.dll','System.Net.Http.dll','System.Configuration.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + $_ }
$kaidenRefs += '/r:' + (Join-Path $kaidenBin 'wrapper.dll')
$kaidenRefs += '/r:' + (Join-Path $kaidenBin 'Newtonsoft.Json.dll')
$kaidenResources = @(
    '/resource:' + (Join-Path $kaidenIntermediate 'Form1.resources') + ',grapher.RawAcceleration.resources'
    '/resource:' + (Join-Path $kaidenIntermediate 'AboutBox.resources') + ',grapher.AboutBox.resources'
    '/resource:' + (Join-Path $kaidenIntermediate 'Properties.Resources.resources') + ',grapher.Properties.Resources.resources'
    '/resource:' + (Join-Path $kaidenRoot 'setup\x64\installer.exe') + ',KaidenAccel.Setup.installer.exe'
    '/resource:' + (Join-Path $kaidenRoot 'setup\x64\driver\rawaccel.sys') + ',KaidenAccel.Setup.rawaccel.sys'
)
& $kaidenCompiler /nologo /langversion:7.3 /target:winexe /platform:x64 /optimize+ ('/win32icon:' + (Join-Path $kaidenRoot 'grapher\mouse.ico')) ('/out:' + (Join-Path $kaidenBin 'KaidenAccel.exe')) @kaidenRefs @kaidenResources @kaidenSources
if ($LASTEXITCODE -ne 0) { throw 'GUI compilation failed' }
& $kaidenCompiler /nologo /langversion:7.3 /target:exe /platform:x64 /optimize+ ('/out:' + (Join-Path $kaidenBin 'writer.exe')) @kaidenRefs (Join-Path $kaidenRoot 'writer\Program.cs') (Join-Path $kaidenRoot 'writer\Properties\AssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Writer compilation failed' }
Copy-Item -LiteralPath (Join-Path $kaidenRoot 'grapher\App.config') -Destination (Join-Path $kaidenBin 'KaidenAccel.exe.config')
Copy-Item -LiteralPath (Join-Path $kaidenRoot 'writer\App.config') -Destination (Join-Path $kaidenBin 'writer.exe.config')
Copy-Item -LiteralPath (Join-Path $kaidenRoot 'LICENSE'), (Join-Path $kaidenRoot 'LICENSE.Newtonsoft.Json.md'), (Join-Path $kaidenRoot 'KAIDEN-README.md'), (Join-Path $kaidenRoot 'ReadMe.md') -Destination $kaidenBin
Write-Output "Build completed: $kaidenBin"

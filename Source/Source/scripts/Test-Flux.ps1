param([Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$fluxRoot = Split-Path $PSScriptRoot -Parent
$fluxCompiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$fluxBin = Join-Path $fluxRoot 'build-flux'
$fluxTests = Join-Path $fluxRoot 'tests\bin-flux'
$fluxWpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
New-Item -ItemType Directory -Force $fluxTests | Out-Null
Copy-Item -LiteralPath (Join-Path $fluxBin 'KaidenFlux.exe'), (Join-Path $fluxBin 'wrapper.dll'), (Join-Path $fluxBin 'Newtonsoft.Json.dll') -Destination $fluxTests
$fluxRefs = @('/r:System.dll','/r:System.Core.dll','/r:System.Net.Http.dll','/r:System.Xaml.dll','/r:System.Security.dll',('/r:' + (Join-Path $fluxBin 'wrapper.dll')),('/r:' + (Join-Path $fluxBin 'Newtonsoft.Json.dll')))
$fluxFakeWriter = Join-Path $fluxTests 'fake-writer'
New-Item -ItemType Directory -Force $fluxFakeWriter | Out-Null
& $fluxCompiler /nologo /platform:x64 ('/out:' + (Join-Path $fluxFakeWriter 'writer.exe')) (Join-Path $fluxRoot 'tests\FakeWriter.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mock writer compilation failed' }
& $fluxCompiler /nologo /langversion:7.3 /platform:x64 ('/out:' + (Join-Path $fluxTests 'FluxTests.exe')) @fluxRefs ('/resource:' + (Join-Path $fluxRoot 'setup\x64\driver\rawaccel.sys') + ',KaidenAccel.Setup.rawaccel.sys') (Join-Path $fluxRoot 'flux\FluxState.cs') (Join-Path $fluxRoot 'flux\FluxInstaller.cs') (Join-Path $fluxRoot 'grapher\AI\WriterBridge.cs') (Join-Path $fluxRoot 'tests\FluxTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Core test compilation failed' }
& (Join-Path $fluxTests 'FluxTests.exe') $fluxFakeWriter
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
foreach ($fluxRef in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $fluxRefs += '/r:' + (Join-Path $fluxWpf $fluxRef) }
& $fluxCompiler /nologo /langversion:7.3 /platform:x64 ('/out:' + (Join-Path $fluxTests 'FluxUiTests.exe')) @fluxRefs (Join-Path $fluxRoot 'tests\FluxUiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed' }
& (Join-Path $fluxTests 'FluxUiTests.exe') (Join-Path $fluxTests 'KaidenFlux.exe')
if ($LASTEXITCODE -ne 0) { throw 'UI checks failed' }
& $fluxCompiler /nologo /langversion:7.3 /platform:x64 ('/out:' + (Join-Path $fluxTests 'TuningTests.exe')) @fluxRefs (Join-Path $fluxRoot 'grapher\AI\TuningProposal.cs') (Join-Path $fluxRoot 'grapher\AI\GeminiTuner.cs') (Join-Path $fluxRoot 'tests\TuningTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Gemini test compilation failed' }
& (Join-Path $fluxTests 'TuningTests.exe') (Join-Path $fluxTests 'default-settings.json')
if ($LASTEXITCODE -ne 0) { throw 'Gemini checks failed' }
& $fluxCompiler /nologo /langversion:7.3 /platform:x64 ('/out:' + (Join-Path $fluxTests 'FoundationTests.exe')) @fluxRefs (Join-Path $fluxRoot 'flux\FluxState.cs') (Join-Path $fluxRoot 'flux\GameProfiles.cs') (Join-Path $fluxRoot 'flux\LocalTuningMemory.cs') (Join-Path $fluxRoot 'flux\SecureApiKeyStore.cs') (Join-Path $fluxRoot 'grapher\AI\TuningProposal.cs') (Join-Path $fluxRoot 'tests\FoundationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Foundation test compilation failed' }
& (Join-Path $fluxTests 'FoundationTests.exe') $fluxTests
if ($LASTEXITCODE -ne 0) { throw 'Foundation checks failed' }
& $fluxCompiler /nologo /langversion:7.3 /platform:x64 ('/out:' + (Join-Path $fluxTests 'StudioTests.exe')) @fluxRefs (Join-Path $fluxRoot 'flux\FluxState.cs') (Join-Path $fluxRoot 'flux\GameProfiles.cs') (Join-Path $fluxRoot 'flux\CurveStudio.cs') (Join-Path $fluxRoot 'flux\AimStudio.cs') (Join-Path $fluxRoot 'tests\LegacyMouseHardware.cs') (Join-Path $fluxRoot 'grapher\AI\TuningProposal.cs') (Join-Path $fluxRoot 'tests\StudioTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Studio test compilation failed' }
& (Join-Path $fluxTests 'StudioTests.exe') $fluxTests
if ($LASTEXITCODE -ne 0) { throw 'Studio checks failed' }

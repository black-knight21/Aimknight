param([Parameter(Mandatory=$true)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$kaidenRoot = Split-Path $PSScriptRoot -Parent
$kaidenCompiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$kaidenBin = Join-Path $kaidenRoot 'build'
$kaidenTests = Join-Path $kaidenRoot 'tests\bin'
New-Item -ItemType Directory -Force $kaidenTests | Out-Null
Copy-Item -LiteralPath (Join-Path $kaidenBin 'wrapper.dll'), (Join-Path $kaidenBin 'Newtonsoft.Json.dll') -Destination $kaidenTests
& $kaidenCompiler /nologo /langversion:7.3 /target:exe /platform:x64 ('/out:' + (Join-Path $kaidenTests 'TuningTests.exe')) ('/r:' + (Join-Path $kaidenBin 'wrapper.dll')) ('/r:' + (Join-Path $kaidenBin 'Newtonsoft.Json.dll')) /r:System.Net.Http.dll (Join-Path $kaidenRoot 'grapher\AI\TuningProposal.cs') (Join-Path $kaidenRoot 'grapher\AI\GeminiTuner.cs') (Join-Path $kaidenRoot 'tests\TuningTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
$kaidenDefaults = Join-Path $kaidenTests 'default-settings.json'
& (Join-Path $kaidenTests 'TuningTests.exe') $kaidenDefaults
if ($LASTEXITCODE -ne 0) { throw 'Validation checks failed' }
& (Join-Path $kaidenBin 'writer.exe') --console --validate $kaidenDefaults
if ($LASTEXITCODE -ne 0) { throw 'Valid settings were rejected' }
$kaidenInvalid = Join-Path $kaidenTests 'invalid-settings.json'
Set-Content -LiteralPath $kaidenInvalid -Value '{}'
& (Join-Path $kaidenBin 'writer.exe') --console --validate $kaidenInvalid
if ($LASTEXITCODE -ne 1) { throw 'Invalid settings did not return exit code 1' }
& (Join-Path $kaidenBin 'writer.exe') --console --validate (Join-Path $kaidenTests ('missing-' + [guid]::NewGuid().ToString('N') + '.json'))
if ($LASTEXITCODE -ne 1) { throw 'Missing file did not return exit code 1' }
Write-Output 'Writer CLI checks passed. No driver activation or live Gemini calls performed.'
Copy-Item -LiteralPath (Join-Path $kaidenBin 'KaidenAccel.exe') -Destination $kaidenTests
& $kaidenCompiler /nologo /target:exe /platform:x64 ('/out:' + (Join-Path $kaidenTests 'SetupTests.exe')) (Join-Path $kaidenRoot 'tests\SetupTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup test compilation failed' }
& (Join-Path $kaidenTests 'SetupTests.exe') (Join-Path $kaidenTests 'KaidenAccel.exe') $kaidenTests
if ($LASTEXITCODE -ne 0) { throw 'Setup payload tests failed' }

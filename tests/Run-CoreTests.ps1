$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src'
$outputRoot = Join-Path $repoRoot 'artifacts\tests'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
$testExe = Join-Path $outputRoot 'CoreTests.exe'
$compilerArgs = @('/nologo', '/optimize+', '/target:exe', '/r:System.Web.Extensions.dll',
    ('/out:' + $testExe), (Join-Path $sourceRoot 'NotificationCore.cs'), (Join-Path $sourceRoot 'CoreTests.cs'))
$argumentLine = ($compilerArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
$stdout = Join-Path $outputRoot 'compile.stdout.txt'
$stderr = Join-Path $outputRoot 'compile.stderr.txt'
$process = Start-Process -FilePath $compiler -ArgumentList $argumentLine -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
Get-Content -LiteralPath $stdout -Encoding UTF8
Get-Content -LiteralPath $stderr -Encoding UTF8
if ($process.ExitCode) { throw 'Core test compilation failed.' }
$stdout = Join-Path $outputRoot 'tests.stdout.txt'
$stderr = Join-Path $outputRoot 'tests.stderr.txt'
$process = Start-Process -FilePath $testExe -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
Get-Content -LiteralPath $stdout -Encoding UTF8
Get-Content -LiteralPath $stderr -Encoding UTF8
if ($process.ExitCode) { throw 'Isolated core tests failed.' }

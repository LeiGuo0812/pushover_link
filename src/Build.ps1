$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Build-Run([string]$File,[string[]]$CompilerArgs) {
 $out=Join-Path $PSScriptRoot 'build.stdout.txt'; $err=Join-Path $PSScriptRoot 'build.stderr.txt'
 $line=($CompilerArgs|ForEach-Object {'"'+$_+'"'}) -join ' '
 $process=Start-Process -FilePath $File -ArgumentList $line -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
 if(Test-Path $out){Get-Content $out}; if(Test-Path $err){Get-Content $err}
 if($process.ExitCode){throw ('Build command failed: '+$process.ExitCode)}
}
Build-Run $csc @('/nologo','/optimize+','/target:exe','/r:System.Drawing.dll',('/out:'+(Join-Path $PSScriptRoot 'IconMaker.exe')),(Join-Path $PSScriptRoot 'IconMaker.cs'))
Build-Run (Join-Path $PSScriptRoot 'IconMaker.exe') @((Join-Path $PSScriptRoot 'app.ico'))
$compilerArgs=@('/nologo','/optimize+','/target:winexe','/platform:x64','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll',('/out:'+(Join-Path $PSScriptRoot 'CodexPushover.exe')),('/win32manifest:'+(Join-Path $PSScriptRoot 'app.manifest')),('/win32icon:'+(Join-Path $PSScriptRoot 'app.ico')),('/resource:'+(Join-Path $PSScriptRoot 'Metadata.py')+',CodexPushover.Metadata.py'),('/resource:'+(Join-Path $PSScriptRoot 'app.ico')+',CodexPushover.AppIcon.ico'),('/resource:'+(Join-Path $PSScriptRoot 'app-mark.png')+',CodexPushover.AppMark.png'),(Join-Path $PSScriptRoot 'NotificationCore.cs'),(Join-Path $PSScriptRoot 'ProcessHost.cs'),(Join-Path $PSScriptRoot 'App.cs'),(Join-Path $PSScriptRoot 'SettingsForm.cs'),(Join-Path $PSScriptRoot 'UiAssets.cs'))
Build-Run $csc $compilerArgs
Get-Item (Join-Path $PSScriptRoot 'CodexPushover.exe') | Select-Object Name,Length | ConvertTo-Json -Compress

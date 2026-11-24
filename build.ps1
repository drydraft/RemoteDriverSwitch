param([string]$OutputFolder='dist')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$buildDirectory = Join-Path $projectRoot 'build'
$outputDirectory = Join-Path $projectRoot $OutputFolder
New-Item -ItemType Directory -Path $buildDirectory,$outputDirectory -Force | Out-Null
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.ServiceProcess.dll','/reference:System.Management.dll','/reference:System.Web.Extensions.dll','/reference:System.IO.Compression.dll','/reference:Microsoft.CSharp.dll')
Add-Type -AssemblyName System.Drawing
$iconFile=Join-Path $buildDirectory 'app.ico'
$bitmap=New-Object System.Drawing.Bitmap 64,64
$graphics=[System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(32,87,218))
$whitePen=New-Object System.Drawing.Pen ([System.Drawing.Color]::White),4
$graphics.DrawArc($whitePen,17,15,30,39,180,180)
$graphics.DrawLine($whitePen,17,34,17,40)
$graphics.DrawArc($whitePen,17,23,30,32,0,180)
$graphics.DrawLine($whitePen,47,34,47,40)
$graphics.DrawLine($whitePen,32,16,32,29)
$graphics.FillEllipse([System.Drawing.Brushes]::White,29,26,6,9)
$handle=$bitmap.GetHicon()
$drawnIcon=[System.Drawing.Icon]::FromHandle($handle)
$stream=[System.IO.File]::Create($iconFile)
try{$drawnIcon.Save($stream)}finally{$stream.Dispose();$drawnIcon.Dispose();$whitePen.Dispose();$graphics.Dispose();$bitmap.Dispose()}
$common = @('/nologo','/utf8output','/langversion:5','/platform:x64','/optimize+',('/win32icon:'+$iconFile)) + $references
& $compiler @common '/target:winexe' ('/win32manifest:'+(Join-Path $projectRoot 'app.manifest')) ('/out:'+(Join-Path $outputDirectory '远控驱动开关.exe')) @sourceFiles
if($LASTEXITCODE -ne 0){throw '主程序编译失败'}
& $compiler @common '/target:exe' '/define:TESTS' ('/out:'+(Join-Path $buildDirectory 'RemoteDriverSwitch.Tests.exe')) @sourceFiles
if($LASTEXITCODE -ne 0){throw '测试程序编译失败'}
& $compiler @common '/target:winexe' '/define:PREVIEW' ('/win32manifest:'+(Join-Path $projectRoot 'preview.manifest')) ('/out:'+(Join-Path $buildDirectory 'RemoteDriverSwitch.Preview.exe')) @sourceFiles
if($LASTEXITCODE -ne 0){throw '预览程序编译失败'}
Get-Item -LiteralPath (Join-Path $outputDirectory '远控驱动开关.exe') | Select-Object FullName,Length

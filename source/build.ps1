$ErrorActionPreference='Stop'
$compiler='C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$wpf='C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF'
$core=Join-Path $PSScriptRoot 'Core.cs'
$desktop=Join-Path $PSScriptRoot 'Desktop.cs'
$target=Join-Path (Split-Path $PSScriptRoot -Parent) 'DayTrace.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/out:$target" "/win32manifest:$PSScriptRoot/app.manifest" "/win32icon:$PSScriptRoot/daytrace.ico" "/resource:$PSScriptRoot/Theme.xaml,Theme.xaml" "/resource:$PSScriptRoot/daytrace.ico,App.ico" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll "/r:$wpf/WindowsBase.dll" "/r:$wpf/PresentationCore.dll" "/r:$wpf/PresentationFramework.dll" /r:System.Xaml.dll $core $desktop (Join-Path $PSScriptRoot "AppVersion.cs") (Join-Path $PSScriptRoot "NativeAppearance.cs") (Join-Path $PSScriptRoot "UpdateChecker.cs") (Join-Path $PSScriptRoot "UpdateInstaller.cs") (Join-Path $PSScriptRoot "UpdateChecks.cs") (Join-Path $PSScriptRoot "BatchAI.cs") (Join-Path $PSScriptRoot "BatchChecks.cs") (Join-Path $PSScriptRoot "ActivityTimeline.cs") (Join-Path $PSScriptRoot "ClassificationRules.cs")
if($LASTEXITCODE -ne 0){throw 'Build failed'}

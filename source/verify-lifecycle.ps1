param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1');if($LASTEXITCODE -ne 0){throw 'Build failed'}}
$folder=Join-Path $root ('work/lifecycle-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$binary=Join-Path $folder 'DayTrace.exe'
Copy-Item -LiteralPath (Join-Path $root 'DayTrace.exe') -Destination $binary
# Test a real desktop notification callback, not merely a direct Exit() call.
foreach($hidden in @($false,$true)){
 $firstLog=Join-Path $folder ("first-$hidden.txt")
 $secondLog=Join-Path $folder ("second-$hidden.txt")
 $firstArgs=@('--demo','--exit-check','--native-tray','--slow-exit','--linger-worker')
 $secondArgs=@('--demo','--exit-check','--native-tray','--linger-worker')
 if($hidden){$firstArgs+='--hidden';$secondArgs+='--hidden'}
 $firstArgs+=('"{0}"' -f $firstLog);$secondArgs+=('"{0}"' -f $secondLog)
 $first=Start-Process -FilePath $binary -ArgumentList $firstArgs -WindowStyle Hidden -PassThru
 $second=$null
 try{
  $clicked=$false
  for($i=0;$i -lt 400;$i++){
   if((Test-Path -LiteralPath $firstLog) -and ((Get-Content -LiteralPath $firstLog -Raw) -match 'clicked exit')){$clicked=$true;break}
   Start-Sleep -Milliseconds 20
  }
  if(-not $clicked){throw 'First process did not click tray exit'}
  $overlap=-not $first.HasExited
  $second=Start-Process -FilePath $binary -ArgumentList $secondArgs -WindowStyle Hidden -PassThru
  if(-not $second.WaitForExit(12000)){throw 'Restart did not complete'}
  if(-not $first.WaitForExit(6000)){throw 'First process remained after exit'}
  if(-not $overlap -or $first.ExitCode -ne 0 -or $second.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $secondLog) -or (Get-Content -LiteralPath $secondLog -Raw) -notmatch 'clicked exit'){throw 'Restart was swallowed by closing instance'}
  Write-Output "PASS: same-path immediate restart with overlapping processes; hidden=$hidden"
 }finally{
  # Only stop fixture processes started and retained by this script.
  if($second -and -not $second.HasExited){$second.Kill();$second.WaitForExit()}
  if(-not $first.HasExited){$first.Kill();$first.WaitForExit()}
 }
}
Write-Output "Lifecycle logs: $folder"

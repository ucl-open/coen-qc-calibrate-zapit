# Checks src\Extensions without the rig: the
# camera's settings file (a missing one refused), ZapitRequests (Point, Pattern, LaserOff and LaserCurve as ZapitDaq commands, and
# every refusal: no test subject, over the ceiling, a bad number, a bad run), and the laser curve's raw file (one row per sample,
# written whole, a ragged read refused).
# Run by tests\test_extensions.py (pytest), which builds the extension into -Bin with its packages. Exit 0 when every check passes.
param([Parameter(Mandatory = $true)][string]$Bin)
$ErrorActionPreference = 'Stop'
foreach ($dll in 'System.Reactive.Interfaces', 'System.Reactive.Core', 'System.Reactive.Linq', 'System.Reactive.PlatformServices', 'Bonsai.Core', 'Newtonsoft.Json', 'OpenRigs.Control', 'OpenRigs.Zapit', 'Extensions') {
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $Bin "$dll.dll"))
}
$failed = 0
function Check($name, $ok) {
    if ($ok) { "  ok    $name" } else { "  FAIL  $name"; $script:failed++ }
}
function Refused($block) { try { $null = & $block; $false } catch { $true } }
$rx = [System.Reactive.Linq.Observable]
function Method($name, $count) { $rx.GetMethods() | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $count } | Select-Object -First 1 }
function Items($type, $source) { @((Method 'ToEnumerable' 1).MakeGenericMethod($type).Invoke($null, @($source))) }
function Obs($type, $items) {
    $array = [Array]::CreateInstance($type, @($items).Count)
    for ($i = 0; $i -lt $array.Length; $i++) { $array.SetValue(@($items)[$i], $i) }
    (Method 'ToObservable' 1).MakeGenericMethod($type).Invoke($null, @(, $array))
}
$wiring = [CoenQcCalibrateZapit.ZapitRequests]
$request = [CoenQcCalibrateZapit.ZapitRequest]
$command = [OpenRigs.Control.ControlEvent]

# The camera's settings file: found beside rig.yml, a missing one named.
$rig = Join-Path ([IO.Path]::GetTempPath()) ("zapit-ext-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory (Join-Path $rig 'calibration/runs/zapit-laser/run1') -Force
$camera = New-Object CoenQcCalibrateZapit.ZapitCameraSettings
$camera.SerialNumber = '24012345'
Check 'camera: a missing settings file is refused' (Refused { Items ([CoenQcCalibrateZapit.ZapitCameraFile]) ($camera.Process((Obs ([string]) @($rig)))) })
Set-Content (Join-Path $rig 'calibration/Zapit-Calibration.pfs') 'pfs' -Encoding ascii
$file = (Items ([CoenQcCalibrateZapit.ZapitCameraFile]) ($camera.Process((Obs ([string]) @($rig)))))[0]
Check 'camera: the serial number and the settings file beside rig.yml' ($file.SerialNumber -eq '24012345' -and $file.ParameterFile -eq (Join-Path $rig 'calibration\Zapit-Calibration.pfs'))

# ZapitRequests.
function New-Requests([bool]$test = $true, [double]$ceiling = 2) {
    $r = New-Object CoenQcCalibrateZapit.ZapitRequests
    $r.RunsFolder = Join-Path $rig 'calibration\runs'; $r.TestSubject = $test; $r.LaserCeilingVolts = $ceiling; $r.SamplesPerLevel = 5
    $r
}
function Send($requests, $name, $text) {
    $e = New-Object OpenRigs.Control.ControlEvent
    $e.Kind = [OpenRigs.Control.ControlEventKind]::Command; $e.Name = $name; $e.Arguments = $text
    Items $request ($requests.Process((Obs $command @($e))))
}
$r = New-Requests
$p = Send $r 'Point' '0.5 -0.25 1.5'
Check 'Point: galvo volts and laser volts as a Point command' ($p.Count -eq 1 -and $null -eq $p[0].Refusal -and "$($p[0].Command.Kind) $($p[0].Command.GalvoXVolts) $($p[0].Command.GalvoYVolts) $($p[0].Command.LaserVolts)" -eq 'Point 0.5 -0.25 1.5')
$p = Send $r 'Point' '0 0 0'
Check 'Point: the laser off is allowed without a test subject' ($null -eq (Send (New-Requests $false) 'Point' '0 0 0')[0].Refusal)
Check 'Point: a laser needs the test subject' ((Send (New-Requests $false) 'Point' '0 0 1')[0].Refusal -match 'test subject')
Check 'Point: over the ceiling refused' ((Send $r 'Point' '0 0 2.5')[0].Refusal -match 'ceiling')
Check 'Point: a ceiling of 0 refuses every laser' ((Send (New-Requests $true 0) 'Point' '0 0 0.1')[0].Refusal -match 'ceiling')
Check 'Point: a galvo beyond 10 V refused' ((Send $r 'Point' '11 0 0')[0].Refusal -match 'galvo')
Check 'Point: a word for a number refused, not thrown' ((Send $r 'Point' '0 zero 0')[0].Refusal -match 'Point takes')
Check 'Point: two numbers refused' ((Send $r 'Point' '0 0')[0].Refusal -match 'Point takes')
$z = Send $r 'LaserOff' ''
Check 'LaserOff: a Zero command' ($z.Count -eq 1 -and $z[0].Command.Kind -eq 'Zero')
Check 'a command that is not the calibration''s is passed over' ((Send $r 'Snapshot' 'x').Count -eq 0)
$h = Send $r 'Point' '0 0 1 50'
Check 'Point with a hold: the point, then every line to zero' ($h.Count -eq 2 -and $h[0].Command.Kind -eq 'Point' -and $h[1].Command.Kind -eq 'Zero')
$pat = Send $r 'Pattern' '20;-1 -1 0;1 -1 0;1 1 1'
Check 'Pattern: each point in turn, then zero' ($pat.Count -eq 4 -and (($pat | ForEach-Object { "$($_.Command.Kind)" }) -join ' ') -eq 'Point Point Point Zero' -and $pat[2].Command.LaserVolts -eq 1)
Check 'Pattern: a point over the ceiling refuses the whole pattern' ((Send $r 'Pattern' '20;0 0 0;0 0 3').Count -eq 1 -and (Send $r 'Pattern' '20;0 0 0;0 0 3')[0].Refusal -match 'point 2')
Check 'Pattern: no dwell refused' ((Send $r 'Pattern' '0 0 0')[0].Refusal -match 'Pattern takes')

# LaserCurve: the volts file in the run, refusals, then the raw file.
$run = Join-Path $rig 'calibration\runs\zapit-laser\run1'
Check 'LaserCurve: no volts file refused' ((Send $r 'LaserCurve' 'zapit-laser/run1')[0].Refusal -match 'staircase_volts')
Set-Content (Join-Path $run 'staircase_volts.csv') "volts`n0`n1`n2`n3`n4" -Encoding ascii
$c = Send $r 'LaserCurve' 'zapit-laser/run1 0.1 -0.2'
Check 'LaserCurve: a Staircase of the file''s volts parked at the galvos' ($c.Count -eq 1 -and $c[0].Command.Kind -eq 'Staircase' -and $c[0].Command.LaserLevelsVolts.Count -eq 5 -and $c[0].Command.SamplesPerLevel -eq 5 -and $c[0].Command.GalvoXVolts -eq 0.1 -and $c[0].Command.GalvoYVolts -eq -0.2 -and $c[0].Folder -eq $run)
Check 'LaserCurve: the ceiling does not apply to the staircase' ($null -eq (Send (New-Requests $true 0.5) 'LaserCurve' 'zapit-laser/run1')[0].Refusal)
Check 'LaserCurve: only with the test subject' ((Send (New-Requests $false) 'LaserCurve' 'zapit-laser/run1')[0].Refusal -match 'test subject')
Check 'LaserCurve: a run outside the runs folder refused' ((Send $r 'LaserCurve' '..\other')[0].Refusal -match 'not a folder')
Set-Content (Join-Path $run 'staircase_volts.csv') "volts`n0`n1`n11`n3" -Encoding ascii
Check 'LaserCurve: a volts value beyond 10 refused' ((Send $r 'LaserCurve' 'zapit-laser/run1')[0].Refusal -match '10 V')
Set-Content (Join-Path $run 'staircase_volts.csv') "volts`n0`n1`n2`n3" -Encoding ascii

# The raw file.
$read = New-Object OpenRigs.Zapit.ZapitDaqEvent
$read.Kind = [OpenRigs.Zapit.ZapitDaqEventKind]::StaircaseRead
$read.AnalogIn = [double[]](0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.0)
$said = [CoenQcCalibrateZapit.SaveLaserCurve]::Save($read, $run)
$lines = [IO.File]::ReadAllLines((Join-Path $run 'Zapit-LaserCurve.csv'))
Check 'raw file: a header and one row per step sample, the dark one dropped' ($said -eq '4 steps x 2 samples' -and $lines.Count -eq 9 -and $lines[0] -eq 'step,volts,reading' -and $lines[1] -eq '0,0,0' -and $lines[8] -eq '3,3,0.7')
Check 'raw file: no part file left behind' (-not (Test-Path (Join-Path $run 'Zapit-LaserCurve.csv.part')))
$read.AnalogIn = [double[]](0.0, 0.1, 0.2)
Check 'raw file: samples that do not divide into the steps refused, the file kept' ((Refused { [CoenQcCalibrateZapit.SaveLaserCurve]::Save($read, $run) }) -and (Test-Path (Join-Path $run 'Zapit-LaserCurve.csv')))

# The chain on ZapitDaq's simulated twin: the staircase command in, the photodiode's samples out, filed in the run's folder.
Set-Content (Join-Path $run 'staircase_volts.csv') "volts`n0`n1`n2`n3`n4" -Encoding ascii
Remove-Item (Join-Path $run 'Zapit-LaserCurve.csv')
$daq = New-Object OpenRigs.Zapit.ZapitDaq
$daq.Simulated = $true; $daq.SampleRate = 100000; $daq.MaxTrialSeconds = 3; $daq.EarlyStopLeadSeconds = 0.05
$sent = Send $r 'LaserCurve' 'zapit-laser/run1'
$commands = Obs ([OpenRigs.Zapit.ZapitDaqCommand]) @($sent[0].Command)
$events = Items ([OpenRigs.Zapit.ZapitDaqEvent]) ($daq.Process($commands))
$staircase = @($events | Where-Object { $_.Kind -eq 'StaircaseRead' })
Check 'simulated twin: one StaircaseRead, a sample for each AO sample and the dark one at the end' ($staircase.Count -eq 1 -and $staircase[0].AnalogIn.Count -eq 26)
$said = [CoenQcCalibrateZapit.SaveLaserCurve]::Save($staircase[0], $run)
Check 'simulated twin: its readings filed, 5 steps x 5 samples' ($said -eq '5 steps x 5 samples' -and ([IO.File]::ReadAllLines((Join-Path $run 'Zapit-LaserCurve.csv'))).Count -eq 26)
Remove-Item $rig -Recurse -Force
if ($failed) { "$failed failed"; exit 1 }
"all passed"

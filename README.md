# coen-qc-calibrate-zapit

Calibrates the stable parts of Zapit on a Coen Lab dome with optogenetics (BLUE-OPTO-1 and BLUE-OPTO-2): the laser power curve (the photodiode, monthly) and the camera and optics set-up (the camera's ROI, `micronsPerPixel`, the scanner invert flags, the `.pfs` exposures), checked when the optics change. The scanner calibration is not here: it runs at the start of every session, inside the session workflow.

Run it from app-rig-control's Calibrations page. It is an open-rigs project under `ExperimentControl`, which the page launches, watches and stops. It is not in the Setup page's Workflow list.

**Not yet run on a rig.** It builds and every check that needs no hardware passes (ZapitDaq's simulated twin included); the NI DAQ, the Basler and the photodiode are for BLUE-OPTO-2.

**Packages.** `OpenRigs.Devices`, `LiveView`, `Logging`, `Trials` and `Zapit` exist only as the branch builds `0.1.1-zapit-native.10` of lib-open-rigs (branch `zapit-native`, never released; its rig model has the `zapit` block), so `.bonsai\Bonsai.config` pins those versions and `local_packages\` carries their `.nupkg` files; the open-rigs pin is that branch's `b6fa5fa`. At cutover this repo is re-pinned to the released `coen-lab.N` (the lab's update-workflows does it), and the local copies go. The other lab packages stay on lib-open-rigs `a820afb`'s; never bump them here by hand.

## Bonsai Measures, app-rig-control Computes

This workflow drives the NI DAQ and the camera and writes raw data, nothing more. Every volts value is lib_zapit's (the staircase's volts, the aim and zap volts from the fit and the laser curve), computed by app-rig-control and sent as text; the laser curve's fit and its records are app-rig-control's Python. The C# in `src\Extensions` reads, checks and files: no maths (Pip, 7 Nov 2026).

## What It Takes

- **A rig with a `zapit` block**, read from the rig's rig.yml (the lab rig model, `RigConfigs`) at Start, so nothing about the wiring is a launch property: the NI `device`, the AO lines (`galvoXChannel`, `galvoYChannel`, `laserChannel`, `ledChannel`), the photodiode's AI line `photodiodeChannel` (ZapitDaq's photodiode channel), `sampleRate`, `maxTrialSeconds`, `earlyStopLeadSeconds`, and `camera.serialNumber` and `camera.calibrationPfsPath`. Each channel is joined to the device (`Dev1/ao0`) by the rig model. The only wiring launch property is `Simulated`, which runs ZapitDaq's twin on a desk.
- **The calibration `.pfs`** (`calibration\Zapit-Calibration.pfs` beside rig.yml by default: fixed exposure, free running), made with the camera set-up. A missing file stops the workflow at Start naming it.
- **The Behavior board** (`BehaviorBoard-Main`) and its clock (`ClockFromBoard`, `SetClock` false), as every lab calibration has them (the lab's lint requires a clock); the board also clears the Zapit trigger line however the workflow stops. A calibration has no experiment folder (`NoExperimentFolder`).
- **The laser safety launch properties**: `TestSubject` (true only for the lab's `test` mouse) and `LaserCeilingVolts` (the rig's `ceilingMw` as volts, from lib_zapit). Without `TestSubject` no laser above 0 V is fired and LaserCurve is refused; with a ceiling of 0 every laser is refused. ZapitDaq also takes rig.yml's `zapit.ceilingMw` (`CeilingMw`): at 0 (laser disabled) it stops the workflow on any laser volts above 0 V, a LaserCurve's included. `SamplesPerLevel` (default 1000, 10 ms at 100 kS/s) is the photodiode samples read per step.

## Commands

All are sent as text to the experiment controller. A refused command is reported as the `refused` metric with its reason and never stops the run; a DAQ error does (ZapitDaq zeroes every line first). A newer command replaces one still running.

| Command | Text | Does |
| --- | --- | --- |
| `Point` | `x y laser [holdMs]` (volts) | Holds the beam at galvo X, Y volts with the laser's control volts. Point mode and Zap Site are both this, at the aiming and the stimulus volts. With `holdMs` every line goes to 0 V after it. |
| `Pattern` | `dwellMs;x y laser;x y laser;...` | Walks the beam through the points (a check pattern for the invert flags and the scale), each for the dwell, then every line to 0 V. |
| `LaserOff` | | Every line to 0 V. |
| `LaserCurve` | `zapit-laser\<run> [x y]` | Test subject only. Runs the laser staircase of the run folder's `staircase_volts.csv`, the galvos parked at x y (0 0 by default), the photodiode read on the AO clock, and files it. Reports `Finished`. |
| `Snapshot` | `zapit-optics\<run>\<still>` | Saves the camera's latest frame, lossless, as `calibration\runs\<text>.png` beside rig.yml, for the `micronsPerPixel` check. |

Metrics: `daq` (the last ZapitDaq event), `refused`, `curve` (`201 steps x 1000 samples`).

## A Laser Curve Run

1. App-rig-control makes the run folder `calibration\runs\zapit-laser\<run>` and writes `staircase_volts.csv` in it: a `volts` header, then one control-volts value per line (lib_zapit's `staircase_volts`, the laser's whole range, ceiling not applied, test subject only). The photodiode must be in the beam.
2. It launches the workflow with `TestSubject` true and the rig's wiring, waits for Ready (the board's time and the camera's first frame), presses Go, and sends `LaserCurve zapit-laser\<run>`.
3. ZapitDaq plays each level for `SamplesPerLevel` samples and reads the photodiode on the same clock. The run folder gets `Zapit-LaserCurve.csv`, columns `step,volts,reading`, one row per sample of each step (the dark sample the read ends with is dropped), written whole and then renamed so a file there is a complete run. `Finished` follows. The fit is app-rig-control's: `lib_zapit.laser_curve.fit_laser_curve(volts, readings)` drops the first half of each step.

## A Camera And Optics Set-Up

The camera streams live as `Camera-Zapit` (the workflow publishes `Camera-Zapit.Data`; the live view streams it once StreamDevices knows the device: a rig.yml camera entry or a temporary Zapit instance in lib's LiveViewPlan, as the balance camera has). A person checks the invert flags and the scale by aiming (`Point`, with the laser at a low volts, test subject only) and by `Pattern`, and `Snapshot`s a ruler or a known grid for the `micronsPerPixel` check. The settings, the `.pfs` files and the flags are then changed by hand in rig.yml and the camera set-up; nothing here writes them.

## Setup

`scripts\deploy.ps1` (or `.bonsai\Setup.ps1`) installs Bonsai and the packages: it syncs the `.venv` from `uv.lock` (uv must be on PATH), then runs the lab's one Bonsai setup from the open-rigs it pins, which also removes package folders `Bonsai.config` no longer names. `Bonsai.Pylon` needs Basler's pylon 7.1 runtime on the rig, and `OpenRigs.Zapit` the NI-DAQmx runtime, only when a real task or camera runs.

Check a change without launching Bonsai: `uv run pytest` (`tests\test_workflow.py`, the lab's `workflow_lint` rules and this workflow's own; `tests\test_extensions.py`, `src\Extensions` built under C# 5 and run through ZapitDaq's twin) and `scripts\check-workflow.ps1` (lib-open-rigs' check-workflow, from the open-rigs this repo pins; run `.bonsai\Setup.ps1` first).

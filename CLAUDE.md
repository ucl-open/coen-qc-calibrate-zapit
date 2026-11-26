# coen-qc-calibrate-zapit

The Zapit stable calibrations (laser curve, camera and optics set-up) as an open-rigs workflow, `src\main.bonsai`, run only from app-rig-control's Calibrations page. Read `README.md` first; the lab's Bonsai guidance is `/bonsai-workflows` in claude-tools.

- Bonsai measures, app-rig-control computes: no maths in `src\Extensions` (C# 5), no fits, no volts maths. Every volts value arrives as command text from lib_zapit.
- The scanner calibration is not here: it runs in every session workflow.
- Commit on `main` and push. Before pushing run what `.github\workflows\checks.yml` runs: `uv run --with pytest --with pytest-xdist pytest -q -n auto`, `.bonsai\Setup.ps1`, `scripts\check-workflow.ps1`. CI runs on a push to main and by hand.
- The OpenRigs packages are the lab's `0.1.1-coen-lab.N` builds (Core `.46`, Devices `.73`, Logging `.37`, Trials `.25`, LiveView `.23`, Vision `.13`, Zapit `.2`); their `.nupkg` files are in `local_packages\`. Never bump a lab package by hand; update-workflows does it.
- The laser is fired only through `ZapitRequests` (test subject, ceiling); keep every command reaching ZapitDaq through it (`tests\test_workflow.py` holds this).
- Edit `.bonsai` XML by targeted replacement, keep the UTF-8 BOM, validate with `scripts\check-workflow.ps1` (builds the workflow with Bonsai's builders, no launch).

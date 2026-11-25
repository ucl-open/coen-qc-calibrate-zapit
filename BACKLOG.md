# Backlog

History is in `git log`.

## Open

- First run on BLUE-OPTO-2 from a scratch copy (needs app-rig-control's `zapit-native` side, or a hand launch with the wiring as properties): Ready (board time, camera first frame), `Point` and `Pattern` on the scope, `LaserCurve` with the photodiode in the beam (the AI on the AO sample clock, the photodiode terminal, the file `Zapit-LaserCurve.csv`), `Snapshot` of a ruler, Stop with every line at 0 V.
- Camera-Zapit in the live view: StreamDevices streams it only when it knows the device (a rig.yml camera entry, or a temporary Zapit instance in lib's LiveViewPlan as the balance camera has); lib-open-rigs' call.
- Cutover: re-pin from `0.1.1-zapit-native.20` to the released `coen-lab.N` (update-workflows) and delete the local `.nupkg` files.
- A `Pattern` or `Point` while a staircase plays makes ZapitDaq refuse and stops the workflow; app-rig-control must not send one then.

## Decided

- All commands are text; volts are lib_zapit's, sent by app-rig-control (Bonsai measures, app-rig-control computes).
- One workflow for both calibrations: everything is a command, so the laser curve and the optics set-up share the camera and the DAQ.
- The raw laser curve is one row per sample, kept whole, written then renamed.

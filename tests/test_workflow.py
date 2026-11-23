r"""src\main.bonsai against the lab's rules for a workflow's hardware (open_rigs.rigs.workflow_lint), and this calibration's own:
every command declared, the laser fired only through ZapitRequests, no bank or trial, the camera stock PylonCapture."""

from pathlib import Path

import pytest
from open_rigs.rigs import workflow_lint as lint

ROOT = Path(__file__).parents[1]
W, XSI = lint.W, lint.XSI
COMMANDS = {"Point", "Pattern", "LaserOff", "LaserCurve", "Snapshot"}
LAUNCH = {"ConfigRoot", "SubjectId", "SessionId", "LaunchId", "Simulated", "TestSubject", "LaserCeilingVolts", "SamplesPerLevel"}


@pytest.fixture(scope="module")
def wf():
    return lint.Workflow(ROOT)


def test_the_hardware_is_the_lab_pieces(wf):
    # Ready also waits for the camera's first frame, the workflow's own stage (ReadyGate's WorkflowStages).
    assert lint.hardware_problems(wf) == []
    assert dict(lint.hardware(wf))[lint.READY_GATE]["WorkflowStages"] == "CalibrationReady"


def test_the_devices_it_drives(wf):
    assert lint.required_devices(wf) == [("BehaviorBoard-Main", "behaviorBoard")]


def test_no_device_opened_by_hand(wf):
    assert lint.hand_opened_devices(wf) == []


def test_no_shared_mapping(wf):
    assert lint.shared_mapping_problems(wf.root) == []


def test_every_announcer_never_completes(wf):
    assert lint.ready_announcer_problems(wf.root) == []


def test_every_handled_command_is_declared(wf):
    assert lint.undeclared_commands(wf) == [] and lint.declared_twice(wf) == []
    declared = {name for _, kind, name in lint.control_declarations(wf) if kind == "command"}
    assert declared == COMMANDS


def test_no_experiment_folder(wf):
    assert lint.no_experiment_folder_problems(wf) == []


def test_the_launch_properties(wf):
    top = lint.nodes_and_edges(lint.child(wf.root, W + "Workflow"))[0]
    names = {p.get("DisplayName") or p.get("Name") for n in top if n.get(XSI) == "ExternalizedMapping" for p in n.iter(W + "Property")}
    assert names == LAUNCH


def test_live_view_is_last_and_fed_by_nothing(wf):
    assert lint.live_view_problems(wf) == []


def test_the_launch_id_reaches_the_controller(wf):
    assert lint.launch_id_problems(wf) == []


def test_a_stop_ends_headless_bonsai(wf):
    assert wf.text.count("orcl:EndAfterStop") == 1


def test_read_lab_rig_and_no_experiment_folder_are_top_level(wf):
    nodes, edges = lint.nodes_and_edges(lint.child(wf.root, W + "Workflow"))
    assert [n.get("Path") for n in nodes[1:3]] == [lint.READ_LAB_RIG, lint.NO_EXPERIMENT_FOLDER]
    assert [p.get("Name") for p in nodes[0].iter(W + "Property")] == ["ConfigRoot"] and (0, 1) in [e[:2] for e in edges]


def test_the_camera_is_stock_pylon_capture_published_as_camera_zapit(wf):
    assert wf.text.count('xsi:type="pyl:PylonCapture"') == 1
    assert "<Name>Camera-Zapit.Data</Name>" in wf.text and 'Camera-Balance' not in wf.text


def test_the_dac_does_on_demand_ao_only(wf):
    # A calibration never loads a bank, arms a trial or waits on the trigger: the commands are the Extensions', which build only
    # Point, Staircase and Zero, and ZapitDaq has no trigger terminal.
    assert wf.text.count('xsi:type="zap:ZapitDaq"') == 1 and "<zap:TriggerTerminal />" in wf.text
    code = (ROOT / "src" / "Extensions" / "Zapit.cs").read_text(encoding="utf-8")
    assert all(kind not in code for kind in ("LoadBank", "ZapitDaqCommandKind.Arm", "ZapitDaqCommandKind.EarlyStop"))


def test_the_laser_is_fired_only_through_the_requests(wf):
    # Every command reaches ZapitDaq through ZapitRequests (the test-subject and ceiling checks): nothing else is its input.
    nodes, edges = lint.nodes_and_edges(next(n for n in lint.nodes_and_edges(lint.child(wf.root, W + "Workflow"))[0]
                                              if n.findtext(W + "Name") == "Calibration").find(W + "Workflow"))
    (daq,) = [i for i, n in enumerate(nodes) if lint.kind(n) == "ozap:ZapitDaq"]
    feeders = [lint.kind(nodes[a]) for a, b, label in edges if b == daq and label == "Source1"]
    assert feeders == ["MemberSelector"]
    assert wf.text.count('xsi:type="zc:ZapitRequests"') == 1


def test_the_raw_curve_file_name_is_the_one_in_the_readme():
    code = (ROOT / "src" / "Extensions" / "Zapit.cs").read_text(encoding="utf-8")
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for name in ("staircase_volts.csv", "Zapit-LaserCurve.csv", "step,volts,reading"):
        assert name in code and name in readme


def test_the_wiring_is_read_from_the_rig_model_not_launch_properties(wf):
    # rig.yml's zapit block through RigConfigs: the camera's serial number and calibration .pfs, ZapitDaq's channels (full paths from
    # the rig model) with photodiodeChannel as its photodiode channel.
    for selector in ("Zapit.Camera.SerialNumber", "Zapit.Camera.CalibrationPfsPath", "Zapit.GalvoXChannelPath", "Zapit.GalvoYChannelPath",
                     "Zapit.LaserChannelPath", "Zapit.LedChannelPath", "Zapit.SampleRate", "Zapit.MaxTrialSeconds", "Zapit.EarlyStopLeadSeconds"):
        assert f'Selector="{selector}"' in wf.text
    assert 'Name="PhotodiodeChannel" Selector="Zapit.PhotodiodeChannelPath"' in wf.text
    assert "zc:ZapitWiring" not in wf.text

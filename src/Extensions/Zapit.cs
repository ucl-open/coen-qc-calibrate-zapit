using Bonsai;
using OpenRigs.Control;
using OpenRigs.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;

// The Zapit calibration's plumbing (Bonsai measures, app-rig-control computes): the camera's settings file, the commands a person or app-rig-control sends as ZapitDaq commands, and the laser curve's
// raw file. No maths here: every volts value is lib_zapit's, computed by app-rig-control and sent as text. This code only
// reads it, refuses what is out of range or not allowed, and files what the DAQ read. Compiled by Bonsai under C# 5.
namespace CoenQcCalibrateZapit
{
    /// <summary>PylonCapture's SerialNumber and ParameterFile.</summary>
    public class ZapitCameraFile
    {
        public string SerialNumber { get; set; }
        public string ParameterFile { get; set; }
    }

    /// <summary>
    /// The calibration camera's settings (.pfs: fixed exposure, free running), found beside rig.yml from rig.yml's
    /// zapit.camera.calibrationPfsPath. A missing file stops the workflow naming it: the camera set-up is what makes it.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Takes rig.yml's folder and gives the Basler camera's serial number and calibration settings file.")]
    public class ZapitCameraSettings
    {
        public ZapitCameraSettings()
        {
            CalibrationPfs = "calibration/Zapit-Calibration.pfs";
        }

        [Description("The Basler camera's serial number; blank opens the first Basler camera.")]
        public string SerialNumber { get; set; }

        [Description("The calibration settings file, relative to rig.yml's folder (zapit.camera.calibrationPfsPath).")]
        public string CalibrationPfs { get; set; }

        public IObservable<ZapitCameraFile> Process(IObservable<string> source)
        {
            return source.Select(delegate (string folder)
            {
                var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, (CalibrationPfs ?? "").Replace('/', '\\')));
                if (!File.Exists(file))
                    throw new FileNotFoundException("The Zapit camera's calibration settings file is missing, so the camera is not opened: " + file + ". Make it with the camera set-up (Pylon Viewer: fixed exposure, free running) and save it there.");
                return new ZapitCameraFile { SerialNumber = SerialNumber ?? "", ParameterFile = file };
            });
        }
    }

    /// <summary>A command made ready for ZapitDaq, or why it was refused.</summary>
    public class ZapitRequest
    {
        public ZapitDaqCommand Command { get; set; }
        /// <summary>Why nothing was done; null when Command is to be sent.</summary>
        public string Refusal { get; set; }
        /// <summary>A laser curve's run folder, where its volts are and its raw file goes.</summary>
        public string Folder { get; set; }
    }

    /// <summary>
    /// The calibration commands as ZapitDaq commands. A newer command replaces one still running (a pattern), and a refusal
    /// is a request with a reason, not an error: a mistyped command must not end the run.
    ///   Point x y laser [holdMs]       galvo X and Y volts and the laser's control volts, held (point mode, zap site);
    ///                                  holdMs above 0 ends it with laser and LED at 0 V after that long
    ///   Pattern dwellMs;x y laser;...  each point in turn for dwellMs, then every line to 0 V (check pattern)
    ///   LaserCurve run [x y]           the staircase from &lt;run&gt;\staircase_volts.csv (one volts column), with the galvos
    ///                                  parked at x y (0 0 default); the photodiode's samples filed in the run folder
    ///   LaserOff                       every line to 0 V
    /// A laser above 0 V needs TestSubject, and a Point or Pattern laser must be at most LaserCeilingVolts (the ceiling
    /// applied by lib_zapit, as volts); the staircase alone covers the laser's range, with the ceiling not applied, and is
    /// refused when LaserCeilingVolts is 0 (ceilingMw 0, the laser disabled), before ZapitDaq would stop the workflow.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Turns the Point, Pattern, LaserCurve and LaserOff commands into ZapitDaq commands.")]
    public class ZapitRequests
    {
        const double MaxVolts = 10;
        const int MaxPatternPoints = 200;
        const double MaxDwellMs = 60000;

        public ZapitRequests()
        {
            SamplesPerLevel = 1000;
        }

        [Description("calibration\\runs beside rig.yml; a LaserCurve's run is a folder in it.")]
        public string RunsFolder { get; set; }

        [Description("Whether the subject is the lab's test mouse: without it no laser above 0 V is fired.")]
        public bool TestSubject { get; set; }

        [Description("The most laser control volts a Point or Pattern may use, the rig's ceiling as volts (0 refuses every laser).")]
        public double LaserCeilingVolts { get; set; }

        [Description("The photodiode samples read at each step of the laser curve.")]
        public int SamplesPerLevel { get; set; }

        public IObservable<ZapitRequest> Process(IObservable<ControlEvent> source)
        {
            return source.Where(e => e.Kind == ControlEventKind.Command)
                .Select(e => Handle(e.Name ?? "", (e.Arguments ?? "").Trim()))
                .Where(r => r != null)
                .Switch();
        }

        IObservable<ZapitRequest> Handle(string name, string arguments)
        {
            switch (name)
            {
                case "LaserOff": return Observable.Return(Zero());
                case "Point": return Point(arguments);
                case "Pattern": return Pattern(arguments);
                case "LaserCurve": return LaserCurve(arguments);
                default: return null;
            }
        }

        static ZapitRequest Zero()
        {
            return new ZapitRequest { Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero } };
        }

        static IObservable<ZapitRequest> Refuse(string why)
        {
            return Observable.Return(new ZapitRequest { Refusal = why });
        }

        static double[] Numbers(string text)
        {
            var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var numbers = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || double.IsNaN(numbers[i]) || double.IsInfinity(numbers[i]))
                    return null;
            return numbers;
        }

        string Check(double x, double y, double laser)
        {
            if (Math.Abs(x) > MaxVolts || Math.Abs(y) > MaxVolts)
                return "a galvo's volts are beyond " + MaxVolts.ToString(CultureInfo.InvariantCulture) + " V";
            if (laser < 0 || laser > MaxVolts)
                return "the laser's volts are not between 0 and " + MaxVolts.ToString(CultureInfo.InvariantCulture);
            if (laser > 0 && !TestSubject)
                return "the laser is fired only with the test subject";
            if (laser > LaserCeilingVolts)
                return "the laser's " + laser.ToString(CultureInfo.InvariantCulture) + " V is above the ceiling's " + LaserCeilingVolts.ToString(CultureInfo.InvariantCulture) + " V";
            return null;
        }

        static ZapitRequest At(double x, double y, double laser)
        {
            return new ZapitRequest { Command = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = x, GalvoYVolts = y, LaserVolts = laser } };
        }

        IObservable<ZapitRequest> Point(string text)
        {
            var n = Numbers(text);
            if (n == null || (n.Length != 3 && n.Length != 4))
                return Refuse("Point takes galvo X volts, galvo Y volts, the laser's volts and optionally a hold in ms, not '" + text + "'");
            var why = Check(n[0], n[1], n[2]);
            if (why != null) return Refuse("Point: " + why);
            var hold = n.Length == 4 ? n[3] : 0;
            if (hold < 0 || hold > MaxDwellMs) return Refuse("Point: the hold is not between 0 and " + MaxDwellMs.ToString(CultureInfo.InvariantCulture) + " ms");
            var point = Observable.Return(At(n[0], n[1], n[2]));
            return hold > 0 ? point.Concat(Observable.Timer(TimeSpan.FromMilliseconds(hold)).Select(_ => Zero())) : point;
        }

        IObservable<ZapitRequest> Pattern(string text)
        {
            var parts = text.Split(';');
            double dwell;
            if (parts.Length < 2 || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dwell) || !(dwell > 0) || dwell > MaxDwellMs)
                return Refuse("Pattern takes a dwell in ms, then x y laser for each point, all separated by ';', not '" + text + "'");
            if (parts.Length - 1 > MaxPatternPoints)
                return Refuse("Pattern has more than " + MaxPatternPoints + " points");
            var points = new List<ZapitRequest>();
            for (var i = 1; i < parts.Length; i++)
            {
                var n = Numbers(parts[i]);
                if (n == null || n.Length != 3)
                    return Refuse("Pattern point " + i + " is not 'x y laser': '" + parts[i].Trim() + "'");
                var why = Check(n[0], n[1], n[2]);
                if (why != null) return Refuse("Pattern point " + i + ": " + why);
                points.Add(At(n[0], n[1], n[2]));
            }
            var steps = points.Select((p, i) => Observable.Timer(TimeSpan.FromMilliseconds(i * dwell)).Select(_ => p)).ToList();
            steps.Add(Observable.Timer(TimeSpan.FromMilliseconds(points.Count * dwell)).Select(_ => Zero()));
            return Observable.Merge(steps);
        }

        IObservable<ZapitRequest> LaserCurve(string text)
        {
            var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 1 && parts.Length != 3)
                return Refuse("LaserCurve takes the run's folder and optionally the galvos' X and Y volts, not '" + text + "'");
            if (!TestSubject) return Refuse("LaserCurve: the laser curve is run only with the test subject");
            if (!(LaserCeilingVolts > 0)) return Refuse("LaserCurve: the laser is disabled (LaserCeilingVolts 0: rig.yml's zapit.ceilingMw is 0); set ceilingMw above 0 to run the laser curve");
            if (string.IsNullOrEmpty(RunsFolder)) return Refuse("LaserCurve: the runs folder is not known yet");
            var run = parts[0].Replace('/', '\\');
            if (run.Contains("..") || System.IO.Path.IsPathRooted(run))
                return Refuse("LaserCurve: the run '" + run + "' is not a folder in " + RunsFolder);
            var galvo = parts.Length == 3 ? Numbers(parts[1] + " " + parts[2]) : new double[] { 0, 0 };
            if (galvo == null || Math.Abs(galvo[0]) > MaxVolts || Math.Abs(galvo[1]) > MaxVolts)
                return Refuse("LaserCurve: the galvos' volts are not numbers within " + MaxVolts.ToString(CultureInfo.InvariantCulture) + " V");
            var folder = System.IO.Path.Combine(RunsFolder, run);
            double[] volts;
            try { volts = LaserCurveFiles.ReadVolts(folder); }
            catch (IOException e) { return Refuse("LaserCurve: " + e.Message); }
            catch (FormatException e) { return Refuse("LaserCurve: " + e.Message); }
            if (SamplesPerLevel < 1) return Refuse("LaserCurve: SamplesPerLevel is below 1");
            return Observable.Return(new ZapitRequest
            {
                Folder = folder,
                Command = new ZapitDaqCommand
                {
                    Kind = ZapitDaqCommandKind.Staircase,
                    GalvoXVolts = galvo[0],
                    GalvoYVolts = galvo[1],
                    LaserLevelsVolts = volts,
                    SamplesPerLevel = SamplesPerLevel,
                },
            });
        }
    }

    /// <summary>The laser curve's files: the volts app-rig-control wrote, and the raw readings filed beside them.</summary>
    public static class LaserCurveFiles
    {
        public const string VoltsFile = "staircase_volts.csv";
        public const string RawFile = "Zapit-LaserCurve.csv";

        /// <summary>The staircase's control volts, one per line under a header, from the run folder.</summary>
        public static double[] ReadVolts(string folder)
        {
            var path = Path.Combine(folder, VoltsFile);
            if (!File.Exists(path)) throw new FileNotFoundException(VoltsFile + " is not in " + folder);
            var volts = new List<double>();
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                double v;
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    if (double.IsNaN(v) || Math.Abs(v) > 10) throw new FormatException(VoltsFile + " line " + (i + 1) + " is " + line + ": a volts value within 10 V is expected");
                    volts.Add(v);
                }
                else if (i != 0) throw new FormatException(VoltsFile + " line " + (i + 1) + " is not a number: " + line);
            }
            if (volts.Count < 4) throw new FormatException(VoltsFile + " holds " + volts.Count + " steps, at least 4 are needed");
            return volts.ToArray();
        }
    }

    /// <summary>
    /// Files a laser curve's photodiode readings in its run folder as Zapit-LaserCurve.csv (step, volts, reading: one row per
    /// AO sample of a step, so the whole run is kept raw; the dark sample ZapitDaq ends with is dropped), written whole under another name and then renamed, so a file there is a
    /// complete run. Gives a line saying what was filed.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Files a laser curve's photodiode readings in its run folder.")]
    public class SaveLaserCurve
    {
        public IObservable<string> Process(IObservable<Tuple<ZapitDaqEvent, string>> source)
        {
            return source.Select(t => Save(t.Item1, t.Item2));
        }

        public static string Save(ZapitDaqEvent read, string folder)
        {
            var volts = LaserCurveFiles.ReadVolts(folder);
            var readings = read.AnalogIn;
            // ZapitDaq reads one sample more than the steps hold: the final one, with the laser at 0 V. It is not a step's.
            var count = readings == null ? 0 : readings.Length - 1;
            if (count < volts.Length || count % volts.Length != 0)
                throw new InvalidOperationException("The photodiode gave " + (readings == null ? 0 : readings.Length) + " samples for " + volts.Length + " steps: each step needs the same number, and one more at the end.");
            var each = count / volts.Length;
            var text = new StringBuilder("step,volts,reading\r\n");
            for (var i = 0; i < count; i++)
            {
                var step = i / each;
                text.Append(step.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(volts[step].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(readings[i].ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            }
            var target = Path.Combine(folder, LaserCurveFiles.RawFile);
            var temp = target + ".part";
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            return volts.Length + " steps x " + each + " samples";
        }
    }
}

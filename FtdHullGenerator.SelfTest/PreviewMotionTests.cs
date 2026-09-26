using System.Diagnostics;
using System.Windows;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Geometry;
using FtdHullGenerator.UI;
using static SlopeFillTestSupport;

/// <summary>
/// Covers the lattice view's gentle turn: the release inertia, the ease back to the gentle
/// rate and the initial turning direction, the stop while the hull is held, the pause at a
/// standing view, and the Auto-turn switch. The motion is stepped directly — no window, no
/// composition clock, and no pointer hardware — so every check is deterministic.
/// </summary>
internal static class PreviewMotionTests
{
    public static void Run(HullGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        RunReleaseRateMapping();
        var hull = generator.Generate(HullParameters.Default with { Length = 16, Width = 7, Height = 6 });
        RunOnControlThread(hull);
        Console.WriteLine("Preview motion: gentle turn, hold stop, throw inertia and return, the flick gate, standing-view pause, kept tilt, and the Auto-turn switch passed.");
    }

    /// <summary>The release mapping: the dead zone, the drag's own orbit mapping, and the throw cap.</summary>
    private static void RunReleaseRateMapping()
    {
        Require(HullPreviewControl.ThrowRate(0) == 0, "A still pointer handed over spin.");
        Require(HullPreviewControl.ThrowRate(100) == 0, "A slow release inside the dead zone handed over spin.");
        Require(HullPreviewControl.ThrowRate(-100) == 0, "A slow leftward release inside the dead zone handed over spin.");
        RequireNear(HullPreviewControl.ThrowRate(200), 0.4, 1e-9, "A flick did not map at the throw's own lower sensitivity.");
        RequireNear(HullPreviewControl.ThrowRate(-200), -0.4, 1e-9, "A leftward flick did not map to a leftward spin.");
        RequireNear(HullPreviewControl.ThrowRate(2000), 3.5d, 1e-9, "A hard flick was not capped.");
        RequireNear(HullPreviewControl.ThrowRate(-5000), -3.5d, 1e-9, "A hard leftward flick was not capped.");
    }

    private static void RunOnControlThread(GeneratedHull hull)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RequireAutoTurnDefaultsOff();
                RequireNoHullStaysStill();
                RequireGentleTurn(hull);
                RequireHoldStopsTheTurn(hull);
                RequireClickBringsTheTurnBack(hull);
                RequireThrowCoastsAndReturns(hull);
                RequireLeftwardThrowReturns(hull);
                RequireRestedReleaseDoesNotThrow(hull);
                RequireStandingViewPauses(hull);
                RequireAutoTurnSwitch(hull);
                RequireTiltIsKept(hull);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    /// <summary>A fresh preview opens with the turn switched off, holding its frame until asked.</summary>
    private static void RequireAutoTurnDefaultsOff()
    {
        var control = new HullPreviewControl();
        Require(!control.AutoRotate, "A fresh preview had the gentle turn switched on.");
    }

    /// <summary>A preview with no hull has nothing to turn, and stays exactly where it is.</summary>
    private static void RequireNoHullStaysStill()
    {
        var control = new HullPreviewControl();
        control.AutoRotate = true;
        var yaw = control.ViewYaw;
        var pitch = control.ViewPitch;
        Step(control, 5);
        Require(control.ViewYaw == yaw && control.ViewPitch == pitch && control.TurnRate == 0,
            "A preview with no hull turned anyway.");
    }

    /// <summary>The resting state: the view eases up to the gentle rate and keeps turning.</summary>
    private static void RequireGentleTurn(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        var start = control.ViewYaw;

        Step(control, 2);
        Require(control.TurnRate > 0 && control.TurnRate < HullPreviewControl.GentleTurnRate,
            "A fresh preview did not ease up to the gentle turn.");

        Step(control, 12);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "The gentle turn did not settle at its own rate.");
        Require(control.ViewYaw > start + 1,
            "The view did not turn a useful amount over fourteen seconds.");
    }

    /// <summary>Taking hold stops the turn dead, and the pointer still orbits while held.</summary>
    private static void RequireHoldStopsTheTurn(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        Step(control, 12);

        control.BeginHold(new Point(120, 120), Ticks(1));
        var heldYaw = control.ViewYaw;
        var heldPitch = control.ViewPitch;
        Step(control, 5);
        Require(control.ViewYaw == heldYaw && control.TurnRate == 0,
            "The gentle turn did not stop while the hull was held.");

        control.MoveHold(new Point(140, 110), Ticks(1.1));
        RequireNear(control.ViewYaw, heldYaw + 0.1, 1e-9, "A held drag did not orbit the view.");
        RequireNear(control.ViewPitch, heldPitch - 0.05, 1e-9, "A held drag did not tilt the view.");

        // Clamping is the standing drag behaviour, unchanged by the turn.
        control.MoveHold(new Point(140, -1000), Ticks(1.1));
        RequireNear(control.ViewPitch, -1.38, 1e-9, "A held drag did not clamp at the tilt limit.");
    }

    /// <summary>A release with no travel is a click: the gentle turn simply comes back.</summary>
    private static void RequireClickBringsTheTurnBack(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        Step(control, 12);

        control.BeginHold(new Point(160, 100), Ticks(2));
        control.EndHold(Ticks(2));
        Require(control.TurnRate == 0, "A click handed over spin the pointer never had.");

        var released = control.ViewYaw;
        Step(control, 12);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "A click did not bring the gentle turn back.");
        Require(control.ViewYaw > released, "The restored gentle turn did not advance the view.");
    }

    /// <summary>A throw coasts on its own speed, then eases back into the initial rotation.</summary>
    private static void RequireThrowCoastsAndReturns(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        control.BeginHold(new Point(200, 120), 0);
        control.MoveHold(new Point(260, 120), Ticks(0.1));
        control.EndHold(Ticks(0.1));
        RequireNear(control.TurnRate, 1.2, 1e-3, "A throw did not hand its own speed to the turn.");

        var released = control.ViewYaw;
        Step(control, 1);
        Require(control.ViewYaw > released + 0.5, "The thrown view did not coast after release.");

        Step(control, 20);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "A throw did not ease back to the gentle rate.");
        var settled = control.ViewYaw;
        Step(control, 1);
        Require(control.ViewYaw > settled, "The settled view did not return to turning the initial way.");
    }

    /// <summary>A throw the other way still ends up turning the initial way, passing through rest.</summary>
    private static void RequireLeftwardThrowReturns(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        control.BeginHold(new Point(260, 120), 0);
        control.MoveHold(new Point(200, 120), Ticks(0.1));
        control.EndHold(Ticks(0.1));
        RequireNear(control.TurnRate, -1.2, 1e-3, "A leftward throw did not hand over its speed.");

        var released = control.ViewYaw;
        Step(control, 1);
        Require(control.ViewYaw < released - 0.5, "The leftward throw did not coast left.");

        Step(control, 20);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "A leftward throw never turned back the other way.");
        var settled = control.ViewYaw;
        Step(control, 1);
        Require(control.ViewYaw > settled,
            "After a leftward throw the view did not return to turning the initial way.");
    }

    /// <summary>A pointer that rested before it was let go was placing the view, not throwing it.</summary>
    private static void RequireRestedReleaseDoesNotThrow(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.BeginHold(new Point(100, 100), 0);
        control.MoveHold(new Point(160, 100), Ticks(0.01));
        control.EndHold(Ticks(0.5));
        Require(control.TurnRate == 0, "A pointer that rested for half a second before release still threw the hull.");
    }

    /// <summary>A standing view is the repeatable frame: it pauses the turn and clears anything in flight.</summary>
    private static void RequireStandingViewPauses(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        Step(control, 6);
        control.ShowView(PreviewView.Side);
        Require(control.ViewYaw == 0 && control.ViewPitch == 0 && control.TurnRate == 0,
            "A standing view did not land exactly on its own angles with the turn stopped.");
        Step(control, 5);
        Require(control.ViewYaw == 0 && control.ViewPitch == 0,
            "The standing view drifted while the turn was running.");

        // A throw in flight is cut dead by the preset, not merely overtaken by it.
        control.BeginHold(new Point(200, 120), 0);
        control.MoveHold(new Point(260, 120), Ticks(0.1));
        control.EndHold(Ticks(0.1));
        control.ShowView(PreviewView.Bow);
        Require(control.ViewYaw == Math.PI / 2 && control.TurnRate == 0,
            "A standing view did not clear a throw in flight.");
        Step(control, 5);
        Require(control.ViewYaw == Math.PI / 2,
            "The standing view drifted on the throw it should have cleared.");

        // Taking hold and letting go is the way back to the turn.
        control.BeginHold(new Point(50, 50), 0);
        control.EndHold(Ticks(10));
        Step(control, 12);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "A grab-and-release did not bring the gentle turn back from a standing view.");
        Require(control.ViewYaw > Math.PI / 2,
            "The view did not turn again after the standing view's pause was cleared.");
    }

    /// <summary>
    /// The Auto-turn switch: off stops the turn and gates the release inertia, so a drag only
    /// moves the view under the pointer and a flick cannot spin it; on starts both again.
    /// </summary>
    private static void RequireAutoTurnSwitch(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        Step(control, 12);
        control.AutoRotate = false;
        Step(control, 12);
        Require(control.TurnRate == 0, "Switching the turn off did not stop it.");
        var stopped = control.ViewYaw;
        Step(control, 5);
        Require(control.ViewYaw == stopped, "The view kept turning after the switch was off.");

        // With the switch off a drag still orbits the view directly, but the release hands
        // over nothing: the inertia belongs to the turn, so a fast flick cannot leave the
        // hull spinning after the pointer is gone.
        var beforeFlick = control.ViewYaw;
        control.BeginHold(new Point(0, 0), 0);
        control.MoveHold(new Point(240, 0), Ticks(0.1));
        control.EndHold(Ticks(0.1));
        RequireNear(control.ViewYaw, beforeFlick + 1.2, 1e-9,
            "A held drag stopped orbiting the view with the turn switched off.");
        Require(control.TurnRate == 0, "A flick with the turn switched off still handed over spin.");
        var releasedYaw = control.ViewYaw;
        Step(control, 20);
        Require(control.ViewYaw == releasedYaw, "The view drifted after a release with the turn switched off.");
        var rested = control.ViewYaw;
        Step(control, 5);
        Require(control.ViewYaw == rested, "The view crept on after a release with the switch off.");

        control.AutoRotate = true;
        Step(control, 12);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "Switching the turn back on did not start it.");
        Require(control.ViewYaw > rested, "The view did not turn once the gentle turn came back.");

        // Switching the turn on is also a way out of a standing view's pause.
        control.ShowView(PreviewView.Top);
        control.AutoRotate = false;
        control.AutoRotate = true;
        Step(control, 12);
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "Switching the turn on did not clear the standing view's pause.");
    }

    /// <summary>A vertical throw carries tilt inertia that fades, and the tilt it reaches is kept.</summary>
    private static void RequireTiltIsKept(GeneratedHull hull)
    {
        var control = NewPreview(hull);
        control.AutoRotate = true;
        // A standing view sets a known pitch and leaves the vertical throw room to move the
        // tilt without reaching the clamp; the grab-and-release that throws also clears the
        // pause the standing view left behind.
        control.ShowView(PreviewView.Side);
        var startPitch = control.ViewPitch;
        control.BeginHold(new Point(100, 100), 0);
        control.MoveHold(new Point(100, 140), Ticks(0.1));
        control.EndHold(Ticks(0.1));
        RequireNear(control.TiltRate, 0.8, 1e-3, "A vertical throw did not carry tilt inertia.");

        Step(control, 8);
        Require(control.TiltRate == 0, "Tilt inertia did not decay to nothing.");
        var settledPitch = control.ViewPitch;
        Require(settledPitch > startPitch + 0.5, "The vertical throw did not move the tilt.");

        Step(control, 5);
        Require(control.ViewPitch == settledPitch,
            "The tilt was pulled back instead of being kept where the throw left it.");
        RequireNear(control.TurnRate, HullPreviewControl.GentleTurnRate, 1e-3,
            "The vertical throw disturbed the gentle turn.");
    }

    private static HullPreviewControl NewPreview(GeneratedHull hull)
    {
        var control = new HullPreviewControl();
        control.SetHull(hull);
        return control;
    }

    /// <summary>Advances the motion in real frame-sized steps, the way the composition clock would.</summary>
    private static void Step(HullPreviewControl control, double seconds)
    {
        const double frame = 1d / 60;
        for (var elapsed = 0d; elapsed < seconds; elapsed += frame)
            control.AdvanceOrbit(frame);
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static void RequireNear(double actual, double expected, double tolerance, string message) =>
        Require(Math.Abs(actual - expected) <= tolerance, $"{message} (got {actual}, expected {expected}).");
}

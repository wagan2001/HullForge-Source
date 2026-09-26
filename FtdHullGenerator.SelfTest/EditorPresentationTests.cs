using FtdHullGenerator;
using FtdHullGenerator.Domain;
using FtdHullGenerator.Infrastructure;
using FtdHullGenerator.UI;
using static SlopeFillTestSupport;

/// <summary>
/// Covers the editor presentation rules that have no geometry behind them: the width
/// row's odd-only ladder, the custom blueprint name, and the stored palette preference.
/// </summary>
internal static class EditorPresentationTests
{
    public static void Run()
    {
        RunWidthLadder();
        RunBlueprintNames();
        RunThemePreference();
    }

    private static void RunWidthLadder()
    {
        // The width row: origin 1, step 2, travel to 99. Every rung it can land on is odd.
        const int origin = 1;
        const int step = 2;
        const int maximum = 99;

        for (var value = origin; value <= maximum; value++)
        {
            var snapped = StepLadder.Snap(value, origin, step, maximum);
            Require(snapped % 2 == 1, $"Width {value} snapped to the even value {snapped}.");
            Require(snapped >= origin && snapped <= maximum,
                $"Width {value} snapped to {snapped}, outside the row's travel.");
            Require(Math.Abs(snapped - value) <= 1, $"Width {value} snapped further than one metre, to {snapped}.");
        }

        Require(StepLadder.Snap(20, origin, step, maximum) == 21 &&
                StepLadder.Snap(22, origin, step, maximum) == 23,
            "An even width did not round away from zero onto the next odd rung.");

        // The ceiling is approached from below: an even maximum must not round up past it.
        Require(StepLadder.Snap(100, origin, step, 100) == 99,
            "The top of an even travel stepped past the slider maximum.");
        Require(StepLadder.Snap(1000, origin, step, maximum) == 99,
            "A value above the travel was not held at the last rung.");
        Require(StepLadder.Snap(-40, origin, step, maximum) == origin,
            "A value below the travel was not held at the first rung.");

        // Stepping off leaves every whole metre reachable, which is what the debug
        // option restores when even widths are allowed.
        for (var value = 1; value <= 100; value++)
        {
            Require(StepLadder.Snap(value, 1, 1, 100) == value,
                $"An unstepped row altered the whole value {value}.");
        }

        // A ladder that starts odd stays odd wherever it starts from.
        Require(StepLadder.Nearest(8, 3, 2) == 9 && StepLadder.Below(8, 3, 2) == 7,
            "The ladder did not count from its own origin.");
    }

    private static void RunBlueprintNames()
    {
        Require(BlueprintNaming.Sanitize("Ambush Corvette") == "Ambush_Corvette",
            "A custom name did not match the exporter's on-disk spelling.");
        Require(BlueprintNaming.Sanitize("  Ambush   Corvette  ") == "Ambush_Corvette",
            "Whitespace was not normalized in a custom name.");
        Require(BlueprintNaming.Sanitize("Ambush/Corvette:v2") == "AmbushCorvettev2",
            "Characters a file name may not carry survived sanitising.");
        Require(BlueprintNaming.Sanitize("Ambush.") == "Ambush",
            "A trailing dot, which Windows drops silently, was not trimmed.");
        Require(BlueprintNaming.Sanitize("///").Length == 0,
            "A name of nothing but illegal characters did not sanitise to empty.");
        Require(BlueprintNaming.Sanitize("Hull\u0001Forge") == "HullForge",
            "A control character survived sanitising.");
        foreach (var reserved in new[] { "NUL", "con", "PRN.txt", "AUX", "COM1", "LPT9.blueprint" })
        {
            Require(BlueprintNaming.Sanitize(reserved).StartsWith('_'),
                $"Windows reserved device name {reserved} remained reachable.");
        }

        // The generated name is unchanged by any of this.
        Require(MainWindow.CreateBlueprintName(HullParameters.Default) == "HF_Dolphin_100x21x12",
            "Custom naming disturbed the generated name.");
    }

    /// <summary>
    /// The dark workbench is the standard palette, so only an explicit request for the light
    /// bench moves away from it — a missing or unreadable preference must not.
    /// </summary>
    private static void RunThemePreference()
    {
        Require(MainWindow.DecodeThemePreference(null),
            "A missing theme preference did not leave the standard dark workbench in place.");
        Require(MainWindow.DecodeThemePreference("dark"),
            "A stored dark preference did not survive being read back.");
        Require(MainWindow.DecodeThemePreference("  Dark  "),
            "A padded dark preference did not survive being read back.");
        Require(!MainWindow.DecodeThemePreference("light"),
            "A stored light preference was not honoured.");
        Require(!MainWindow.DecodeThemePreference(" LIGHT "),
            "A padded light preference was not honoured.");
        Require(MainWindow.DecodeThemePreference("something else"),
            "An unreadable theme preference did not fall back to the standard dark workbench.");
    }
}

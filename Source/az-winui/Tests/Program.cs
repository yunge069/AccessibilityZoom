using AccessibilityZoom;

if (args.Length == 2 && args[0] == "--verify-release-symbols")
{
    ReleaseArtifactChecks.VerifySymbols(args[1]);
    return;
}

if (args.Contains("--audio-child")) { AudioIntegrationTests.RunChild(); return; }
if (args.Contains("--startup-diagnostics"))
{
    var startupSettings = SettingsStore.Load();
    var connected = DisplayHelper.GetDisplays();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
        startupSettings.StartWithColorEnabled, startupSettings.FilterEnabled, startupSettings.FilterMonitorDevice,
        Displays = connected.Select(d => new { d.DeviceName, Bounds = d.Bounds.ToString(), d.Primary }),
        TargetPresent = DisplayHelper.FindSavedDisplay(connected, startupSettings.FilterMonitorDevice) != null
    }));
    return;
}
int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}

ZoomModifierTests.Run(Check);
CrosshairCursorTests.Run(Check);
GameplayCursorPolicyTests.Run(Check);
await UpdateTests.RunAsync(Check);

HotkeyBindings defaults = HotkeyBindings.Defaults();
Check(defaults[HotkeyAction.AppVolume] == new InputBinding(0x79), "F10 must control both volume actions.");
Check(defaults[HotkeyAction.CycleProfiles] == new InputBinding(0x7A), "F11 must cycle presets.");
Check(defaults[HotkeyAction.HoldZoom] == new InputBinding(5), "Zoom must default to Mouse 4.");
Check(!defaults[HotkeyAction.ToggleZoom].Enabled, "Fresh settings must start with one zoom shortcut.");
Check(defaults[HotkeyAction.AppVolume].ToString() == "F10", "F10 label must match its key code.");

HotkeyStateMachine input = new(defaults);
Check(input.Process(0x79, true, InputModifiers.None, 1000, out var press) && press == null, "Volume must wait for release.");
Check(input.Process(0x79, true, InputModifiers.None, 1400, out press) && press == null, "Auto-repeat must not restart the volume timer.");
Check(input.Process(0x79, false, InputModifiers.Shift, 1499, out press) &&
    press == new HotkeyPress(HotkeyAction.AppVolume, 499), "A tap must retain its duration even if modifiers change.");
Check(!input.Process(0x79, false, InputModifiers.None, 1500, out press) && press == null, "Repeated releases must not act twice.");
input.Process(0x79, true, InputModifiers.None, 2000, out _);
Check(input.Process(0x79, false, InputModifiers.None, 2500, out press) &&
    press == new HotkeyPress(HotkeyAction.AppVolume, 500), "A hold must preserve the quiet-mode threshold.");
Check(!input.Process(0x79, true, InputModifiers.Shift, 3000, out press) && press == null, "Shift+F10 must pass through.");
Check(!input.Process(0x79, true, InputModifiers.None, 3100, out press) && press == null, "Releasing a modifier during repeat must not create a shortcut.");
input.Process(0x79, false, InputModifiers.None, 3200, out _);
Check(input.Process(0x7A, true, InputModifiers.None, 4000, out press) && press?.Action == HotkeyAction.CycleProfiles,
    "F11 must cycle immediately and never call volume.");
Check(input.Process(0x7A, true, InputModifiers.None, 4500, out press) && press == null, "Holding F11 must cycle only once.");
Check(input.Process(0x7A, false, InputModifiers.None, 4600, out press) && press == null, "F11 release must not cycle again.");
Check(!input.Process(6, true, InputModifiers.None, 4700, out press) && press == null, "Mouse 5 must not auto-select a zoom binding.");
input.Process(6, false, InputModifiers.None, 4800, out _);
Check(!input.Process(5, true, InputModifiers.None, 4900, out press), "Mouse 4 must pass through to the 1 ms zoom sampler.");
input.Process(5, false, InputModifiers.None, 4950, out _);

HotkeyBindings swapped = defaults.Assign(HotkeyAction.AppVolume, defaults[HotkeyAction.CycleProfiles]);
Check(swapped[HotkeyAction.AppVolume] == new InputBinding(0x7A) &&
    swapped[HotkeyAction.CycleProfiles] == new InputBinding(0x79), "Duplicate assignment must swap both shortcuts.");
Check(defaults[HotkeyAction.AppVolume] == new InputBinding(0x79), "Editing bindings must not mutate an older snapshot.");
var toggleWithMouse = defaults.AssignZoom(HotkeyAction.HoldZoom, HotkeyAction.ToggleZoom, new InputBinding(5));
Check(!toggleWithMouse[HotkeyAction.HoldZoom].Enabled && toggleWithMouse[HotkeyAction.ToggleZoom] == new InputBinding(5),
    "Changing zoom mode must move the mouse binding to the selected mode.");
var legacyZoom = defaults.Assign(HotkeyAction.ToggleZoom, new InputBinding(0x77));
var combinedZoom = legacyZoom.AssignZoom(HotkeyAction.HoldZoom, HotkeyAction.ToggleZoom, defaults[HotkeyAction.ToggleColor]);
Check(!combinedZoom[HotkeyAction.HoldZoom].Enabled &&
    combinedZoom[HotkeyAction.ToggleZoom] == defaults[HotkeyAction.ToggleColor] &&
    combinedZoom[HotkeyAction.ToggleColor] == defaults[HotkeyAction.HoldZoom],
    "Changing the zoom mode and using an occupied binding must retain the shortcut swap.");
input.Process(0x79, true, InputModifiers.None, 5000, out _);
input.SetBindings(swapped);
Check(input.Process(0x79, false, InputModifiers.None, 5700, out press) && press == null,
    "Rebinding a held volume key must consume its release without applying a stale action.");
Check(input.Process(0x79, true, InputModifiers.None, 5800, out press) && press?.Action == HotkeyAction.CycleProfiles,
    "A remapped key must use the new action on its next press.");

foreach (HotkeyAction action in HotkeyBindings.Actions)
{
    var mouse = new InputBinding(6, InputModifiers.Control | InputModifiers.Alt);
    var mapped = defaults.Assign(action, mouse);
    Check(mapped[action] == mouse, action + " must accept a mouse shortcut with modifiers.");
    Check(InputBinding.TryParse(mouse.Serialize(), out var parsed) && parsed == mouse, "Mouse shortcut must round trip.");
    var keyboard = new InputBinding(0x4B, InputModifiers.Shift | InputModifiers.Windows);
    mapped = mapped.Assign(action, keyboard);
    Check(mapped[action] == keyboard, action + " must accept a keyboard shortcut with modifiers.");
    mapped = mapped.Assign(action, default);
    Check(!mapped[action].Enabled, action + " must support unassigning.");
    var router = new HotkeyStateMachine(mapped);
    Check(!router.Process(defaults[action].Key, true, defaults[action].Modifiers, 0, out _), "Unassigned actions must not fire.");
}

var mouseVolume = defaults.Assign(HotkeyAction.AppVolume, new InputBinding(6, InputModifiers.Control));
input = new(mouseVolume);
Check(!input.Process(6, true, InputModifiers.None, 0, out _), "Unmodified mouse shortcut must pass through when Ctrl is required.");
input.Process(6, false, InputModifiers.None, 10, out _);
Check(input.Process(6, true, InputModifiers.Control, 20, out press) && press == null, "Modified mouse volume must wait for release.");
Check(input.Process(6, false, InputModifiers.None, 720, out press) && press?.HeldMilliseconds == 700,
    "Mouse volume holds must retain quiet-mode timing after Ctrl release.");
input = new(defaults);
input.SeedDown(0x79);
Check(!input.Process(0x79, true, InputModifiers.None, 100, out _), "Keys held at startup must wait for release.");
input.Process(0x79, false, InputModifiers.None, 200, out _);
Check(input.Process(0x79, true, InputModifiers.None, int.MaxValue - 100, out _), "F10 must accept a fresh press.");
input.Process(0x79, false, InputModifiers.None, unchecked(int.MaxValue + 500), out press);
Check(press?.HeldMilliseconds == 600, "Volume hold timing must survive TickCount wraparound.");

var leftClick = defaults.Assign(HotkeyAction.ToggleZoom, new InputBinding(1));
input = new(leftClick);
Check(!input.Process(1, true, InputModifiers.None, 0, out _, allowNewPress: false), "Control-panel mouse clicks must remain usable.");
Check(!input.Process(1, false, InputModifiers.None, 10, out _, allowNewPress: false), "Control-panel click release must pass through.");
Check(input.Process(1, true, InputModifiers.None, 20, out press) && press?.Action == HotkeyAction.ToggleZoom,
    "Left mouse bindings must work outside the control panel.");
Check(input.Process(1, false, InputModifiers.None, 30, out _, allowNewPress: false),
    "An accepted outside click must consume its matching release even after focus changes.");

foreach (string invalid in new[] { "oops", "121", "0,1", "16,0", "121,16", "-1,0", "999,0", "121,0,0" })
    Check(!InputBinding.TryParse(invalid, out _), "Malformed bindings must be rejected: " + invalid);

string directory = Path.Combine(Path.GetTempPath(), "az-hotkeys-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "zoom.txt");
try
{
    var fresh = SettingsStore.Load(path);
    Check(fresh.SelectedProfileId == string.Empty && fresh.ColorFilter.NearlyEquals(ColorFilterSettings.Neutral),
        "A new installation must start with neutral colors and no selected preset.");
    File.WriteAllText(path, "zoom=2.75\nbutton=2\nprofileId=night\nimageSharpening=1\nimageSharpness=75\n");
    var legacy = SettingsStore.Load(path);
    Check(legacy.Zoom == 2.75f && legacy.SelectedProfileId == "night", "Legacy zoom/profile settings must survive migration.");
    Check(!legacy.ColorFilter.RadeonImageSharpening && legacy.ColorFilter.RadeonImageSharpeningSharpness == 0,
        "A legacy built-in preset must still migrate its old sharpening values.");
    Check(legacy.StartWithColorEnabled, "Existing installations must enable the saved monitor preset on startup by default.");
    Check(legacy.QuietVolumePercent == 30, "Settings without a quiet volume must retain the 30 percent default.");
    Check(legacy.Bindings[HotkeyAction.HoldZoom] == new InputBinding(5), "Legacy auto-selected Mouse 5 must migrate to Mouse 4.");
    var custom = swapped.Assign(HotkeyAction.HoldZoom, new InputBinding(0x48, InputModifiers.Control))
        .Assign(HotkeyAction.ToggleZoom, new InputBinding(0x77))
        .Assign(HotkeyAction.Exit, default);
    ColorFilterSettings updatedNight = ColorFilterSettings.Night;
    updatedNight.RadeonImageSharpening = true;
    updatedNight.RadeonImageSharpeningSharpness = 35;
    Check(SettingsStore.Save(2.75f, 1, true, updatedNight, "night", true, true, out string error,
        path, custom), "Settings save failed: " + error);
    var loaded = SettingsStore.Load(path);
    Check(loaded.Bindings.SameAs(custom), "Every swapped, modified, and unassigned binding must survive restart.");
    Check(loaded.SoundsEnabled && loaded.ReduceTransparency && loaded.FilterEnabled, "Other settings must remain intact.");
    Check(loaded.ColorFilter.NearlyEquals(updatedNight), "Color values for an editable preset must remain intact.");
    Check(loaded.StartWithColorEnabled, "Monitor startup preference must default to enabled when saving.");
    Check(SettingsStore.Save(2.75f, 1, false, ColorFilterSettings.Night, "night", true, true, out error,
        path, custom, false, @"\\.\DISPLAY2", quietVolumePercent: 0), "Settings save failed: " + error);
    var startup = SettingsStore.Load(path);
    Check(!startup.FilterEnabled && !startup.StartWithColorEnabled && startup.FilterMonitorDevice == @"\\.\DISPLAY2",
        "Startup preference and monitor identity must persist independently of the temporary enabled state.");
    Check(startup.QuietVolumePercent == 0, "A quiet volume of zero must survive a restart.");
    File.AppendAllText(path, "quietVolumePercent=65\nquietVolumePercent=invalid\n");
    Check(SettingsStore.Load(path).QuietVolumePercent == 65, "A malformed quiet volume must not overwrite a valid value.");
    File.AppendAllText(path, "hotkey.AppVolume=garbage\nhotkey.Unknown=121,0\nhotkey.999=121,0\n");
    Check(SettingsStore.Load(path).Bindings.SameAs(custom), "Invalid entries must not destroy valid saved bindings.");
    // Exercise permutations, including swaps through disabled assignments.
    for (int round = 0; round < 30; round++)
    {
        foreach (HotkeyAction action in HotkeyBindings.Actions)
            custom = custom.Assign(action, round % 3 == 0 ? default : custom[HotkeyBindings.Actions[(int)(action + round) % HotkeyBindings.Actions.Length]]);
        SettingsStore.Save(2, 1, false, ColorFilterSettings.General, "general", false, false, out error, path, custom);
        Check(SettingsStore.Load(path).Bindings.SameAs(custom), "Binding permutation must survive serialization.");
    }
}
finally
{
    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
Check(ScopeMath.RunSelfTest(), "Existing zoom math and color profile self-tests must pass.");
foreach (var screen in new[] { new System.Drawing.Rectangle(0, 0, 1920, 1080),
    new System.Drawing.Rectangle(-2560, -1440, 2560, 1440), new System.Drawing.Rectangle(1920, 0, 2560, 1440) })
{
    foreach (float zoom in new[] { 1f, 1.25f, 2f, 4f, 8f })
    {
        var target = new System.Drawing.Rectangle(screen.Right - 300, screen.Top + 20, 280, 82);
        var offset = ScopeMath.CalculateFixedCenterOffset(screen, zoom);
        var source = ScopeMath.CalculateOverlaySourceBounds(target, zoom, offset);
        Check(Math.Abs((source.X - offset.X) * zoom - target.X) <= zoom / 2 &&
            Math.Abs((source.Y - offset.Y) * zoom - target.Y) <= zoom / 2,
            "Preset popup must stay at the top right while zoom changes, including negative monitor coordinates.");
        Check(source.Width * zoom >= target.Width && source.Height * zoom >= target.Height,
            "Popup window must not clip its scaled content.");
    }
}
string previewDirectory = args.FirstOrDefault(argument => argument.StartsWith("--preview-directory="))?.Substring("--preview-directory=".Length);
await CrosshairTests.RunAsync(Check, args.Contains("--live-crosshair-tests"), previewDirectory);
await DesktopTransformTests.RunAsync(Check);
CrosshairScalingTests.Run(Check, previewDirectory);

QuietVolumePolicyTests.Run(Check);
var displays = new[] {
    new DisplayChoice(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), true),
    new DisplayChoice(@"\\.\DISPLAY2", new(-2560, 0, 2560, 1440), false)
};
Check(DisplayHelper.FindSavedDisplay(displays, "") == displays[0], "Legacy startup must choose the primary monitor.");
Check(DisplayHelper.FindSavedDisplay(displays.Reverse(), @"\\.\display2") == displays[1],
    "Saved monitor identity must survive enumeration order and use current coordinates.");
Check(DisplayHelper.FindSavedDisplay(displays, @"\\.\DISPLAY3") == null,
    "Disconnected saved monitors must not redirect startup color to a different monitor.");
if (args.Contains("--audio-self-test")) AudioIntegrationTests.Run(Check);
await StartupTests.RunAsync(Check);
Console.WriteLine($"Passed {checks} checks: hotkeys, volume timing, persistence, crosshair imports/rendering/placement, and zoom/color math.");

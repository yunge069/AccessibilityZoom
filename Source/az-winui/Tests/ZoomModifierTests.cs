using AccessibilityZoom;

internal static class ZoomModifierTests
{
    internal static void Run(Action<bool, string> check)
    {
        var zoom = new InputBinding(5);
        for (int mask = 0; mask < 16; mask++)
        {
            var modifiers = (InputModifiers)mask;
            check(zoom.MatchesHold(true, modifiers), "Extra gameplay modifiers must not block or cancel hold zoom: " + modifiers);
            check(!zoom.MatchesHold(false, modifiers), "Releasing zoom must stop it with any modifier held.");
        }
        var chord = new InputBinding(5, InputModifiers.Control);
        check(!chord.MatchesHold(true, InputModifiers.Shift), "Explicit zoom modifiers must still be required.");
        check(chord.MatchesHold(true, InputModifiers.Control | InputModifiers.Shift), "Extra modifiers must not block explicitly modified zoom.");
        check(!default(InputBinding).MatchesHold(true, InputModifiers.None), "Unassigned zoom must stay off.");
        check(!new InputBinding(1).MatchesHold(true, InputModifiers.Control, true), "Own control panel must remain clickable.");
        check(new InputBinding(0x48).MatchesHold(true, InputModifiers.Control | InputModifiers.Alt), "Keyboard zoom must tolerate extra modifiers too.");
        var bindings = HotkeyBindings.Defaults().Assign(HotkeyAction.ToggleColor, new InputBinding(5, InputModifiers.Control));
        var input = new HotkeyStateMachine(bindings);
        check(!input.Process(5, true, InputModifiers.Control, 0, out var press) && press == null,
            "Zoom must take priority over another action on its key with extra modifiers.");
        check(!input.Process(5, false, InputModifiers.Control, 10, out press), "Zoom release must pass through to the sampler.");
        input = new HotkeyStateMachine(bindings.Assign(HotkeyAction.HoldZoom, chord)
            .Assign(HotkeyAction.ToggleZoom, new InputBinding(0x77)));
        check(!input.Process(5, true, InputModifiers.Control | InputModifiers.Shift, 20, out press) && press == null,
            "Modified hold zoom must keep priority with extra modifiers.");
        check(input.Process(0x77, true, InputModifiers.Control, 30, out press) && press?.Action == HotkeyAction.ToggleZoom,
            "Toggle zoom on F8 must trigger even when Ctrl or extra modifiers are held.");
        check(input.Process(0x77, false, InputModifiers.Control, 40, out _), "Toggle zoom release must be accepted.");
    }
}

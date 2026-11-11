using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AccessibilityZoom
{
    [Flags]
    internal enum InputModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }

    internal enum HotkeyAction
    {
        HoldZoom, ToggleZoom, ToggleColor, CycleProfiles, AppVolume,
        IncreaseZoom, DecreaseZoom, ResetView, Exit, ToggleCrosshair
    }

    internal readonly record struct InputBinding(int Key, InputModifiers Modifiers = InputModifiers.None)
    {
        internal bool Enabled => Key != 0;
        internal bool IsMouse => Key is 1 or 2 or 4 or 5 or 6;
        internal bool IsValid => Key == 0 ? Modifiers == InputModifiers.None :
            Key > 0 && Key <= 0xFE && Key is not (3 or 7 or 0xFF) && !IsModifier(Key) &&
            (Modifiers & ~(InputModifiers.Control | InputModifiers.Alt | InputModifiers.Shift | InputModifiers.Windows)) == 0;

        internal static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
            0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

        internal static InputModifiers ReadModifiers()
        {
            InputModifiers result = InputModifiers.None;
            if (Down(0x11)) result |= InputModifiers.Control;
            if (Down(0x12)) result |= InputModifiers.Alt;
            if (Down(0x10)) result |= InputModifiers.Shift;
            if (Down(0x5B) || Down(0x5C)) result |= InputModifiers.Windows;
            return result;
        }

        private static bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
        // Hold zoom requires configured modifiers; extra gameplay keys never cancel it.
        internal bool MatchesHold(bool keyDown, InputModifiers modifiers, bool ownWindowForeground = false) =>
            Enabled && keyDown && (modifiers & Modifiers) == Modifiers &&
            (Key is not (1 or 2) || !ownWindowForeground);

        internal bool IsDown() => MatchesHold(Down(Key), ReadModifiers(),
            Key is 1 or 2 && NativeMethods.IsOwnWindowForeground());

        public override string ToString()
        {
            if (!Enabled) return "Unassigned";
            string prefix = "";
            if (Modifiers.HasFlag(InputModifiers.Control)) prefix += "Ctrl + ";
            if (Modifiers.HasFlag(InputModifiers.Alt)) prefix += "Alt + ";
            if (Modifiers.HasFlag(InputModifiers.Shift)) prefix += "Shift + ";
            if (Modifiers.HasFlag(InputModifiers.Windows)) prefix += "Win + ";
            return prefix + KeyName(Key);
        }

        internal string Serialize() => Key.ToString(CultureInfo.InvariantCulture) + "," +
            ((int)Modifiers).ToString(CultureInfo.InvariantCulture);

        internal static bool TryParse(string value, out InputBinding binding)
        {
            binding = default;
            string[] parts = value.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int key) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int modifiers)) return false;
            binding = new InputBinding(key, (InputModifiers)modifiers);
            return binding.IsValid;
        }

        internal static string KeyName(int key)
        {
            if (key >= 0x70 && key <= 0x87) return "F" + (key - 0x6F);
            if (key >= 0x30 && key <= 0x39 || key >= 0x41 && key <= 0x5A) return ((char)key).ToString();
            if (key >= 0x60 && key <= 0x69) return "Numpad " + (key - 0x60);
            return key switch
            {
                0 => "Unassigned", 1 => "Mouse 1 · Left", 2 => "Mouse 2 · Right", 4 => "Mouse 3 · Middle",
                5 => "Mouse 4 · Back", 6 => "Mouse 5 · Forward", 8 => "Backspace", 9 => "Tab", 13 => "Enter",
                0x13 => "Pause", 0x14 => "Caps Lock", 0x1B => "Escape", 0x20 => "Space", 0x21 => "Page Up",
                0x22 => "Page Down", 0x23 => "End", 0x24 => "Home", 0x25 => "Left arrow", 0x26 => "Up arrow",
                0x27 => "Right arrow", 0x28 => "Down arrow", 0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete",
                0x5D => "Menu", 0x6A => "Numpad *", 0x6B => "Numpad +", 0x6D => "Numpad -",
                0x6E => "Numpad decimal", 0x6F => "Numpad /", 0x90 => "Num Lock", 0x91 => "Scroll Lock",
                0xAD => "Volume mute", 0xAE => "Volume down", 0xAF => "Volume up",
                0xB0 => "Media next", 0xB1 => "Media previous", 0xB2 => "Media stop", 0xB3 => "Media play/pause",
                0xBA => ";", 0xBB => "+ / =", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
                0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'", 0xE2 => "OEM 102",
                _ => "Key 0x" + key.ToString("X2")
            };
        }

        internal static IEnumerable<int> SelectableKeys => new[] { 0, 1, 2, 4, 5, 6, 8, 9, 13, 0x13, 0x14, 0x1B }
            .Concat(Enumerable.Range(0x20, 9)).Concat(new[] { 0x2C, 0x2D, 0x2E })
            .Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x41, 26)).Concat(new[] { 0x5D })
            .Concat(Enumerable.Range(0x60, 10)).Concat(new[] { 0x6A, 0x6B, 0x6D, 0x6E, 0x6F })
            .Concat(Enumerable.Range(0x70, 24)).Concat(new[] { 0x90, 0x91 })
            .Concat(Enumerable.Range(0xAD, 7)).Concat(new[] { 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0xE2 });
    }

    // Immutable snapshots make a complete swap visible to input threads at once.
    internal sealed class HotkeyBindings
    {
        internal static readonly HotkeyAction[] Actions = Enum.GetValues<HotkeyAction>();
        private readonly Dictionary<HotkeyAction, InputBinding> values;
        private HotkeyBindings(Dictionary<HotkeyAction, InputBinding> values) { this.values = values; }
        internal InputBinding this[HotkeyAction action] => values[action];
        internal static HotkeyBindings Defaults() => new HotkeyBindings(new Dictionary<HotkeyAction, InputBinding>
        {
            [HotkeyAction.HoldZoom] = new(0x05), [HotkeyAction.ToggleZoom] = default,
            [HotkeyAction.ToggleColor] = new(0x78), [HotkeyAction.CycleProfiles] = new(0x7A),
            [HotkeyAction.AppVolume] = new(0x79),
            [HotkeyAction.IncreaseZoom] = new(0xBB, InputModifiers.Control | InputModifiers.Alt),
            [HotkeyAction.DecreaseZoom] = new(0xBD, InputModifiers.Control | InputModifiers.Alt),
            [HotkeyAction.ResetView] = new(0x30, InputModifiers.Control | InputModifiers.Alt),
            [HotkeyAction.Exit] = new(0x51, InputModifiers.Control | InputModifiers.Alt),
            [HotkeyAction.ToggleCrosshair] = new(0x76)
        });

        internal HotkeyBindings Assign(HotkeyAction action, InputBinding binding)
        {
            if (!binding.IsValid) throw new ArgumentException("Invalid shortcut.", nameof(binding));
            var next = new Dictionary<HotkeyAction, InputBinding>(values);
            if (binding.Enabled)
                foreach (HotkeyAction other in Actions)
                    if (other != action && next[other] == binding) next[other] = values[action];
            next[action] = binding;
            return new HotkeyBindings(next);
        }

        internal HotkeyBindings AssignZoom(HotkeyAction currentAction, HotkeyAction nextAction, InputBinding binding)
        {
            HotkeyAction secondary = currentAction == HotkeyAction.HoldZoom ? HotkeyAction.ToggleZoom : HotkeyAction.HoldZoom;
            HotkeyBindings next = Assign(secondary, default).Assign(currentAction, binding);
            return currentAction == nextAction ? next : next.Assign(currentAction, default).Assign(nextAction, binding);
        }

        internal bool SameAs(HotkeyBindings other) => other != null && Actions.All(action => this[action] == other[action]);
        internal static string Label(HotkeyAction action) => action switch
        {
            HotkeyAction.HoldZoom => "Hold to zoom", HotkeyAction.ToggleZoom => "Toggle zoom",
            HotkeyAction.ToggleColor => "Toggle display color", HotkeyAction.CycleProfiles => "Next monitor preset",
            HotkeyAction.AppVolume => "Mute / reduce app volume", HotkeyAction.IncreaseZoom => "Increase zoom",
            HotkeyAction.DecreaseZoom => "Decrease zoom", HotkeyAction.ResetView => "Reset view",
            HotkeyAction.ToggleCrosshair => "Toggle crosshair", _ => "Exit app"
        };
    }

    internal readonly record struct HotkeyPress(HotkeyAction Action, int HeldMilliseconds);

    // Key-up belongs to the accepted key-down even if modifiers or settings changed.
    internal sealed class HotkeyStateMachine
    {
        private readonly HashSet<int> down = new HashSet<int>();
        private readonly Dictionary<int, (HotkeyAction Action, int Tick, int Generation)> accepted = new();
        private HotkeyBindings bindings;
        private int generation;
        internal HotkeyStateMachine(HotkeyBindings bindings) { this.bindings = bindings; }
        internal void SetBindings(HotkeyBindings next) { bindings = next; generation++; }
        internal void SeedDown(int key) => down.Add(key);

        internal bool Process(int key, bool isDown, InputModifiers modifiers, int tick, out HotkeyPress? press, bool allowNewPress = true)
        {
            press = null;
            if (key == 0) return false;
            if (!isDown)
            {
                down.Remove(key);
                if (!accepted.Remove(key, out var held)) return false;
                if (held.Action == HotkeyAction.AppVolume && held.Generation == generation)
                    press = new HotkeyPress(held.Action, unchecked(tick - held.Tick));
                return true;
            }
            if (!down.Add(key)) return accepted.ContainsKey(key);
            if (!allowNewPress) return false;
            // Reserve the physical zoom key for the engine before other shortcuts.
            InputBinding holdZoom = bindings[HotkeyAction.HoldZoom];
            if (holdZoom.Key == key && holdZoom.MatchesHold(true, modifiers)) return false;
            foreach (HotkeyAction action in HotkeyBindings.Actions)
            {
                // The engine samples hold-to-zoom at 1 ms. Let its physical input pass.
                if (action == HotkeyAction.HoldZoom) continue;
                if (bindings[action] != new InputBinding(key, modifiers)) continue;
                accepted[key] = (action, tick, generation);
                if (action != HotkeyAction.AppVolume) press = new HotkeyPress(action, 0);
                return true;
            }
            // Toggle zoom on dedicated keys (mouse / function keys) tolerates extra gameplay modifiers.
            InputBinding toggleZoom = bindings[HotkeyAction.ToggleZoom];
            if (toggleZoom.Enabled && toggleZoom.Key == key && (toggleZoom.IsMouse || (key >= 0x70 && key <= 0x87)) &&
                (modifiers & toggleZoom.Modifiers) == toggleZoom.Modifiers)
            {
                accepted[key] = (HotkeyAction.ToggleZoom, tick, generation);
                press = new HotkeyPress(HotkeyAction.ToggleZoom, 0);
                return true;
            }
            return false;
        }
    }
}

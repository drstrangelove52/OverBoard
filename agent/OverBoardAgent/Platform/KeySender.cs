using System.Runtime.InteropServices;

namespace OverBoardAgent.Platform;

/// <summary>
/// Sendet Tastenkombinationen wie "Ctrl+Shift+S" oder "Win+D" mit SendInput.
/// Grenzen (Windows): Fenster mit Administratorrechten nehmen Eingaben eines normal gestarteten Programms nicht an
/// (UIPI), und Win+L lässt sich nicht senden (dafür gibt es die Aktion "lock").
/// </summary>
public static class KeySender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    // Die Union von INPUT ist so gross wie MOUSEINPUT: ohne diese Auffüllung stimmt die Strukturgrösse nicht
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["strg"] = 0x11, ["control"] = 0x11,
        ["shift"] = 0x10, ["umschalt"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B,
    };

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["leertaste"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["entf"] = 0x2E,
        ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["printscreen"] = 0x2C, ["druck"] = 0x2C,
        ["mediaplaypause"] = 0xB3, ["medianext"] = 0xB0, ["mediaprev"] = 0xB1, ["mediastop"] = 0xB2,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
    };

    private static readonly HashSet<ushort> Extended = [0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x26, 0x28, 0x25, 0x27, 0x5B];

    /// <summary>Gibt die Tasten der Kombination zurück (zuerst die Modifizierer), oder null mit Fehlertext.</summary>
    public static List<ushort>? Parse(string combo, out string? error)
    {
        error = null;
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "leer";
            return null;
        }
        var keys = new List<ushort>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (i < parts.Length - 1)
            {
                if (!Modifiers.TryGetValue(p, out var m))
                {
                    error = $"'{p}' ist kein Modifizierer (Ctrl, Shift, Alt, Win)";
                    return null;
                }
                keys.Add(m);
            }
            else if (Modifiers.TryGetValue(p, out var m2)) keys.Add(m2);   // z. B. nur "Win"
            else if (Named.TryGetValue(p, out var n)) keys.Add(n);
            else if (p.Length >= 2 && (p[0] is 'F' or 'f') && int.TryParse(p[1..], out var f) && f is >= 1 and <= 24)
                keys.Add((ushort)(0x70 + f - 1));
            else if (p.Length == 1 && char.IsAsciiLetterOrDigit(p[0])) keys.Add((ushort)char.ToUpperInvariant(p[0]));
            else
            {
                error = $"Taste '{p}' unbekannt";
                return null;
            }
        }
        return keys;
    }

    public static bool Send(string combo)
    {
        var keys = Parse(combo, out _);
        if (keys == null) return false;

        var inputs = new List<Input>();
        foreach (var k in keys) inputs.Add(Make(k, up: false));
        for (var i = keys.Count - 1; i >= 0; i--) inputs.Add(Make(keys[i], up: true));
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        return sent == inputs.Count;
    }

    private static Input Make(ushort vk, bool up)
    {
        var flags = (Extended.Contains(vk) ? KeyEventExtended : 0) | (up ? KeyEventKeyUp : 0u);
        return new Input { Type = InputKeyboard, Keyboard = new KeyboardInput { VirtualKey = vk, Flags = flags } };
    }
}

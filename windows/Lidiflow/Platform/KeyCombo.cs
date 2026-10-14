using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Lidiflow.Platform;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// A global shortcut. One of three kinds:
/// - a key plus optional modifiers (Ctrl + Alt + T, F6, Ctrl + Win + Espaço)
/// - modifiers alone, held or tapped (Ctrl + Win, Ctrl dir.)
/// - an extra mouse button plus optional modifiers (botão do meio, botões laterais)
/// Key codes are Windows virtual-key codes.
public sealed class KeyCombo : IEquatable<KeyCombo>
{
    public int? KeyCode { get; init; }
    public Mods Modifiers { get; init; }
    public string KeyLabel { get; init; } = "";
    /// For a single modifier alone: which physical key (left/right side).
    public int? ModifierKey { get; init; }
    /// Mouse button number (2 = middle, 3 = back, 4 = forward), as in the macOS app.
    public int? MouseButton { get; init; }

    /// Physical modifier keys and the flag each one sets.
    public static readonly IReadOnlyDictionary<int, (Mods Flag, string Label)> ModifierKeys =
        new Dictionary<int, (Mods Flag, string Label)>
        {
            [0xA2] = (Mods.Ctrl, "Ctrl esq."), [0xA3] = (Mods.Ctrl, "Ctrl dir."),
            [0xA4] = (Mods.Alt, "Alt esq."), [0xA5] = (Mods.Alt, "Alt dir."),
            [0xA0] = (Mods.Shift, "Shift esq."), [0xA1] = (Mods.Shift, "Shift dir."),
            [0x5B] = (Mods.Win, "Win esq."), [0x5C] = (Mods.Win, "Win dir."),
            [0x11] = (Mods.Ctrl, "Ctrl"), [0x12] = (Mods.Alt, "Alt"), [0x10] = (Mods.Shift, "Shift"),
        };

    private static readonly Dictionary<int, string> SpecialLabels = new()
    {
        [0x20] = "Espaço", [0x0D] = "Enter", [0x09] = "Tab", [0x08] = "Backspace", [0x2E] = "Del",
        [0x2D] = "Insert", [0x1B] = "Esc", [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓",
        [0x24] = "Home", [0x23] = "End", [0x21] = "PgUp", [0x22] = "PgDn", [0x14] = "Caps Lock",
        [0x13] = "Pause", [0x2C] = "PrtSc",
    };

    public static KeyCombo Key(int vk, Mods modifiers, string? label = null) =>
        new() { KeyCode = vk, Modifiers = modifiers, KeyLabel = label ?? LabelFor(vk) };

    /// Modifiers alone. `side` pins a single modifier to one side of the keyboard.
    public static KeyCombo ModifiersOnly(Mods modifiers, int? side = null) =>
        new() { Modifiers = modifiers, ModifierKey = side };

    public static KeyCombo Mouse(int button, Mods modifiers) => new() { MouseButton = button, Modifiers = modifiers };

    [JsonIgnore]
    public bool IsModifierOnly => KeyCode == null && MouseButton == null;

    /// Key cap labels, e.g. ["Ctrl", "Win", "Espaço"].
    [JsonIgnore]
    public List<string> Keys
    {
        get
        {
            if (IsModifierOnly && ModifierKey is int side && ModifierKeys.TryGetValue(side, out var info))
            {
                return new List<string> { info.Label };
            }
            var keys = new List<string>();
            if (Modifiers.HasFlag(Mods.Ctrl)) keys.Add("Ctrl");
            if (Modifiers.HasFlag(Mods.Win)) keys.Add("Win");
            if (Modifiers.HasFlag(Mods.Alt)) keys.Add("Alt");
            if (Modifiers.HasFlag(Mods.Shift)) keys.Add("Shift");
            if (MouseButton is int button) keys.Add(MouseLabel(button));
            if (KeyCode != null) keys.Add(KeyLabel);
            return keys;
        }
    }

    [JsonIgnore]
    public string Label => string.Join(" + ", Keys);

    public static string MouseLabel(int button) => button switch
    {
        2 => "Mouse meio",
        3 => "Mouse voltar",
        4 => "Mouse avançar",
        _ => $"Mouse {button + 1}",
    };

    public static bool IsFunctionKey(int vk) => vk >= 0x70 && vk <= 0x87;

    public static string LabelFor(int vk)
    {
        if (IsFunctionKey(vk)) return "F" + (vk - 0x6F);
        if (SpecialLabels.TryGetValue(vk, out var label)) return label;
        if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
        var character = Native.MapVirtualKey((uint)vk, 2) & 0x7FFF;
        return character > 0 ? ((char)character).ToString().ToUpperInvariant() : $"Tecla {vk}";
    }

    /// Whether the combo can be a global shortcut without blocking normal typing.
    /// A plain key is only allowed for F-keys (or anything when `allowPlainKeys`).
    public bool IsUsable(bool allowPlainKeys = false)
    {
        if (IsModifierOnly) return Modifiers != Mods.None;
        if (MouseButton != null) return true;
        return Modifiers != Mods.None || allowPlainKeys || IsFunctionKey(KeyCode ?? -1);
    }

    public bool Matches(int keyCode, Mods modifiers) => KeyCode == keyCode && Modifiers == modifiers;

    public bool MatchesMouse(int button, Mods modifiers) => MouseButton == button && Modifiers == modifiers;

    /// Whether held modifiers, just changed by physical key `changedKey`, press this combo.
    public bool MatchesModifiers(Mods modifiers, int changedKey) =>
        IsModifierOnly && Modifiers == modifiers && (ModifierKey == null || ModifierKey == changedKey);

    // Identity ignores the display label.
    public bool Equals(KeyCombo? other) =>
        other != null && KeyCode == other.KeyCode && Modifiers == other.Modifiers
        && ModifierKey == other.ModifierKey && MouseButton == other.MouseButton;

    public override bool Equals(object? obj) => Equals(obj as KeyCombo);

    public override int GetHashCode() => HashCode.Combine(KeyCode, Modifiers, ModifierKey, MouseButton);
}

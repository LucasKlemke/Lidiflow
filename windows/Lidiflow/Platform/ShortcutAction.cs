using System.Collections.Generic;

namespace Lidiflow.Platform;

/// Things a global shortcut can do. Each action takes up to two shortcuts.
public enum ShortcutAction { Dictate, HandsFree, Command, PasteLast, CopyLast, Cancel }

public static class ShortcutActions
{
    public const int MaxShortcuts = 2;

    public static readonly ShortcutAction[] All =
    {
        ShortcutAction.Dictate, ShortcutAction.HandsFree, ShortcutAction.Command,
        ShortcutAction.PasteLast, ShortcutAction.CopyLast, ShortcutAction.Cancel,
    };

    public static string Title(this ShortcutAction action) => action switch
    {
        ShortcutAction.Dictate => "Ditar",
        ShortcutAction.HandsFree => "Mãos livres",
        ShortcutAction.Command => "Modo comando",
        ShortcutAction.PasteLast => "Colar a última transcrição",
        ShortcutAction.CopyLast => "Copiar a última transcrição",
        _ => "Cancelar o ditado",
    };

    public static string Detail(this ShortcutAction action) => action switch
    {
        ShortcutAction.Dictate => "Segure para falar, solte para colar; dois toques: mãos livres",
        ShortcutAction.HandsFree => "Aperte para começar e de novo para parar",
        ShortcutAction.Command => "Segure com um texto selecionado e diga o que mudar",
        ShortcutAction.PasteLast or ShortcutAction.CopyLast => "Funciona em qualquer app",
        _ => "Só vale enquanto estiver gravando",
    };

    /// Held actions run while the shortcut is down; the rest fire once.
    public static bool IsHold(this ShortcutAction action) =>
        action is ShortcutAction.Dictate or ShortcutAction.Command;

    /// Windows keyboards have no Fn the OS can see, so Ctrl + Win plays its part.
    public static Dictionary<ShortcutAction, List<KeyCombo>> Defaults()
    {
        const Mods Base = Mods.Ctrl | Mods.Win;
        return new Dictionary<ShortcutAction, List<KeyCombo>>
        {
            [ShortcutAction.Dictate] = new() { KeyCombo.ModifiersOnly(Base) },
            [ShortcutAction.HandsFree] = new() { KeyCombo.Key(0x20, Base, "Espaço") },
            [ShortcutAction.Command] = new() { KeyCombo.ModifiersOnly(Base | Mods.Alt) },
            [ShortcutAction.PasteLast] = new() { KeyCombo.Key(0x56, Mods.Alt | Mods.Win, "V") },
            [ShortcutAction.CopyLast] = new() { KeyCombo.Key(0x43, Mods.Alt | Mods.Win, "C") },
            [ShortcutAction.Cancel] = new() { KeyCombo.Key(0x1B, Mods.None, "Esc") },
        };
    }
}

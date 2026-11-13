using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Wmap.Client;

/// <summary>Routes the existing map action without changing the player's saved key combination.</summary>
public sealed class MapKeyBinding : IDisposable
{
    public const string Code = "worldmapdialog";
    readonly HotKey hotkey;
    readonly ActionConsumable<KeyCombination> previous, handler;
    readonly string previousName;
    public MapKeyBinding(HotKey hotkey, Action open)
    {
        this.hotkey = hotkey;
        previous = hotkey.Handler; previousName = hotkey.Name;
        handler = _ => { open(); return true; };
        hotkey.Name = "Wmap: world atlas";
        hotkey.Handler = handler;
    }
    public bool OpenVanilla() => previous?.Invoke(hotkey.CurrentMapping) ?? false;
    public void Dispose()
    {
        // Do not overwrite a handler subsequently installed by another mod.
        if (hotkey.Handler != handler) return;
        hotkey.Handler = previous; hotkey.Name = previousName;
    }
}

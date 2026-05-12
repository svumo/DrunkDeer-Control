namespace Driver;

// Maps a connected keyboard (HID device + spec response) to one of the 19
// known DrunkDeer models in KeyboardModels.All.
//
// `KeyboardWithSpecs` is the tuple alias declared globally in
// Driver/Properties/GlobalUsings.cs — same shape KeyboardManager exposes.
//
// Lookup priority (highest confidence first):
//   1. The keyboard reported a TypeCode via its spec response — that's
//      directly from firmware and unambiguous.
//   2. Fall back to the USB Product ID — less confident because the same
//      PID can appear on multiple variants and the PID-to-model map is
//      only partially populated (mostly null until telemetry confirms).
//   3. Return null when none match. Callers should fall back to a default
//      layout (e.g. A75 Pro) and surface an "unknown model" warning in
//      the UI so the user can report it.
public static class KeyboardLayoutResolver
{
    public static KeyboardModel? Resolve(KeyboardWithSpecs? keyboard)
    {
        if (keyboard is not { } kb) return null;

        if (kb.Specs.KeyboardType is int typeCode &&
            KeyboardModels.FindByTypeCode(typeCode) is { } byType)
        {
            return byType;
        }

        if (KeyboardModels.FindByPid(kb.Keyboard.ProductID) is { } byPid)
        {
            return byPid;
        }

        return null;
    }
}

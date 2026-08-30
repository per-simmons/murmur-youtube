using System.Runtime.InteropServices;
using Murmur.Abstractions;

namespace Murmur.Platform.Windows;

/// <summary>
/// Types text into whatever application has focus.
/// </summary>
/// <remarks>
/// <para>
/// <b>UI Automation is not an option here.</b> On macOS the Accessibility API can write text
/// directly; Windows has no counterpart. <c>TextPattern</c> is documented as providing no
/// means to insert or modify text, and <c>ValuePattern</c> replaces an entire field rather
/// than inserting at the caret — and multi-line controls do not support it at all. Microsoft's
/// own sample code says plainly: "text input must be simulated."
/// </para>
/// <para>
/// So <c>SendInput</c> is the primary path, not a fallback. Short text is typed as Unicode
/// packets, which leaves the clipboard untouched. Longer text goes via the clipboard, because
/// some terminals and Electron apps drop characters when thousands of synthetic keystrokes
/// arrive back to back.
/// </para>
/// </remarks>
public sealed class SendInputTextInjector : ITextInjector
{
    /// <summary>Above this many characters, paste instead of typing.</summary>
    private const int PasteThreshold = 200;

    /// <summary>Characters per <c>SendInput</c> call when typing.</summary>
    private const int ChunkSize = 40;

    /// <summary>Pause between chunks, so slower targets keep up.</summary>
    private static readonly TimeSpan ChunkGap = TimeSpan.FromMilliseconds(4);

    /// <summary>
    /// Time for the target to observe the new clipboard before Ctrl+V arrives. Without it, a
    /// fast paste can pick up the <i>previous</i> contents.
    /// </summary>
    private static readonly TimeSpan ClipboardSettle = TimeSpan.FromMilliseconds(60);

    /// <summary>Time for the target to finish reading before the clipboard is restored.</summary>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(500);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;
    private const int VK_RETURN = 0x0D;

    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParamL, ParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    /// <summary>Called to place text on the clipboard and paste it.</summary>
    /// <remarks>
    /// Injected rather than called directly because clipboard access is UI-framework specific
    /// and must happen on an STA thread — the app supplies an implementation that marshals to
    /// its own dispatcher. Settable (not init-only) because the app can only build it once a
    /// window exists, which is after this injector is constructed. UNSET, THIS IS A LIVE BUG:
    /// multi-line text falls back to typing REAL Enter keys, and in any chat box Enter sends
    /// the message — observed as "the chat app sent my half-written prompt when I finished
    /// dictating".
    /// </remarks>
    public Func<string, CancellationToken, Task<bool>>? ClipboardPaste { get; set; }

    /// <inheritdoc />
    public async ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return true;

        // Newlines sent as Unicode packets do not reliably produce a new line — many controls
        // want a real VK_RETURN. Long text goes to the clipboard anyway, which handles them.
        var hasNewlines = text.Contains('\n', StringComparison.Ordinal);

        if (text.Length <= PasteThreshold && !hasNewlines)
        {
            return TypeUnicode(text);
        }

        if (ClipboardPaste is not null)
        {
            return await ClipboardPaste(text, cancellationToken).ConfigureAwait(false);
        }

        return TypeWithNewlines(text);
    }

    /// <summary>Types arbitrary text as Unicode packets.</summary>
    /// <remarks>
    /// <para>
    /// Iterating by UTF-16 code unit is correct and intentional. A non-BMP character — an
    /// emoji, some CJK extensions — is two surrogate halves, and each is sent as its own
    /// event; Unicode-aware controls recombine them. The 16-bit <c>ScanCode</c> field cannot
    /// carry a full 21-bit code point.
    /// </para>
    /// <para>
    /// <b>Failure here is silent.</b> <c>SendInput</c> is subject to UIPI, and Microsoft
    /// documents that when it is blocked "neither GetLastError nor the return value will
    /// indicate the failure was caused by UIPI blocking." Injecting into an elevated window
    /// from a normal process can return success and do nothing.
    /// </para>
    /// </remarks>
    private static bool TypeUnicode(string text)
    {
        for (var offset = 0; offset < text.Length; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, text.Length - offset);

            // Never split a surrogate pair across two SendInput calls.
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length++;

            var inputs = new INPUT[length * 2];
            for (var i = 0; i < length; i++)
            {
                var unit = text[offset + i];
                inputs[i * 2] = UnicodeInput(unit, up: false);
                inputs[(i * 2) + 1] = UnicodeInput(unit, up: true);
            }

            if (SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length) return false;
            if (offset + length < text.Length) Thread.Sleep(ChunkGap);
        }

        return true;
    }

    /// <summary>Types text, sending a real Return for each newline.</summary>
    private static bool TypeWithNewlines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && !TypeUnicode(lines[i])) return false;

            if (i < lines.Length - 1)
            {
                INPUT[] enter = [KeyInput(VK_RETURN, up: false), KeyInput(VK_RETURN, up: true)];
                if (SendInput(2, enter, InputSize) != 2) return false;
            }
        }

        return true;
    }

    /// <summary>Presses Ctrl+V, temporarily lifting any modifier the user is resting on.</summary>
    /// <remarks>
    /// Microsoft: "This function does not reset the keyboard's current state. Any keys that
    /// are already pressed when the function is called might interfere." A user resting on
    /// Shift would otherwise get Ctrl+Shift+V — "paste as plain text", or something else
    /// entirely depending on the app.
    /// </remarks>
    public static bool PressCtrlV()
    {
        var held = new List<int>();
        foreach (var key in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0) held.Add(key);
        }

        var sequence = new List<INPUT>();
        foreach (var key in held) sequence.Add(KeyInput(key, up: true));

        sequence.Add(KeyInput(VK_CONTROL, up: false));
        sequence.Add(KeyInput(VK_V, up: false));
        sequence.Add(KeyInput(VK_V, up: true));
        sequence.Add(KeyInput(VK_CONTROL, up: true));

        foreach (var key in held) sequence.Add(KeyInput(key, up: false));

        var inputs = sequence.ToArray();
        return SendInput((uint)inputs.Length, inputs, InputSize) == inputs.Length;
    }

    /// <summary>How long to wait after setting the clipboard before pasting.</summary>
    public static TimeSpan ClipboardSettleDelay => ClipboardSettle;

    /// <summary>How long to wait after pasting before restoring the clipboard.</summary>
    public static TimeSpan PasteSettleDelay => PasteSettle;

    private static INPUT UnicodeInput(ushort codeUnit, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = 0,   // must be 0 for KEYEVENTF_UNICODE
                ScanCode = codeUnit,
                Flags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                Time = 0,
                ExtraInfo = PushToTalkHook.InjectedTag,
            },
        },
    };

    private static INPUT KeyInput(int virtualKey, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = (ushort)virtualKey,
                    ScanCode = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC),
                    Flags = flags,
                    Time = 0,
                    // Tagged so our own hook ignores it and doesn't re-trigger dictation.
                    ExtraInfo = PushToTalkHook.InjectedTag,
                },
            },
        };
    }

    private static bool IsExtendedKey(int virtualKey) => virtualKey is
        0xA3 or 0xA5 or 0x5B or 0x5C or   // right ctrl/alt, both Windows keys
        0x2D or 0x2E or 0x24 or 0x23 or   // insert, delete, home, end
        0x21 or 0x22 or                   // page up/down
        0x25 or 0x26 or 0x27 or 0x28;     // arrows
}

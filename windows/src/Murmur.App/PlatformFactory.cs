using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Murmur.Abstractions;

namespace Murmur.App;

/// <summary>
/// Loads the Windows platform layer, if it is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reflection rather than a project reference:</b> <c>Murmur.Platform.Windows</c>
/// targets <c>net10.0-windows</c>. Referencing it directly would force this project onto that
/// TFM too, and the app could then no longer be built or headless-tested on macOS — losing
/// the fast local loop that is the whole reason for choosing Avalonia.
/// </para>
/// <para>
/// The assembly is shipped alongside the app on Windows and simply absent elsewhere, so the
/// lookup failing is the normal, expected case on a developer's Mac.
/// </para>
/// </remarks>
internal static class PlatformFactory
{
    private const string AssemblyName = "Murmur.Platform.Windows";
    private const string Namespace = "Murmur.Platform.Windows";

    private static Assembly? _assembly;
    private static bool _attempted;

    /// <summary>Whether the Windows platform assembly could be loaded.</summary>
    public static bool IsAvailable => Load() is not null;

    /// <summary>
    /// Teaches the default load context to find the platform assembly beside the executable.
    /// </summary>
    /// <remarks>
    /// <c>PublishSingleFile</c> only bundles assemblies the compiler knows about, and this
    /// one is deliberately invisible to it — that is what keeps <c>Murmur.App</c> on plain
    /// <c>net10.0</c>. It therefore ships as a loose file next to the exe. Default probing
    /// normally finds it, but a single-file host resolves differently enough that relying on
    /// that alone is a bet — and losing it means the app starts fine and then does nothing
    /// when the user presses the key. Explicit is cheaper than that failure.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Murmur.Platform.Windows ships whole beside the executable and is "
                      + "never trimmed; nothing it depends on can have been removed.")]
    public static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (!string.Equals(name.Name, AssemblyName, StringComparison.Ordinal)) return null;

            var candidate = System.IO.Path.Combine(AppContext.BaseDirectory, AssemblyName + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }

    private static bool _resolverInstalled;

    /// <summary>Creates the WASAPI capture, or null off Windows.</summary>
    public static IAudioCapture? CreateAudioCapture() =>
        Create<IAudioCapture>("WasapiAudioCapture", [null]);

    /// <summary>Creates the low-level keyboard hook, or null off Windows.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "Murmur.Platform.Windows is published whole and never trimmed.")]
    public static IHotkeySource? CreateHotkeySource(int virtualKey)
    {
        var hook = Create<IHotkeySource>("PushToTalkHook", []);
        if (hook is null) return null;

        // Key is an enum on the concrete type; set it by name to avoid referencing it.
        var property = hook.GetType().GetProperty("Key");
        if (property is not null && property.PropertyType.IsEnum)
        {
            property.SetValue(hook, Enum.ToObject(property.PropertyType, virtualKey));
        }

        return hook;
    }

    /// <summary>Creates the SendInput injector, or null off Windows.</summary>
    public static ITextInjector? CreateTextInjector() =>
        Create<ITextInjector>("SendInputTextInjector", []);

    /// <summary>
    /// Wires the clipboard-paste path into an injector created by
    /// <see cref="CreateTextInjector"/>. Without it, multi-line text is typed with REAL
    /// Enter keys — which, in a chat box, sends the message mid-injection.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "Murmur.Platform.Windows is published whole and never trimmed.")]
    public static void AttachClipboardPaste(
        ITextInjector injector, Func<string, CancellationToken, Task<bool>> paste)
    {
        var property = injector.GetType().GetProperty("ClipboardPaste");
        property?.SetValue(injector, paste);
    }

    /// <summary>Presses Ctrl+V in the focused app, lifting any resting modifiers first.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "Murmur.Platform.Windows is published whole and never trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Murmur.Platform.Windows is published whole and never trimmed.")]
    public static bool PressCtrlV()
    {
        try
        {
            var method = Load()?
                .GetType($"{Namespace}.SendInputTextInjector")?
                .GetMethod("PressCtrlV");
            return method?.Invoke(null, null) is true;
        }
        catch (Exception e) when (e is System.Reflection.TargetInvocationException or MissingMethodException)
        {
            return false;
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The platform assembly is published whole alongside the app and is "
                      + "never trimmed; its types are resolved by name at startup.")]
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:AssemblyLocation",
        Justification = "Assembly.Load resolves from the bundle, not from a file path.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072:DynamicallyAccessedMembers",
        Justification = "Murmur.Platform.Windows is published whole and never trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "Murmur.Platform.Windows is published whole and never trimmed.")]
    private static T? Create<T>(string typeName, object?[] arguments) where T : class
    {
        var assembly = Load();
        var type = assembly?.GetType($"{Namespace}.{typeName}");
        if (type is null) return null;

        try
        {
            return Activator.CreateInstance(type, arguments) as T;
        }
        catch (Exception e) when (e is MissingMethodException or TargetInvocationException)
        {
            return null;
        }
    }

    private static Assembly? Load()
    {
        if (_attempted) return _assembly;
        _attempted = true;

        // Absent on macOS and Linux, which is expected and must stay silent — this runs on
        // every launch of the headless test host.
        try
        {
            _assembly = Assembly.Load(AssemblyName);
        }
        catch (Exception e) when (e is FileNotFoundException or BadImageFormatException)
        {
            _assembly = null;
        }

        return _assembly;
    }
}

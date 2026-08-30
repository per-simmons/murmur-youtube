using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Murmur.App.Views;

namespace Murmur.App;

/// <summary>The application.</summary>
public partial class App : Application
{
    private Composition? _composition;
    private MainWindow? _main;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _composition = Composition.Create();
            _main = new MainWindow(_composition);
            desktop.MainWindow = _main;

            // Clipboard paste for the injector. Without this the ClipboardPaste hook the
            // injector was designed around stays null, and multi-line dictation is typed
            // with real Enter keys — in a chat box each Enter SENDS the half-written
            // message. Found dictating into a chat app on the first real-hardware run.
            if (_composition.Injector is { } injector)
            {
                PlatformFactory.AttachClipboardPaste(injector, PasteViaClipboardAsync);
            }

            // Closing the window leaves Murmur running in the tray — the hotkey still works,
            // which is the whole point of a dictation app. Quit is explicit, from the tray
            // menu or the app menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Disposing tears down the keyboard hook and releases the audio device. Leaving
            // a low-level hook installed after exit is the kind of thing that makes a
            // machine feel broken until it is rebooted.
            desktop.ShutdownRequested += (_, _) =>
            {
                _composition?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _composition = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayShow(object? sender, EventArgs e) => ShowMain();

    private void OnTraySettings(object? sender, EventArgs e)
    {
        ShowMain();
        if (_main is not null && _composition is not null)
        {
            _ = new SettingsWindow(_composition.Settings).ShowDialog(_main);
        }
    }

    private void OnTrayQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private void ShowMain()
    {
        if (_main is null) return;

        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard, pastes it with Ctrl+V, and restores
    /// whatever text was there before. Atomic from the target app's point of view — no
    /// synthetic Enter ever reaches it.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread because Avalonia's clipboard requires it. Only textual
    /// clipboard content survives the round trip; an image on the clipboard is lost, which
    /// is the accepted cost of not typing Enters into chat boxes. The settle delays match
    /// the platform layer's documented values: 60 ms for the target to observe the new
    /// clipboard, 500 ms for it to finish reading before restore.
    /// </remarks>
    private async Task<bool> PasteViaClipboardAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = _main?.Clipboard;
                if (clipboard is null) return false;

                string? previous = null;
                try
                {
                    previous = await Avalonia.Input.Platform.ClipboardExtensions
                        .TryGetTextAsync(clipboard);
                }
                catch (Exception)
                {
                    // Non-text clipboard content; nothing to restore.
                }

                await clipboard.SetTextAsync(text);
                await Task.Delay(60, cancellationToken);

                if (!PlatformFactory.PressCtrlV()) return false;

                await Task.Delay(500, cancellationToken);
                if (previous is not null)
                {
                    try
                    {
                        await clipboard.SetTextAsync(previous);
                    }
                    catch (Exception)
                    {
                        // Restoring is best-effort; the paste already succeeded.
                    }
                }

                return true;
            });
        }
        catch (Exception)
        {
            // The injector falls back to typing when paste reports failure.
            return false;
        }
    }
}

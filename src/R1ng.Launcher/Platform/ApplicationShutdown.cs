using System.Windows;

namespace R1ng.Launcher.Platform;

/// <summary>
/// Test seam for the explicit quit path. The real implementation shuts the WPF application down, but
/// the launcher only exposes it through this abstraction so the quit flow is unit-testable without a
/// live <see cref="Application"/> instance.
/// </summary>
public interface IApplicationShutdown
{
    void Shutdown();
}

/// <summary>
/// Shuts down the current WPF application. The launcher uses
/// <see cref="ShutdownMode.OnExplicitShutdown"/>, so this is the only path that releases the window,
/// the global hotkey and the single-instance guard.
/// </summary>
public sealed class WpfApplicationShutdown : IApplicationShutdown
{
    public void Shutdown() => Application.Current?.Shutdown();
}

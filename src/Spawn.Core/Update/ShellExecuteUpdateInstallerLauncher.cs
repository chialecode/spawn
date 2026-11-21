using System.Diagnostics;
using Spawn.Contracts;

namespace Spawn.Core.Update;

/// <summary>
/// Launches the verified per-user Inno Setup installer silently via <c>ShellExecute</c> (<c>UseShellExecute = true</c>),
/// with zero UAC elevation verb and zero sensitive Win32 imports.
/// </summary>
public sealed class ShellExecuteUpdateInstallerLauncher : IUpdateInstallerLauncher
{
    public bool LaunchSilentInstaller(string installerPath, string arguments, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            errorMessage = "待执行的安装包文件不存在。";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = "系统未能创建更新安装器进程。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Conservative fallback <see cref="IUpdateIdleEvaluator"/> used when Core DI is assembled outside <c>Spawn.Host</c>.
/// Reports the window as open so silent auto-restart never fires without explicit Host wiring.
/// </summary>
public sealed class ConservativeFallbackUpdateIdleEvaluator : IUpdateIdleEvaluator
{
    public static readonly ConservativeFallbackUpdateIdleEvaluator Instance = new();

    public UpdateIdleSnapshot Evaluate() => new() { IsMainWindowOpen = true };
}

/// <summary>
/// Safe no-op fallback <see cref="IUpdateQuitCoordinator"/> used outside <c>Spawn.Host</c>.
/// </summary>
public sealed class NoOpUpdateQuitCoordinator : IUpdateQuitCoordinator
{
    public static readonly NoOpUpdateQuitCoordinator Instance = new();

    public Task RequestQuitForUpdateAsync(string reason, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void CompleteDeferredShutdown()
    {
    }

    public Task AbortDeferredShutdownAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

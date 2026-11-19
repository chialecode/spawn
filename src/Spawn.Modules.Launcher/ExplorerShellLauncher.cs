using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Parameters for launching a target game executable.
/// </summary>
public sealed record GameProcessStartRequest(
    string ExecutableFullPath,
    string WorkingDirectory,
    string? Arguments = null,
    bool CreateNoWindow = false);

/// <summary>
/// Abstraction over OS process creation (direct <c>CreateProcess</c> for Tier 0/1 vs Explorer Shell COM
/// PPID-decoupled launch for Tier 2/3 protected multiplayer games), enabling 100% silent headless unit testing.
/// </summary>
public interface IGameProcessStarter
{
    Process StartDirectProcess(GameProcessStartRequest request);
    Task StartExplorerDecoupledAsync(GameProcessStartRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="IGameProcessStarter"/> implementation.
/// <list type="bullet">
/// <item><description>Tier 0/1: Standard <see cref="Process.Start(ProcessStartInfo)"/> with <c>UseShellExecute = false</c> for Job Object attachment.</description></item>
/// <item><description>Tier 2/3: Out-of-proc Explorer Shell COM (<c>CLSID_ShellWindows</c> -&gt; <c>SWC_DESKTOP</c> -&gt; <c>Document.Application.ShellExecute</c>) so the kernel parent PID (<c>InheritedFromUniqueProcessId</c>) belongs to <c>explorer.exe</c> with zero handle inheritance from <c>Spawn.Host.exe</c>.</description></item>
/// </list>
/// </summary>
public sealed class DefaultGameProcessStarter : IGameProcessStarter
{
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const int SwShowNormal = 1;
    private const int SwHide = 0;

    public Process StartDirectProcess(GameProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutableFullPath);

        string workDir = !string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory)
            ? request.WorkingDirectory
            : (Path.GetDirectoryName(request.ExecutableFullPath) ?? Environment.CurrentDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = request.ExecutableFullPath,
            Arguments = request.Arguments ?? string.Empty,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = request.CreateNoWindow,
        };

        if (request.CreateNoWindow)
        {
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }

        var proc = Process.Start(psi);
        return proc ?? throw new InvalidOperationException($"未能启动目标游戏进程：{request.ExecutableFullPath}");
    }

    public Task StartExplorerDecoupledAsync(
        GameProcessStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutableFullPath);
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            try
            {
                ExecuteViaDesktopExplorerShellCom(request);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SPAWN-ExplorerShellLaunch",
        };

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        return tcs.Task.WaitAsync(cancellationToken);
    }

    private static void ExecuteViaDesktopExplorerShellCom(GameProcessStartRequest request)
    {
        string workDir = !string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory)
            ? request.WorkingDirectory
            : (Path.GetDirectoryName(request.ExecutableFullPath) ?? Environment.CurrentDirectory);

        int showCmd = request.CreateNoWindow ? SwHide : SwShowNormal;
        object? shellWindows = null;
        object? desktopWindow = null;
        object? document = null;
        object? shellDispatch = null;

        try
        {
            // Connect to out-of-proc explorer.exe ShellWindows automation server.
            Type? shellWindowsType = Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: false);
            if (shellWindowsType is not null)
            {
                shellWindows = Activator.CreateInstance(shellWindowsType);
                if (shellWindows is not null)
                {
                    object emptyLoc = Missing.Value;
                    object emptyLocRoot = Missing.Value;
                    object?[] findArgs = [emptyLoc, emptyLocRoot, SwcDesktop, 0, SwfoNeedDispatch];

                    desktopWindow = shellWindowsType.InvokeMember(
                        "FindWindowSW",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: shellWindows,
                        args: findArgs);

                    if (desktopWindow is not null)
                    {
                        document = desktopWindow.GetType().InvokeMember(
                            "Document",
                            BindingFlags.GetProperty,
                            binder: null,
                            target: desktopWindow,
                            args: null);

                        if (document is not null)
                        {
                            shellDispatch = document.GetType().InvokeMember(
                                "Application",
                                BindingFlags.GetProperty,
                                binder: null,
                                target: document,
                                args: null);
                        }
                    }
                }
            }

            // Fallback to Shell.Application if explorer.exe desktop shell is unavailable in a headless session.
            if (shellDispatch is null)
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false);
                if (shellAppType is not null)
                {
                    shellDispatch = Activator.CreateInstance(shellAppType);
                }
            }

            if (shellDispatch is null)
            {
                throw new InvalidOperationException("无法连接到 Windows Explorer Shell COM 组件以执行脱钩启动。");
            }

            object?[] shellExecArgs =
            [
                request.ExecutableFullPath,
                request.Arguments ?? string.Empty,
                workDir,
                "open",
                showCmd,
            ];

            _ = shellDispatch.GetType().InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shellDispatch,
                args: shellExecArgs);
        }
        finally
        {
            ReleaseComObjectSafe(shellDispatch);
            ReleaseComObjectSafe(document);
            ReleaseComObjectSafe(desktopWindow);
            ReleaseComObjectSafe(shellWindows);
        }
    }

    private static void ReleaseComObjectSafe(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            try
            {
                _ = Marshal.ReleaseComObject(comObj);
            }
            catch
            {
                // Ignore COM teardown errors.
            }
        }
    }
}

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Spawn.Contracts;

namespace Spawn.TrainerWorker;

/// <summary>
/// Injectable Worker-side executor for adding a single-folder Windows Defender sandbox exclusion via WMI
/// (<c>root\Microsoft\Windows\Defender</c>, <c>MSFT_MpPreference.Add(ExclusionPath=[sandboxPath])</c>)
/// followed by mandatory readback verification. Zero <c>powershell.exe</c> invocation.
/// </summary>
public interface IDefenderWmiExecutor
{
    Task<TrainerAddDefenderExclusionResponse> AddAndVerifyExclusionAsync(
        string sandboxPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates candidate sandbox exclusion paths and delegates to WMI COM (<c>WbemScripting.SWbemLocator</c>)
/// on a dedicated MTA thread without spawning <c>powershell.exe</c>.
/// </summary>
internal sealed class ComDefenderWmiExecutor : IDefenderWmiExecutor
{
    private const int WbemEAccessDenied = unchecked((int)0x80041003);
    private const int WbemEInvalidNamespace = unchecked((int)0x8004100E);
    private const int WbemEInvalidClass = unchecked((int)0x80041010);
    private const int EAccessDenied = unchecked((int)0x80070005);

    public static ComDefenderWmiExecutor Instance { get; } = new();

    private ComDefenderWmiExecutor()
    {
    }

    public static bool TryValidateSandboxExclusionPath(
        string? rawPath,
        out string normalizedPath,
        out string? rejectReason)
    {
        normalizedPath = string.Empty;
        rejectReason = null;

        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath.Trim()))
        {
            rejectReason = "Sandbox exclusion path must be a fully qualified directory path.";
            return false;
        }

        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejectReason = $"Invalid sandbox exclusion path: {ex.Message}";
            return false;
        }

        string? root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(Path.TrimEndingDirectorySeparator(root), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "Refusing to exclude a filesystem drive root.";
            return false;
        }

        string[] forbiddenRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ];

        foreach (string forbidden in forbiddenRoots)
        {
            if (string.IsNullOrWhiteSpace(forbidden))
            {
                continue;
            }

            string normForbidden = Path.TrimEndingDirectorySeparator(Path.GetFullPath(forbidden));
            if (string.Equals(normalizedPath, normForbidden, StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = $"Refusing to exclude broad system or user root directory: {normalizedPath}";
                return false;
            }
        }

        return true;
    }

    public Task<TrainerAddDefenderExclusionResponse> AddAndVerifyExclusionAsync(
        string sandboxPath,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSandboxExclusionPath(sandboxPath, out string normalizedPath, out string? rejectReason))
        {
            return Task.FromResult(new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = false,
                Status = DefenderExclusionStatus.Failed,
                ErrorMessage = rejectReason,
            });
        }

        var tcs = new TaskCompletionSource<TrainerAddDefenderExclusionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mtaThread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tcs.TrySetResult(ExecuteWmiOnMtaThread(normalizedPath));
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(MapException(ex));
            }
        })
        {
            Name = "Spawn.TrainerWorker.DefenderWmi",
            IsBackground = true,
        };

        mtaThread.SetApartmentState(ApartmentState.MTA);
        mtaThread.Start();
        return tcs.Task;
    }

    private static TrainerAddDefenderExclusionResponse ExecuteWmiOnMtaThread(string normalizedSandboxPath)
    {
        object? locator = null;
        object? services = null;
        object? mpClass = null;
        object? methods = null;
        object? addMethod = null;
        object? inParamsTemplate = null;
        object? inParams = null;
        object? inProps = null;
        object? exclusionProp = null;
        object? outParams = null;
        object? outProps = null;
        object? returnProp = null;

        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: false);
            if (locatorType is null)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = false,
                    VerifiedByReadback = false,
                    Status = DefenderExclusionStatus.Failed,
                    ErrorMessage = "WbemScripting.SWbemLocator COM component is unavailable.",
                };
            }

            locator = Activator.CreateInstance(locatorType);
            if (locator is null)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = false,
                    VerifiedByReadback = false,
                    Status = DefenderExclusionStatus.Failed,
                    ErrorMessage = "Failed to instantiate WbemScripting.SWbemLocator.",
                };
            }

            services = InvokeMethod(locator, "ConnectServer", ".", @"root\Microsoft\Windows\Defender");
            if (services is null)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = false,
                    VerifiedByReadback = false,
                    Status = DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode,
                    ErrorMessage = "Unable to connect to WMI namespace root\\Microsoft\\Windows\\Defender.",
                };
            }

            mpClass = InvokeMethod(services, "Get", "MSFT_MpPreference");
            methods = GetProperty(mpClass!, "Methods_");
            addMethod = InvokeMethod(methods!, "Item", "Add");
            inParamsTemplate = GetProperty(addMethod!, "InParameters");
            inParams = InvokeMethod(inParamsTemplate!, "SpawnInstance_");
            inProps = GetProperty(inParams!, "Properties_");
            exclusionProp = InvokeMethod(inProps!, "Item", "ExclusionPath");
            SetProperty(exclusionProp!, "Value", new[] { normalizedSandboxPath });

            outParams = InvokeMethod(mpClass!, "ExecMethod_", "Add", inParams);
            int returnValue = 0;
            if (outParams is not null)
            {
                outProps = GetProperty(outParams, "Properties_");
                returnProp = InvokeMethod(outProps!, "Item", "ReturnValue");
                object? rawVal = GetProperty(returnProp!, "Value");
                if (rawVal is not null)
                {
                    returnValue = Convert.ToInt32(rawVal, CultureInfo.InvariantCulture);
                }
            }

            // Mandatory readback verification: Tamper Protection can silently drop Add() while returning 0.
            bool verified = ReadbackContainsPath(services, normalizedSandboxPath);
            if (returnValue == 0 && verified)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = true,
                    VerifiedByReadback = true,
                    Status = DefenderExclusionStatus.AddedAndVerified,
                    WmiReturnValue = returnValue,
                };
            }

            if (returnValue == 0 && !verified)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = false,
                    VerifiedByReadback = false,
                    Status = DefenderExclusionStatus.TamperProtectionBlocked,
                    WmiReturnValue = returnValue,
                    ErrorMessage = "MSFT_MpPreference.Add returned 0, but readback did not contain the sandbox path (likely blocked by Tamper Protection / Event 5013).",
                };
            }

            return new TrainerAddDefenderExclusionResponse
            {
                Succeeded = false,
                VerifiedByReadback = verified,
                Status = DefenderExclusionStatus.VerificationReadbackFailed,
                WmiReturnValue = returnValue,
                ErrorMessage = $"MSFT_MpPreference.Add failed with WMI ReturnValue {returnValue}.",
            };
        }
        finally
        {
            ReleaseComSafe(returnProp);
            ReleaseComSafe(outProps);
            ReleaseComSafe(outParams);
            ReleaseComSafe(exclusionProp);
            ReleaseComSafe(inProps);
            ReleaseComSafe(inParams);
            ReleaseComSafe(inParamsTemplate);
            ReleaseComSafe(addMethod);
            ReleaseComSafe(methods);
            ReleaseComSafe(mpClass);
            ReleaseComSafe(services);
            ReleaseComSafe(locator);
        }
    }

    private static bool ReadbackContainsPath(object services, string expectedNormalizedPath)
    {
        object? queryResult = null;
        try
        {
            queryResult = InvokeMethod(services, "ExecQuery", "SELECT ExclusionPath FROM MSFT_MpPreference");
            if (queryResult is not IEnumerable enumerable)
            {
                return false;
            }

            foreach (object? instance in enumerable)
            {
                if (instance is null)
                {
                    continue;
                }

                object? props = null;
                object? prop = null;
                try
                {
                    props = GetProperty(instance, "Properties_");
                    prop = InvokeMethod(props!, "Item", "ExclusionPath");
                    object? rawValue = GetProperty(prop!, "Value");
                    if (ContainsNormalizedPath(rawValue, expectedNormalizedPath))
                    {
                        return true;
                    }
                }
                finally
                {
                    ReleaseComSafe(prop);
                    ReleaseComSafe(props);
                    ReleaseComSafe(instance);
                }
            }

            return false;
        }
        finally
        {
            ReleaseComSafe(queryResult);
        }
    }

    internal static bool ContainsNormalizedPath(object? rawValue, string expectedNormalizedPath)
    {
        if (rawValue is null)
        {
            return false;
        }

        if (rawValue is string single)
        {
            return PathsMatch(single, expectedNormalizedPath);
        }

        if (rawValue is IEnumerable items)
        {
            foreach (object? item in items)
            {
                if (item is string text && PathsMatch(text, expectedNormalizedPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PathsMatch(string candidate, string expectedNormalizedPath)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Trim()));
            return string.Equals(normalized, expectedNormalizedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static TrainerAddDefenderExclusionResponse MapException(Exception ex)
    {
        Exception root = ex is TargetInvocationException tie && tie.InnerException is not null
            ? tie.InnerException
            : ex;

        if (root is COMException comEx)
        {
            if (comEx.HResult is WbemEInvalidNamespace or WbemEInvalidClass)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = false,
                    VerifiedByReadback = false,
                    Status = DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode,
                    ErrorMessage = $"Microsoft Defender WMI provider is unavailable (0x{comEx.HResult:X8}).",
                };
            }

            if (comEx.HResult is WbemEAccessDenied or EAccessDenied)
            {
                return new TrainerAddDefenderExclusionResponse
                {
                    Succeeded = false,
                    VerifiedByReadback = false,
                    Status = DefenderExclusionStatus.TamperProtectionBlocked,
                    ErrorMessage = $"Access denied writing Defender ExclusionPath (0x{comEx.HResult:X8}).",
                };
            }
        }

        return new TrainerAddDefenderExclusionResponse
        {
            Succeeded = false,
            VerifiedByReadback = false,
            Status = DefenderExclusionStatus.Failed,
            ErrorMessage = root.Message,
        };
    }

    private static object? InvokeMethod(object target, string methodName, params object?[] args) =>
        target.GetType().InvokeMember(methodName, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? GetProperty(object target, string propertyName) =>
        target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static void SetProperty(object target, string propertyName, object? value) =>
        target.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);

    private static void ReleaseComSafe(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            try
            {
                _ = Marshal.FinalReleaseComObject(comObj);
            }
            catch
            {
                // Ignore COM teardown races.
            }
        }
    }
}

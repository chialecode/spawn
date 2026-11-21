using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Spawn.Modules.Progress.Security;

/// <summary>
/// Read-only Windows 11 Smart App Control policy state (<c>VerifiedAndReputablePolicyState</c>).
/// </summary>
public enum SmartAppControlMode
{
    Off = 0,
    Enforced = 1,
    Evaluation = 2,
    Unknown = 3,
}

/// <summary>
/// Read-only environment snapshot gathered before prompting UAC or diagnosing Defender/SAC behavior.
/// </summary>
public sealed record DefenderEnvironmentProbeSnapshot
{
    /// <summary>
    /// <c>true</c> when Defender Tamper Protection explicitly protects exclusions (<c>TPExclusions = 1</c>).
    /// </summary>
    public bool? TamperProtectionExclusionsProtected { get; init; }

    /// <summary>
    /// <c>true</c> when Defender Tamper Protection is enabled globally (registry or <c>MSFT_MpComputerStatus.IsTamperProtected</c>).
    /// </summary>
    public bool? IsTamperProtectionEnabled { get; init; }

    /// <summary>
    /// Raw registry value of <c>HKLM\SOFTWARE\Microsoft\Windows Defender\ManagedDefenderProductType</c>, if present.
    /// </summary>
    public int? ManagedDefenderProductType { get; init; }

    /// <summary>
    /// <c>true</c> when Defender is running in passive mode (e.g., third-party antivirus owns active real-time protection).
    /// </summary>
    public bool IsThirdPartyPassiveMode { get; init; }

    /// <summary>
    /// <c>true</c> when Windows Defender WMI provider / service is reachable.
    /// </summary>
    public bool IsDefenderServiceAvailable { get; init; } = true;

    /// <summary>
    /// Read-only Smart App Control mode from <c>HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState</c>.
    /// </summary>
    public SmartAppControlMode SmartAppControlState { get; init; } = SmartAppControlMode.Unknown;

    /// <summary>
    /// Currently configured Defender <c>ExclusionPath</c> entries when readable without elevation.
    /// </summary>
    public IReadOnlyList<string> CurrentExclusionPaths { get; init; } = [];

    /// <summary>
    /// <c>true</c> when the probe was permitted to read <c>ExclusionPath</c> (false when Windows hides exclusions from standard users).
    /// </summary>
    public bool CanReadExclusionPaths { get; init; }

    public string? DiagnosticDetail { get; init; }
}

/// <summary>
/// Injectable read-only diagnostic probe for Windows Defender Tamper Protection, passive mode,
/// current exclusion paths, and Smart App Control policy.
/// Zero registry writes, zero UAC prompts, zero <c>powershell.exe</c> invocation.
/// </summary>
public interface IDefenderEnvironmentProbe
{
    Task<DefenderEnvironmentProbeSnapshot> ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// Production Windows implementation of <see cref="IDefenderEnvironmentProbe"/> using purely read-only
/// HKLM registry reads and read-only COM WMI (<c>WbemScripting.SWbemLocator</c>) on a dedicated MTA thread.
/// </summary>
public sealed class WindowsDefenderEnvironmentProbe : IDefenderEnvironmentProbe
{
    private const string DefenderFeaturesKeyPath = @"SOFTWARE\Microsoft\Windows Defender\Features";
    private const string DefenderRootKeyPath = @"SOFTWARE\Microsoft\Windows Defender";
    private const string DefenderExclusionsPathsKeyPath = @"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths";
    private const string CiPolicyKeyPath = @"SYSTEM\CurrentControlSet\Control\CI\Policy";

    private const int WbemEInvalidNamespace = unchecked((int)0x8004100E);
    private const int WbemEInvalidClass = unchecked((int)0x80041010);

    public static WindowsDefenderEnvironmentProbe Instance { get; } = new();

    public Task<DefenderEnvironmentProbeSnapshot> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<DefenderEnvironmentProbeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetResult(ExecuteReadOnlyProbe());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new DefenderEnvironmentProbeSnapshot
                {
                    IsDefenderServiceAvailable = false,
                    DiagnosticDetail = $"Defender 只读诊断异常：{ex.Message}",
                });
            }
        })
        {
            Name = "Spawn.Progress.DefenderProbe",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }

    private static DefenderEnvironmentProbeSnapshot ExecuteReadOnlyProbe()
    {
        bool? tpExclusions = null;
        bool? isTamperProtected = null;
        int? managedProductType = null;
        bool passiveMode = false;
        SmartAppControlMode sacMode = ReadSmartAppControlMode();

        // 1. Read-only HKLM Windows Defender registry inspection.
        try
        {
            using RegistryKey? featuresKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(DefenderFeaturesKeyPath, writable: false);
            if (featuresKey is not null)
            {
                int? rawTpExcl = TryReadDword(featuresKey, "TPExclusions");
                if (rawTpExcl.HasValue)
                {
                    tpExclusions = rawTpExcl.Value != 0;
                }

                int? rawTp = TryReadDword(featuresKey, "TamperProtection");
                if (rawTp.HasValue)
                {
                    // TamperProtection value 5 (0x5) or odd low bit indicates active Tamper Protection on Windows 10/11.
                    isTamperProtected = (rawTp.Value & 0x1) != 0;
                }
            }
        }
        catch
        {
            // Non-admin or policy-restricted registry key: continue gracefully.
        }

        try
        {
            using RegistryKey? rootKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(DefenderRootKeyPath, writable: false);
            if (rootKey is not null)
            {
                managedProductType = TryReadDword(rootKey, "ManagedDefenderProductType");
                int? rawPassive = TryReadDword(rootKey, "PassiveMode");
                int? rawDisable = TryReadDword(rootKey, "DisableAntiSpyware");
                if (rawPassive == 1 || rawDisable == 1)
                {
                    passiveMode = true;
                }
            }
        }
        catch
        {
            // Continue gracefully.
        }

        // 2. Read-only WMI inspection of MSFT_MpComputerStatus & MSFT_MpPreference (zero writes, zero powershell.exe).
        var exclusions = new List<string>();
        bool canReadExclusions = false;
        bool defenderAvailable = true;
        string? diagnostic = null;

        ProbeWmiReadOnly(
            exclusions,
            ref canReadExclusions,
            ref defenderAvailable,
            ref passiveMode,
            ref isTamperProtected,
            ref diagnostic);

        // 3. Fallback read-only registry check for ExclusionPath if WMI hid it but registry is readable.
        if (!canReadExclusions)
        {
            try
            {
                using RegistryKey? exclKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(DefenderExclusionsPathsKeyPath, writable: false);
                if (exclKey is not null)
                {
                    foreach (string name in exclKey.GetValueNames())
                    {
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            exclusions.Add(name.Trim());
                        }
                    }

                    canReadExclusions = true;
                }
            }
            catch
            {
                // Standard users on Windows 11 cannot read Exclusions\Paths directly; expected.
            }
        }

        return new DefenderEnvironmentProbeSnapshot
        {
            TamperProtectionExclusionsProtected = tpExclusions,
            IsTamperProtectionEnabled = isTamperProtected ?? tpExclusions,
            ManagedDefenderProductType = managedProductType,
            IsThirdPartyPassiveMode = passiveMode,
            IsDefenderServiceAvailable = defenderAvailable,
            SmartAppControlState = sacMode,
            CurrentExclusionPaths = exclusions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CanReadExclusionPaths = canReadExclusions,
            DiagnosticDetail = diagnostic,
        };
    }

    private static SmartAppControlMode ReadSmartAppControlMode()
    {
        try
        {
            using RegistryKey? ciKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(CiPolicyKeyPath, writable: false);
            int? state = ciKey is null ? null : TryReadDword(ciKey, "VerifiedAndReputablePolicyState");
            return state switch
            {
                0 => SmartAppControlMode.Off,
                1 => SmartAppControlMode.Enforced,
                2 => SmartAppControlMode.Evaluation,
                _ => SmartAppControlMode.Unknown,
            };
        }
        catch
        {
            return SmartAppControlMode.Unknown;
        }
    }

    private static void ProbeWmiReadOnly(
        List<string> exclusions,
        ref bool canReadExclusions,
        ref bool defenderAvailable,
        ref bool passiveMode,
        ref bool? isTamperProtected,
        ref string? diagnostic)
    {
        object? locator = null;
        object? services = null;
        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: false);
            if (locatorType is null)
            {
                diagnostic = "WbemScripting.SWbemLocator 不可用。";
                return;
            }

            locator = Activator.CreateInstance(locatorType);
            if (locator is null)
            {
                return;
            }

            services = InvokeMethod(locator, "ConnectServer", ".", @"root\Microsoft\Windows\Defender");
            if (services is null)
            {
                defenderAvailable = false;
                passiveMode = true;
                diagnostic = "无法连接到 root\\Microsoft\\Windows\\Defender WMI 命名空间。";
                return;
            }

            // Read AMRunningMode & IsTamperProtected from MSFT_MpComputerStatus
            TryQueryComputerStatus(services, ref passiveMode, ref isTamperProtected);

            // Read ExclusionPath from MSFT_MpPreference (Windows 11 non-admin may return null/empty)
            TryQueryExclusionPaths(services, exclusions, ref canReadExclusions);
        }
        catch (Exception ex)
        {
            Exception root = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            if (root is COMException comEx && comEx.HResult is WbemEInvalidNamespace or WbemEInvalidClass)
            {
                defenderAvailable = false;
                passiveMode = true;
                diagnostic = $"Microsoft Defender WMI 提供程序未启用 (0x{comEx.HResult:X8})。";
            }
            else
            {
                diagnostic = root.Message;
            }
        }
        finally
        {
            ReleaseComSafe(services);
            ReleaseComSafe(locator);
        }
    }

    private static void TryQueryComputerStatus(object services, ref bool passiveMode, ref bool? isTamperProtected)
    {
        object? queryResult = null;
        try
        {
            queryResult = InvokeMethod(services, "ExecQuery", "SELECT AMRunningMode, IsTamperProtected FROM MSFT_MpComputerStatus");
            if (queryResult is not IEnumerable enumerable)
            {
                return;
            }

            foreach (object? instance in enumerable)
            {
                if (instance is null)
                {
                    continue;
                }

                object? props = null;
                object? modeProp = null;
                object? tamperProp = null;
                try
                {
                    props = GetProperty(instance, "Properties_");
                    if (props is null)
                    {
                        continue;
                    }

                    modeProp = InvokeMethod(props, "Item", "AMRunningMode");
                    string? modeStr = GetProperty(modeProp!, "Value") as string;
                    if (!string.IsNullOrWhiteSpace(modeStr)
                        && (modeStr.Contains("Passive", StringComparison.OrdinalIgnoreCase)
                            || modeStr.Contains("Not Running", StringComparison.OrdinalIgnoreCase)
                            || modeStr.Contains("SxS", StringComparison.OrdinalIgnoreCase)))
                    {
                        passiveMode = true;
                    }

                    tamperProp = InvokeMethod(props, "Item", "IsTamperProtected");
                    object? rawTamper = GetProperty(tamperProp!, "Value");
                    if (rawTamper is bool b)
                    {
                        isTamperProtected = b;
                    }
                }
                catch
                {
                    // Property unavailable on older Windows builds.
                }
                finally
                {
                    ReleaseComSafe(tamperProp);
                    ReleaseComSafe(modeProp);
                    ReleaseComSafe(props);
                    ReleaseComSafe(instance);
                }
            }
        }
        catch
        {
            // Non-fatal: continue with registry evidence.
        }
        finally
        {
            ReleaseComSafe(queryResult);
        }
    }

    private static void TryQueryExclusionPaths(object services, List<string> exclusions, ref bool canReadExclusions)
    {
        object? queryResult = null;
        try
        {
            queryResult = InvokeMethod(services, "ExecQuery", "SELECT ExclusionPath FROM MSFT_MpPreference");
            if (queryResult is not IEnumerable enumerable)
            {
                return;
            }

            foreach (object? instance in enumerable)
            {
                if (instance is null)
                {
                    continue;
                }

                object? props = null;
                object? exclProp = null;
                try
                {
                    props = GetProperty(instance, "Properties_");
                    if (props is null)
                    {
                        continue;
                    }

                    exclProp = InvokeMethod(props, "Item", "ExclusionPath");
                    object? rawValue = GetProperty(exclProp!, "Value");
                    if (rawValue is string single && !string.IsNullOrWhiteSpace(single))
                    {
                        exclusions.Add(single.Trim());
                        canReadExclusions = true;
                    }
                    else if (rawValue is IEnumerable items)
                    {
                        foreach (object? item in items)
                        {
                            if (item is string text && !string.IsNullOrWhiteSpace(text))
                            {
                                exclusions.Add(text.Trim());
                            }
                        }

                        canReadExclusions = true;
                    }
                }
                catch
                {
                    // Access denied or hidden from non-admin users.
                }
                finally
                {
                    ReleaseComSafe(exclProp);
                    ReleaseComSafe(props);
                    ReleaseComSafe(instance);
                }
            }
        }
        catch
        {
            // Access denied on non-admin query is normal on Windows 11.
        }
        finally
        {
            ReleaseComSafe(queryResult);
        }
    }

    private static int? TryReadDword(RegistryKey key, string valueName)
    {
        object? raw = key.GetValue(valueName);
        if (raw is int i)
        {
            return i;
        }

        if (raw is long l)
        {
            return unchecked((int)l);
        }

        if (raw is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static object? InvokeMethod(object target, string methodName, params object?[] args) =>
        target.GetType().InvokeMember(methodName, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? GetProperty(object target, string propertyName) =>
        target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static void ReleaseComSafe(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                _ = Marshal.FinalReleaseComObject(comObject);
            }
            catch
            {
                // Ignore COM release race.
            }
        }
    }
}

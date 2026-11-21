using System.Buffers.Binary;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Spawn.Core;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Hard architectural security gate enforcing Clean-Host isolation:
/// 1. MSBuild project &amp; NuGet dependency graph isolation;
/// 2. Binary PE Import Directory + Delay-Import + Managed ImplMap P/Invoke static audit via <see cref="PEReader"/>;
/// 3. Minimum-privilege <c>0x1000</c> (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>) process inspection enforcement.
/// </summary>
public sealed class CleanHostArchitectureGateTests
{
    private static readonly HashSet<string> TrainerWorkerM3ForbiddenSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cross-process memory & remote code execution (ipc-and-security-contracts.md §4.1 & §4.2)
        "ReadProcessMemory",
        "WriteProcessMemory",
        "NtReadVirtualMemory",
        "NtWriteVirtualMemory",
        "ZwReadVirtualMemory",
        "ZwWriteVirtualMemory",
        "VirtualAllocEx",
        "VirtualProtectEx",
        "CreateRemoteThread",
        "CreateRemoteThreadEx",
        "NtCreateThreadEx",
        "ZwCreateThreadEx",
        "QueueUserAPC",
        "QueueUserAPC2",
        "RtlCreateUserThread",

        // Process debugging, suspension & thread hijacking (ipc-and-security-contracts.md §4.1 & §4.2)
        "DebugActiveProcess",
        "NtSuspendProcess",
        "ZwSuspendProcess",
        "NtResumeProcess",
        "ZwResumeProcess",
        "SuspendThread",
        "SetThreadContext",
        "GetThreadContext",
        "Wow64SetThreadContext",
    };

    private static readonly HashSet<string> ForbiddenNativeSymbols = new(
        TrainerWorkerM3ForbiddenSymbols.Concat(
        [
            // Global low-level hooks & synthetic input injection (forbidden in Clean Host; SendInput allowed in TrainerWorker)
            "SetWindowsHookEx",
            "SetWindowsHookExA",
            "SetWindowsHookExW",
            "SendInput",
            "keybd_event",
            "mouse_event",
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GloballyBannedPackagePrefixes =
    [
        "Reloaded",
        "SevenZipExtractor",
        "SevenZipSharp",
        "Squid-Box.SevenZipSharp",
        "Keystone",
        "KeystoneEngine",
        "Memory.dll",
        "MinHook",
        "EasyHook",
        "Squalr",
    ];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "OpenCvSharp",
        "Sdcb.PaddleOCR",
        "Reloaded",
        "Reloaded.Memory",
        "Reloaded.Hooks",
        "SevenZipExtractor",
        "SevenZipSharp",
        "Squid-Box.SevenZipSharp",
        "Keystone",
        "Memory.dll",
        "MinHook",
        "EasyHook",
        "Iced",
        "Maa.Framework",
        "Nefarius.ViGEm",
        "AlphaVSS",
        "Hi3Helper.Sophon",
        "Spawn.BlueprintWorker",
        "Spawn.TrainerWorker",
        "Spawn.WorkflowWorker",
        "Spawn.LegacyHook",
    ];

    private static readonly string[] CleanHostProjectNames =
    [
        "Spawn.Contracts",
        "Spawn.Core",
        "Spawn.Modules.Launcher",
        "Spawn.Modules.Achievement",
        "Spawn.Modules.Progress",
        "Spawn.Host",
        "Spawn.OverlayHost",
    ];

    private static readonly HashSet<string> ProgressExclusivePackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "SharpCompress",
        "AngleSharp",
    };

    [Fact]
    public void ProjectGraph_CleanHostProjects_NeverReferenceSensitivePackagesOrWorkers()
    {
        string repoRoot = LocateRepositoryRoot();
        string srcRoot = Path.Combine(repoRoot, "src");
        var csprojFiles = Directory.GetFiles(srcRoot, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(csprojFiles);

        var projectMap = new Dictionary<string, (string Path, List<string> ProjectRefs, List<string> PackageRefs)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (string csproj in csprojFiles)
        {
            string projName = Path.GetFileNameWithoutExtension(csproj);
            XDocument doc = XDocument.Load(csproj);

            var projRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
                .ToList();

            var pkgRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();

            projectMap[projName] = (csproj, projRefs, pkgRefs);
        }

        // 1. Spawn.Contracts must stay 100% pure (zero project references).
        Assert.True(projectMap.ContainsKey("Spawn.Contracts"), "Spawn.Contracts.csproj must exist.");
        Assert.Empty(projectMap["Spawn.Contracts"].ProjectRefs);

        // 2. Walk transitive project & package closure of every Clean-Host project.
        var violations = new List<string>();
        foreach (string cleanProj in CleanHostProjectNames)
        {
            if (!projectMap.ContainsKey(cleanProj))
            {
                violations.Add($"Missing expected Clean-Host project '{cleanProj}'.");
                continue;
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(cleanProj);

            while (stack.Count > 0)
            {
                string current = stack.Pop();
                if (!visited.Add(current) || !projectMap.TryGetValue(current, out var info))
                {
                    continue;
                }

                if (IsForbiddenAssemblyOrPackage(current))
                {
                    violations.Add($"{cleanProj} transitively references isolated worker project '{current}'.");
                }

                foreach (string pkg in info.PackageRefs)
                {
                    if (IsForbiddenAssemblyOrPackage(pkg))
                    {
                        violations.Add($"{cleanProj} -> {current} references forbidden sensitive package '{pkg}'.");
                    }
                }

                foreach (string child in info.ProjectRefs)
                {
                    stack.Push(child);
                }
            }
        }

        // 3. Spawn.Modules.Progress must not reference other Spawn.Modules.* or any Worker.
        if (projectMap.TryGetValue("Spawn.Modules.Progress", out var progressInfo))
        {
            foreach (string child in progressInfo.ProjectRefs)
            {
                if (child.StartsWith("Spawn.Modules.", StringComparison.OrdinalIgnoreCase)
                    || child.EndsWith("Worker", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"Spawn.Modules.Progress must not reference '{child}'.");
                }
            }
        }

        // 4. SharpCompress & AngleSharp may only appear directly in Spawn.Modules.Progress under src/.
        foreach (var (projName, info) in projectMap)
        {
            foreach (string pkg in info.PackageRefs)
            {
                if (ProgressExclusivePackages.Contains(pkg)
                    && !string.Equals(projName, "Spawn.Modules.Progress", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"Package '{pkg}' is restricted to Spawn.Modules.Progress but was directly referenced by '{projName}'.");
                }
            }
        }

        if (projectMap.TryGetValue("Spawn.Modules.Progress", out var progCheck))
        {
            foreach (string requiredPkg in ProgressExclusivePackages)
            {
                if (!progCheck.PackageRefs.Contains(requiredPkg, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add($"Expected Spawn.Modules.Progress to directly reference '{requiredPkg}'.");
                }
            }
        }

        // 5. Spawn.TrainerWorker must exist and stay isolated from Clean Host / domain modules.
        if (!projectMap.TryGetValue("Spawn.TrainerWorker", out var workerInfo))
        {
            violations.Add("Missing expected isolated worker project 'Spawn.TrainerWorker'.");
        }
        else
        {
            foreach (string child in workerInfo.ProjectRefs)
            {
                if (child.Equals("Spawn.Host", StringComparison.OrdinalIgnoreCase)
                    || child.Equals("Spawn.OverlayHost", StringComparison.OrdinalIgnoreCase)
                    || child.StartsWith("Spawn.Modules.", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"Spawn.TrainerWorker must not reference '{child}'.");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("Spawn.Host")]
    [InlineData("Spawn.OverlayHost")]
    public void BinaryPeAudit_HostAndOverlayOutputClosure_ZeroSensitiveWin32ImportsOrPackages(string hostProjectName)
    {
        string repoRoot = LocateRepositoryRoot();
        string outputDir = LocateBuildOutputDirectory(repoRoot, hostProjectName);

        string mainDll = Path.Combine(outputDir, $"{hostProjectName}.dll");
        string mainExe = Path.Combine(outputDir, $"{hostProjectName}.exe");
        Assert.True(File.Exists(mainDll), $"Expected compiled assembly missing: {mainDll}");
        Assert.True(File.Exists(mainExe), $"Expected compiled native apphost missing: {mainExe}");

        var binariesToAudit = EnumerateWindowsPeBinariesInOutput(outputDir);
        Assert.True(binariesToAudit.Count >= 3, $"Expected at least Host/Core/Contracts binaries in {outputDir}.");

        var violations = new List<string>();
        int totalExtractedSymbols = 0;

        foreach (string pePath in binariesToAudit)
        {
            string fileName = Path.GetFileName(pePath);
            string baseName = Path.GetFileNameWithoutExtension(pePath);

            if (IsForbiddenAssemblyOrPackage(baseName))
            {
                violations.Add($"[{hostProjectName}] Forbidden binary copied into output directory: {fileName}");
            }

            PeInspectionReport report = InspectPeBinary(pePath);
            totalExtractedSymbols += report.ImportedSymbols.Count;

            foreach (string asmRef in report.ReferencedAssemblies)
            {
                if (IsForbiddenAssemblyOrPackage(asmRef))
                {
                    violations.Add($"[{hostProjectName}] {fileName} references forbidden assembly '{asmRef}'.");
                }
            }

            foreach (var (moduleName, symbolName, sourceKind) in report.ImportedSymbols)
            {
                if (ForbiddenNativeSymbols.Contains(symbolName))
                {
                    violations.Add(
                        $"[{hostProjectName}] {fileName} imports forbidden symbol '{moduleName}!{symbolName}' via {sourceKind}.");
                }
            }
        }

        // Non-vacuity guard: apphost (.exe) and Spawn.Core.dll genuinely import benign Win32 symbols.
        Assert.True(
            totalExtractedSymbols > 0,
            $"PEReader must extract non-empty native/PInvoke symbol tables across {hostProjectName} output binaries.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void BinaryPeAudit_ProgressModuleOutputClosure_ZeroSensitiveWin32ImportsOrPackages()
    {
        string repoRoot = LocateRepositoryRoot();
        string outputDir = LocateBuildOutputDirectory(repoRoot, "Spawn.Modules.Progress", requireNativeExe: false);

        string mainDll = Path.Combine(outputDir, "Spawn.Modules.Progress.dll");
        Assert.True(File.Exists(mainDll), $"Expected compiled assembly missing: {mainDll}");

        var binariesToAudit = EnumerateWindowsPeBinariesInOutput(outputDir);
        Assert.True(binariesToAudit.Count >= 3, $"Expected at least Progress/Core/Contracts binaries in {outputDir}.");

        var violations = new List<string>();
        int totalExtractedSymbols = 0;

        foreach (string pePath in binariesToAudit)
        {
            string fileName = Path.GetFileName(pePath);
            string baseName = Path.GetFileNameWithoutExtension(pePath);

            if (IsForbiddenAssemblyOrPackage(baseName))
            {
                violations.Add($"[Spawn.Modules.Progress] Forbidden binary copied into output directory: {fileName}");
            }

            PeInspectionReport report = InspectPeBinary(pePath);
            totalExtractedSymbols += report.ImportedSymbols.Count;

            foreach (string asmRef in report.ReferencedAssemblies)
            {
                if (IsForbiddenAssemblyOrPackage(asmRef))
                {
                    violations.Add($"[Spawn.Modules.Progress] {fileName} references forbidden assembly '{asmRef}'.");
                }
            }

            foreach (var (moduleName, symbolName, sourceKind) in report.ImportedSymbols)
            {
                if (ForbiddenNativeSymbols.Contains(symbolName))
                {
                    violations.Add(
                        $"[Spawn.Modules.Progress] {fileName} imports forbidden symbol '{moduleName}!{symbolName}' via {sourceKind}.");
                }
            }
        }

        Assert.True(
            totalExtractedSymbols > 0,
            "PEReader must extract non-empty native/PInvoke symbol tables across Spawn.Modules.Progress output closure.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void BinaryPeAudit_TrainerWorkerOutputClosure_ZeroCrossProcessMemoryOrDebugImports()
    {
        string repoRoot = LocateRepositoryRoot();
        string outputDir = LocateBuildOutputDirectory(repoRoot, "Spawn.TrainerWorker", requireNativeExe: true);

        string mainDll = Path.Combine(outputDir, "Spawn.TrainerWorker.dll");
        string mainExe = Path.Combine(outputDir, "Spawn.TrainerWorker.exe");
        Assert.True(File.Exists(mainDll), $"Expected compiled assembly missing: {mainDll}");
        Assert.True(File.Exists(mainExe), $"Expected compiled native apphost missing: {mainExe}");

        var binariesToAudit = EnumerateWindowsPeBinariesInOutput(outputDir);
        Assert.True(binariesToAudit.Count >= 3, $"Expected at least TrainerWorker/Core/Contracts binaries in {outputDir}.");

        var violations = new List<string>();
        int totalExtractedSymbols = 0;

        foreach (string pePath in binariesToAudit)
        {
            string fileName = Path.GetFileName(pePath);
            string baseName = Path.GetFileNameWithoutExtension(pePath);

            if (IsGloballyBannedPackage(baseName) || baseName.Equals("Iced", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"[Spawn.TrainerWorker] Forbidden binary copied into M3 worker output directory: {fileName}");
            }

            PeInspectionReport report = InspectPeBinary(pePath);
            totalExtractedSymbols += report.ImportedSymbols.Count;

            foreach (string asmRef in report.ReferencedAssemblies)
            {
                if (IsGloballyBannedPackage(asmRef) || asmRef.Equals("Iced", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"[Spawn.TrainerWorker] {fileName} references forbidden M3 assembly '{asmRef}'.");
                }
            }

            foreach (var (moduleName, symbolName, sourceKind) in report.ImportedSymbols)
            {
                if (TrainerWorkerM3ForbiddenSymbols.Contains(symbolName))
                {
                    violations.Add(
                        $"[Spawn.TrainerWorker] {fileName} imports forbidden M3 symbol '{moduleName}!{symbolName}' via {sourceKind}.");
                }
            }
        }

        // Also enforce ipc-and-security-contracts.md §4.3: zero PROCESS_VM_* / PROCESS_ALL_ACCESS declarations in M3 worker source.
        string workerSourceDir = Path.Combine(repoRoot, "src", "Spawn.TrainerWorker");
        var vmAccessRegex = new Regex(
            @"\b(?:PROCESS_VM_READ|PROCESS_VM_WRITE|PROCESS_VM_OPERATION|PROCESS_CREATE_THREAD|PROCESS_ALL_ACCESS)\b",
            RegexOptions.Compiled);
        foreach (string csFile in Directory.EnumerateFiles(workerSourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsUnderIntermediateOrBinDirectory(csFile))
            {
                continue;
            }

            string stripped = StripCSharpComments(File.ReadAllText(csFile, Encoding.UTF8));
            Match m = vmAccessRegex.Match(stripped);
            if (m.Success)
            {
                string relPath = Path.GetRelativePath(repoRoot, csFile).Replace('\\', '/');
                violations.Add($"{relPath}: forbidden M3 cross-process VM access right ('{m.Value}')");
            }
        }

        Assert.True(
            totalExtractedSymbols > 0,
            "PEReader must extract non-empty native/PInvoke symbol tables across Spawn.TrainerWorker output binaries.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void PackageAssetsAudit_WholeRepository_ZeroBannedPackagesInTransitiveClosure()
    {
        string repoRoot = LocateRepositoryRoot();
        var projectDirs = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Select(p => Path.GetDirectoryName(p)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(projectDirs.Count >= 10, $"Expected at least 10 projects across src/ and tests/, found {projectDirs.Count}.");

        var violations = new List<string>();
        int auditedAssetsCount = 0;

        foreach (string projDir in projectDirs)
        {
            string assetsPath = Path.Combine(projDir, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                violations.Add($"Missing restored assets file: {Path.GetRelativePath(repoRoot, assetsPath).Replace('\\', '/')}");
                continue;
            }

            auditedAssetsCount++;
            string relAssets = Path.GetRelativePath(repoRoot, assetsPath).Replace('\\', '/');
            using var stream = new FileStream(assetsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("libraries", out JsonElement libraries)
                && libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty lib in libraries.EnumerateObject())
                {
                    string packageId = lib.Name.Split('/')[0];
                    if (IsGloballyBannedPackage(packageId))
                    {
                        violations.Add($"{relAssets}: banned package '{lib.Name}' detected in dependency graph.");
                    }
                }
            }
        }

        Assert.Equal(projectDirs.Count, auditedAssetsCount);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void BinaryPeAudit_ScannerNonVacuityProof_DetectsRealNativeAndManagedImports()
    {
        string repoRoot = LocateRepositoryRoot();
        string hostOut = LocateBuildOutputDirectory(repoRoot, "Spawn.Host");

        // 1. Verify managed MetadataReader ImplMap extraction on Spawn.Core.dll
        string coreDll = Path.Combine(hostOut, "Spawn.Core.dll");
        PeInspectionReport coreReport = InspectPeBinary(coreDll);
        Assert.Contains(
            coreReport.ImportedSymbols,
            s => string.Equals(s.Symbol, "OpenProcess", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            coreReport.ImportedSymbols,
            s => string.Equals(s.Symbol, "QueryFullProcessImageNameW", StringComparison.OrdinalIgnoreCase));

        // 2. Verify native PE Import Directory RVA walking on Spawn.Host.exe apphost
        string hostExe = Path.Combine(hostOut, "Spawn.Host.exe");
        PeInspectionReport exeReport = InspectPeBinary(hostExe);
        Assert.Contains(
            exeReport.ImportedSymbols,
            s => s.SourceKind == "NativeImportTable" && !string.IsNullOrWhiteSpace(s.Symbol));
    }

    [Fact]
    public void SafeProcessPrivilegeAudit_CleanHostSourceTree_UsesOnlyLimitedQueryRights()
    {
        Assert.Equal(0x00001000u, SafeProcessInspector.ProcessQueryLimitedInformation);
        Assert.Equal(0x00100000u, SafeProcessInspector.Synchronize);

        string repoRoot = LocateRepositoryRoot();
        var violations = new List<string>();

        string[] cleanDirs =
        [
            Path.Combine(repoRoot, "src", "Spawn.Contracts"),
            Path.Combine(repoRoot, "src", "Spawn.Core"),
            Path.Combine(repoRoot, "src", "Spawn.Modules.Launcher"),
            Path.Combine(repoRoot, "src", "Spawn.Modules.Achievement"),
            Path.Combine(repoRoot, "src", "Spawn.Modules.Progress"),
            Path.Combine(repoRoot, "src", "Spawn.Host"),
            Path.Combine(repoRoot, "src", "Spawn.OverlayHost"),
        ];

        (string Label, Regex Pattern)[] forbiddenSourcePatterns =
        [
            ("PROCESS_VM_READ / PROCESS_VM_WRITE / PROCESS_VM_OPERATION / PROCESS_ALL_ACCESS",
                new Regex(@"\b(?:PROCESS_VM_READ|PROCESS_VM_WRITE|PROCESS_VM_OPERATION|PROCESS_CREATE_THREAD|PROCESS_ALL_ACCESS)\b", RegexOptions.Compiled)),
            ("Hex VM_READ/ALL_ACCESS flags on OpenProcess",
                new Regex(@"OpenProcess\s*\(\s*(?:0x0*1F0FFF|0x0*1FFFFF|0x0*10|0x0*20|0x0*28|0x0*38)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("Process.MainModule cross-process VM_READ hazard",
                new Regex(@"\.\s*MainModule\b", RegexOptions.Compiled)),
            ("Low-level global input hooks (WH_KEYBOARD_LL / WH_MOUSE_LL)",
                new Regex(@"\b(?:WH_KEYBOARD_LL|WH_MOUSE_LL)\b", RegexOptions.Compiled)),
            ("Game-killing JobObject flag (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE / 0x2000)",
                new Regex(@"\bJOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE\b", RegexOptions.Compiled)),
        ];

        foreach (string dir in cleanDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string csFile in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsUnderIntermediateOrBinDirectory(csFile))
                {
                    continue;
                }

                string rawCode = File.ReadAllText(csFile, Encoding.UTF8);
                string stripped = StripCSharpComments(rawCode);
                string relPath = Path.GetRelativePath(repoRoot, csFile).Replace('\\', '/');

                foreach (var (label, regex) in forbiddenSourcePatterns)
                {
                    Match m = regex.Match(stripped);
                    if (m.Success)
                    {
                        violations.Add($"{relPath}: forbidden {label} ('{m.Value}')");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static bool IsGloballyBannedPackage(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Contains("SevenZipSharp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string prefix in GloballyBannedPackagePrefixes)
        {
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForbiddenAssemblyOrPackage(string name)
    {
        if (IsGloballyBannedPackage(name))
        {
            return true;
        }

        foreach (string prefix in ForbiddenAssemblyPrefixes)
        {
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string LocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Spawn.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Spawn.sln.");
    }

    private static string LocateBuildOutputDirectory(string repoRoot, string projectName, bool requireNativeExe = true)
    {
        string activeConfig = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        string[] preferredConfigs = activeConfig == "Release"
            ? ["Release", "Debug"]
            : ["Debug", "Release"];

        foreach (string cfg in preferredConfigs)
        {
            string binConfigDir = Path.Combine(repoRoot, "src", projectName, "bin", cfg);
            if (!Directory.Exists(binConfigDir))
            {
                continue;
            }

            foreach (string tfmDir in Directory.GetDirectories(binConfigDir))
            {
                bool hasDll = File.Exists(Path.Combine(tfmDir, $"{projectName}.dll"));
                bool hasExe = !requireNativeExe || File.Exists(Path.Combine(tfmDir, $"{projectName}.exe"));
                if (hasDll && hasExe)
                {
                    return tfmDir;
                }
            }
        }

        throw new DirectoryNotFoundException($"Build output directory for {projectName} was not found.");
    }

    private static List<string> EnumerateWindowsPeBinariesInOutput(string outputDir)
    {
        var results = new List<string>();

        foreach (string file in Directory.EnumerateFiles(outputDir, "*", SearchOption.TopDirectoryOnly))
        {
            string ext = Path.GetExtension(file);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(file);
            }
        }

        string runtimesDir = Path.Combine(outputDir, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            foreach (string winRuntimeDir in Directory.EnumerateDirectories(runtimesDir, "win*"))
            {
                foreach (string nativeFile in Directory.EnumerateFiles(winRuntimeDir, "*.dll", SearchOption.AllDirectories))
                {
                    results.Add(nativeFile);
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static PeInspectionReport InspectPeBinary(string pePath)
    {
        var importedSymbols = new List<(string Module, string Symbol, string SourceKind)>();
        var referencedAssemblies = new List<string>();

        using var fs = new FileStream(pePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(fs);

        // 1. Managed Metadata P/Invoke (ImplMap) + AssemblyRef audit
        if (peReader.HasMetadata)
        {
            MetadataReader md = peReader.GetMetadataReader();

            foreach (AssemblyReferenceHandle asmHandle in md.AssemblyReferences)
            {
                AssemblyReference asmRef = md.GetAssemblyReference(asmHandle);
                string asmName = md.GetString(asmRef.Name);
                referencedAssemblies.Add(asmName);
            }

            foreach (MethodDefinitionHandle methodHandle in md.MethodDefinitions)
            {
                MethodDefinition methodDef = md.GetMethodDefinition(methodHandle);
                MethodImport import = methodDef.GetImport();
                if (import.Name.IsNil || import.Module.IsNil)
                {
                    continue;
                }

                ModuleReference modRef = md.GetModuleReference(import.Module);
                string modName = modRef.Name.IsNil ? string.Empty : md.GetString(modRef.Name);
                string symName = md.GetString(import.Name);
                importedSymbols.Add((modName, symName, "ManagedPInvoke"));
            }
        }

        // 2. Native PE Import Directory & Delay-Import Directory audit
        PEHeader? peHeader = peReader.PEHeaders.PEHeader;
        if (peHeader is not null)
        {
            bool isPe32Plus = peHeader.Magic == PEMagic.PE32Plus;
            ReadNativeImportDirectory(peReader, peHeader.ImportTableDirectory, isPe32Plus, isDelayLoad: false, importedSymbols);
            ReadNativeImportDirectory(peReader, peHeader.DelayImportTableDirectory, isPe32Plus, isDelayLoad: true, importedSymbols);
        }

        return new PeInspectionReport(importedSymbols, referencedAssemblies);
    }

    private static void ReadNativeImportDirectory(
        PEReader peReader,
        DirectoryEntry importDir,
        bool isPe32Plus,
        bool isDelayLoad,
        List<(string Module, string Symbol, string SourceKind)> sink)
    {
        if (importDir.RelativeVirtualAddress <= 0 || importDir.Size <= 0)
        {
            return;
        }

        PEMemoryBlock dirBlock;
        try
        {
            dirBlock = peReader.GetSectionData(importDir.RelativeVirtualAddress);
        }
        catch
        {
            return;
        }

        ReadOnlySpan<byte> span = dirBlock.GetContent().AsSpan();
        int descriptorSize = isDelayLoad ? 32 : 20;
        string sourceKind = isDelayLoad ? "NativeDelayImportTable" : "NativeImportTable";

        for (int offset = 0; offset + descriptorSize <= span.Length && offset < importDir.Size; offset += descriptorSize)
        {
            int nameRva;
            int iltRva;

            if (isDelayLoad)
            {
                // ImgDelayDescr v2: grAttrs(0), rvaDLLName(4), rvaHmod(8), rvaIAT(12), rvaINT(16)
                nameRva = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
                iltRva = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 16, 4));
                if (nameRva == 0 && iltRva == 0)
                {
                    break;
                }
            }
            else
            {
                // IMAGE_IMPORT_DESCRIPTOR: OriginalFirstThunk(0), TimeDateStamp(4), ForwarderChain(8), Name(12), FirstThunk(16)
                int originalFirstThunk = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
                nameRva = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 12, 4));
                int firstThunk = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 16, 4));
                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                {
                    break;
                }

                iltRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            }

            if (nameRva <= 0 || iltRva <= 0)
            {
                continue;
            }

            string dllName = TryReadAsciiStringAtRva(peReader, nameRva) ?? string.Empty;
            ReadThunkSymbolNames(peReader, iltRva, isPe32Plus, dllName, sourceKind, sink);
        }
    }

    private static void ReadThunkSymbolNames(
        PEReader peReader,
        int thunkRva,
        bool isPe32Plus,
        string dllName,
        string sourceKind,
        List<(string Module, string Symbol, string SourceKind)> sink)
    {
        PEMemoryBlock thunkBlock;
        try
        {
            thunkBlock = peReader.GetSectionData(thunkRva);
        }
        catch
        {
            return;
        }

        ReadOnlySpan<byte> thunks = thunkBlock.GetContent().AsSpan();
        int step = isPe32Plus ? 8 : 4;

        for (int pos = 0; pos + step <= thunks.Length; pos += step)
        {
            int hintNameRva;
            if (isPe32Plus)
            {
                ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(thunks.Slice(pos, 8));
                if (entry == 0)
                {
                    break;
                }

                // Bit 63 set = import by ordinal.
                if ((entry & 0x8000_0000_0000_0000UL) != 0)
                {
                    continue;
                }

                hintNameRva = (int)(entry & 0x7FFF_FFFFUL);
            }
            else
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(thunks.Slice(pos, 4));
                if (entry == 0)
                {
                    break;
                }

                // Bit 31 set = import by ordinal.
                if ((entry & 0x8000_0000U) != 0)
                {
                    continue;
                }

                hintNameRva = (int)(entry & 0x7FFF_FFFFU);
            }

            // IMAGE_IMPORT_BY_NAME: 2-byte Hint followed by null-terminated ASCII name.
            string? funcName = TryReadAsciiStringAtRva(peReader, hintNameRva + 2);
            if (!string.IsNullOrWhiteSpace(funcName))
            {
                sink.Add((dllName, funcName, sourceKind));
            }
        }
    }

    private static string? TryReadAsciiStringAtRva(PEReader peReader, int rva)
    {
        if (rva <= 0)
        {
            return null;
        }

        try
        {
            PEMemoryBlock block = peReader.GetSectionData(rva);
            ReadOnlySpan<byte> bytes = block.GetContent().AsSpan();
            int len = bytes.IndexOf((byte)0);
            if (len < 0)
            {
                len = Math.Min(bytes.Length, 256);
            }

            return len == 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..len]);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderIntermediateOrBinDirectory(string fullPath)
    {
        string normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripCSharpComments(string source)
    {
        // Strip /* ... */ block comments and // ... line comments while preserving code tokens.
        string withoutBlock = Regex.Replace(source, @"/\*[\s\S]*?\*/", " ");
        return Regex.Replace(withoutBlock, @"//[^\r\n]*", " ");
    }

    private sealed record PeInspectionReport(
        IReadOnlyList<(string Module, string Symbol, string SourceKind)> ImportedSymbols,
        IReadOnlyList<string> ReferencedAssemblies);
}

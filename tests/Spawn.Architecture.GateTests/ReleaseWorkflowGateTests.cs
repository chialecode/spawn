using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Structural security and supply-chain gate for <c>.github/workflows/release.yml</c>
/// (ADR-0007 §3.2, M3-W11, <c>M3-A-21</c>).
/// Parsed using .NET BCL only (zero external YAML packages).
/// </summary>
public sealed class ReleaseWorkflowGateTests
{
    private const string ExpectedNsis313ZipUrl =
        "https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip";

    private const string ExpectedNsis313ZipSha256 =
        "ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8";

    private const string ExpectedUploadArtifactRef =
        "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9";

    private const string ExpectedDownloadArtifactRef =
        "actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333";

    private static readonly Regex PinnedActionRefRegex = new(
        @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_./-]+)?@[0-9a-f]{40}$",
        RegexOptions.Compiled);

    private static readonly string[] ExpectedJobOrder = ["plan", "check", "build", "publish"];
    private static readonly string[] ExpectedCheckNeeds = ["plan"];
    private static readonly string[] ExpectedBuildNeeds = ["plan", "check"];
    private static readonly string[] ExpectedPublishNeeds = ["plan", "build"];

    [Fact]
    public void ReleaseWorkflow_TriggersPermissionsAndConcurrency_EnforceStrictScheduleAndDispatchOnly()
    {
        ParsedWorkflow workflow = LoadReleaseWorkflow();

        // 1. Triggers: ONLY schedule ("0 0 * * 1") and workflow_dispatch (boolean input publish, default false).
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "schedule", "workflow_dispatch" },
            workflow.TriggerKeys);
        Assert.DoesNotContain("push", workflow.TriggerKeys);
        Assert.DoesNotContain("pull_request", workflow.TriggerKeys);
        Assert.DoesNotContain("pull_request_target", workflow.TriggerKeys);

        string onBlock = workflow.GetTopLevelBlock("on");
        Assert.Matches(new Regex(@"-\s+cron:\s*[""']0 0 \* \* 1[""']", RegexOptions.Multiline), onBlock);

        string publishInputBlock = ExtractNestedMappingBlock(onBlock, "publish", parentIndent: 4);
        Assert.Matches(new Regex(@"^\s+type:\s*boolean\s*$", RegexOptions.Multiline), publishInputBlock);
        Assert.Matches(new Regex(@"^\s+default:\s*false\s*$", RegexOptions.Multiline), publishInputBlock);

        // 2. Top-level permissions MUST be explicitly empty: `permissions: {}`.
        string permissionsBlock = workflow.GetTopLevelBlock("permissions").Trim();
        Assert.Equal("permissions: {}", permissionsBlock);

        // 3. Top-level concurrency MUST serialize releases without canceling in-progress runs.
        string concurrencyBlock = workflow.GetTopLevelBlock("concurrency");
        Assert.Matches(new Regex(@"^\s+group:\s*release\s*$", RegexOptions.Multiline), concurrencyBlock);
        Assert.Matches(new Regex(@"^\s+cancel-in-progress:\s*false\s*$", RegexOptions.Multiline), concurrencyBlock);
    }

    [Fact]
    public void ReleaseWorkflow_FourJobGraphAndLeastPrivilegePermissions_RestrictWriteOnlyToPublish()
    {
        ParsedWorkflow workflow = LoadReleaseWorkflow();

        // Exact 4-job chain: plan -> check -> build -> publish.
        Assert.Equal(ExpectedJobOrder, workflow.Jobs.Keys.ToArray());

        ParsedJob plan = workflow.Jobs["plan"];
        ParsedJob check = workflow.Jobs["check"];
        ParsedJob build = workflow.Jobs["build"];
        ParsedJob publish = workflow.Jobs["publish"];

        // Runners
        Assert.Equal("ubuntu-24.04", plan.RunsOn);
        Assert.Equal("windows-2025", check.RunsOn);
        Assert.Equal("windows-2025", build.RunsOn);
        Assert.Equal("ubuntu-24.04", publish.RunsOn);

        // Dependencies (needs)
        Assert.Empty(plan.Needs);
        Assert.Equal(ExpectedCheckNeeds, check.Needs);
        Assert.Equal(ExpectedBuildNeeds, build.Needs);
        Assert.Equal(ExpectedPublishNeeds, publish.Needs);

        // Conditions & environment boundaries
        Assert.Contains("needs.plan.outputs.release == 'true'", check.IfCondition, StringComparison.Ordinal);
        Assert.Equal("release", build.Environment);
        Assert.Null(plan.Environment);
        Assert.Null(check.Environment);
        Assert.Null(publish.Environment);

        // F-02: releases may only run from main. plan gates the whole chain (check/build/publish skip
        // with it) and publish re-asserts the ref so a dispatch from any other branch can never publish.
        const string MainRefGuard = "github.ref == 'refs/heads/main'";
        Assert.NotNull(plan.IfCondition);
        Assert.Equal(MainRefGuard, plan.IfCondition!.Trim());
        Assert.NotNull(publish.IfCondition);
        Assert.StartsWith(MainRefGuard + " && ", publish.IfCondition!.Trim(), StringComparison.Ordinal);
        Assert.False(
            ContainsTopLevelOr(publish.IfCondition!),
            "publish if-condition must not contain a top-level `||` that could bypass the main ref guard.");
        Assert.Contains("vars.RELEASE_PUBLISH == '1'", publish.IfCondition, StringComparison.Ordinal);
        Assert.DoesNotContain("SPAWN_RELEASE_PUBLISH", publish.IfCondition, StringComparison.Ordinal);
        Assert.Contains("github.event_name == 'schedule'", publish.IfCondition, StringComparison.Ordinal);
        Assert.Contains("inputs.publish == true", publish.IfCondition, StringComparison.Ordinal);

        // Plan outputs & command
        Assert.Matches(new Regex(@"^\s+release:\s*\$\{\{\s*steps\.plan\.outputs\.release\s*\}\}\s*$", RegexOptions.Multiline), plan.RawBlock);
        Assert.Matches(new Regex(@"^\s+version:\s*\$\{\{\s*steps\.plan\.outputs\.version\s*\}\}\s*$", RegexOptions.Multiline), plan.RawBlock);
        Assert.Contains("python3 -B scripts/release.py --plan", plan.AllRunScripts, StringComparison.Ordinal);

        // Least privilege: ONLY publish holds contents: write; every other job is read-only.
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" }, plan.Permissions);
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" }, check.Permissions);
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" }, build.Permissions);
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "write" }, publish.Permissions);

        foreach (ParsedJob job in workflow.Jobs.Values)
        {
            foreach (var (scope, level) in job.Permissions)
            {
                if (level.Contains("write", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal("publish", job.Name);
                    Assert.Equal("contents", scope);
                }
            }
        }

        // CI=true on Windows jobs so dev-install hooks are skipped cleanly.
        Assert.Matches(new Regex(@"^\s+CI:\s*[""']true[""']\s*$", RegexOptions.Multiline), check.RawBlock);
        Assert.Matches(new Regex(@"^\s+CI:\s*[""']true[""']\s*$", RegexOptions.Multiline), build.RawBlock);
    }

    [Fact]
    public void ReleaseWorkflow_AllThirdPartyActions_PinExact40HexCommitShaAndSyncWithCi()
    {
        ParsedWorkflow workflow = LoadReleaseWorkflow();
        string repoRoot = LocateRepositoryRoot();
        string ciText = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));

        var allSteps = workflow.Jobs.Values.SelectMany(j => j.Steps).ToList();
        var usesSteps = allSteps.Where(s => !string.IsNullOrWhiteSpace(s.Uses)).ToList();
        Assert.NotEmpty(usesSteps);

        foreach (ParsedStep step in usesSteps)
        {
            string usesRef = step.Uses!;
            if (usesRef.StartsWith("./", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                PinnedActionRefRegex.IsMatch(usesRef),
                $"Workflow action '{usesRef}' in step '{step.Name}' must pin an exact 40-character lowercase hex commit SHA.");
        }

        // Every checkout step must disable credential persistence; plan/check/build need full history/tags.
        foreach (ParsedJob job in workflow.Jobs.Values)
        {
            var checkouts = job.Steps
                .Where(s => s.Uses is not null && s.Uses.StartsWith("actions/checkout@", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(checkouts);

            foreach (ParsedStep checkout in checkouts)
            {
                Assert.Contains(checkout.Uses!, ciText, StringComparison.Ordinal);
                Assert.Matches(new Regex(@"^\s+persist-credentials:\s*false\s*$", RegexOptions.Multiline), checkout.RawBlock);

                if (job.Name is "plan" or "check" or "build")
                {
                    Assert.Matches(new Regex(@"^\s+fetch-depth:\s*0\s*$", RegexOptions.Multiline), checkout.RawBlock);
                }
            }
        }

        // setup-dotnet SHA must stay in exact sync with ci.yml.
        var setupDotnetSteps = allSteps
            .Where(s => s.Uses is not null && s.Uses.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, setupDotnetSteps.Count);
        foreach (ParsedStep dotnetStep in setupDotnetSteps)
        {
            Assert.Contains(dotnetStep.Uses!, ciText, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"^\s+global-json-file:\s*global\.json\s*$", RegexOptions.Multiline), dotnetStep.RawBlock);
        }

        // Artifact transfer actions must use exact 7-day-cooldown verified commit SHAs.
        Assert.Contains(workflow.Jobs["build"].Steps, s => string.Equals(s.Uses, ExpectedUploadArtifactRef, StringComparison.Ordinal));
        Assert.Contains(workflow.Jobs["publish"].Steps, s => string.Equals(s.Uses, ExpectedDownloadArtifactRef, StringComparison.Ordinal));

        // Tracked .github/actions-policy.json must require SHA pinning and allowlist every third-party action used.
        string policyPath = Path.Combine(repoRoot, ".github", "actions-policy.json");
        Assert.True(File.Exists(policyPath), $"Missing Actions policy manifest: {policyPath}");
        using JsonDocument policyDoc = JsonDocument.Parse(File.ReadAllText(policyPath));
        Assert.True(policyDoc.RootElement.GetProperty("permissions").GetProperty("sha_pinning_required").GetBoolean());
        HashSet<string> allowedPatterns = policyDoc.RootElement
            .GetProperty("selectedActions")
            .GetProperty("patterns_allowed")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        foreach (ParsedStep step in usesSteps)
        {
            string usesRef = step.Uses!;
            if (usesRef.StartsWith("./", StringComparison.Ordinal))
            {
                continue;
            }

            string actionRepo = usesRef[..usesRef.IndexOf('@', StringComparison.Ordinal)];
            Assert.Contains($"{actionRepo}@*", allowedPatterns);
        }
    }

    [Fact]
    public void ReleaseWorkflow_CheckJob_MatchesQuickGateIsolationOfflineTripwireAndDocsGates()
    {
        ParsedWorkflow workflow = LoadReleaseWorkflow();
        ParsedJob check = workflow.Jobs["check"];
        string scripts = check.AllRunScripts;

        Assert.Contains("dotnet restore Spawn.sln", scripts, StringComparison.Ordinal);
        Assert.Contains("dotnet build Spawn.sln -c Release", scripts, StringComparison.Ordinal);
        Assert.Contains("dotnet test Spawn.sln -c Release --no-build", scripts, StringComparison.Ordinal);

        // Temp data isolation, headless/mute flags, and offline network tripwire matching quick_gate.py.
        Assert.Contains("SPAWN_DATA_DIR", scripts, StringComparison.Ordinal);
        Assert.Contains("SPAWN_VAULT_DIR", scripts, StringComparison.Ordinal);
        Assert.Contains("SPAWN_CHANNEL", scripts, StringComparison.Ordinal);
        Assert.Contains("SPAWN_TEST_HEADLESS", scripts, StringComparison.Ordinal);
        Assert.Contains("SPAWN_HEADLESS_TEST", scripts, StringComparison.Ordinal);
        Assert.Contains("SPAWN_MUTE_AUDIO", scripts, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:9", scripts, StringComparison.Ordinal);
        Assert.Contains("HTTP_PROXY", scripts, StringComparison.Ordinal);
        Assert.Contains("HTTPS_PROXY", scripts, StringComparison.Ordinal);
        Assert.Contains("SPAWN_NETWORK_TRIPWIRE", scripts, StringComparison.Ordinal);
        Assert.Contains("NO_PROXY", scripts, StringComparison.Ordinal);

        // Release and packaging tooling unit tests, doc governance gate, and whitespace hygiene.
        Assert.Contains("python -B scripts/test_release.py", scripts, StringComparison.Ordinal);
        Assert.Contains("python -B scripts/test_package.py", scripts, StringComparison.Ordinal);
        Assert.Contains("python -B scripts/check_docs.py", scripts, StringComparison.Ordinal);
        Assert.Contains("git diff --check", scripts, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_BuildJob_UsesPinnedNsis313IsolatesSigningSecretAndRunsInstallDrill()
    {
        ParsedWorkflow workflow = LoadReleaseWorkflow();
        ParsedJob build = workflow.Jobs["build"];

        // 1. Secret UPDATE_SIGNING_KEY_PEM must appear ONLY in the build job (with environment: release).
        Assert.DoesNotContain("UPDATE_SIGNING_KEY_PEM", workflow.HeaderText, StringComparison.Ordinal);
        foreach (ParsedJob job in workflow.Jobs.Values)
        {
            Assert.DoesNotContain("SPAWN_UPDATE_SIGNING_KEY_PEM", job.RawBlock, StringComparison.Ordinal);
            if (job.Name == "build")
            {
                Assert.Contains("secrets.UPDATE_SIGNING_KEY_PEM", job.RawBlock, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("UPDATE_SIGNING_KEY_PEM", job.RawBlock, StringComparison.Ordinal);
                Assert.DoesNotContain("SPAWN_UPDATE_SIGNING_KEY", job.RawBlock, StringComparison.Ordinal);
            }
        }

        // 2. The installer compiler is the pinned official NSIS 3.13 zip that scripts/package.py fetches
        //    and checks by SHA-256; the workflow never points it at another makensis, and
        //    installer/spawn.nsi refuses any other NSIS version.
        string repoRoot = LocateRepositoryRoot();
        string workflowText = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
        Assert.DoesNotContain("SPAWN_MAKENSIS_PATH", workflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("SPAWN_RELEASE_PUBLISH", workflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("SPAWN_UPDATE_SIGNING_KEY_PEM", workflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("installer/update-signing-public.pem", workflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("innosetup", workflowText, StringComparison.OrdinalIgnoreCase);
        string packagePy = File.ReadAllText(Path.Combine(repoRoot, "scripts", "package.py"));
        Assert.Contains($"NSIS_ZIP_URL = \"{ExpectedNsis313ZipUrl}\"", packagePy, StringComparison.Ordinal);
        Assert.Contains($"NSIS_ZIP_SHA256 = \"{ExpectedNsis313ZipSha256}\"", packagePy, StringComparison.Ordinal);
        Assert.Contains("hashlib.sha256(data).hexdigest()", packagePy, StringComparison.Ordinal);
        string nsiText = File.ReadAllText(Path.Combine(repoRoot, "installer", "spawn.nsi"));
        Assert.Contains("!if \"${NSIS_VERSION}\" != \"v3.13\"", nsiText, StringComparison.Ordinal);

        // 3. Signing step: writes secret to RUNNER_TEMP random file, runs release.py --build, cleans up in finally, never echoes key.
        ParsedStep signStep = Assert.Single(
            build.Steps,
            s => s.RawBlock.Contains("secrets.UPDATE_SIGNING_KEY_PEM", StringComparison.Ordinal));
        string signRun = signStep.Run ?? string.Empty;
        Assert.Contains("RUNNER_TEMP", signRun, StringComparison.Ordinal);
        Assert.Contains("SPAWN_UPDATE_SIGNING_KEY", signRun, StringComparison.Ordinal);
        Assert.Contains("python -B scripts/release.py --build --version", signRun, StringComparison.Ordinal);
        Assert.Contains("try {", signRun, StringComparison.Ordinal);
        Assert.Contains("} finally {", signRun, StringComparison.Ordinal);
        Assert.Contains("Remove-Item", signRun, StringComparison.Ordinal);
        Assert.Contains("-Force", signRun, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?:Write-Host|Write-Output|\becho\b)[^\r\n]*(?:SIGNING_KEY|keyFile|\.pem)", RegexOptions.IgnoreCase),
            signRun);

        // The NSIS download happens in its own step, before the signing key is written.
        var buildSteps = build.Steps.ToList();
        int fetchIndex = buildSteps.FindIndex(
            s => (s.Run ?? string.Empty).Contains("python -B scripts/package.py --fetch-nsis", StringComparison.Ordinal));
        Assert.True(fetchIndex >= 0, "release build job must fetch the pinned NSIS in its own step");
        Assert.True(fetchIndex < buildSteps.IndexOf(signStep), "NSIS must be fetched before the signing step");
        Assert.DoesNotContain("SPAWN_UPDATE_SIGNING_KEY", buildSteps[fetchIndex].RawBlock, StringComparison.Ordinal);

        // 4. Isolated spawn-test install drill.
        Assert.Contains("python -B scripts/release.py --drill-install --version", build.AllRunScripts, StringComparison.Ordinal);

        // 5. Upload artifact step pins retention-days: 7, if-no-files-found: error, and all 8 dist/spawn files.
        ParsedStep uploadStep = Assert.Single(
            build.Steps,
            s => string.Equals(s.Uses, ExpectedUploadArtifactRef, StringComparison.Ordinal));
        Assert.Matches(new Regex(@"^\s+retention-days:\s*7\s*$", RegexOptions.Multiline), uploadStep.RawBlock);
        Assert.Matches(new Regex(@"^\s+if-no-files-found:\s*error\s*$", RegexOptions.Multiline), uploadStep.RawBlock);

        string[] expectedBundleFragments =
        [
            "dist/spawn/spawn-setup-",
            "dist/spawn/spawn-symbols-",
            "dist/spawn/update-windows-x64.json",
            "dist/spawn/update-windows-x64.json.sig",
            "dist/spawn/VERSION.txt",
            "dist/spawn/THIRD-PARTY-NOTICES.txt",
            "dist/spawn/notes.md",
            "dist/spawn/SHA256SUMS",
        ];
        foreach (string fragment in expectedBundleFragments)
        {
            Assert.Contains(fragment, uploadStep.RawBlock, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReleaseWorkflow_PublishJob_VerifiesChecksumsVersionAndSignatureBeforeDraftToLatestPromotion()
    {
        ParsedWorkflow workflow = LoadReleaseWorkflow();
        ParsedJob publish = workflow.Jobs["publish"];
        string scripts = publish.AllRunScripts;

        // 1. Pre-publish verification: SHA256SUMS, VERSION.txt (channel=release & version=), OpenSSL ECDSA verify.
        int shaIndex = scripts.IndexOf("sha256sum -c SHA256SUMS", StringComparison.Ordinal);
        int channelIndex = scripts.IndexOf("channel=release", StringComparison.Ordinal);
        int versionIndex = scripts.IndexOf("version=${RELEASE_VERSION}", StringComparison.Ordinal);
        int opensslIndex = scripts.IndexOf(
            "openssl dgst -sha256 -verify release-keys/98ada0750903e1ab.pub.pem",
            StringComparison.Ordinal);

        Assert.True(shaIndex >= 0, "publish job must verify artifacts with `sha256sum -c SHA256SUMS`.");
        Assert.True(channelIndex > shaIndex, "publish job must verify `channel=release` in VERSION.txt after checksum verification.");
        Assert.True(versionIndex > shaIndex, "publish job must verify matching `version=` in VERSION.txt after checksum verification.");
        Assert.True(opensslIndex > versionIndex, "publish job must verify signature with `openssl dgst -sha256 -verify release-keys/98ada0750903e1ab.pub.pem`.");
        Assert.Contains("update-windows-x64.json.sig", scripts, StringComparison.Ordinal);
        Assert.Contains("update-windows-x64.json", scripts, StringComparison.Ordinal);

        // 2. Draft release -> asset verification -> promote to latest, strictly in order after verification.
        int createIndex = scripts.IndexOf("gh release create", StringComparison.Ordinal);
        int draftFlagIndex = scripts.IndexOf("--draft", createIndex >= 0 ? createIndex : 0, StringComparison.Ordinal);
        int notesFlagIndex = scripts.IndexOf("--notes-file", createIndex >= 0 ? createIndex : 0, StringComparison.Ordinal);
        int viewAssetsIndex = scripts.IndexOf("--json assets", createIndex >= 0 ? createIndex : 0, StringComparison.Ordinal);
        int promoteIndex = scripts.IndexOf("gh release edit", viewAssetsIndex >= 0 ? viewAssetsIndex : 0, StringComparison.Ordinal);

        Assert.True(
            createIndex > shaIndex && createIndex > channelIndex && createIndex > versionIndex && createIndex > opensslIndex,
            "All integrity, VERSION.txt, and signature checks must run before `gh release create`.");
        Assert.True(draftFlagIndex > createIndex, "Release must be created with `--draft` first.");
        Assert.True(notesFlagIndex > createIndex, "Release must attach release notes with `--notes-file`.");
        Assert.True(viewAssetsIndex > createIndex, "Draft release must verify uploaded assets via `gh release view ... --json assets` before promotion.");
        Assert.True(promoteIndex > viewAssetsIndex, "Release promotion (`gh release edit`) must run only after draft asset verification.");

        string promoteSegment = scripts[promoteIndex..];
        Assert.Contains("--draft=false", promoteSegment, StringComparison.Ordinal);
        Assert.Contains("--latest", promoteSegment, StringComparison.Ordinal);

        // 3. All 7 downloadable release assets must be uploaded and verified on the draft release.
        string[] expectedReleaseAssets =
        [
            "spawn-setup-${RELEASE_VERSION}-x64.exe",
            "spawn-symbols-${RELEASE_VERSION}-x64.zip",
            "update-windows-x64.json",
            "update-windows-x64.json.sig",
            "VERSION.txt",
            "THIRD-PARTY-NOTICES.txt",
            "SHA256SUMS",
        ];
        string createAndVerifySegment = scripts[createIndex..promoteIndex];
        foreach (string asset in expectedReleaseAssets)
        {
            Assert.Contains(asset, createAndVerifySegment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReleaseWorkflow_Parser_HandlesBlockScalarsAndDetectsTamperedTriggersOrUnpinnedUses()
    {
        const string tamperedYaml = """
            name: Tampered
            on:
              push:
                branches: [main]
              schedule:
                - cron: "0 0 * * 1"
            permissions: {}
            jobs:
              plan:
                runs-on: ubuntu-24.04
                permissions:
                  contents: write
                steps:
                  - name: Bad unpinned checkout
                    uses: actions/checkout@v7
                  - name: Script with fake YAML lines inside block scalar
                    run: |
                      uses: fake/not-a-step@v1
                      push:
                        branches: [ignored]
            """;

        ParsedWorkflow parsed = ParseWorkflow(tamperedYaml);
        Assert.Contains("push", parsed.TriggerKeys);
        Assert.Single(parsed.Jobs);
        Assert.Equal("write", parsed.Jobs["plan"].Permissions["contents"]);
        Assert.Equal(2, parsed.Jobs["plan"].Steps.Count);
        Assert.Equal("actions/checkout@v7", parsed.Jobs["plan"].Steps[0].Uses);
        Assert.DoesNotMatch(PinnedActionRefRegex, parsed.Jobs["plan"].Steps[0].Uses!);
        Assert.Null(parsed.Jobs["plan"].Steps[1].Uses);
    }

    private static ParsedWorkflow LoadReleaseWorkflow()
    {
        string repoRoot = LocateRepositoryRoot();
        string workflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml");
        Assert.True(File.Exists(workflowPath), $"Missing weekly release workflow: {workflowPath}");

        byte[] rawBytes = File.ReadAllBytes(workflowPath);
        Assert.False(
            rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF,
            ".github/workflows/release.yml must be UTF-8 without BOM.");

        string text = Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("\t", text, StringComparison.Ordinal);

        return ParseWorkflow(text);
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

    private static ParsedWorkflow ParseWorkflow(string yamlText)
    {
        string[] lines = yamlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var topLevelBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var headerLines = new List<string>();
        bool seenJobs = false;

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (!seenJobs)
            {
                headerLines.Add(line);
            }

            if (TryMatchKeyAtIndent(line, 0, out string topKey, out _))
            {
                if (topKey == "jobs")
                {
                    seenJobs = true;
                    headerLines.RemoveAt(headerLines.Count - 1);
                }

                int start = i;
                i = SkipBlock(lines, i, parentIndent: 0);
                topLevelBlocks[topKey] = string.Join('\n', lines[start..i]);
            }
            else
            {
                i++;
            }
        }

        // Parse trigger keys directly under `on:` (indent 2).
        var triggerKeys = new HashSet<string>(StringComparer.Ordinal);
        if (topLevelBlocks.TryGetValue("on", out string? onBlock))
        {
            string[] onLines = onBlock.Split('\n');
            int j = 1;
            while (j < onLines.Length)
            {
                if (TryMatchKeyAtIndent(onLines[j], 2, out string trigKey, out _))
                {
                    triggerKeys.Add(trigKey);
                    j = SkipBlock(onLines, j, parentIndent: 2);
                }
                else
                {
                    j++;
                }
            }
        }

        // Parse jobs directly under `jobs:` (indent 2).
        var jobs = new Dictionary<string, ParsedJob>(StringComparer.Ordinal);
        if (topLevelBlocks.TryGetValue("jobs", out string? jobsBlock))
        {
            string[] jobLines = jobsBlock.Split('\n');
            int j = 1;
            while (j < jobLines.Length)
            {
                if (TryMatchKeyAtIndent(jobLines[j], 2, out string jobName, out _))
                {
                    int start = j;
                    j = SkipBlock(jobLines, j, parentIndent: 2);
                    string rawJobBlock = string.Join('\n', jobLines[start..j]);
                    jobs[jobName] = ParseJob(jobName, jobLines[start..j], rawJobBlock);
                }
                else
                {
                    j++;
                }
            }
        }

        return new ParsedWorkflow(
            string.Join('\n', headerLines),
            topLevelBlocks,
            triggerKeys,
            jobs);
    }

    private static ParsedJob ParseJob(string jobName, string[] jobLines, string rawJobBlock)
    {
        string runsOn = string.Empty;
        string? ifCondition = null;
        string? environment = null;
        var needs = new List<string>();
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<ParsedStep>();

        int i = 1;
        while (i < jobLines.Length)
        {
            if (!TryMatchKeyAtIndent(jobLines[i], 4, out string key, out string inlineVal))
            {
                i++;
                continue;
            }

            int start = i;
            i = SkipBlock(jobLines, i, parentIndent: 4);
            string[] propLines = jobLines[start..i];

            switch (key)
            {
                case "runs-on":
                    runsOn = StripYamlQuotes(StripInlineComment(inlineVal));
                    break;
                case "if":
                    ifCondition = StripInlineComment(inlineVal);
                    break;
                case "environment":
                    environment = ParseEnvironmentName(propLines, inlineVal);
                    break;
                case "needs":
                    needs.AddRange(ParseNeeds(propLines, inlineVal));
                    break;
                case "permissions":
                    foreach (var kv in ParsePermissions(propLines, inlineVal))
                    {
                        permissions[kv.Key] = kv.Value;
                    }

                    break;
                case "steps":
                    steps.AddRange(ParseSteps(propLines));
                    break;
            }
        }

        return new ParsedJob(jobName, rawJobBlock, runsOn, ifCondition, environment, needs, permissions, steps);
    }

    private static string ParseEnvironmentName(string[] propLines, string inlineVal)
    {
        string cleaned = StripYamlQuotes(StripInlineComment(inlineVal));
        if (!string.IsNullOrEmpty(cleaned))
        {
            return cleaned;
        }

        for (int k = 1; k < propLines.Length; k++)
        {
            if (TryMatchKeyAtIndent(propLines[k], 6, out string subKey, out string subVal) && subKey == "name")
            {
                return StripYamlQuotes(StripInlineComment(subVal));
            }
        }

        return string.Empty;
    }

    private static List<string> ParseNeeds(string[] propLines, string inlineVal)
    {
        var result = new List<string>();
        string cleaned = StripInlineComment(inlineVal);
        if (!string.IsNullOrEmpty(cleaned))
        {
            if (cleaned.StartsWith('[') && cleaned.EndsWith(']'))
            {
                foreach (string item in cleaned[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = StripYamlQuotes(item.Trim());
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Add(name);
                    }
                }
            }
            else
            {
                result.Add(StripYamlQuotes(cleaned));
            }

            return result;
        }

        for (int k = 1; k < propLines.Length; k++)
        {
            Match m = Regex.Match(propLines[k], @"^\s+-\s+(.+?)\s*$");
            if (m.Success)
            {
                result.Add(StripYamlQuotes(StripInlineComment(m.Groups[1].Value)));
            }
        }

        return result;
    }

    private static Dictionary<string, string> ParsePermissions(string[] propLines, string inlineVal)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string cleaned = StripInlineComment(inlineVal);
        if (!string.IsNullOrEmpty(cleaned))
        {
            if (cleaned == "{}")
            {
                return result;
            }

            if (cleaned.StartsWith('{') && cleaned.EndsWith('}'))
            {
                foreach (string entry in cleaned[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = entry.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        result[parts[0].Trim()] = StripYamlQuotes(parts[1].Trim());
                    }
                }
            }
            else
            {
                result["*"] = StripYamlQuotes(cleaned);
            }

            return result;
        }

        for (int k = 1; k < propLines.Length; k++)
        {
            if (TryMatchKeyAtIndent(propLines[k], 6, out string scope, out string level))
            {
                result[scope] = StripYamlQuotes(StripInlineComment(level));
            }
        }

        return result;
    }

    private static List<ParsedStep> ParseSteps(string[] stepsPropLines)
    {
        var stepBlocks = new List<List<string>>();
        List<string>? current = null;

        int i = 1;
        while (i < stepsPropLines.Length)
        {
            string line = stepsPropLines[i];
            if (Regex.IsMatch(line, @"^ {6}-\s"))
            {
                current = [line];
                stepBlocks.Add(current);
                i++;
                continue;
            }

            if (current is not null)
            {
                current.Add(line);
            }

            i++;
        }

        var parsed = new List<ParsedStep>();
        foreach (List<string> block in stepBlocks)
        {
            string stepName = string.Empty;
            string? uses = null;
            string? run = null;

            int k = 0;
            while (k < block.Count)
            {
                string line = block[k];
                string normalized = k == 0 && Regex.IsMatch(line, @"^ {6}-\s")
                    ? "        " + line[8..]
                    : line;

                if (TryMatchKeyAtIndent(normalized, 8, out string key, out string val))
                {
                    if (key == "name")
                    {
                        stepName = StripYamlQuotes(StripInlineComment(val));
                        k++;
                        continue;
                    }

                    if (key == "uses")
                    {
                        uses = StripYamlQuotes(StripInlineComment(val));
                        k++;
                        continue;
                    }

                    if (key == "run")
                    {
                        string trimmedVal = val.Trim();
                        if (trimmedVal.StartsWith('|') || trimmedVal.StartsWith('>'))
                        {
                            var runLines = new List<string>();
                            k++;
                            while (k < block.Count)
                            {
                                string next = block[k];
                                if (string.IsNullOrWhiteSpace(next))
                                {
                                    runLines.Add(string.Empty);
                                    k++;
                                    continue;
                                }

                                if (CountLeadingSpaces(next) <= 8)
                                {
                                    break;
                                }

                                runLines.Add(next);
                                k++;
                            }

                            run = string.Join('\n', runLines);
                            continue;
                        }

                        run = StripYamlQuotes(trimmedVal);
                        k++;
                        continue;
                    }

                    // Skip nested block scalars or maps under other step properties (with, env, etc.)
                    k = SkipStepSubBlock(block, k, propertyIndent: 8);
                    continue;
                }

                k++;
            }

            parsed.Add(new ParsedStep(stepName, uses, run, string.Join('\n', block)));
        }

        return parsed;
    }

    private static int SkipStepSubBlock(List<string> block, int startIndex, int propertyIndent)
    {
        string firstLine = block[startIndex];
        string trimmed = firstLine.TrimEnd();
        bool isBlockScalar = Regex.IsMatch(trimmed, @":\s*[|>][+-]?\d*\s*$");
        int k = startIndex + 1;

        while (k < block.Count)
        {
            string line = block[k];
            if (string.IsNullOrWhiteSpace(line))
            {
                k++;
                continue;
            }

            int indent = CountLeadingSpaces(line);
            if (!isBlockScalar && line.TrimStart().StartsWith('#'))
            {
                k++;
                continue;
            }

            if (indent <= propertyIndent)
            {
                break;
            }

            k++;
        }

        return k;
    }

    private static string ExtractNestedMappingBlock(string blockText, string targetKey, int parentIndent)
    {
        string[] lines = blockText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (TryMatchKeyAtIndent(lines[i], parentIndent + 2, out string key, out _) && key == targetKey)
            {
                int end = SkipBlock(lines, i, parentIndent + 2);
                return string.Join('\n', lines[i..end]);
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected YAML mapping key '{targetKey}' at indent {parentIndent + 2}.");
    }

    private static int SkipBlock(string[] lines, int startIndex, int parentIndent)
    {
        int i = startIndex + 1;
        bool inScalar = IsBlockScalarHeader(lines[startIndex]);
        int scalarBaseIndent = parentIndent;

        while (i < lines.Length)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            int indent = CountLeadingSpaces(line);
            if (inScalar)
            {
                if (indent <= scalarBaseIndent)
                {
                    inScalar = false;
                }
                else
                {
                    i++;
                    continue;
                }
            }

            if (line.TrimStart().StartsWith('#'))
            {
                if (indent <= parentIndent)
                {
                    break;
                }

                i++;
                continue;
            }

            if (indent <= parentIndent)
            {
                break;
            }

            if (IsBlockScalarHeader(line))
            {
                inScalar = true;
                scalarBaseIndent = indent;
            }

            i++;
        }

        return i;
    }

    private static bool IsBlockScalarHeader(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            return false;
        }

        return Regex.IsMatch(line.TrimEnd(), @":\s*[|>][+-]?\d*\s*(?:#.*)?$");
    }

    private static bool TryMatchKeyAtIndent(string line, int expectedIndent, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int indent = CountLeadingSpaces(line);
        if (indent != expectedIndent)
        {
            return false;
        }

        string trimmed = line[indent..];
        if (trimmed.StartsWith('#') || trimmed.StartsWith('-'))
        {
            return false;
        }

        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx <= 0)
        {
            return false;
        }

        string candidateKey = trimmed[..colonIdx].Trim();
        if (!Regex.IsMatch(candidateKey, @"^[A-Za-z0-9_.-]+$"))
        {
            return false;
        }

        // Require colon to be end-of-line or followed by whitespace (avoid matching URLs or expressions).
        if (colonIdx + 1 < trimmed.Length && !char.IsWhiteSpace(trimmed[colonIdx + 1]))
        {
            return false;
        }

        key = candidateKey;
        value = colonIdx + 1 < trimmed.Length ? trimmed[(colonIdx + 1)..].Trim() : string.Empty;
        return true;
    }

    private static int CountLeadingSpaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string StripInlineComment(string value)
    {
        bool inSingle = false;
        bool inDouble = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value[..i].Trim();
            }
        }

        return value.Trim();
    }

    private static string StripYamlQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private sealed record ParsedWorkflow(
        string HeaderText,
        Dictionary<string, string> TopLevelBlocks,
        HashSet<string> TriggerKeys,
        Dictionary<string, ParsedJob> Jobs)
    {
        public string GetTopLevelBlock(string key)
        {
            Assert.True(TopLevelBlocks.TryGetValue(key, out string? block), $"Missing top-level YAML key '{key}'.");
            return block!;
        }
    }

    /// <summary>
    /// True when the GitHub Actions expression contains a <c>||</c> outside any parentheses
    /// (which would bind looser than the leading <c>&amp;&amp;</c> guard and could bypass it).
    /// </summary>
    private static bool ContainsTopLevelOr(string expression)
    {
        int depth = 0;
        bool inQuote = false;
        for (int i = 0; i < expression.Length; i++)
        {
            char ch = expression[i];
            if (ch == '\'')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && ch == '(')
            {
                depth++;
            }
            else if (!inQuote && ch == ')')
            {
                depth--;
            }
            else if (!inQuote && depth == 0 && ch == '|' && i + 1 < expression.Length && expression[i + 1] == '|')
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ParsedJob(
        string Name,
        string RawBlock,
        string RunsOn,
        string? IfCondition,
        string? Environment,
        IReadOnlyList<string> Needs,
        IReadOnlyDictionary<string, string> Permissions,
        IReadOnlyList<ParsedStep> Steps)
    {
        public string AllRunScripts =>
            string.Join("\n\n", Steps.Where(s => !string.IsNullOrWhiteSpace(s.Run)).Select(s => s.Run));
    }

    private sealed record ParsedStep(
        string Name,
        string? Uses,
        string? Run,
        string RawBlock);
}

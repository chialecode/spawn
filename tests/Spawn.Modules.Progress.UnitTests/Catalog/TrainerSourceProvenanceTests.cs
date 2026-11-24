using Spawn.Contracts;
using Spawn.Modules.Progress.Catalog;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Catalog;

public sealed class TrainerSourceProvenanceTests
{
    [Theory]
    [InlineData(TrainerProviderKind.FlingMain, "https://flingtrainer.com/trainers/hollow-knight-trainer/", "外部网站 · FLiNG Trainer (flingtrainer.com)", "flingtrainer.com")]
    [InlineData(TrainerProviderKind.FlingMain, "https://www.flingtrainer.com/trainers/celeste-trainer/", "外部网站 · FLiNG Trainer (www.flingtrainer.com)", "www.flingtrainer.com")]
    [InlineData(TrainerProviderKind.FlingArchive, "https://archive.flingtrainer.com/files/Hollow.Knight.v1.5.Trainer.rar", "外部网站 · FLiNG Trainer 归档站 (archive.flingtrainer.com)", "archive.flingtrainer.com")]
    [InlineData(TrainerProviderKind.FlingMain, "https://mirror.example.org/trainers/sample/", "外部网站 · 第三方站点 (mirror.example.org)", "mirror.example.org")]
    [InlineData(TrainerProviderKind.FlingMain, null, "外部网站 · FLiNG Trainer (flingtrainer.com)", "flingtrainer.com")]
    [InlineData(TrainerProviderKind.FlingArchive, "", "外部网站 · FLiNG Trainer 归档站 (archive.flingtrainer.com)", "archive.flingtrainer.com")]
    public void FormatSourceBadgeLabel_ExternalWebProviders_ExtractsActualHostAndMarksExternalWebsite(
        TrainerProviderKind provider,
        string? sourceUrl,
        string expectedBadge,
        string expectedHost)
    {
        Assert.True(TrainerSourceProvenance.IsExternalWebsiteProvider(provider));
        Assert.Equal(expectedHost, TrainerSourceProvenance.ResolveSourceHost(provider, sourceUrl));
        Assert.Equal(expectedBadge, TrainerSourceProvenance.FormatSourceBadgeLabel(provider, sourceUrl));
    }

    [Fact]
    public void DescribeRecord_ExternalWebsiteVsLocalImport_ClearlyDistinguishesProvenanceAndDisclosures()
    {
        var externalRecord = new TrainerCatalogRecord
        {
            TrainerId = "tr-ext-01",
            GameId = "game-hk",
            Provider = TrainerProviderKind.FlingMain,
            DisplayName = "Hollow Knight v1.5 Plus 10 Trainer",
            VersionLabel = "v1.5+",
            SourcePageUrl = "https://flingtrainer.com/trainers/hollow-knight-trainer/",
            SandboxRelativePath = @"game-hk\tr-ext-01\HollowKnightTrainer.exe",
            FileSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };

        TrainerSourceProvenanceInfo extInfo = TrainerSourceProvenance.DescribeRecord(externalRecord);
        Assert.True(extInfo.IsExternalWebsite);
        Assert.Equal("外部网站 · FLiNG Trainer (flingtrainer.com)", extInfo.BadgeText);
        Assert.Equal("flingtrainer.com", extInfo.SourceHost);
        Assert.True(extInfo.HasValidHttpsSourceUrl);
        Assert.Equal("https://flingtrainer.com/trainers/hollow-knight-trainer/", extInfo.ValidatedHttpsSourceUrl);
        Assert.Contains("第三方外部网站", extInfo.DetailedDisclosureText, StringComparison.Ordinal);
        Assert.Contains("FLiNG Trainer (flingtrainer.com)", extInfo.DetailedDisclosureText, StringComparison.Ordinal);
        Assert.Contains("非 SPAWN 内置", extInfo.DetailedDisclosureText, StringComparison.Ordinal);
        Assert.Contains("主动点击下载", extInfo.DetailedDisclosureText, StringComparison.Ordinal);
        Assert.Contains("SHA-256", extInfo.DetailedDisclosureText, StringComparison.Ordinal);
        Assert.Contains("TrainerWorker 沙箱", extInfo.DetailedDisclosureText, StringComparison.Ordinal);

        var localRecord = new TrainerCatalogRecord
        {
            TrainerId = "tr-loc-01",
            GameId = "game-hk",
            Provider = TrainerProviderKind.LocalImport,
            DisplayName = "Custom Local Trainer",
            SandboxRelativePath = "game-hk/tr-loc-01/CustomTrainer.exe",
            FileSha256 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
        };

        TrainerSourceProvenanceInfo localInfo = TrainerSourceProvenance.DescribeRecord(localRecord);
        Assert.False(localInfo.IsExternalWebsite);
        Assert.Equal("本地导入 · 用户手动添加", localInfo.BadgeText);
        Assert.Equal(string.Empty, localInfo.SourceHost);
        Assert.False(localInfo.HasValidHttpsSourceUrl);
        Assert.Null(localInfo.ValidatedHttpsSourceUrl);
        Assert.Equal("CustomTrainer.exe", localInfo.SandboxFileName);
        Assert.Contains("用户从本机手动导入", localInfo.CompactOriginSummary, StringComparison.Ordinal);
        Assert.Contains("CustomTrainer.exe", localInfo.CompactOriginSummary, StringComparison.Ordinal);
        Assert.Contains("用户从本机手动添加", localInfo.DetailedDisclosureText, StringComparison.Ordinal);
        Assert.Contains("非外部在线检索且非 SPAWN 内置组件", localInfo.DetailedDisclosureText, StringComparison.Ordinal);

        var localWithOriginUrl = localRecord with
        {
            SourcePageUrl = "https://flingtrainer.com/trainers/celeste-trainer/",
        };
        TrainerSourceProvenanceInfo localOriginInfo = TrainerSourceProvenance.DescribeRecord(localWithOriginUrl);
        Assert.False(localOriginInfo.IsExternalWebsite);
        Assert.Equal("本地导入 · 用户手动添加 (flingtrainer.com)", localOriginInfo.BadgeText);
        Assert.True(localOriginInfo.HasValidHttpsSourceUrl);
        Assert.Contains("https://flingtrainer.com/trainers/celeste-trainer/", localOriginInfo.CompactOriginSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://flingtrainer.com/trainers/elden-ring-trainer/", true, "flingtrainer.com")]
    [InlineData("  HTTPS://Archive.FlingTrainer.com/files/test.rar  ", true, "archive.flingtrainer.com")]
    [InlineData("http://flingtrainer.com/trainers/insecure/", false, "")]
    [InlineData("file:///C:/Windows/System32/calc.exe", false, "")]
    [InlineData("javascript:alert(1)", false, "")]
    [InlineData("cmd.exe /c calc", false, "")]
    [InlineData("https://userinfo@flingtrainer.com/trainers/test/", false, "")]
    [InlineData("https://flingtrainer.com/trainers/bad\r\nheader", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void TryNormalizeHttpsSourceUrl_EnforcesStrictHttpsWithoutUserInfoOrControlChars(
        string? rawUrl,
        bool expectedValid,
        string expectedHost)
    {
        bool actual = TrainerSourceProvenance.TryNormalizeHttpsSourceUrl(rawUrl, out string normalizedUrl, out string host);
        Assert.Equal(expectedValid, actual);
        Assert.Equal(expectedHost, host);
        if (expectedValid)
        {
            Assert.StartsWith("https://", normalizedUrl, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(string.Empty, normalizedUrl);
        }
    }

    [Fact]
    public void DescribeCandidateAndStaticDisclosures_StateExternalThirdPartyOriginWithoutOfficialSourceMisnomer()
    {
        var candidate = new TrainerCandidate
        {
            CandidateId = "cand-1",
            Provider = TrainerProviderKind.FlingArchive,
            Title = "Dead Cells Trainer",
            PageUrl = "https://archive.flingtrainer.com/files/Dead.Cells.Trainer.rar",
        };

        TrainerCandidateProvenanceInfo candInfo = TrainerSourceProvenance.DescribeCandidate(candidate);
        Assert.Equal("外部网站 · FLiNG Trainer 归档站 (archive.flingtrainer.com)", candInfo.BadgeText);
        Assert.True(candInfo.HasValidHttpsPageUrl);
        Assert.Equal("https://archive.flingtrainer.com/files/Dead.Cells.Trainer.rar", candInfo.ValidatedHttpsPageUrl);
        Assert.Contains("第三方外部网站", candInfo.ProvenanceHintText, StringComparison.Ordinal);
        Assert.Contains("非 SPAWN 内置", candInfo.ProvenanceHintText, StringComparison.Ordinal);

        string[] allStaticCopies =
        [
            TrainerSourceProvenance.OnlineDiscoveryDisclosureTitle,
            TrainerSourceProvenance.OnlineDiscoveryDisclosureBody,
            TrainerSourceProvenance.UnboundProvenanceHintText,
            TrainerSourceProvenance.OverviewProvenanceSummaryText,
            TrainerSourceProvenance.OverviewEmptyStateDescriptionText,
            TrainerSourceProvenance.SettingsProvenanceTitle,
            TrainerSourceProvenance.SettingsProvenanceDescription,
        ];

        foreach (string copy in allStaticCopies)
        {
            Assert.False(string.IsNullOrWhiteSpace(copy));
            Assert.DoesNotContain("官方修改器", copy, StringComparison.Ordinal);
            Assert.DoesNotContain("官方源", copy, StringComparison.Ordinal);
        }

        Assert.Contains("flingtrainer.com", TrainerSourceProvenance.SettingsProvenanceDescription, StringComparison.Ordinal);
        Assert.Contains("archive.flingtrainer.com", TrainerSourceProvenance.SettingsProvenanceDescription, StringComparison.Ordinal);
        Assert.Contains("本地导入 · 用户手动添加", TrainerSourceProvenance.SettingsProvenanceDescription, StringComparison.Ordinal);
        Assert.Contains("SHA-256", TrainerSourceProvenance.SettingsProvenanceDescription, StringComparison.Ordinal);
        Assert.Contains("TrainerWorker", TrainerSourceProvenance.SettingsProvenanceDescription, StringComparison.Ordinal);
    }
}

using System.Text;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Saves.Rules;

/// <summary>
/// Built-in compiled UTF-8 JSON seed of Windows save (<c>[save]</c>), configuration (<c>[config]</c>),
/// and <c>HKCU</c> registry rules for popular single-player PC games (Steam AppIDs &amp; GOG IDs).
/// </summary>
public static class LudusaviRuleSeedData
{
    private static readonly JsonSerializerOptions SeedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Compiled UTF-8 JSON array of built-in <see cref="LudusaviRuleRecord"/> entries with strict
    /// <c>savePaths</c>, <c>configPaths</c>, and <c>registryRules</c> separation.
    /// </summary>
    public static ReadOnlySpan<byte> BuiltInRulesUtf8Json => """
        [
          {
            "ruleName": "Elden Ring",
            "steamAppId": 1245620,
            "gogId": null,
            "aliases": ["ELDEN RING", "艾尔登法环"],
            "savePaths": [
              "<winAppData>/EldenRing/<storeUserId>/*.sl2",
              "<winAppData>/EldenRing/<storeUserId>/*.sl2.bak",
              "<winAppData>/EldenRing/<storeUserId>/*.co2"
            ],
            "configPaths": [
              "<winAppData>/EldenRing/GraphicsConfig.xml"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Cyberpunk 2077",
            "steamAppId": 1091500,
            "gogId": "1423049311",
            "aliases": ["赛博朋克 2077", "赛博朋克2077"],
            "savePaths": [
              "<home>/Saved Games/CD Projekt Red/Cyberpunk 2077/**"
            ],
            "configPaths": [
              "<winLocalAppData>/CD Projekt Red/Cyberpunk 2077/UserSettings.json"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Baldur's Gate 3",
            "steamAppId": 1086940,
            "gogId": "1456460669",
            "aliases": ["Baldurs Gate 3", "BG3", "博德之门 3", "博德之门3"],
            "savePaths": [
              "<winLocalAppData>/Larian Studios/Baldur's Gate 3/PlayerProfiles/Public/Savegames/**",
              "<winLocalAppData>/Larian Studios/Baldur's Gate 3/PlayerProfiles/Public/profile8.lsf"
            ],
            "configPaths": [
              "<winLocalAppData>/Larian Studios/Baldur's Gate 3/graphicSettings.lsx",
              "<winLocalAppData>/Larian Studios/Baldur's Gate 3/PlayerProfiles/Public/config.lsf"
            ],
            "registryRules": []
          },
          {
            "ruleName": "The Witcher 3: Wild Hunt",
            "steamAppId": 292030,
            "gogId": "1207664643",
            "aliases": ["The Witcher 3", "Witcher 3", "巫师 3：狂猎", "巫师3"],
            "savePaths": [
              "<winDocuments>/The Witcher 3/gamesaves/**"
            ],
            "configPaths": [
              "<winDocuments>/The Witcher 3/user.settings",
              "<winDocuments>/The Witcher 3/dx12user.settings",
              "<winDocuments>/The Witcher 3/input.settings"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Hollow Knight",
            "steamAppId": 367520,
            "gogId": "1308320804",
            "aliases": ["空洞骑士"],
            "savePaths": [
              "<winLocalAppDataLow>/Team Cherry/Hollow Knight/*.dat",
              "<winLocalAppDataLow>/Team Cherry/Hollow Knight/*.dat.bak*"
            ],
            "configPaths": [
              "<winLocalAppDataLow>/Team Cherry/Hollow Knight/Config.ini"
            ],
            "registryRules": [
              "HKCU\\Software\\Team Cherry\\Hollow Knight"
            ]
          },
          {
            "ruleName": "Hollow Knight: Silksong",
            "steamAppId": 1030300,
            "gogId": null,
            "aliases": ["Silksong", "空洞骑士：丝之歌"],
            "savePaths": [
              "<winLocalAppDataLow>/Team Cherry/Hollow Knight Silksong/*.dat",
              "<winLocalAppDataLow>/Team Cherry/Hollow Knight Silksong/*.dat.bak*"
            ],
            "configPaths": [
              "<winLocalAppDataLow>/Team Cherry/Hollow Knight Silksong/Config.ini"
            ],
            "registryRules": [
              "HKCU\\Software\\Team Cherry\\Hollow Knight Silksong"
            ]
          },
          {
            "ruleName": "Hades",
            "steamAppId": 1145360,
            "gogId": null,
            "aliases": ["哈迪斯"],
            "savePaths": [
              "<winDocuments>/Saved Games/Hades/*.sav",
              "<winDocuments>/Saved Games/Hades/*.sav.bak*",
              "<winDocuments>/Saved Games/Hades/*.sjson"
            ],
            "configPaths": [
              "<winDocuments>/Saved Games/Hades/ActiveProfile",
              "<winDocuments>/Saved Games/Hades/*.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Hades II",
            "steamAppId": 1145350,
            "gogId": null,
            "aliases": ["Hades 2", "哈迪斯 2", "哈迪斯2"],
            "savePaths": [
              "<home>/Saved Games/Hades II/*.sav",
              "<home>/Saved Games/Hades II/*.sav.bak*",
              "<home>/Saved Games/Hades II/*.sjson"
            ],
            "configPaths": [
              "<home>/Saved Games/Hades II/ActiveProfile",
              "<home>/Saved Games/Hades II/*.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Stardew Valley",
            "steamAppId": 413150,
            "gogId": "1453375253",
            "aliases": ["星露谷物语"],
            "savePaths": [
              "<winAppData>/StardewValley/Saves/**"
            ],
            "configPaths": [
              "<winAppData>/StardewValley/startup_preferences"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Terraria",
            "steamAppId": 105600,
            "gogId": "1207665503",
            "aliases": ["泰拉瑞亚"],
            "savePaths": [
              "<winDocuments>/My Games/Terraria/Players/**",
              "<winDocuments>/My Games/Terraria/Worlds/**"
            ],
            "configPaths": [
              "<winDocuments>/My Games/Terraria/config.json",
              "<winDocuments>/My Games/Terraria/input profiles.json"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Dark Souls III",
            "steamAppId": 374320,
            "gogId": null,
            "aliases": ["DARK SOULS III", "Dark Souls 3", "黑暗之魂 3", "黑暗之魂3"],
            "savePaths": [
              "<winAppData>/DarkSoulsIII/<storeUserId>/*.sl2",
              "<winAppData>/DarkSoulsIII/*/*.sl2"
            ],
            "configPaths": [
              "<winAppData>/DarkSoulsIII/GraphicsConfig.xml"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Sekiro: Shadows Die Twice",
            "steamAppId": 814380,
            "gogId": null,
            "aliases": ["Sekiro", "只狼：影逝二度", "只狼"],
            "savePaths": [
              "<winAppData>/Sekiro/<storeUserId>/*.sl2",
              "<winAppData>/Sekiro/*/*.sl2"
            ],
            "configPaths": [
              "<winAppData>/Sekiro/GraphicsConfig.xml"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Armored Core VI: Fires of Rubicon",
            "steamAppId": 1888160,
            "gogId": null,
            "aliases": ["ARMORED CORE VI FIRES OF RUBICON", "Armored Core VI", "Armored Core 6", "装甲核心6"],
            "savePaths": [
              "<winAppData>/ArmoredCore6/<storeUserId>/*.sl2",
              "<winAppData>/ArmoredCore6/*/*.sl2"
            ],
            "configPaths": [
              "<winAppData>/ArmoredCore6/GraphicsConfig.xml"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Monster Hunter: World",
            "steamAppId": 582010,
            "gogId": null,
            "aliases": ["Monster Hunter World", "MHW", "怪物猎人：世界"],
            "savePaths": [
              "<root>/userdata/<storeUserId>/582010/remote/**"
            ],
            "configPaths": [
              "<base>/graphics_option.ini",
              "<base>/config.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Monster Hunter Rise",
            "steamAppId": 1446780,
            "gogId": null,
            "aliases": ["MONSTER HUNTER RISE", "MHR", "怪物猎人：崛起"],
            "savePaths": [
              "<root>/userdata/<storeUserId>/1446780/remote/win64_save/**"
            ],
            "configPaths": [
              "<base>/config.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Vampire Survivors",
            "steamAppId": 1794680,
            "gogId": null,
            "aliases": ["吸血鬼幸存者"],
            "savePaths": [
              "<winAppData>/Vampire_Survivors/saves/**",
              "<root>/userdata/<storeUserId>/1794680/remote/**"
            ],
            "configPaths": [
              "<winAppData>/Vampire_Survivors/Preferences"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Celeste",
            "steamAppId": 504230,
            "gogId": null,
            "aliases": ["蔚蓝"],
            "savePaths": [
              "<base>/Saves/*.celeste",
              "<winDocuments>/Saved Games/Celeste/*.celeste"
            ],
            "configPaths": [
              "<base>/Saves/settings.celeste",
              "<winDocuments>/Saved Games/Celeste/settings.celeste"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Dead Cells",
            "steamAppId": 588650,
            "gogId": "1666753257",
            "aliases": ["死亡细胞", "重生细胞"],
            "savePaths": [
              "<base>/save/**",
              "<root>/userdata/<storeUserId>/588650/remote/**"
            ],
            "configPaths": [
              "<base>/save/options.json"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Slay the Spire",
            "steamAppId": 646570,
            "gogId": "1262888202",
            "aliases": ["杀戮尖塔"],
            "savePaths": [
              "<base>/preferences/**",
              "<base>/saves/**",
              "<base>/runs/**"
            ],
            "configPaths": [
              "<base>/info.displayconfig"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Subnautica",
            "steamAppId": 264710,
            "gogId": null,
            "aliases": ["深海迷航"],
            "savePaths": [
              "<winLocalAppDataLow>/Unknown Worlds/Subnautica/Subnautica/SavedGames/**",
              "<base>/SNAppData/SavedGames/**"
            ],
            "configPaths": [
              "<winLocalAppDataLow>/Unknown Worlds/Subnautica/Subnautica/options.bin"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Factorio",
            "steamAppId": 427520,
            "gogId": null,
            "aliases": ["异星工厂"],
            "savePaths": [
              "<winAppData>/Factorio/saves/**",
              "<winAppData>/Factorio/blueprint-storage*.dat",
              "<winAppData>/Factorio/player-data.json"
            ],
            "configPaths": [
              "<winAppData>/Factorio/config/config.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "The Elder Scrolls V: Skyrim Special Edition",
            "steamAppId": 489830,
            "gogId": "1711230643",
            "aliases": ["Skyrim Special Edition", "Skyrim SE", "上古卷轴 5：天际特别版"],
            "savePaths": [
              "<winDocuments>/My Games/Skyrim Special Edition/Saves/**"
            ],
            "configPaths": [
              "<winDocuments>/My Games/Skyrim Special Edition/Skyrim.ini",
              "<winDocuments>/My Games/Skyrim Special Edition/SkyrimPrefs.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Fallout 4",
            "steamAppId": 377160,
            "gogId": "1998527297",
            "aliases": ["辐射 4", "辐射4"],
            "savePaths": [
              "<winDocuments>/My Games/Fallout4/Saves/**"
            ],
            "configPaths": [
              "<winDocuments>/My Games/Fallout4/Fallout4.ini",
              "<winDocuments>/My Games/Fallout4/Fallout4Prefs.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Disco Elysium",
            "steamAppId": 632470,
            "gogId": "1771589310",
            "aliases": ["Disco Elysium - The Final Cut", "极乐迪斯科"],
            "savePaths": [
              "<winLocalAppDataLow>/ZAUM Studio/Disco Elysium/SaveGames/**"
            ],
            "configPaths": [
              "<winLocalAppDataLow>/ZAUM Studio/Disco Elysium/Settings/**"
            ],
            "registryRules": [
              "HKCU\\Software\\ZAUM Studio\\Disco Elysium"
            ]
          },
          {
            "ruleName": "Persona 5 Royal",
            "steamAppId": 1687950,
            "gogId": null,
            "aliases": ["P5R", "女神异闻录 5 皇家版"],
            "savePaths": [
              "<winAppData>/SEGA/P5R/Steam/<storeUserId>/savedata/**",
              "<winAppData>/SEGA/P5R/Steam/*/savedata/**"
            ],
            "configPaths": [
              "<winAppData>/SEGA/P5R/Steam/<storeUserId>/system.cfg"
            ],
            "registryRules": []
          },
          {
            "ruleName": "NieR:Automata",
            "steamAppId": 524220,
            "gogId": null,
            "aliases": ["NieR Automata", "NieR: Automata", "尼尔：机械纪元"],
            "savePaths": [
              "<winDocuments>/My Games/NieR_Automata/SlotData_*.dat",
              "<winDocuments>/My Games/NieR_Automata/GameData.dat"
            ],
            "configPaths": [
              "<winDocuments>/My Games/NieR_Automata/SystemData.dat"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Black Myth: Wukong",
            "steamAppId": 2358720,
            "gogId": null,
            "aliases": ["Black Myth Wukong", "黑神话：悟空"],
            "savePaths": [
              "<base>/b1/Saved/SaveGames/**",
              "<winLocalAppData>/b1/Saved/SaveGames/**"
            ],
            "configPaths": [
              "<winLocalAppData>/b1/Saved/Config/Windows/**"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Red Dead Redemption 2",
            "steamAppId": 1174180,
            "gogId": null,
            "aliases": ["RDR2", "荒野大镖客：救赎 2", "荒野大镖客2"],
            "savePaths": [
              "<winDocuments>/Rockstar Games/Red Dead Redemption 2/Profiles/*/SRDR*",
              "<winDocuments>/Rockstar Games/Red Dead Redemption 2/Profiles/*/Player"
            ],
            "configPaths": [
              "<winDocuments>/Rockstar Games/Red Dead Redemption 2/Settings/system.xml"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Resident Evil 4",
            "steamAppId": 2050650,
            "gogId": null,
            "aliases": ["Resident Evil 4 Remake", "RE4", "BIOHAZARD RE:4", "生化危机 4 重制版"],
            "savePaths": [
              "<root>/userdata/<storeUserId>/2050650/remote/win64_save/**"
            ],
            "configPaths": [
              "<base>/config.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "LIMBO",
            "steamAppId": 48000,
            "gogId": "1724299977",
            "aliases": ["Limbo", "地狱边境"],
            "savePaths": [
              "<winLocalAppData>/Playdead/LIMBO/savegame.txt",
              "<base>/save_game/**",
              "<root>/userdata/<storeUserId>/48000/remote/**"
            ],
            "configPaths": [
              "<base>/settings.txt"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Cuphead",
            "steamAppId": 268910,
            "gogId": "1963513391",
            "aliases": ["茶杯头"],
            "savePaths": [
              "<winAppData>/Cuphead/*.sav"
            ],
            "configPaths": [
              "<winAppData>/Cuphead/settings.ini"
            ],
            "registryRules": [
              "HKCU\\Software\\StudioMDHR\\Cuphead"
            ]
          },
          {
            "ruleName": "Ori and the Will of the Wisps",
            "steamAppId": 1057090,
            "gogId": null,
            "aliases": ["Ori 2", "精灵与萤火意志"],
            "savePaths": [
              "<winLocalAppData>/Ori and the Will of The Wisps/*.uberstate",
              "<winLocalAppData>/Ori and the Will of The Wisps/*.sav"
            ],
            "configPaths": [
              "<winLocalAppData>/Ori and the Will of The Wisps/options.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Ori and the Blind Forest: Definitive Edition",
            "steamAppId": 387290,
            "gogId": "1887990941",
            "aliases": ["Ori and the Blind Forest", "奥日与黑暗森林"],
            "savePaths": [
              "<winLocalAppData>/Ori and the Blind Forest DE/*.sav"
            ],
            "configPaths": [
              "<winLocalAppData>/Ori and the Blind Forest DE/ControllerButtonRemapping.txt"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Palworld",
            "steamAppId": 1623730,
            "gogId": null,
            "aliases": ["幻兽帕鲁"],
            "savePaths": [
              "<winLocalAppData>/Pal/Saved/SaveGames/**"
            ],
            "configPaths": [
              "<winLocalAppData>/Pal/Saved/Config/Windows/**"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Balatro",
            "steamAppId": 2379780,
            "gogId": null,
            "aliases": ["小丑牌"],
            "savePaths": [
              "<winAppData>/Balatro/1/**",
              "<winAppData>/Balatro/2/**",
              "<winAppData>/Balatro/3/**"
            ],
            "configPaths": [
              "<winAppData>/Balatro/settings.jkr"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Divinity: Original Sin 2",
            "steamAppId": 435150,
            "gogId": "1584823040",
            "aliases": ["Divinity: Original Sin 2 - Definitive Edition", "神界：原罪 2"],
            "savePaths": [
              "<winDocuments>/Larian Studios/Divinity Original Sin 2 Definitive Edition/PlayerProfiles/*/Savegames/**"
            ],
            "configPaths": [
              "<winDocuments>/Larian Studios/Divinity Original Sin 2 Definitive Edition/graphicSettings.lsx"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Undertale",
            "steamAppId": 391540,
            "gogId": null,
            "aliases": ["UNDERTALE", "传说之下"],
            "savePaths": [
              "<winLocalAppData>/UNDERTALE/file*",
              "<winLocalAppData>/UNDERTALE/undertale.ini"
            ],
            "configPaths": [
              "<winLocalAppData>/UNDERTALE/config.ini"
            ],
            "registryRules": []
          },
          {
            "ruleName": "Clair Obscur: Expedition 33",
            "steamAppId": 1903340,
            "gogId": null,
            "aliases": ["Expedition 33", "光与影：33号远征队"],
            "savePaths": [
              "<winLocalAppData>/Sandfall/Saved/SaveGames/**"
            ],
            "configPaths": [
              "<winLocalAppData>/Sandfall/Saved/Config/Windows/**"
            ],
            "registryRules": []
          }
        ]
        """u8;

    /// <summary>
    /// Built-in seed JSON as a UTF-16 string.
    /// </summary>
    public static string BuiltInRulesJson => Encoding.UTF8.GetString(BuiltInRulesUtf8Json);

    /// <summary>
    /// Deserializes all built-in seed rules with a uniform <paramref name="timestampUtc"/>.
    /// </summary>
    public static IReadOnlyList<LudusaviRuleRecord> GetBuiltInRules(DateTimeOffset? timestampUtc = null)
    {
        DateTimeOffset now = timestampUtc ?? DateTimeOffset.UtcNow;
        SeedRuleDto[]? raw = JsonSerializer.Deserialize<SeedRuleDto[]>(BuiltInRulesUtf8Json, SeedJsonOptions);
        if (raw is null || raw.Length == 0)
        {
            return [];
        }

        var results = new List<LudusaviRuleRecord>(raw.Length);
        foreach (SeedRuleDto item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.RuleName))
            {
                continue;
            }

            results.Add(new LudusaviRuleRecord
            {
                RuleName = item.RuleName.Trim(),
                SteamAppId = item.SteamAppId,
                GogId = string.IsNullOrWhiteSpace(item.GogId) ? null : item.GogId.Trim(),
                Aliases = item.Aliases ?? [],
                SavePaths = item.SavePaths ?? [],
                ConfigPaths = item.ConfigPaths ?? [],
                RegistryRules = item.RegistryRules ?? [],
                UpdatedAtUtc = now,
            });
        }

        return results;
    }

    private sealed class SeedRuleDto
    {
        public string? RuleName { get; set; }
        public int? SteamAppId { get; set; }
        public string? GogId { get; set; }
        public string[]? Aliases { get; set; }
        public string[]? SavePaths { get; set; }
        public string[]? ConfigPaths { get; set; }
        public string[]? RegistryRules { get; set; }
    }
}

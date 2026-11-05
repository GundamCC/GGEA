namespace GGEA.App.Config;

/// <summary>区服限定游戏包与支持语言；游戏语言只决定素材，不改变助手界面语言。</summary>
public static class GameProfiles
{
    public const string WorldwidePackage = "com.bandainamcoent.gget_WW";
    public const string JapanPackage = "com.bandainamcoent.gget_JP";

    /// <summary>设置下拉项及持久化标识。</summary>
    public sealed record Option(string Id, string Label)
    {
        public override string ToString() => Localization.L.Text(Label);
    }

    public static IReadOnlyList<Option> Servers { get; } = Array.AsReadOnly(new[]
    {
        new Option("worldwide", "国际服"), new Option("japan", "日服")
    });

    public static IReadOnlyList<Option> Languages { get; } = Array.AsReadOnly(new[]
    {
        new Option("zh-CN", "简体中文"), new Option("ja", "日本語"), new Option("en", "English"),
        new Option("ko", "한국어"), new Option("zh-TW", "繁體中文（台灣）"), new Option("zh-HK", "繁體中文（香港）")
    });

    /// <summary>只允许官方两个游戏包；旧自定义配置无法识别时回到国际服。</summary>
    public static string ServerForPackage(string package) => package == JapanPackage ? "japan" : "worldwide";

    /// <summary>国际服提供五种非日语语言，日服只提供日语。</summary>
    public static IReadOnlyList<Option> LanguagesForServer(string server)
        => Languages.Where(language => (language.Id == "ja") == (server == "japan")).ToArray();

    /// <summary>设置只开放已适配语言；完整语言表仍用于识别旧配置和资源路径。</summary>
    public static IReadOnlyList<Option> SelectableLanguagesForServer(string server)
        => LanguagesForServer(server).Where(language => language.Id is "zh-CN" or "ja").ToArray();

    /// <summary>加载和保存共用边界，旧配置中的跨区服语言或自定义包不能继续生效。</summary>
    public static void Normalize(AppConfig config)
    {
        if (!Servers.Any(s => s.Id == config.GameServer)) config.GameServer = ServerForPackage(config.GamePackage);
        config.GamePackage = config.GameServer == "japan" ? JapanPackage : WorldwidePackage;
        if (!LanguagesForServer(config.GameServer).Any(l => l.Id == config.GameLanguage))
            config.GameLanguage = config.GameServer == "japan" ? "ja" : "zh-CN";
    }
}

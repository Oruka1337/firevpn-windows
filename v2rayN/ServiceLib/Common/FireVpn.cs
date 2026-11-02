using System.Net.Http;
using Microsoft.Win32;

namespace ServiceLib.Common;

/// <summary>
/// FireVPN: наши адреса, сведения о подписке, обновления с сайта, ссылки firevpn://.
/// Общее для версии Windows (WPF) и macOS/Linux (Avalonia).
/// </summary>
public static class FireVpn
{
    // сборка FireVPN (а не исходный v2rayN): отключает лишнее из v2rayN
    public const bool IsFireVpn = true;

    public const string AppVersion = "1.0.3";
    public const string SubName = "FireVPN";
    public const string Site = "https://fire-vpn.online";
    // у каждой системы свой latest.json и свой файл обновления
    public static string UpdateUrl => Site + (OperatingSystem.IsMacOS() ? "/download/macos/latest.json" : "/download/windows/latest.json");

    private static string UpdateAssetSuffix =>
        OperatingSystem.IsMacOS()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-macos-arm64.dmg" : "-macos-64.dmg")
            : "-windows-64.zip";
    public const string DownloadPage = Site + "/download/";
    public const string CabinetUrl = Site + "/my/";
    public const string Bot = "https://t.me/VpnFire57_bot";
    public const string SupportUrl = Bot + "?start=support";
    public const string InviteUrl = Bot + "?start=invite";
    public const string Scheme = "firevpn";

    // подписка сама обновляется раз в 6 часов — новые серверы появляются без кнопок
    public const int SubAutoUpdateMinutes = 360;

    // проверка новой версии — не чаще раза в 12 часов
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    private static readonly string _tag = "FireVpn";
    // напрямую, мимо системного прокси: при запуске он может указывать на ещё не поднятое ядро
    private static readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly HttpClient _download = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(15) };

    #region Settings

    public class FireSettings
    {
        public bool FullDevice { get; set; }
        public bool PendingTun { get; set; }
        public DateTime LastUpdateCheck { get; set; }
    }

    private static FireSettings? _settings;

    private static string SettingsPath => Utils.GetConfigPath("firevpn.json");

    public static FireSettings Settings
    {
        get
        {
            if (_settings != null)
            {
                return _settings;
            }
            try
            {
                if (File.Exists(SettingsPath))
                {
                    _settings = JsonSerializer.Deserialize<FireSettings>(File.ReadAllText(SettingsPath));
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
            return _settings ??= new FireSettings();
        }
    }

    public static void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #endregion Settings

    #region Subscription info

    public sealed record SubInfo(DateTime? Expire, long Used, long Total, string? Title);

    /// <summary>
    /// Срок и трафик из заголовка subscription-userinfo (его отдаёт сервер подписок)
    /// </summary>
    public static async Task<SubInfo?> FetchSubInfo(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", Utils.GetVersion(false));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string? Header(string name)
            {
                if (resp.Headers.TryGetValues(name, out var v))
                {
                    return v.FirstOrDefault();
                }
                return resp.Content.Headers.TryGetValues(name, out var c) ? c.FirstOrDefault() : null;
            }

            long up = 0, down = 0, total = 0, expire = 0;
            foreach (var part in (Header("subscription-userinfo") ?? "").Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), out var n))
                {
                    continue;
                }
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "upload": up = n; break;
                    case "download": down = n; break;
                    case "total": total = n; break;
                    case "expire": expire = n; break;
                }
            }

            var title = Header("profile-title");
            if (title != null && title.StartsWith("base64:"))
            {
                try
                {
                    title = Encoding.UTF8.GetString(Convert.FromBase64String(title[7..]));
                }
                catch
                {
                    title = null;
                }
            }

            DateTime? expireAt = expire > 0 ? DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime : null;
            return new SubInfo(expireAt, up + down, total, title);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} {units[0]}" : $"{v:0.#} {units[i]}";
    }

    #endregion Subscription info

    #region Update

    public sealed record UpdateInfo(string Version, string Notes, string Url);

    public static bool UpdateCheckDue()
    {
        return DateTime.Now - Settings.LastUpdateCheck > UpdateCheckInterval;
    }

    /// <summary>
    /// latest.json на нашем сайте (формат релиза GitHub, как у приложения для Android)
    /// </summary>
    public static async Task<UpdateInfo?> CheckUpdate()
    {
        try
        {
            var json = await _http.GetStringAsync(UpdateUrl);
            Settings.LastUpdateCheck = DateTime.Now;
            SaveSettings();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            if (!Version.TryParse(tag, out var remote) || remote <= Version.Parse(AppVersion))
            {
                return null;
            }

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(UpdateAssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateInfo(tag, notes, asset.GetProperty("browser_download_url").GetString() ?? "");
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>
    /// Скачать архив новой версии и отдать его AmazTool: он закроет FireVPN, распакует поверх и запустит снова
    /// </summary>
    public static async Task<bool> DownloadAndUpgrade(UpdateInfo update, Action<int> progress)
    {
        try
        {
            if (!update.Url.StartsWith(Site + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (OperatingSystem.IsMacOS())
            {
                // на Маке программа лежит в «Программах» внутри .app — новую версию человек ставит из .dmg сам
                ProcUtils.ProcessStart("open", update.Url);
                return true;
            }
            var file = Utils.GetTempPath($"FireVPN-{update.Version}-{Utils.GetGuid(false)}.zip");
            using (var resp = await _download.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(file);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                    {
                        progress((int)(done * 100 / total));
                    }
                }
            }

            if (!Utils.UpgradeAppExists(out var upgradeFileName))
            {
                return false;
            }
            var id = ProcUtils.ProcessStart(upgradeFileName, Utils.UrlEncode(file), Utils.StartupPath());
            if (id > 0)
            {
                await AppManager.Instance.AppExitAsync(true);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    #endregion Update

    #region firevpn:// links

    private static string PendingImportPath => Utils.GetConfigPath("firevpn-import.txt");

    /// <summary>
    /// Ссылки firevpn:// открывают FireVPN (как в приложении для Android)
    /// </summary>
    public static void RegisterScheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;  // на Маке ссылки firevpn:// объявлены в Info.plist
        }
        try
        {
            var exe = Utils.GetExePath();
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            key.SetValue("", "URL:FireVPN");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// firevpn://install-sub?url=... и firevpn://install-config?url=... → ссылка внутри; иначе null
    /// </summary>
    public static string? ParseLink(string? arg)
    {
        if (arg.IsNullOrEmpty() || !arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var uri = Utils.TryUri(arg);
        if (uri == null || (uri.Host != "install-sub" && uri.Host != "install-config"))
        {
            return null;
        }
        var url = Utils.ParseQueryString(uri.Query)["url"];
        return url.IsNullOrEmpty() ? null : url;
    }

    /// <summary>
    /// Второй запуск со ссылкой: кладём её в файл, первая копия FireVPN заберёт её, когда проснётся
    /// </summary>
    public static void SavePendingImport(string url)
    {
        try
        {
            File.WriteAllText(PendingImportPath, url);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static string? TakePendingImport()
    {
        try
        {
            if (!File.Exists(PendingImportPath))
            {
                return null;
            }
            var url = File.ReadAllText(PendingImportPath).Trim();
            File.Delete(PendingImportPath);
            return url.IsNullOrEmpty() ? null : url;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    #endregion firevpn:// links
}

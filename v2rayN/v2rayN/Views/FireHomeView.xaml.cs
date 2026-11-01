using System.Windows.Controls;
using ServiceLib.Services;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace v2rayN.Views;

/// <summary>
/// Сервер в списке FireVPN (флаг, название, скорость ответа)
/// </summary>
public class FireServerItem : INotifyPropertyChanged
{
    public string IndexId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Country { get; init; } = "";
    public string CodeText { get; init; } = "";
    public ImageSource? Flag { get; init; }

    private string _ping = "";
    public string Ping
    {
        get => _ping;
        set { _ping = value; OnChanged(nameof(Ping)); }
    }

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; OnChanged(nameof(IsSelected)); OnChanged(nameof(SelectedTag)); }
    }

    public string SelectedTag => _selected ? "True" : "False";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// FireVPN: минималистичный главный экран. Выключено — Фаер за решёткой, подключение — ломает решётку,
/// включено — на свободе. Всё остальное — в расширенном режиме v2rayN.
/// </summary>
public partial class FireHomeView : UserControl
{
    private MainWindowViewModel? _vm;
    private readonly Config _config = AppManager.Instance.Config;
    private StatusBarViewModel Bar => StatusBarViewModel.Instance;
    private readonly DispatcherTimer _subTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private FireVpn.UpdateInfo? _update;
    private List<FireServerItem> _servers = [];
    private readonly Dictionary<string, string> _pings = [];
    private bool _refreshingServers;
    private bool _breaking;
    private bool? _shownState;
    private DateTime _connectedAt;
    private SpeedtestService? _speedtest;

    private static readonly ImageSource ImgCage = Img("firevpn_cage.jpg");
    private static readonly ImageSource ImgBreak = Img("firevpn_break.jpg");
    private static readonly ImageSource ImgFree = Img("firevpn_free.jpg");

    public event Action? AdvancedRequested;

    public FireHomeView()
    {
        InitializeComponent();

        txtVersion.Text = $"FireVPN {FireVpn.AppVersion} · основан на v2rayN (GPL-3.0)";

        btnSettings.Click += (_, _) => ShowPage(pageSettings);
        btnSettingsBack.Click += (_, _) => ShowPage(pageMain);
        btnServerCard.Click += (_, _) => OpenServers();
        btnServersBack.Click += (_, _) => ShowPage(pageMain);
        btnPing.Click += async (_, _) => await TestPing();
        btnAdvanced.Click += (_, _) => AdvancedRequested?.Invoke();
        btnPaste.Click += async (_, _) => await ImportFromClipboard();
        btnAddSub.Click += async (_, _) => await ImportFromClipboard();
        btnGetBot.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.Bot);
        btnGetSite.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.CabinetUrl);
        btnSite.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.Site);
        btnInvite.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.InviteUrl);
        btnCabinet.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.CabinetUrl);
        btnSupport.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.SupportUrl);
        btnSubRefresh.Click += async (_, _) => await UpdateSubscription();
        btnFire.Click += (_, _) => ToggleConnect();
        btnUpdate.Click += async (_, _) => await ApplyUpdate();
        btnModeProxy.Click += (_, _) => SetMode(false);
        btnModeTun.Click += (_, _) => SetMode(true);
        chkAutoRun.Click += async (_, _) => await SetAutoRun(chkAutoRun.IsChecked == true);

        _subTimer.Tick += async (_, _) => await RefreshSubInfo();
        _clock.Tick += (_, _) => UpdateTimer();
    }

    private static ImageSource Img(string name)
    {
        var img = new BitmapImage(new Uri($"pack://application:,,,/Resources/{name}", UriKind.Absolute));
        img.Freeze();
        return img;
    }

    public void Init(MainWindowViewModel vm)
    {
        _vm = vm;

        chkAutoRun.IsChecked = _config.GuiItem.AutoRun;
        UpdateModeUi();

        // список серверов в StatusBarViewModel обновляется после каждой загрузки подписки — перерисовываем свой
        Bar.Servers.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(async () => await RefreshServers());

        Bar.WhenAnyValue(x => x.BlSystemProxySet, x => x.EnableTun)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateConnectUi());

        _subTimer.Start();
        Dispatcher.InvokeAsync(async () =>
        {
            await EnsureSubscriptionDefaults();
            await RefreshServers();
            await RefreshSubInfo();

            // перезапустились от администратора ради туннеля — включаем сразу
            if (FireVpn.Settings.PendingTun && Utils.IsAdministrator())
            {
                FireVpn.Settings.PendingTun = false;
                FireVpn.SaveSettings();
                Bar.SystemProxySelected = (int)ESysProxyType.ForcedClear;
                Bar.EnableTun = true;
            }

            if (FireVpn.UpdateCheckDue())
            {
                ShowUpdate(await FireVpn.CheckUpdate());
            }
            await TestPing();
        });
    }

    private void ShowPage(Grid page)
    {
        pageMain.Visibility = page == pageMain ? Visibility.Visible : Visibility.Collapsed;
        pageServers.Visibility = page == pageServers ? Visibility.Visible : Visibility.Collapsed;
        pageSettings.Visibility = page == pageSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    #region Subscription

    private async Task<SubItem?> GetSub()
    {
        return (await AppManager.Instance.SubItems())?.FirstOrDefault(t => t.Url.IsNotEmpty());
    }

    /// <summary>
    /// Ссылка из буфера: подписка https://... или отдельные vless://...
    /// </summary>
    private async Task ImportFromClipboard()
    {
        var text = WindowsUtils.GetClipboardData()?.Trim();
        if (text.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue("В буфере обмена нет ссылки — скопируйте её из бота или личного кабинета");
            return;
        }
        await ImportLink(text);
    }

    public async Task ImportLink(string text)
    {
        if (_vm == null)
        {
            return;
        }
        var link = FireVpn.ParseLink(text) ?? text;
        var isSub = link.StartsWith(Global.HttpsProtocol) || link.StartsWith(Global.HttpProtocol);
        var ret = await ConfigHandler.AddBatchServers(_config, link, _config.SubIndexId, false);
        if (ret <= 0 && !isSub)
        {
            NoticeManager.Instance.Enqueue("Не похоже на ссылку FireVPN — скопируйте её из бота или личного кабинета ещё раз");
            return;
        }
        ShowPage(pageMain);
        await EnsureSubscriptionDefaults();
        await UpdateSubscription();
    }

    /// <summary>
    /// Подписке — наше имя и автообновление раз в 6 часов
    /// </summary>
    private async Task EnsureSubscriptionDefaults()
    {
        foreach (var sub in (await AppManager.Instance.SubItems()) ?? [])
        {
            if (sub.Url.IsNullOrEmpty())
            {
                continue;
            }
            var changed = false;
            if (sub.Remarks.IsNullOrEmpty() || sub.Remarks == "import_sub")
            {
                sub.Remarks = FireVpn.SubName;
                changed = true;
            }
            if (sub.AutoUpdateInterval <= 0)
            {
                sub.AutoUpdateInterval = FireVpn.SubAutoUpdateMinutes;
                changed = true;
            }
            if (changed)
            {
                await ConfigHandler.AddSubItem(_config, sub);
            }
        }
    }

    private async Task UpdateSubscription()
    {
        if (_vm == null)
        {
            return;
        }
        btnSubRefresh.IsEnabled = false;
        btnPaste.IsEnabled = false;
        NoticeManager.Instance.Enqueue("Обновляю подписку…");
        try
        {
            await _vm.UpdateSubscriptionProcess("", false);
            await RefreshServers();
            await RefreshSubInfo();
            await TestPing();
        }
        finally
        {
            btnSubRefresh.IsEnabled = true;
            btnPaste.IsEnabled = true;
        }
    }

    private async Task RefreshSubInfo()
    {
        var sub = await GetSub();
        SetHasSub(sub != null);
        if (sub == null)
        {
            txtSubTitle.Text = FireVpn.SubName;
            txtSubLeft.Text = "Нет подписки";
            txtTraffic.Text = "";
            return;
        }

        txtSubTitle.Text = sub.Remarks.IsNullOrEmpty() ? FireVpn.SubName : sub.Remarks;
        var info = await FireVpn.FetchSubInfo(sub.Url);
        if (info == null)
        {
            return;
        }
        if (info.Title.IsNotEmpty())
        {
            txtSubTitle.Text = info.Title;
        }
        if (info.Expire is { } exp)
        {
            var left = exp - DateTime.Now;
            var date = exp.ToString("d MMMM yyyy", new CultureInfo("ru-RU"));
            txtSubLeft.Text = left <= TimeSpan.Zero
                ? $"Срок истёк {date}"
                : left.TotalDays >= 1
                    ? $"Осталось {DaysText((int)left.TotalDays)} {(left.Hours > 0 ? $"{left.Hours} ч" : "")} · до {date}"
                    : $"Осталось {Math.Max(1, (int)left.TotalHours)} ч · до {date}";
        }
        else
        {
            txtSubLeft.Text = "Без ограничения срока";
        }
        txtTraffic.Text = $"Трафик: {FireVpn.FormatBytes(info.Used)} из {(info.Total > 0 ? FireVpn.FormatBytes(info.Total) : "∞")}";
    }

    private static string DaysText(int n)
    {
        var word = (n % 10 == 1 && n % 100 != 11) ? "день"
            : (n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20)) ? "дня" : "дней";
        return $"{n} {word}";
    }

    private void SetHasSub(bool hasSub)
    {
        bdWelcome.Visibility = hasSub ? Visibility.Collapsed : Visibility.Visible;
        btnServerCard.Visibility = hasSub ? Visibility.Visible : Visibility.Collapsed;
        spQuick.Visibility = hasSub && _servers.Count > 1 ? Visibility.Visible : Visibility.Hidden;
    }

    #endregion Subscription

    #region Servers

    private async Task RefreshServers()
    {
        if (_refreshingServers)
        {
            return;
        }
        _refreshingServers = true;
        try
        {
            var models = await AppManager.Instance.ProfileModels("", "") ?? [];
            _servers = models.Select(t =>
            {
                var code = FireFlags.CountryCode(t.Remarks);
                return new FireServerItem
                {
                    IndexId = t.IndexId,
                    Name = FireFlags.StripFlags(t.Remarks),
                    Country = FireFlags.CountryName(code),
                    CodeText = code?.ToUpperInvariant() ?? "🔥",
                    Flag = FireFlags.Get(code),
                    Ping = _pings.TryGetValue(t.IndexId, out var p) ? p : "",
                };
            }).ToList();

            // после первой загрузки подписки сервер ещё не выбран — берём первый
            if (_servers.Count > 0 && _servers.All(t => t.IndexId != _config.IndexId))
            {
                Bar.SetDefaultServerRequested.Publish(_servers[0].IndexId);
                MarkSelected(_servers[0].IndexId);
            }
            else
            {
                MarkSelected(_config.IndexId);
            }

            icServers.ItemsSource = _servers;
            icQuick.ItemsSource = _servers.Take(4).ToList();
            SetHasSub(await GetSub() != null || _servers.Count > 0);
        }
        finally
        {
            _refreshingServers = false;
        }
    }

    private void MarkSelected(string? indexId)
    {
        FireServerItem? current = null;
        foreach (var s in _servers)
        {
            s.IsSelected = s.IndexId == indexId;
            if (s.IsSelected)
            {
                current = s;
            }
        }
        ccServerFlag.Content = current;
        txtServerName.Text = current?.Name ?? "Выберите сервер";
        txtServerSub.Text = current == null ? "" : string.Join(" · ", new[] { current.Country, current.Ping }.Where(t => t.IsNotEmpty()));
        txtQuickName.Text = current?.Name ?? "";
    }

    private void SelectServer(FireServerItem item)
    {
        if (item.IndexId != _config.IndexId)
        {
            Bar.SetDefaultServerRequested.Publish(item.IndexId);
        }
        MarkSelected(item.IndexId);
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FireServerItem item)
        {
            SelectServer(item);
        }
    }

    private void Server_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FireServerItem item)
        {
            SelectServer(item);
            ShowPage(pageMain);
        }
    }

    private void OpenServers()
    {
        ShowPage(pageServers);
        _ = TestPing();
    }

    /// <summary>
    /// Настоящая задержка до каждого сервера (через реле), как «Тест реальной задержки» в v2rayN
    /// </summary>
    private async Task TestPing()
    {
        var items = await AppManager.Instance.ProfileItems("");
        if (items == null || items.Count == 0)
        {
            return;
        }
        foreach (var s in _servers)
        {
            s.Ping = "…";
        }
        _speedtest ??= new SpeedtestService(_config, result =>
        {
            Dispatcher.InvokeAsync(() => SetPing(result));
            return Task.CompletedTask;
        });
        _ = _speedtest.RunLoop(ESpeedActionType.Realping, items);
    }

    private void SetPing(SpeedTestResult result)
    {
        if (result.IndexId.IsNullOrEmpty() || result.Delay.IsNullOrEmpty())
        {
            return;
        }
        // пока идёт проверка, приходит текст «Тестирование…» — ждём число (-1 — сервер не ответил)
        if (!int.TryParse(result.Delay.Trim(), out var ms))
        {
            return;
        }
        var text = ms > 0 ? $"{ms} мс" : "нет ответа";
        _pings[result.IndexId] = text;
        var item = _servers.FirstOrDefault(t => t.IndexId == result.IndexId);
        if (item != null)
        {
            item.Ping = text;
            if (item.IsSelected)
            {
                MarkSelected(item.IndexId);
            }
        }
    }

    #endregion Servers

    #region Connect

    private bool IsConnected => Bar.BlSystemProxySet || Bar.EnableTun;

    private void ToggleConnect()
    {
        if (IsConnected)
        {
            Bar.EnableTun = false;
            Bar.SystemProxySelected = (int)ESysProxyType.ForcedClear;
            return;
        }
        if (_servers.Count == 0)
        {
            NoticeManager.Instance.Enqueue("Сначала вставьте ссылку-подписку из бота или личного кабинета");
            return;
        }

        // Фаер ломает решётку
        _breaking = true;
        SetFireImage(ImgBreak);
        txtStatus.Text = "Подключение…";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _breaking = false;
            UpdateConnectUi();
        };
        t.Start();

        if (FireVpn.Settings.FullDevice)
        {
            StartTun();
        }
        else
        {
            Bar.SystemProxySelected = (int)ESysProxyType.ForcedChange;
        }
    }

    /// <summary>
    /// Туннель (весь компьютер) — нужны права администратора: FireVPN перезапустится и включит его сам
    /// </summary>
    private void StartTun()
    {
        if (!Utils.IsAdministrator())
        {
            FireVpn.Settings.PendingTun = true;
            FireVpn.SaveSettings();
            NoticeManager.Instance.Enqueue("Для туннеля FireVPN перезапустится от имени администратора");
        }
        Bar.SystemProxySelected = (int)ESysProxyType.ForcedClear;
        Bar.EnableTun = true;
    }

    private void SetMode(bool tun)
    {
        if (FireVpn.Settings.FullDevice == tun)
        {
            return;
        }
        FireVpn.Settings.FullDevice = tun;
        FireVpn.SaveSettings();
        UpdateModeUi();
        if (!IsConnected)
        {
            return;
        }
        // уже подключены — переключаем режим на ходу
        if (tun)
        {
            StartTun();
        }
        else
        {
            Bar.EnableTun = false;
            Bar.SystemProxySelected = (int)ESysProxyType.ForcedChange;
        }
    }

    private void UpdateModeUi()
    {
        btnModeProxy.Tag = FireVpn.Settings.FullDevice ? "False" : "True";
        btnModeTun.Tag = FireVpn.Settings.FullDevice ? "True" : "False";
    }

    private async Task SetAutoRun(bool on)
    {
        _config.GuiItem.AutoRun = on;
        await AutoStartupHandler.UpdateTask(_config);
        await ConfigHandler.SaveConfig(_config);
    }

    private void UpdateConnectUi()
    {
        if (_breaking)
        {
            return;
        }
        var on = IsConnected;
        if (_shownState != on)
        {
            if (on)
            {
                _connectedAt = DateTime.Now;
            }
            _shownState = on;
            SetFireImage(on ? ImgFree : ImgCage);
            AnimateGlow(on);
        }

        elRing.Stroke = on ? (Brush)FindResource("FireFill") : (Brush)FindResource("FireRingOff");
        txtStatus.Text = on ? (Bar.EnableTun ? "Подключено · весь компьютер" : "Подключено") : "Не подключено";
        txtStatus.Foreground = on ? (Brush)FindResource("FireAccent") : (Brush)FindResource("FireMuted");
        txtTimer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        txtHint.Text = on ? "Нажмите, чтобы отключиться" : "Нажмите на Фаера, чтобы освободить его";
        if (on)
        {
            UpdateTimer();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private void UpdateTimer()
    {
        var d = DateTime.Now - _connectedAt;
        txtTimer.Text = $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}";
    }

    private void SetFireImage(ImageSource img)
    {
        brFire.ImageSource = img;
        elFire.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(450)));
        var pop = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new BackEase { Amplitude = 0.4 } };
        fireScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        fireScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // включено — мягкое «дыхание» свечения вокруг Фаера
    private void AnimateGlow(bool on)
    {
        if (on)
        {
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
            });
        }
        else
        {
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        }
    }

    #endregion Connect

    #region Update

    private void ShowUpdate(FireVpn.UpdateInfo? update)
    {
        _update = update;
        bdUpdate.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
        if (update != null)
        {
            txtUpdateTitle.Text = $"Доступна версия {update.Version}";
            txtUpdateNotes.Text = update.Notes;
        }
    }

    private async Task ApplyUpdate()
    {
        if (_update == null)
        {
            return;
        }
        btnUpdate.IsEnabled = false;
        var ok = await FireVpn.DownloadAndUpgrade(_update, p => Dispatcher.InvokeAsync(() => btnUpdate.Content = $"{p}%"));
        if (!ok)
        {
            btnUpdate.Content = "Обновить";
            btnUpdate.IsEnabled = true;
            NoticeManager.Instance.Enqueue("Не удалось обновить — скачайте новую версию на fire-vpn.online/download");
            ProcUtils.ProcessStart(FireVpn.DownloadPage);
        }
    }

    #endregion Update
}

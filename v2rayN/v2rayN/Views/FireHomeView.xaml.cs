using System.Windows.Controls;
using ServiceLib.Services;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

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
    private bool _pingRetryPlanned;
    private Task? _pingTask;

    private static readonly ImageSource ImgCage = Img("firevpn_cage.jpg");
    private static readonly ImageSource ImgBreak = Img("firevpn_break.jpg");
    private static readonly ImageSource ImgFree = Img("firevpn_free.jpg");

    public event Action? AdvancedRequested;

    public FireHomeView()
    {
        InitializeComponent();
        BuildComet();

        txtVersion.Text = $"FireVPN {FireVpn.AppVersion} · основан на v2rayN (GPL-3.0)";

        btnSettings.Click += (_, _) => ShowPage(pageSettings);
        btnSettingsBack.Click += (_, _) => ShowPage(pageMain);
        btnServerCard.Click += (_, _) => OpenServers();
        btnServersBack.Click += (_, _) => ShowPage(pageMain);
        btnPing.Click += async (_, _) => await TestPing();
        btnAdvanced.Click += (_, _) => AdvancedRequested?.Invoke();
        btnPaste.Click += async (_, _) => await ImportFromClipboard();
        // стартовый экран: «Начать» — пробный день в боте, потом вставить ссылку; «У меня есть подписка» — сразу вставить
        btnStart.Click += (_, _) =>
        {
            ProcUtils.ProcessStart(FireVpn.Bot + "?start=trial");
            ShowWelcomeStep(true);
        };
        btnHaveSub.Click += (_, _) => ShowWelcomeStep(true);
        btnWelcomeBack.Click += (_, _) => ShowWelcomeStep(false);
        btnWelcomePaste.Click += async (_, _) => await ImportFromClipboard();
        btnWelcomeBot.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.Bot);
        btnWelcomeSite.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.CabinetUrl);
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
            // при запуске ядро ещё поднимается — проверяем задержку чуть позже
            await Task.Delay(TimeSpan.FromSeconds(4));
            await TestPing();
        });
    }

    private void ShowPage(Grid page)
    {
        var wasWelcome = pageWelcome.Visibility == Visibility.Visible;
        pageMain.Visibility = page == pageMain ? Visibility.Visible : Visibility.Collapsed;
        pageServers.Visibility = page == pageServers ? Visibility.Visible : Visibility.Collapsed;
        pageSettings.Visibility = page == pageSettings ? Visibility.Visible : Visibility.Collapsed;
        pageWelcome.Visibility = page == pageWelcome ? Visibility.Visible : Visibility.Collapsed;

        // стартовый экран всегда «горит»: свечение и угольки, как у подключённого Фаера
        if (page == pageWelcome && !wasWelcome)
        {
            bgGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromSeconds(2.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
            });
            bgBottom.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(1)));
            welcomeRing.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
            });
            HookEmbers();
        }
        else if (page != pageWelcome && wasWelcome && !_fxOn)
        {
            bgGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            bgBottom.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        }
    }

    private void ShowWelcomeStep(bool paste)
    {
        spWelcomeStart.Visibility = paste ? Visibility.Collapsed : Visibility.Visible;
        spWelcomePaste.Visibility = paste ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool WelcomeShown => pageWelcome.Visibility == Visibility.Visible;

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
        btnWelcomePaste.IsEnabled = false;
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
            btnWelcomePaste.IsEnabled = true;
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
        // нет подписки — стартовый экран вместо главного; появилась — сразу на главный
        bdWelcome.Visibility = Visibility.Collapsed;
        if (!hasSub && pageMain.Visibility == Visibility.Visible)
        {
            ShowWelcomeStep(false);
            ShowPage(pageWelcome);
        }
        else if (hasSub && WelcomeShown)
        {
            ShowPage(pageMain);
        }
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
        // две проверки сразу мешают друг другу (общие тестовые порты) — ждём, пока закончится текущая
        if (_pingTask is { IsCompleted: false })
        {
            return;
        }
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
        _pingTask = _speedtest.RunLoop(ESpeedActionType.Realping, items);
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
        if (ms <= 0 && !_pingRetryPlanned)
        {
            // первая неудача бывает случайной (сеть, запуск ядра) — одна повторная проверка
            _pingRetryPlanned = true;
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                await TestPing();
            });
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

        // Фаер ломает решётку — сноп искр
        _breaking = true;
        SetFireImage(ImgBreak);
        Burst();
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
        if (tun && !Utils.IsAdministrator())
        {
            // туннелю нужны права администратора — перезапускаемся с ними сразу, а не при подключении,
            // чтобы человек видел, что выбор сработал; был подключён — после перезапуска туннель включится сам
            FireVpn.Settings.PendingTun = IsConnected;
            FireVpn.SaveSettings();
            NoticeManager.Instance.Enqueue("Туннелю нужны права администратора — FireVPN перезапускается с ними");
            _ = AppManager.Instance.RebootAsAdmin();
            return;
        }
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
        var admin = Utils.IsAdministrator();
        txtAdmin.Text = admin ? "✅ Права администратора есть" : "Сейчас без прав администратора";
        txtAdmin.Foreground = admin ? (Brush)FindResource("FireAccent") : (Brush)FindResource("FireDim");
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
            SetFx(on);
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

    #region Fire effects

    private sealed class Ember
    {
        public required Ellipse El;
        public double X, Y, Vx, Vy, Life, MaxLife, Phase;
    }

    private readonly List<Ember> _embers = [];
    private readonly Random _rnd = new();
    private readonly Stopwatch _fxClock = new();
    private TimeSpan _fxLast;
    private bool _fxOn;
    private bool _fxHooked;
    private const int MaxEmbers = 70;

    private static readonly Color[] EmberColors =
    [
        Color.FromRgb(0xFF, 0xD3, 0x6B), Color.FromRgb(0xFF, 0xB1, 0x3B), Color.FromRgb(0xFF, 0x7A, 0x1A), Color.FromRgb(0xE8, 0x28, 0x3C),
    ];

    /// <summary>
    /// Огненная «комета» на кольце: яркая голова и хвост из дуг, которые тускнеют и тоньшают к концу
    /// </summary>
    private void BuildComet()
    {
        const double c = 118, r = 115.5, step = 5;
        const int n = 26;
        for (var i = 0; i < n; i++)
        {
            var a1 = (-90 - (i * step)) * Math.PI / 180;
            var a2 = (-90 - ((i + 1) * step) - 0.5) * Math.PI / 180;
            var fig = new PathFigure { StartPoint = new Point(c + (r * Math.Cos(a1)), c + (r * Math.Sin(a1))) };
            fig.Segments.Add(new ArcSegment(new Point(c + (r * Math.Cos(a2)), c + (r * Math.Sin(a2))), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
            var k = i / (double)n;
            var col = k < 0.15 ? Color.FromRgb(0xFF, 0xF4, 0xC2) : k < 0.35 ? EmberColors[0] : k < 0.6 ? EmberColors[1] : k < 0.85 ? EmberColors[2] : EmberColors[3];
            comet.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry([fig]),
                Stroke = new SolidColorBrush(col) { Opacity = 1 - k },
                StrokeThickness = 10 - (7 * k),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
        var head = new Ellipse
        {
            Width = 13,
            Height = 13,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xC2)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xFF, 0x8A, 0x1A), BlurRadius = 22, ShadowDepth = 0, Opacity = 1 },
        };
        Canvas.SetLeft(head, c - 6.5);
        Canvas.SetTop(head, c - r - 6.5);
        comet.Children.Add(head);
        // свечение вокруг всего хвоста
        comet.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xFF, 0x6A, 0x1A), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 };
    }

    /// <summary>
    /// Включено: бегущий импульс по кольцу, волны от круга, свечение и угольки на фоне
    /// </summary>
    private void SetFx(bool on)
    {
        _fxOn = on;
        var fade = TimeSpan.FromMilliseconds(on ? 900 : 400);
        comet.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, fade));
        if (on)
        {
            cometRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
            StartWave(wave1, TimeSpan.Zero);
            StartWave(wave2, TimeSpan.FromSeconds(1.2));
            bgGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromSeconds(2.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
            });
            bgBottom.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(1.2)));
        }
        else
        {
            cometRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            foreach (var w in new[] { wave1, wave2 })
            {
                w.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            }
            bgGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            bgBottom.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
        }
        HookEmbers();
    }

    // волна: кольцо расширяется от Фаера и гаснет
    private static void StartWave(Ellipse wave, TimeSpan delay)
    {
        var dur = TimeSpan.FromSeconds(2.4);
        var scale = new DoubleAnimation(1, 1.32, dur) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var st = (ScaleTransform)wave.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        wave.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, 0, dur) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>
    /// Фаер вырвался: сноп искр из центра во все стороны
    /// </summary>
    private void Burst()
    {
        var center = FireCenter();
        for (var i = 0; i < 60; i++)
        {
            var a = _rnd.NextDouble() * Math.PI * 2;
            var speed = 140 + (_rnd.NextDouble() * 260);
            var r = 70 + (_rnd.NextDouble() * 50);
            Spawn(center.X + (Math.Cos(a) * r), center.Y + (Math.Sin(a) * r), Math.Cos(a) * speed, (Math.Sin(a) * speed) - 60, 0.7 + (_rnd.NextDouble() * 0.7), 1.8);
        }
        HookEmbers();
    }

    private Point FireCenter()
    {
        try
        {
            return btnFire.TranslatePoint(new Point(btnFire.ActualWidth / 2, btnFire.ActualHeight / 2), embers);
        }
        catch
        {
            return new Point(embers.ActualWidth / 2, embers.ActualHeight / 2);
        }
    }

    private void Spawn(double x, double y, double vx, double vy, double life, double sizeMul = 1)
    {
        if (_embers.Count >= MaxEmbers + 50)
        {
            return;
        }
        var size = (2 + (_rnd.NextDouble() * 4)) * sizeMul;
        var col = EmberColors[_rnd.Next(EmberColors.Length)];
        var el = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new RadialGradientBrush(Color.FromArgb(255, 255, 240, 200), col),
            IsHitTestVisible = false,
        };
        embers.Children.Add(el);
        _embers.Add(new Ember { El = el, X = x, Y = y, Vx = vx, Vy = vy, Life = life, MaxLife = life, Phase = _rnd.NextDouble() * 6.28 });
    }

    private void HookEmbers()
    {
        if (_fxHooked || (!_fxOn && !WelcomeShown && _embers.Count == 0))
        {
            return;
        }
        _fxHooked = true;
        _fxClock.Restart();
        _fxLast = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFxFrame;
    }

    private void OnFxFrame(object? sender, EventArgs e)
    {
        var now = _fxClock.Elapsed;
        var dt = Math.Min(0.05, (now - _fxLast).TotalSeconds);
        _fxLast = now;
        if (dt <= 0)
        {
            return;
        }
        var w = embers.ActualWidth;
        var h = embers.ActualHeight;

        // пока включено (и на стартовом экране) — угольки поднимаются снизу и от Фаера
        var ambient = (_fxOn && pageMain.Visibility == Visibility.Visible) || WelcomeShown;
        if (ambient && _embers.Count < MaxEmbers && w > 0)
        {
            var count = _rnd.NextDouble() < dt * 22 ? 1 : 0;
            for (var i = 0; i < count; i++)
            {
                if (WelcomeShown || _rnd.NextDouble() < 0.7)
                {
                    Spawn(_rnd.NextDouble() * w, h + 6, (_rnd.NextDouble() - 0.5) * 20, -(45 + (_rnd.NextDouble() * 90)), 3 + (_rnd.NextDouble() * 4));
                }
                else
                {
                    var c = FireCenter();
                    var a = _rnd.NextDouble() * Math.PI * 2;
                    Spawn(c.X + (Math.Cos(a) * 118), c.Y + (Math.Sin(a) * 118), Math.Cos(a) * 25, -(30 + (_rnd.NextDouble() * 60)), 1.6 + (_rnd.NextDouble() * 1.6));
                }
            }
        }

        for (var i = _embers.Count - 1; i >= 0; i--)
        {
            var p = _embers[i];
            p.Life -= dt;
            if (p.Life <= 0 || p.Y < -10)
            {
                embers.Children.Remove(p.El);
                _embers.RemoveAt(i);
                continue;
            }
            p.Phase += dt * 3;
            p.Vx *= 1 - (dt * 1.2);
            p.Vy = (p.Vy * (1 - (dt * 0.6))) - (dt * 12);
            p.X += (p.Vx + (Math.Sin(p.Phase) * 14)) * dt;
            p.Y += p.Vy * dt;
            var k = p.Life / p.MaxLife;
            p.El.Opacity = Math.Min(1, k * 1.6) * (0.75 + (0.25 * Math.Sin(p.Phase * 2.3)));
            Canvas.SetLeft(p.El, p.X);
            Canvas.SetTop(p.El, p.Y);
        }

        if (!_fxOn && !WelcomeShown && _embers.Count == 0)
        {
            CompositionTarget.Rendering -= OnFxFrame;
            _fxHooked = false;
        }
    }

    #endregion Fire effects

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

    #region Toast

    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// Огненное уведомление снизу экрана: всплывает и через несколько секунд гаснет (длиннее текст — дольше)
    /// </summary>
    public void ShowToast(string text)
    {
        text = FireCountries.Humanize(text);
        txtToast.Text = text;
        toast.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        toastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(2.5 + (text.Length * 0.04), 3, 7)) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) =>
            {
                if (toast.Opacity == 0)
                {
                    toast.Visibility = Visibility.Collapsed;
                }
            };
            toast.BeginAnimation(OpacityProperty, fade);
            toastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, TimeSpan.FromMilliseconds(300)));
        };
        _toastTimer.Start();
    }

    #endregion Toast

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

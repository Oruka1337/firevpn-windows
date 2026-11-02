using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Immutable;
using Avalonia.Media.Transformation;
using ServiceLib.Services;
using v2rayN.Desktop.Common;
using Path = Avalonia.Controls.Shapes.Path;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Сервер в списке FireVPN (флаг, название, скорость ответа)
/// </summary>
public class FireServerItem : INotifyPropertyChanged
{
    public string IndexId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Country { get; init; } = "";
    public string CodeText { get; init; } = "";
    public IImage? Flag { get; init; }

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
        set { _selected = value; OnChanged(nameof(IsSelected)); OnChanged(nameof(QuickSize)); }
    }

    // выбранный в быстром ряду — крупнее
    public double QuickSize => _selected ? 52 : 38;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// FireVPN (macOS/Linux, Avalonia): тот же минималистичный экран, что в версии для Windows.
/// Выключено — Фаер за решёткой, подключение — ломает решётку, включено — на свободе.
/// Анимации считаются в одном цикле кадров (OnFrame), а не отдельными раскадровками.
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

    private static readonly Bitmap ImgCage = Img("firevpn_cage.jpg");
    private static readonly Bitmap ImgBreak = Img("firevpn_break.jpg");
    private static readonly Bitmap ImgFree = Img("firevpn_free.jpg");

    public event Action? AdvancedRequested;

    public bool IsReady => _vm != null;

    public FireHomeView()
    {
        InitializeComponent();
        BuildComet();
        FireBrush.Source = ImgCage;

        txtVersion.Text = $"FireVPN {FireVpn.AppVersion} · основан на v2rayN (GPL-3.0)";
        if (!OperatingSystem.IsMacOS())
        {
            txtTunHint.Text = "Игры и все программы. FireVPN попросит пароль администратора.";
        }

        btnSettings.Click += (_, _) => ShowPage(pageSettings);
        btnSettingsBack.Click += (_, _) => ShowPage(pageMain);
        btnServerCard.Click += (_, _) => OpenServers();
        btnServersBack.Click += (_, _) => ShowPage(pageMain);
        btnPing.Click += async (_, _) => await TestPing();
        btnAdvanced.Click += (_, _) => AdvancedRequested?.Invoke();
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
        btnSite.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.Site);
        btnInvite.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.InviteUrl);
        btnCabinet.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.CabinetUrl);
        btnSupport.Click += (_, _) => ProcUtils.ProcessStart(FireVpn.SupportUrl);
        btnSubRefresh.Click += async (_, _) => await UpdateSubscription();
        btnFire.Click += (_, _) => ToggleConnect();
        btnUpdate.Click += async (_, _) => await ApplyUpdate();
        btnModeProxy.Click += (_, _) => SetMode(false);
        btnModeTun.Click += (_, _) => SetMode(true);
        chkAutoRun.IsCheckedChanged += async (_, _) =>
        {
            if (_vm != null && chkAutoRun.IsChecked != _config.GuiItem.AutoRun)
            {
                await SetAutoRun(chkAutoRun.IsChecked == true);
            }
        };

        _subTimer.Tick += async (_, _) => await RefreshSubInfo();
        _clock.Tick += (_, _) => UpdateTimer();
    }

    private ImageBrush FireBrush => (ImageBrush)elFire.Fill!;

    private static Bitmap Img(string name) => new(AssetLoader.Open(new Uri($"avares://v2rayN/Assets/{name}")));

    public void Init(MainWindowViewModel vm)
    {
        _vm = vm;

        chkAutoRun.IsChecked = _config.GuiItem.AutoRun;
        UpdateModeUi();

        // список серверов в StatusBarViewModel обновляется после каждой загрузки подписки — перерисовываем свой
        Bar.Servers.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(async () => await RefreshServers());

        Bar.WhenAnyValue(x => x.BlSystemProxySet, x => x.EnableTun)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateConnectUi());

        _subTimer.Start();
        Dispatcher.UIThread.Post(async () =>
        {
            await EnsureSubscriptionDefaults();
            await RefreshServers();
            await RefreshSubInfo();

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
        var wasWelcome = pageWelcome.IsVisible;
        pageMain.IsVisible = page == pageMain;
        pageServers.IsVisible = page == pageServers;
        pageSettings.IsVisible = page == pageSettings;
        pageWelcome.IsVisible = page == pageWelcome;
        if (page == pageWelcome && !wasWelcome)
        {
            // стартовый экран всегда «горит»: свечение и угольки, как у подключённого Фаера
            Tween(bgBottom, OpacityProperty, bgBottom.Opacity, 1, 1.0);
            StartFrames();
        }
        else if (page != pageWelcome && wasWelcome && !_fxOn)
        {
            Tween(bgBottom, OpacityProperty, bgBottom.Opacity, 0, 0.4);
        }
    }

    private void ShowWelcomeStep(bool paste)
    {
        spWelcomeStart.IsVisible = !paste;
        spWelcomePaste.IsVisible = paste;
    }

    private bool WelcomeShown => pageWelcome.IsVisible;

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
        var text = TopLevel.GetTopLevel(this) is Window w ? (await AvaUtils.GetClipboardData(w))?.Trim() : null;
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
        if (!hasSub && pageMain.IsVisible)
        {
            ShowWelcomeStep(false);
            ShowPage(pageWelcome);
        }
        else if (hasSub && WelcomeShown)
        {
            ShowPage(pageMain);
        }
        btnServerCard.IsVisible = hasSub;
        spQuick.Opacity = hasSub && _servers.Count > 1 ? 1 : 0;
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
                var code = FireCountries.CountryCode(t.Remarks);
                return new FireServerItem
                {
                    IndexId = t.IndexId,
                    Name = FireCountries.StripFlags(t.Remarks),
                    Country = FireCountries.CountryName(code),
                    CodeText = code?.ToUpperInvariant() ?? "",
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

    private void Quick_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FireServerItem item)
        {
            SelectServer(item);
        }
    }

    private void Server_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FireServerItem item)
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
            Dispatcher.UIThread.Post(() => SetPing(result));
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
            Dispatcher.UIThread.Post(async () =>
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
        DispatcherTimer.RunOnce(() =>
        {
            _breaking = false;
            UpdateConnectUi();
        }, TimeSpan.FromSeconds(1.3));

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
    /// Туннель (весь компьютер): на Маке и Linux v2rayN сам спросит пароль от компьютера
    /// </summary>
    private void StartTun()
    {
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
        btnModeProxy.Classes.Set("selected", !FireVpn.Settings.FullDevice);
        btnModeTun.Classes.Set("selected", FireVpn.Settings.FullDevice);
    }

    private async Task SetAutoRun(bool on)
    {
        _config.GuiItem.AutoRun = on;
        await AutoStartupHandler.UpdateTask(_config);
        await ConfigHandler.SaveConfig(_config);
    }

    private IBrush Res(string key) => (IBrush)this.FindResource(key)!;

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
            SetFx(on);
        }

        elRing.Stroke = on ? Res("FireFill") : Res("FireRingOff");
        txtStatus.Text = on ? (Bar.EnableTun ? "Подключено · весь компьютер" : "Подключено") : "Не подключено";
        txtStatus.Foreground = on ? Res("FireAccent") : Res("FireMuted");
        txtTimer.IsVisible = on;
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

    private void SetFireImage(Bitmap img)
    {
        FireBrush.Source = img;
        Tween(elFire, OpacityProperty, 0.35, 1, 0.45);
        _popStart = _frameClock.Elapsed.TotalSeconds;
        StartFrames();
    }

    #endregion Connect

    #region Fire effects

    private sealed class Ember
    {
        public required Ellipse El;
        public double X, Y, Vx, Vy, Life, MaxLife, Phase;
    }

    private sealed class TweenItem
    {
        public required AvaloniaObject Target;
        public required AvaloniaProperty<double> Prop;
        public double From, To, Start, Duration;
    }

    private readonly List<Ember> _embers = [];
    private readonly List<TweenItem> _tweens = [];
    private readonly Random _rnd = new();
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastFrame;
    private double _popStart = -10;
    private double _fxSince;
    private bool _fxOn;
    private bool _framesOn;
    private const int MaxEmbers = 70;

    private static readonly Color[] EmberColors =
    [
        Color.FromRgb(0xFF, 0xD3, 0x6B), Color.FromRgb(0xFF, 0xB1, 0x3B), Color.FromRgb(0xFF, 0x7A, 0x1A), Color.FromRgb(0xE8, 0x28, 0x3C),
    ];

    private readonly RotateTransform _cometRotate = new();
    private readonly ScaleTransform _fireScale = new();
    private readonly ScaleTransform _wave1Scale = new();
    private readonly ScaleTransform _wave2Scale = new();

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
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(c + (r * Math.Cos(a1)), c + (r * Math.Sin(a1))), false);
                ctx.ArcTo(new Point(c + (r * Math.Cos(a2)), c + (r * Math.Sin(a2))), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
            }
            var k = i / (double)n;
            var col = k < 0.15 ? Color.FromRgb(0xFF, 0xF4, 0xC2) : k < 0.35 ? EmberColors[0] : k < 0.6 ? EmberColors[1] : k < 0.85 ? EmberColors[2] : EmberColors[3];
            comet.Children.Add(new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(col, 1 - k),
                StrokeThickness = 10 - (7 * k),
                StrokeLineCap = PenLineCap.Round,
            });
        }
        var head = new Ellipse
        {
            Width = 13,
            Height = 13,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xC2)),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x8A, 0x1A), BlurRadius = 22, OffsetX = 0, OffsetY = 0, Opacity = 1 },
        };
        Canvas.SetLeft(head, c - 6.5);
        Canvas.SetTop(head, c - r - 6.5);
        comet.Children.Add(head);
        // свечение вокруг всего хвоста
        comet.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x6A, 0x1A), BlurRadius = 16, OffsetX = 0, OffsetY = 0, Opacity = 0.9 };

        comet.RenderTransform = _cometRotate;
        btnFire.RenderTransform = _fireScale;
        wave1.RenderTransform = _wave1Scale;
        wave2.RenderTransform = _wave2Scale;
    }

    /// <summary>
    /// Включено: бегущий импульс по кольцу, волны от круга, свечение и угольки на фоне
    /// </summary>
    private void SetFx(bool on)
    {
        _fxOn = on;
        _fxSince = _frameClock.Elapsed.TotalSeconds;
        Tween(comet, OpacityProperty, comet.Opacity, on ? 1 : 0, on ? 0.9 : 0.4);
        Tween(glow, OpacityProperty, glow.Opacity, on ? 1 : 0, on ? 0.9 : 0.3);
        if (!WelcomeShown)
        {
            Tween(bgBottom, OpacityProperty, bgBottom.Opacity, on ? 1 : 0, on ? 1.2 : 0.4);
        }
        if (!on)
        {
            Tween(wave1, OpacityProperty, wave1.Opacity, 0, 0.4);
            Tween(wave2, OpacityProperty, wave2.Opacity, 0, 0.4);
        }
        StartFrames();
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
        StartFrames();
    }

    private Point FireCenter()
    {
        var p = btnFire.TranslatePoint(new Point(btnFire.Bounds.Width / 2, btnFire.Bounds.Height / 2), embers);
        return p ?? new Point(embers.Bounds.Width / 2, embers.Bounds.Height / 2);
    }

    private void Spawn(double x, double y, double vx, double vy, double life, double sizeMul = 1)
    {
        if (_embers.Count >= MaxEmbers + 60)
        {
            return;
        }
        var size = (2 + (_rnd.NextDouble() * 4)) * sizeMul;
        var col = EmberColors[_rnd.Next(EmberColors.Length)];
        var el = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new RadialGradientBrush
            {
                GradientStops = [new GradientStop(Color.FromRgb(255, 240, 200), 0), new GradientStop(col, 1)],
            },
            IsHitTestVisible = false,
        };
        embers.Children.Add(el);
        _embers.Add(new Ember { El = el, X = x, Y = y, Vx = vx, Vy = vy, Life = life, MaxLife = life, Phase = _rnd.NextDouble() * 6.28 });
    }

    private void Tween(AvaloniaObject target, AvaloniaProperty<double> prop, double from, double to, double seconds)
    {
        _tweens.RemoveAll(t => t.Target == target && t.Prop == prop);
        _tweens.Add(new TweenItem { Target = target, Prop = prop, From = from, To = to, Start = _frameClock.Elapsed.TotalSeconds, Duration = seconds });
        target.SetValue(prop, from);
        StartFrames();
    }

    private bool NeedFrames => _fxOn || WelcomeShown || _embers.Count > 0 || _tweens.Count > 0
                               || _frameClock.Elapsed.TotalSeconds - _popStart < 0.5;

    private void StartFrames()
    {
        if (_framesOn || !NeedFrames)
        {
            return;
        }
        _framesOn = true;
        _lastFrame = _frameClock.Elapsed.TotalSeconds;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        var now = _frameClock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.05, now - _lastFrame);
        _lastFrame = now;

        // плавные переходы прозрачности
        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var t = _tweens[i];
            var k = t.Duration <= 0 ? 1 : Math.Clamp((now - t.Start) / t.Duration, 0, 1);
            var e = 1 - Math.Pow(1 - k, 3);
            t.Target.SetValue(t.Prop, t.From + ((t.To - t.From) * e));
            if (k >= 1)
            {
                _tweens.RemoveAt(i);
            }
        }

        // «прыжок» Фаера при смене картинки
        var pk = Math.Clamp((now - _popStart) / 0.45, 0, 1);
        var pop = pk >= 1 ? 1 : 0.94 + (0.06 * (1 - Math.Pow(1 - pk, 3) * Math.Cos(pk * 5)));
        _fireScale.ScaleX = _fireScale.ScaleY = Math.Min(1.02, pop);

        var on = _fxOn && pageMain.IsVisible;
        if (_fxOn)
        {
            // комета обегает кольцо за 2,4 с, волны расходятся каждые 2,4 с со сдвигом
            _cometRotate.Angle = (now * 150) % 360;
            Wave(wave1, _wave1Scale, now - _fxSince);
            Wave(wave2, _wave2Scale, now - _fxSince - 1.2);
            glow.Opacity = _tweens.Any(t => t.Target == glow) ? glow.Opacity : 0.775 + (0.225 * Math.Sin(now * Math.PI / 1.6));
        }
        if (on || WelcomeShown)
        {
            // мягкое «дыхание» фонового свечения
            bgGlow.Opacity = 0.82 + (0.18 * Math.Sin(now * Math.PI / 2.2));
            if (WelcomeShown)
            {
                welcomeRing.Opacity = 0.775 + (0.225 * Math.Sin(now * Math.PI / 1.6));
            }
        }
        else if (!_tweens.Any(t => t.Target == bgGlow) && bgGlow.Opacity > 0)
        {
            Tween(bgGlow, OpacityProperty, bgGlow.Opacity, 0, 0.4);
        }

        UpdateEmbers(dt, on || WelcomeShown);

        if (NeedFrames)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }
        else
        {
            _framesOn = false;
        }
    }

    // волна: кольцо расширяется от Фаера и гаснет
    private static void Wave(Ellipse wave, ScaleTransform scale, double t)
    {
        if (t < 0)
        {
            wave.Opacity = 0;
            return;
        }
        var k = t % 2.4 / 2.4;
        var e = 1 - Math.Pow(1 - k, 2);
        scale.ScaleX = scale.ScaleY = 1 + (0.32 * e);
        wave.Opacity = 0.85 * (1 - k);
    }

    private void UpdateEmbers(double dt, bool spawn)
    {
        var w = embers.Bounds.Width;
        var h = embers.Bounds.Height;

        // пока включено (и на стартовом экране) — угольки поднимаются снизу и от Фаера
        if (spawn && _embers.Count < MaxEmbers && w > 0 && _rnd.NextDouble() < dt * 22)
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
    }

    #endregion Fire effects

    #region Toast

    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// Огненное уведомление снизу экрана: всплывает и через несколько секунд гаснет (длиннее текст — дольше)
    /// </summary>
    public void ShowToast(string text)
    {
        text = FireCountries.Humanize(text);
        txtToast.Text = text;
        toast.IsVisible = true;
        toast.Opacity = 1;
        toast.RenderTransform = TransformOperations.Parse("translateY(0px)");

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(2.5 + (text.Length * 0.04), 3, 7)) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            toast.Opacity = 0;
            toast.RenderTransform = TransformOperations.Parse("translateY(12px)");
            DispatcherTimer.RunOnce(() =>
            {
                if (toast.Opacity == 0)
                {
                    toast.IsVisible = false;
                    toast.RenderTransform = TransformOperations.Parse("translateY(20px)");
                }
            }, TimeSpan.FromMilliseconds(320));
        };
        _toastTimer.Start();
    }

    #endregion Toast

    #region Update

    private void ShowUpdate(FireVpn.UpdateInfo? update)
    {
        _update = update;
        bdUpdate.IsVisible = update != null;
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
        var ok = await FireVpn.DownloadAndUpgrade(_update, p => Dispatcher.UIThread.Post(() => btnUpdate.Content = $"{p}%"));
        if (!ok)
        {
            btnUpdate.Content = "Обновить";
            btnUpdate.IsEnabled = true;
            NoticeManager.Instance.Enqueue("Не удалось обновить — скачайте новую версию на fire-vpn.online/download");
            ProcUtils.ProcessStart(FireVpn.DownloadPage);
        }
        else if (OperatingSystem.IsMacOS())
        {
            btnUpdate.Content = "Обновить";
            btnUpdate.IsEnabled = true;
            NoticeManager.Instance.Enqueue("Откройте скачанный файл и перетащите FireVPN в «Программы»");
        }
    }

    #endregion Update
}

using DialogHostAvalonia;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class StatusBarView : ReactiveUserControl<StatusBarViewModel>
{
    private static Config _config;

    public StatusBarView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        txtRunningServerDisplay.Tapped += TxtRunningServerDisplay_Tapped;
        txtRunningInfoDisplay.Tapped += TxtRunningServerDisplay_Tapped;

        this.WhenActivated(disposables =>
        {
            //status bar
            this.OneWayBind(ViewModel, vm => vm.InboundDisplay, v => v.txtInboundDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.InboundLanDisplay, v => v.txtInboundLanDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedDirectDisplay, v => v.txtSpeedDirectDisplay.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.togEnableTun.IsChecked).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SystemProxySelected, v => v.cmbSystemProxy.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings2.SelectedItem).DisposeWith(disposables);

            ViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                var strData = interaction.Input;
                await AvaUtils.SetClipboardData(this, strData);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.PasswordInputInteraction.RegisterHandler(async interaction =>
            {
                var result = await PasswordInputAsync();
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            // FireVPN: вместо «[VLESS] FI Хельсинки(168***127:2096)» — статус и сервер по-человечески
            ViewModel.WhenAnyValue(vm => vm.RunningServerDisplay, vm => vm.BlSystemProxySet, vm => vm.EnableTun)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(async _ => await UpdateTrayTip())
                .DisposeWith(disposables);

            ViewModel.DispatcherRefreshIconInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(RefreshIcon, DispatcherPriority.Default);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
        });

        //spEnableTun.IsVisible = (Utils.IsWindows() || AppHandler.Instance.IsAdministrator);

        if (Utils.IsNonWindows() && cmbSystemProxy.Items.IsReadOnly == false)
        {
            cmbSystemProxy.Items.RemoveAt(cmbSystemProxy.Items.Count - 1);
        }

        // Because this view has not yet been initialized when DispatcherRefreshIconInteraction is first called.
        RefreshIcon();
    }

    private NativeMenuItem? _trayStatus;

    /// <summary>
    /// Подсказка у значка и первая строка его меню: «FireVPN — Подключено · Хельсинки · Финляндия»
    /// </summary>
    private async Task UpdateTrayTip()
    {
        var on = ViewModel?.BlSystemProxySet == true || ViewModel?.EnableTun == true;
        var server = await ConfigHandler.GetDefaultServer(_config);
        var where = server == null ? "" : FireCountries.ServerLabel(server.Remarks);
        var status = on ? (ViewModel?.EnableTun == true ? "Подключено · весь компьютер" : "Подключено") : "Не подключено";

        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons == null || icons.Count == 0)
        {
            return;
        }
        var tray = icons[0];
        tray.ToolTipText = $"FireVPN — {status}" + (where.IsNotEmpty() ? $"\nСервер: {where}" : "");

        if (_trayStatus == null && tray.Menu != null)
        {
            _trayStatus = new NativeMenuItem { IsEnabled = false };
            tray.Menu.Items.Insert(0, _trayStatus);
            tray.Menu.Items.Insert(1, new NativeMenuItemSeparator());
        }
        if (_trayStatus != null)
        {
            _trayStatus.Header = (on ? "🟢 " : "⚪ ") + status + (where.IsNotEmpty() ? $" · {where}" : "");
        }
    }

    private void RefreshIcon()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow.Icon = AvaUtils.GetAppIcon(_config.SystemProxyItem.SysProxyType);
            var iconslist = TrayIcon.GetIcons(Application.Current);
            iconslist[0].Icon = desktop.MainWindow.Icon;
            TrayIcon.SetIcons(Application.Current, iconslist);
        }
    }

    private async Task<string?> PasswordInputAsync()
    {
        var dialog = new SudoPasswordInputView();
        var obj = await DialogHost.Show(dialog);

        var password = obj?.ToString();
        if (password.IsNullOrEmpty())
        {
            togEnableTun.IsChecked = false;
            return password;
        }

        AppManager.Instance.LinuxSudoPwd = password;
        return password;
    }

    private void TxtRunningServerDisplay_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ViewModel?.TestServerAvailability();
    }
}

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Dragablz;
using DynamicData;
using EyeAuras.DefaultAuras.Triggers.HotkeyIsActive;
using EyeAuras.Shared.Services;
using EyeAuras.UI.Core.Models;
using EyeAuras.UI.Core.Utilities;
using EyeAuras.UI.Core.ViewModels;
using EyeAuras.UI.MainWindow.Models;
using EyeAuras.UI.Prism.Modularity;
using EyeAuras.UI.RegionSelector.Services;
using JetBrains.Annotations;
using KellermanSoftware.CompareNetObjects;
using log4net;
using PoeShared;
using PoeShared.Modularity;
using PoeShared.Native;
using PoeShared.Prism;
using PoeShared.Scaffolding;
using PoeShared.Scaffolding.WPF;
using PoeShared.Squirrel.Updater;
using PoeShared.UI;
using PoeShared.UI.Hotkeys;
using ReactiveUI;
using Unity;

namespace EyeAuras.UI.MainWindow.ViewModels
{
    internal class MainWindowViewModel : DisposableReactiveObject, IMainWindowViewModel
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(MainWindowViewModel));
        private static readonly int UndoStackDepth = 10;

        private static readonly string ExplorerExecutablePath = Environment.ExpandEnvironmentVariables(@"%WINDIR%\explorer.exe");
        private static readonly TimeSpan ConfigSaveSamplingTimeout = TimeSpan.FromSeconds(5);

        private readonly IFactory<IOverlayAuraModel> auraModelFactory;
        private readonly IFactory<IOverlayAuraViewModel, IOverlayAuraModel> auraViewModelFactory;
        private readonly IClipboardManager clipboardManager;
        private readonly IConfigProvider<EyeAurasConfig> configProvider;
        private readonly IConfigSerializer configSerializer;
        private readonly ISubject<Unit> configUpdateSubject = new Subject<Unit>();

        private readonly CompareLogic diffLogic = new CompareLogic(
            new ComparisonConfig
            {
                DoublePrecision = 0.01,
                MaxDifferences = byte.MaxValue
            });

        private readonly IHotkeyConverter hotkeyConverter;
        private readonly IFactory<HotkeyIsActiveTrigger> hotkeyTriggerFactory;
        private readonly TabablzPositionMonitor<IOverlayAuraViewModel> positionMonitor = new TabablzPositionMonitor<IOverlayAuraViewModel>();
        private readonly CircularBuffer<OverlayAuraProperties> recentlyClosedQueries = new CircularBuffer<OverlayAuraProperties>(UndoStackDepth);
        private readonly IRegionSelectorService regionSelectorService;
        private readonly ISourceList<IOverlayAuraViewModel> tabsListSource = new SourceList<IOverlayAuraViewModel>();
        private double height;
        private double left;

        private GridLength listWidth;

        private IOverlayAuraViewModel selectedTab;
        private double top;
        private double width;
        private WindowState windowState;

        public MainWindowViewModel(
            [NotNull] IFactory<IOverlayAuraViewModel, IOverlayAuraModel> auraViewModelFactory,
            [NotNull] IFactory<IOverlayAuraModel> auraModelFactory,
            [NotNull] IApplicationUpdaterViewModel appUpdater,
            [NotNull] IClipboardManager clipboardManager,
            [NotNull] IConfigSerializer configSerializer,
            [NotNull] EyeAurasSettingsViewModel settingsViewModel,
            [NotNull] IMessageBoxViewModel messageBox,
            [NotNull] IHotkeyConverter hotkeyConverter,
            [NotNull] IFactory<HotkeyIsActiveTrigger> hotkeyTriggerFactory,
            [NotNull] IConfigProvider<EyeAurasConfig> configProvider,
            [NotNull] IConfigProvider rootConfigProvider,
            [NotNull] IPrismModuleStatusViewModel moduleStatus,
            [NotNull] IMainWindowBlocksProvider mainWindowBlocksProvider,
            [NotNull] IRegionSelectorService regionSelectorService,
            [NotNull] ISharedContext sharedContext,
            [NotNull] [Dependency(WellKnownSchedulers.UI)] IScheduler uiScheduler)
        {
            ModuleStatus = moduleStatus.AddTo(Anchors);
            var executingAssemblyName = Assembly.GetExecutingAssembly().GetName();
            Title = $"{(AppArguments.Instance.IsDebugMode ? "[D]" : "")} {executingAssemblyName.Name} v{executingAssemblyName.Version}";
            Disposable.Create(() => Log.Info("Disposing Main view model")).AddTo(Anchors);

            ApplicationUpdater = appUpdater.AddTo(Anchors);
            MessageBox = messageBox.AddTo(Anchors);
            Settings = settingsViewModel.AddTo(Anchors);
            StatusBarItems = mainWindowBlocksProvider.StatusBarItems;

            this.auraViewModelFactory = auraViewModelFactory;
            this.auraModelFactory = auraModelFactory;
            this.configProvider = configProvider;
            this.regionSelectorService = regionSelectorService;
            this.clipboardManager = clipboardManager;
            this.configSerializer = configSerializer;
            this.hotkeyConverter = hotkeyConverter;
            this.hotkeyTriggerFactory = hotkeyTriggerFactory;

            CreateNewTabCommand = CommandWrapper.Create(() => AddNewCommandExecuted(OverlayAuraProperties.Default));
            CloseTabCommand = CommandWrapper
                .Create<IOverlayAuraViewModel>(CloseTabCommandExecuted, CloseTabCommandCanExecute)
                .RaiseCanExecuteChangedWhen(this.WhenAnyProperty(x => x.SelectedTab));

            DuplicateTabCommand = CommandWrapper
                .Create(DuplicateTabCommandExecuted, DuplicateTabCommandCanExecute)
                .RaiseCanExecuteChangedWhen(this.WhenAnyProperty(x => x.SelectedTab));
            CopyTabToClipboardCommand = CommandWrapper
                .Create(CopyTabToClipboardExecuted, CopyTabToClipboardCommandCanExecute)
                .RaiseCanExecuteChangedWhen(this.WhenAnyProperty(x => x.SelectedTab).Select(x => x));

            PasteTabCommand = CommandWrapper.Create(PasteTabCommandExecuted);
            UndoCloseTabCommand = CommandWrapper.Create(UndoCloseTabCommandExecuted, UndoCloseTabCommandCanExecute);
            OpenAppDataDirectoryCommand = CommandWrapper.Create(OpenAppDataDirectory);
            SelectRegionCommand = CommandWrapper.Create(SelectRegionCommandExecuted);

            Observable
                .FromEventPattern<OrderChangedEventArgs>(h => positionMonitor.OrderChanged += h, h => positionMonitor.OrderChanged -= h)
                .Select(x => x.EventArgs)
                .Subscribe(OnTabOrderChanged, Log.HandleUiException)
                .AddTo(Anchors);

            tabsListSource
                .Connect()
                .ObserveOn(uiScheduler)
                .Bind(out var readonlyTabsListSource)
                .OnItemAdded(x => SelectedTab = x)
                .Subscribe(() => { }, Log.HandleUiException)
                .AddTo(Anchors);
            TabsList = readonlyTabsListSource;

            this.WhenAnyValue(x => x.SelectedTab)
                .Subscribe(x => Log.Debug($"Selected tab: {x}"))
                .AddTo(Anchors);

            LoadConfig();
            rootConfigProvider.Save();

            if (tabsListSource.Count == 0)
            {
                CreateNewTabCommand.Execute(null);
            }

            configUpdateSubject
                .Subscribe(SaveConfig, Log.HandleException)
                .AddTo(Anchors);

            Observable.Merge(
                    this.WhenAnyProperty(x => x.Left, x => x.Top, x => x.Width, x => x.Height)
                        .Select(x => $"[{x.Sender}] Main window property change: {x.EventArgs.PropertyName}"),
                    tabsListSource.Connect()
                        .Select(x => "Tabs list change"),
                    tabsListSource.Connect()
                        .Transform(x => x.Model)
                        .WhenPropertyChanged(x => x.Properties)
                        .WithPrevious((prev, curr) => new {prev, curr})
                        .Select(x => new { x.curr.Sender, ComparisonResult = diffLogic.Compare(x.prev?.Value, x.curr.Value) })
                        .Where(x => !x.ComparisonResult.AreEqual)
                        .Select(x => $"[{x.Sender.Name}] Model properties change: {x.ComparisonResult.DifferencesString}"))
                .Buffer(ConfigSaveSamplingTimeout)
                .Where(x => x.Count > 0)
                .Subscribe(
                    reasons =>
                    {
                        const int maxReasonsToOutput = 50;
                        Log.Debug(
                            $"Config Save reasons{(reasons.Count <= maxReasonsToOutput ? string.Empty : $"first {maxReasonsToOutput} of {reasons.Count} items")}:\r\n\t{reasons.Take(maxReasonsToOutput).DumpToTable()}");
                        configUpdateSubject.OnNext(Unit.Default);
                    },
                    Log.HandleUiException)
                .AddTo(Anchors);

            GlobalHotkeyTrigger = hotkeyTriggerFactory.Create().AddTo(sharedContext.SystemTrigger.Triggers);
            GlobalHotkeyTrigger.IsActive = true;
            GlobalHotkeyTrigger.SuppressKey = true;
            Observable.Merge(
                    configProvider.ListenTo(x => x.FreezeAurasHotkey).ToUnit(),
                    configProvider.ListenTo(x => x.FreezeAurasHotkeyMode).ToUnit())
                .Select(
                    () => new
                    {
                        FreezeAurasHotkey = hotkeyConverter.ConvertFromString(configProvider.ActualConfig.FreezeAurasHotkey),
                        configProvider.ActualConfig.FreezeAurasHotkeyMode
                    })
                .DistinctUntilChanged()
                .WithPrevious((prev, curr) => new {prev, curr})
                .ObserveOn(uiScheduler)
                .Subscribe(
                    cfg =>
                    {
                        Log.Debug($"Setting new FreezeAurasHotkey configuration, {cfg.prev.DumpToTextRaw()} => {cfg.curr.DumpToTextRaw()}");
                        GlobalHotkeyTrigger.Hotkey = cfg.curr.FreezeAurasHotkey;
                        GlobalHotkeyTrigger.HotkeyMode = cfg.curr.FreezeAurasHotkeyMode;
                    },
                    Log.HandleException)
                .AddTo(Anchors);

            var globalUnlockHotkeyTrigger = hotkeyTriggerFactory.Create().AddTo(Anchors);
            globalUnlockHotkeyTrigger.SuppressKey = true;
            globalUnlockHotkeyTrigger.HotkeyMode = HotkeyMode.Hold;
            globalUnlockHotkeyTrigger.IsActive = false;
            globalUnlockHotkeyTrigger.WhenAnyValue(x => x.IsActive)
                .Subscribe(
                    unlock =>
                    {
                        var allOverlays = TabsList.Select(y => y.Model.Overlay);
                        if (unlock)
                        {
                            allOverlays
                                .Where(overlay => overlay.UnlockWindowCommand.CanExecute(null))
                                .ForEach(overlay => overlay.UnlockWindowCommand.Execute(null));
                        }
                        else
                        {
                            allOverlays
                                .Where(overlay => overlay.LockWindowCommand.CanExecute(null))
                                .ForEach(overlay => overlay.LockWindowCommand.Execute(null));
                        }
                    })
                .AddTo(Anchors);

            Observable.Merge(
                    configProvider.ListenTo(x => x.UnlockAurasHotkey).ToUnit(),
                    configProvider.ListenTo(x => x.UnlockAurasHotkeyMode).ToUnit())
                .Select(
                    () => new
                    {
                        UnlockAurasHotkey = hotkeyConverter.ConvertFromString(configProvider.ActualConfig.UnlockAurasHotkey),
                        configProvider.ActualConfig.UnlockAurasHotkeyMode
                    })
                .DistinctUntilChanged()
                .WithPrevious((prev, curr) => new {prev, curr})
                .ObserveOn(uiScheduler)
                .Subscribe(
                    cfg =>
                    {
                        Log.Debug($"Setting new UnlockAurasHotkey configuration, {cfg.prev.DumpToTextRaw()} => {cfg.curr.DumpToTextRaw()}");
                        globalUnlockHotkeyTrigger.Hotkey = cfg.curr.UnlockAurasHotkey;
                        globalUnlockHotkeyTrigger.HotkeyMode = cfg.curr.UnlockAurasHotkeyMode;
                    },
                    Log.HandleException)
                .AddTo(Anchors);

            RegisterSelectRegionHotkey()
                .Where(isActive => isActive)
                .Subscribe(isActive => SelectRegionCommandExecuted())
                .AddTo(Anchors);
        }

        public IPrismModuleStatusViewModel ModuleStatus { get; }

        public ReadOnlyObservableCollection<object> StatusBarItems { [NotNull] get; }

        public string Title { get; }

        public bool IsElevated => AppArguments.Instance.IsElevated;

        public HotkeyIsActiveTrigger GlobalHotkeyTrigger { get; }

        public IApplicationUpdaterViewModel ApplicationUpdater { get; }

        public IMessageBoxViewModel MessageBox { get; }

        public ReadOnlyObservableCollection<IOverlayAuraViewModel> TabsList { get; }

        public PositionMonitor PositionMonitor => positionMonitor;

        public CommandWrapper OpenAppDataDirectoryCommand { get; }

        public EyeAurasSettingsViewModel Settings { get; }

        public IOverlayAuraViewModel SelectedTab
        {
            get => selectedTab;
            set => RaiseAndSetIfChanged(ref selectedTab, value);
        }

        public GridLength ListWidth
        {
            get => listWidth;
            set => RaiseAndSetIfChanged(ref listWidth, value);
        }

        public double Width
        {
            get => width;
            set => RaiseAndSetIfChanged(ref width, value);
        }

        public double Height
        {
            get => height;
            set => RaiseAndSetIfChanged(ref height, value);
        }

        public double Left
        {
            get => left;
            set => RaiseAndSetIfChanged(ref left, value);
        }

        public double Top
        {
            get => top;
            set => RaiseAndSetIfChanged(ref top, value);
        }

        public WindowState WindowState
        {
            get => windowState;
            set => RaiseAndSetIfChanged(ref windowState, value);
        }

        public CommandWrapper CreateNewTabCommand { get; }

        public CommandWrapper CloseTabCommand { get; }

        public CommandWrapper CopyTabToClipboardCommand { get; }

        public CommandWrapper DuplicateTabCommand { get; }

        public CommandWrapper UndoCloseTabCommand { get; }

        public CommandWrapper PasteTabCommand { get; }

        public CommandWrapper SelectRegionCommand { get; }

        public override void Dispose()
        {
            Log.Debug("Disposing viewmodel...");
            SaveConfig();

            foreach (var mainWindowTabViewModel in TabsList)
            {
                mainWindowTabViewModel.Dispose();
            }

            base.Dispose();

            Log.Debug("Viewmodel disposed");
        }

        private IObservable<bool> RegisterSelectRegionHotkey()
        {
            var selectRegionHotkeyTrigger = hotkeyTriggerFactory.Create().AddTo(Anchors);
            selectRegionHotkeyTrigger.SuppressKey = true;
            selectRegionHotkeyTrigger.HotkeyMode = HotkeyMode.Hold;
            selectRegionHotkeyTrigger.IsActive = false;

            Observable.Merge(configProvider.ListenTo(x => x.RegionSelectHotkey).ToUnit())
                .Select(
                    () => new
                    {
                        RegionSelectHotkey = hotkeyConverter.ConvertFromString(configProvider.ActualConfig.RegionSelectHotkey)
                    })
                .DistinctUntilChanged()
                .WithPrevious((prev, curr) => new {prev, curr})
                .Subscribe(
                    cfg =>
                    {
                        Log.Debug($"Setting new {nameof(selectRegionHotkeyTrigger)} configuration, {cfg.prev.DumpToTextRaw()} => {cfg.curr.DumpToTextRaw()}");
                        selectRegionHotkeyTrigger.Hotkey = cfg.curr.RegionSelectHotkey;
                    },
                    Log.HandleException)
                .AddTo(Anchors);

            return selectRegionHotkeyTrigger.WhenAnyValue(x => x.IsActive).DistinctUntilChanged();
        }

        private async void SelectRegionCommandExecuted()
        {
            Log.Debug($"Requesting screen Region from {regionSelectorService}...");
            WindowState = WindowState.Minimized;
            var result = await regionSelectorService.SelectRegion();

            if (result?.IsValid ?? false)
            {
                Log.Debug($"ScreenRegion selection result: {result}");
                var newTabProperties = new OverlayAuraProperties
                {
                    WindowMatch = new WindowMatchParams
                    {
                        Title = result.Window.Title
                    },
                    IsEnabled = true,
                    OverlayBounds = result.AbsoluteSelection,
                    SourceRegionBounds = result.Selection,
                    MaintainAspectRatio = true
                };
                Log.Info($"Quick-Creating new tab using {newTabProperties.DumpToTextRaw()} args...");
                var viewModel = CreateAndAddTab(newTabProperties);
                if (viewModel.Model.Overlay.UnlockWindowCommand.CanExecute(null))
                {
                    viewModel.Model.Overlay.UnlockWindowCommand.Execute(null);
                }
            }
            else
            {
                Log.Debug("ScreenRegion selection cancelled/failed");
            }
        }

        private async Task OpenAppDataDirectory()
        {
            await Task.Run(
                () =>
                {
                    Log.Debug($"Opening App directory: {AppArguments.Instance.AppDataDirectory}");
                    if (!Directory.Exists(AppArguments.Instance.AppDataDirectory))
                    {
                        Log.Debug($"App directory does not exist, creating dir: {AppArguments.Instance.AppDataDirectory}");
                        Directory.CreateDirectory(AppArguments.Instance.AppDataDirectory);
                    }

                    Process.Start(ExplorerExecutablePath, AppArguments.Instance.AppDataDirectory);
                });
        }

        private bool CopyTabToClipboardCommandCanExecute()
        {
            return selectedTab != null;
        }

        private void CopyTabToClipboardExecuted()
        {
            Guard.ArgumentIsTrue(() => CopyTabToClipboardCommandCanExecute());

            Log.Debug($"Copying tab {selectedTab}...");

            var cfg = selectedTab.Model.Properties;
            var data = configSerializer.Compress(cfg);
            clipboardManager.SetText(data);
        }

        private bool UndoCloseTabCommandCanExecute()
        {
            return !recentlyClosedQueries.IsEmpty;
        }

        private void UndoCloseTabCommandExecuted()
        {
            if (!UndoCloseTabCommandCanExecute())
            {
                return;
            }

            var closedAuraProperties = recentlyClosedQueries.PopBack();
            CreateAndAddTab(closedAuraProperties);
            UndoCloseTabCommand.RaiseCanExecuteChanged();
        }

        private void PasteTabCommandExecuted()
        {
            using var unused = new OperationTimer(elapsed => Log.Debug($"{nameof(PasteTabCommand)} took {elapsed.TotalMilliseconds:F0}ms"));

            var content = "";
            try
            {
                content = clipboardManager.GetText() ?? "";
                content = content.Trim();
                var cfg = configSerializer.Decompress<OverlayAuraProperties>(content);
                CreateAndAddTab(cfg);
            }
            catch (Exception e)
            {
                MessageBox.Content = string.IsNullOrWhiteSpace(content)
                    ? "<Clipboard is empty>"
                    : content;
                MessageBox.IsOpen = true;
                MessageBox.ContentHint = "Item has invalid format";
                MessageBox.Title = "Failed to insert new item";
                throw new FormatException($"Failed to parse/get clipboard content:\n{content}", e);
            }
        }

        private bool DuplicateTabCommandCanExecute()
        {
            return selectedTab != null;
        }

        private void DuplicateTabCommandExecuted()
        {
            Guard.ArgumentIsTrue(() => DuplicateTabCommandCanExecute());

            var cfg = selectedTab.Model.Properties;
            CreateAndAddTab(cfg);
        }

        private bool CloseTabCommandCanExecute(IOverlayAuraViewModel tab)
        {
            return tab != null;
        }

        private void CloseTabCommandExecuted(IOverlayAuraViewModel tab)
        {
            using var unused = new OperationTimer(elapsed => Log.Debug($"{nameof(CloseTabCommand)} took {elapsed.TotalMilliseconds:F0}ms"));
            Guard.ArgumentNotNull(tab, nameof(tab));

            Log.Debug($"Removing tab {tab}...");

            var items = positionMonitor.Items.ToArray();
            var tabIdx = items.IndexOf(tab);
            if (tabIdx > 0)
            {
                var tabToSelect = items[tabIdx - 1];
                Log.Debug($"Selecting neighbour tab {tabToSelect}...");
                SelectedTab = tabToSelect;
            }

            tabsListSource.Remove(tab);

            var cfg = tab.Model.Properties;
            recentlyClosedQueries.PushBack(cfg);
            UndoCloseTabCommand.RaiseCanExecuteChanged();

            tab.Dispose();
        }

        private void SaveConfig()
        {
            using var unused = new OperationTimer(elapsed => Log.Debug($"{nameof(SaveConfig)} took {elapsed.TotalMilliseconds:F0}ms"));
            Log.Debug($"Saving config (provider: {configProvider})...");

            var config = PrepareConfig();
            configProvider.Save(config);
        }

        private EyeAurasConfig PrepareConfig()
        {
            using var unused = new OperationTimer(elapsed => Log.Debug($"{nameof(PrepareConfig)} took {elapsed.TotalMilliseconds:F0}ms"));

            var positionedItems = positionMonitor.Items.ToArray();
            Log.Debug($"Preparing config, tabs count: {positionedItems.Length}");
            var config = configProvider.ActualConfig.CloneJson();

            config.Auras = tabsListSource.Items
                .Select(x => new {Idx = positionedItems.IndexOf(x), Tab = x})
                .OrderBy(x => x.Idx)
                .Select(x => x.Tab)
                .Select(tab => tab.Model.Properties)
                .ToArray();

            config.MainWindowBounds = new Rect(Left, Top, Width, Height);
            config.ListWidth = ListWidth.Value;
            return config;
        }

        private void LoadConfig()
        {
            using var unused = new OperationTimer(elapsed => Log.Debug($"{nameof(LoadConfig)} took {elapsed.TotalMilliseconds:F0}ms"));

            Log.Debug($"Loading config (provider: {configProvider})...");

            var config = configProvider.ActualConfig;

            Log.Debug($"Received configuration DTO:\r\n{config.DumpToText()}");

            var desktopHandle = UnsafeNative.GetDesktopWindow();
            var systemInformation = new
            {
                SystemInformation.MonitorCount,
                SystemInformation.VirtualScreen,
                MonitorBounds = UnsafeNative.GetMonitorBounds(desktopHandle),
                MonitorInfo = UnsafeNative.GetMonitorInfo(desktopHandle)
            };
            Log.Debug($"Current SystemInformation: {systemInformation.DumpToTextRaw()}");

            if (UnsafeNative.IsOutOfBounds(config.MainWindowBounds, systemInformation.MonitorBounds))
            {
                var screenCenter = UnsafeNative.GetPositionAtTheCenter(systemInformation.MonitorBounds, config.MainWindowBounds.Size);
                Log.Warn(
                    $"Main window is out of screen bounds(screen: {systemInformation.MonitorBounds}, main window bounds: {config.MainWindowBounds}) , resetting Location to {screenCenter}");
                config.MainWindowBounds = new Rect(screenCenter, config.MainWindowBounds.Size);
            }

            foreach (var auraProperties in config.Auras.EmptyIfNull())
            {
                CreateAndAddTab(auraProperties);
            }

            Left = config.MainWindowBounds.Left;
            Top = config.MainWindowBounds.Top;
            Width = config.MainWindowBounds.Width;
            Height = config.MainWindowBounds.Height;

            ListWidth = config.ListWidth <= 0 || double.IsNaN(config.ListWidth)
                ? GridLength.Auto
                : new GridLength(config.ListWidth, GridUnitType.Pixel);

            Log.Debug($"Successfully loaded config, tabs count: {TabsList.Count}");
        }

        private void OnTabOrderChanged(OrderChangedEventArgs args)
        {
            var existingItems = tabsListSource.Items.ToList();
            var newItems = args.NewOrder.OfType<IOverlayAuraViewModel>().ToList();

            Log.Debug(
                $"Source ordering:\n\tSource: {string.Join(" => ", existingItems.Select(x => x.Model.Name))}\n\tView: {string.Join(" => ", newItems.Select(x => x.Model.Name))}");
            configUpdateSubject.OnNext(Unit.Default);
        }

        private IOverlayAuraViewModel CreateAndAddTab(OverlayAuraProperties tabProperties)
        {
            using var unused = new OperationTimer(elapsed => Log.Debug($"CreateNewTab operation took {elapsed.TotalMilliseconds:F0}ms"));

            Log.Debug($"Adding new tab using config {tabProperties.DumpToTextRaw()}...");

            var auraModel = auraModelFactory.Create();
            var auraViewModel = auraViewModelFactory.Create(auraModel);
            var auraCloseController = new CloseController<IOverlayAuraViewModel>(auraViewModel, () => CloseTabCommandExecuted(auraViewModel));
            tabsListSource.Add(auraViewModel);

            auraModel.SetCloseController(auraCloseController);

            auraModel.Properties = tabProperties;

            return auraViewModel;
        }

        private void AddNewCommandExecuted(OverlayAuraProperties tabProperties)
        {
            try
            {
                var newTab = CreateAndAddTab(tabProperties);
                if (newTab.Model.Overlay.UnlockWindowCommand.CanExecute(null))
                {
                    newTab.Model.Overlay.UnlockWindowCommand.Execute(null);
                }
            }
            catch (Exception e)
            {
                Log.HandleUiException(e);
            }
        }
    }
}
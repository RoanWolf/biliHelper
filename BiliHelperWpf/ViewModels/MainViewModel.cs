using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BiliHelperWpf.Models;
using BiliHelperWpf.Services;

namespace BiliHelperWpf.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly BiliService _biliService = new();
    private readonly AiReadService _aiReadService = new();
    private readonly AuthService _authService = new();
    private CancellationTokenSource? _cts;

    // 当前视频 read.json 的内存缓存——避免切换分P 时每次同步读磁盘+全量解析（曾导致 UI 卡约 1 秒）
    private List<ReadPartData>? _readPartsCache;
    // 缓存是否已加载（区分「未加载」与「已加载但为空」：无 read.json 时也只需读一次盘）
    private bool _readPartsCacheLoaded;
    // 缓存是否正在后台加载中（加载完成前其它分P 切换不重复触发读盘）
    private bool _readPartsCacheLoading;

    // ── Cookie 状态 ─────────────────────────────────────────
    private CookieState _cookieState;
    public CookieState CookieState
    {
        get => _cookieState;
        private set => SetProperty(ref _cookieState, value);
    }

    /// <summary>cookie 状态描述（tooltip）。</summary>
    private string _cookieTooltip = string.Empty;
    public string CookieTooltip
    {
        get => _cookieTooltip;
        private set => SetProperty(ref _cookieTooltip, value);
    }

    // ── 输入 ────────────────────────────────────────────────
    private string _url = string.Empty;
    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
                OnPropertyChanged(nameof(CanFetch));
        }
    }

    // ── 状态 ────────────────────────────────────────────────
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanFetch));
        }
    }

    public bool CanFetch => !IsBusy && !string.IsNullOrWhiteSpace(Url);

    private string _progressText = string.Empty;
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    private bool _hasProgress;
    public bool HasProgress
    {
        get => _hasProgress;
        set => SetProperty(ref _hasProgress, value);
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    // ── 数据 ────────────────────────────────────────────────
    private BiliVideoInfo? _videoInfo;
    public BiliVideoInfo? VideoInfo
    {
        get => _videoInfo;
        set
        {
            if (SetProperty(ref _videoInfo, value))
                OnPropertyChanged(nameof(HasVideoInfo));
        }
    }

    public bool HasVideoInfo => VideoInfo != null;

    private PartInfo? _selectedPart;
    public PartInfo? SelectedPart
    {
        get => _selectedPart;
        set
        {
            var oldPart = _selectedPart;
            if (SetProperty(ref _selectedPart, value))
            {
                // 记住旧分P 最后使用的 TAB
                if (oldPart != null)
                    _partTabMemory[oldPart.PartNumber] = _selectedSubtitleTab;

                // 恢复新分P 记忆中的 TAB（无记忆则回默认第一个 tab「原始字幕」）
                if (value != null)
                {
                    var tab = _partTabMemory.TryGetValue(value.PartNumber, out var t)
                        ? t
                        : TabOriginal;
                    if (tab != _selectedSubtitleTab)
                        SelectedSubtitleTab = tab;
                }

                EnsurePartEntriesLoaded(value);
                ScheduleFilter();
                OnPropertyChanged(nameof(IsOriginalSubtitleVisible));
                OnPropertyChanged(nameof(IsFullTextVisible));
                OnPropertyChanged(nameof(ShowAiReadEmptyCard));
                OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
                LoadAiReadForSelectedPart();
                RefreshFeishuUi();
            }
        }
    }

    // ── 搜索 ────────────────────────────────────────────────
    private string _searchText = string.Empty;
    private DispatcherTimer? _filterDebounce;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ScheduleFilter();
        }
    }

    private ObservableCollection<SubtitleEntry> _filteredEntries = [];
    public ObservableCollection<SubtitleEntry> FilteredEntries
    {
        get => _filteredEntries;
        private set => SetProperty(ref _filteredEntries, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// 当前总加载进度文本（如 "3/29 分P"）。
    /// </summary>
    private string _loadProgress = string.Empty;
    public string LoadProgress
    {
        get => _loadProgress;
        set => SetProperty(ref _loadProgress, value);
    }

    /// <summary>
    /// 是否正在流式加载（已收到 meta，但还没 complete）。
    /// </summary>
    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    // ── 字幕 TAB ────────────────────────────────────────────
    /// <summary>TAB 索引常量。</summary>
    public const int TabOriginal = 0;
    public const int TabFullText = 1;

    /// <summary>按分P 记忆最后使用的 TAB 索引（分P号 → tab）。</summary>
    private readonly Dictionary<int, int> _partTabMemory = new();

    private int _selectedSubtitleTab;
    public int SelectedSubtitleTab
    {
        get => _selectedSubtitleTab;
        set
        {
            if (SetProperty(ref _selectedSubtitleTab, value))
            {
                // 记录当前分P 最后使用的 TAB（切分P 时恢复用）
                if (SelectedPart != null)
                    _partTabMemory[SelectedPart.PartNumber] = value;

                OnPropertyChanged(nameof(IsOriginalSubtitleVisible));
                OnPropertyChanged(nameof(IsFullTextVisible));
            }
        }
    }

    public bool IsOriginalSubtitleVisible => SelectedPart != null && SelectedSubtitleTab == TabOriginal;
    public bool IsFullTextVisible => SelectedPart != null && SelectedSubtitleTab == TabFullText;

    // ── AI 整理（原始全文 TAB）────────────────────────────────
    private List<Paragraph> _aiParagraphs = [];

    /// <summary>当前分P 的 AI 整理段落（只读展示用）。</summary>
    public IReadOnlyList<Paragraph> AiParagraphs => _aiParagraphs;

    public bool HasAiParagraphs => _aiParagraphs.Count > 0;

    /// <summary>
    /// 是否显示未整理空卡片（有选中分P 且该分P 尚未 AI 整理）。
    /// </summary>
    public bool ShowAiReadEmptyCard => SelectedPart != null && !HasAiParagraphs;

    // ── AI 整理并发状态（按分P 维护，支持多个分P 并行整理）──
    private readonly Dictionary<int, CancellationTokenSource> _aiReadCtsMap = new();
    private readonly Dictionary<int, string> _aiReadProgress = new();
    private readonly HashSet<int> _aiReadBusyParts = new();

    /// <summary>AI 整理并发闸门：最多 5 个子进程同时调用 DeepSeek，其余排队等位。</summary>
    private const int MaxConcurrentAiRead = 5;
    private readonly SemaphoreSlim _aiReadGate = new(MaxConcurrentAiRead, MaxConcurrentAiRead);

    /// <summary>
    /// 当前选中分P 是否正在 AI 整理（按钮隐藏/进度显示依据）。
    /// </summary>
    public bool IsCurrentPartAiReadBusy =>
        SelectedPart != null && _aiReadBusyParts.Contains(SelectedPart.PartNumber);

    /// <summary>
    /// 兼容属性：当前选中分P 是否正在 AI 整理（XAML 按钮隐藏绑定用）。
    /// </summary>
    public bool IsAiReadBusy => IsCurrentPartAiReadBusy;

    public bool CanAiRead =>
        SelectedPart != null && VideoInfo != null
        && !IsCurrentPartAiReadBusy && !HasAiParagraphs;

    private string _aiReadStatus = string.Empty;
    public string AiReadStatus
    {
        get => _aiReadStatus;
        set => SetProperty(ref _aiReadStatus, value);
    }

    private bool _aiReadError;
    public bool AiReadError
    {
        get => _aiReadError;
        set => SetProperty(ref _aiReadError, value);
    }

    private string _aiReadErrorMessage = string.Empty;
    public string AiReadErrorMessage
    {
        get => _aiReadErrorMessage;
        set => SetProperty(ref _aiReadErrorMessage, value);
    }

    // ── 飞书同步状态（按分P 维护；串行闸门保证同一时刻一个 feishu 子进程）──
    private readonly FeishuService _feishuService = new();
    private readonly SemaphoreSlim _feishuGate = new(1, 1);
    private readonly Dictionary<int, string> _feishuStatus = new();
    private readonly Dictionary<int, string> _feishuError = new();
    private readonly HashSet<int> _feishuSyncing = new();

    /// <summary>当前选中分P 的飞书同步状态文本（🔄同步中 / ✓已同步 / ⚠失败）。</summary>
    private string _feishuSyncStatus = string.Empty;
    public string FeishuSyncStatus
    {
        get => _feishuSyncStatus;
        private set => SetProperty(ref _feishuSyncStatus, value);
    }

    private string _feishuSyncError = string.Empty;
    public string FeishuSyncError
    {
        get => _feishuSyncError;
        private set => SetProperty(ref _feishuSyncError, value);
    }

    public bool FeishuSyncFailed => !string.IsNullOrEmpty(_feishuSyncError);

    public bool IsCurrentPartFeishuSyncing =>
        SelectedPart != null && _feishuSyncing.Contains(SelectedPart.PartNumber);

    // ── 历史记录 ────────────────────────────────────────────
    private bool _isHistoryOpen;
    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        set => SetProperty(ref _isHistoryOpen, value);
    }

    public ObservableCollection<HistoryGroup> HistoryGroups { get; } = [];

    // ── 命令 ────────────────────────────────────────────────
    public ICommand FetchCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public ICommand LoadFromHistoryCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand AiReadCommand { get; }
    public ICommand FeishuRetryCommand { get; }

    public MainViewModel()
    {
        FetchCommand = new RelayCommand(async _ => await FetchAsync(), _ => CanFetch);
        ToggleHistoryCommand = new RelayCommand(async _ => await ToggleHistoryAsync());
        LoadFromHistoryCommand = new RelayCommand(async param =>
        {
            if (param is HistoryItem item)
                await LoadHistoryItem(item);
        });
        DeleteHistoryCommand = new RelayCommand(async param =>
        {
            if (param is HistoryItem item)
                await DeleteHistoryItemAsync(item);
        });
        AiReadCommand = new RelayCommand(async _ => await GenerateReadAsync(), _ => CanAiRead);
        FeishuRetryCommand = new RelayCommand(_ =>
        {
            if (SelectedPart != null && FeishuSyncFailed && !IsCurrentPartFeishuSyncing)
                AutoSyncToFeishuAsync(SelectedPart.PartNumber);
        });
    }

    /// <summary>
    /// 启动时探测 cookie 状态。返回是否已登录。
    /// </summary>
    public async Task<bool> RefreshCookieStatusAsync(CancellationToken ct = default)
    {
        var (state, message) = await _authService.CheckAsync(ct);
        ApplyCookieState(state, message);
        return CookieState == CookieState.Valid;
    }

    /// <summary>
    /// 打开扫码登录。成功后刷新 cookie 状态。
    /// 回调在后台线程触发，UI 需自行切回主线程。
    /// </summary>
    public Task<bool> LoginAsync(
        Action<string> onQr,
        Action<int> onStatus,
        Action<string>? onError = null,
        CancellationToken ct = default)
    {
        return _authService.LoginAsync(
            onQr,
            onStatus,
            _ => ApplyCookieState("valid", "B 站已登录"),
            onError ?? (_ => { }),
            ct);
    }

    /// <summary>
    /// 删除本地 cookie，状态置为未登录。
    /// </summary>
    public async Task DeleteCookiesAsync(CancellationToken ct = default)
    {
        bool deleted = await _authService.DeleteAsync(ct);
        App.Log($"MainViewModel: 删除 cookie 结果: {deleted}");
        CookieState = CookieState.None;
        CookieTooltip = "未登录 B 站";
    }

    private void ApplyCookieState(string state, string message)
    {
        CookieState = state switch
        {
            "valid" => CookieState.Valid,
            _ => CookieState.Invalid,
        };
        CookieTooltip = string.IsNullOrEmpty(message)
            ? (CookieState == CookieState.Valid ? "B 站已登录" : "B 站未登录")
            : message;
    }

    /// <summary>
    /// 打开/关闭历史记录抽屉。
    /// 先打开抽屉（动画流畅），再异步加载历史列表。
    /// </summary>
    private async Task ToggleHistoryAsync()
    {
        if (IsHistoryOpen)
        {
            // 关闭抽屉，不做任何 I/O
            IsHistoryOpen = false;
            return;
        }

        // 先清空旧数据，然后打开抽屉（滑入动画不卡）
        HistoryGroups.Clear();
        IsHistoryOpen = true;

        // 后台线程慢慢读取历史文件，不阻塞 UI
        var groups = await Task.Run(() => HistoryService.LoadGroups());

        // 切回 UI 线程填充列表
        foreach (var group in groups)
            HistoryGroups.Add(group);
    }

    /// <summary>
    /// 从历史记录加载视频。
    /// </summary>
    private async Task LoadHistoryItem(HistoryItem item)
    {
        // 关闭抽屉
        IsHistoryOpen = false;

        var info = HistoryService.LoadVideo(item.BvId);
        if (info == null)
        {
            HasError = true;
            ErrorMessage = $"无法加载历史数据: {item.BvId}";
            return;
        }

        // 清空当前状态
        ErrorMessage = string.Empty;
        HasError = false;
        HasProgress = false;
        ProgressText = string.Empty;
        SelectedPart = null;
        FilteredEntries = new ObservableCollection<SubtitleEntry>();
        ResetAiReadState();
        StatusMessage = $"📂 已从历史加载 · {info.Title} · 共 {info.TotalParts}P · {info.TotalSubtitleCount} 条字幕";

        // 赋值到 UI，null 再赋值强制刷新
        VideoInfo = null;
        VideoInfo = info;

        // 选中第一个有字幕的分P
        var firstWithSubs = info.Parts.FirstOrDefault(p => p.HasSubtitles);
        SelectPart(firstWithSubs ?? info.Parts.FirstOrDefault()!);
    }

    /// <summary>
    /// 删除一条历史记录（含本地文件），并从当前历史列表移除。
    /// 若删除的是当前加载的视频，同时清空当前展示。
    /// </summary>
    private async Task DeleteHistoryItemAsync(HistoryItem item)
    {
        var confirm = MessageBox.Show(
            $"确定删除「{item.DisplayTitle}」吗？\n该操作会删除本地字幕与整理数据，不可恢复。",
            "删除历史记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        await Task.Run(() => HistoryService.Delete(item.BvId));

        foreach (var group in HistoryGroups)
            group.Items.Remove(item);

        if (VideoInfo?.BvId == item.BvId)
        {
            VideoInfo = null;
            SelectedPart = null;
            FilteredEntries = new ObservableCollection<SubtitleEntry>();
            ResetAiReadState();
            StatusMessage = "🗑 已删除该历史记录";
        }
        App.Log($"历史记录已删除: {item.BvId}");
    }

    /// <summary>
    /// 从 URL 中提取 BV ID。
    /// </summary>
    private static string? ExtractBvId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url, @"BV[\w]{10,}");
        return m.Success ? m.Value : null;
    }

    private async Task FetchAsync()
    {
        if (!CanFetch) return;

        // 重置状态
        ErrorMessage = string.Empty;
        HasError = false;
        HasProgress = true;
        IsStreaming = true;
        ProgressPercent = 0;
        ProgressText = "正在启动...";
        LoadProgress = string.Empty;
        VideoInfo = null;
        SelectedPart = null;
        FilteredEntries = new ObservableCollection<SubtitleEntry>();
        ResetAiReadState();

        // 先准备好空的 VideoInfo，onMeta 时赋值并绑定 UI，后续 onPart 直接追加
        var info = new BiliVideoInfo();


        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;

        App.Log($"FetchAsync 开始流式加载: {Url}");

        // 捕获 UI 线程调度器（onPart 等回调在后台线程调用，需要切回 UI 线程更新集合）
        var ui = App.Current.Dispatcher;

        try
        {
            var progress = new Progress<(string text, double percent)>();
            progress.ProgressChanged += (_, p) =>
            {
                ProgressText = p.text;
                if (p.percent >= 0)
                    ProgressPercent = p.percent;
            };

            await _biliService.FetchStreamAsync(
                Url,

                // onMeta: 收到元信息，立即绑定到 UI，让分P列表可见
                ev =>
                {
                    info.Title = ev.Title ?? "";
                    info.BvId = ev.BvId ?? "";
                    info.TotalParts = ev.TotalParts;
                    App.Log($"收到 meta: title={info.Title}, totalParts={info.TotalParts}");
                    ui.Invoke(() =>
                    {
                        VideoInfo = info;
                    });
                },

                // onPart: 每个分P 加载完成（后台线程调用，需切回 UI 线程）
                ev =>
                {
                    var part = new PartInfo
                    {
                        PartNumber = ev.PartNumber,
                        PartTitle = ev.PartTitle ?? "",
                        Duration = ev.Duration,
                        SubtitleCount = ev.SubtitleCount,
                        SubtitleSource = ev.SubtitleSource,
                        SubtitleLang = ev.SubtitleLang,
                        EntriesLoaded = true, // 流式逐P 到达即完整，无需懒加载
                    };

                    if (ev.Entries != null)
                    {
                        foreach (var entry in ev.Entries)
                            part.Entries.Add(entry);
                    }

                    // 记录视频封面（视频级，取第一个非空；供飞书文档封面）
                    if (string.IsNullOrEmpty(info.CoverUrl) && !string.IsNullOrEmpty(ev.CoverUrl))
                        info.CoverUrl = ev.CoverUrl;

                    // 记录 UP 主名（视频级，取第一个非空；供飞书卡片消息作者）
                    if (string.IsNullOrEmpty(info.Uploader) && !string.IsNullOrEmpty(ev.Uploader))
                        info.Uploader = ev.Uploader;

                    // 切回 UI 线程再操作 ObservableCollection 和 UI 属性
                    ui.Invoke(() =>
                    {
                        info.Parts.Add(part);
                        // 第一个分P 自动选中（不管有没有字幕）
                        if (SelectedPart == null)
                            SelectPart(part);
                        LoadProgress = $"已加载 {info.Parts.Count}/{info.TotalParts} 分P";
                    });

                    App.Log($"收到 P{ev.PartNumber}: {ev.SubtitleCount} 条字幕");
                },

                // onComplete: 全部完成（后台线程调用，需切回 UI 线程）
                ev =>
                {
                    info.Status = ev.Status ?? "empty";

                    ui.Invoke(async () =>
                    {
                        IsStreaming = false;
                        HasProgress = false;

                        // 保存当前选中，null 再赋值强制刷新 UI（状态灯、总字幕数）
                        var savedPart = SelectedPart;
                        VideoInfo = null;
                        VideoInfo = info;

                        // 恢复选中
                        if (savedPart != null)
                            SelectPart(savedPart);
                        else if (info.Parts.Count > 0)
                            SelectPart(info.Parts[0]);

                        // 保存到历史记录（后台线程，不阻塞 UI）
                        await Task.Run(() => HistoryService.SaveFromVideoInfo(info));

                        StatusMessage = $"✅ 已加载 · {info.Title} · 共 {info.TotalParts}P · {info.TotalSubtitleCount} 条字幕";
                    });

                    App.Log($"流式加载完成: status={info.Status}, parts={info.TotalParts}, subs={info.TotalSubtitleCount}");
                },

                // onError: 非致命错误（后台线程调用，需切回 UI 线程）
                msg =>
                {
                    ui.Invoke(() =>
                    {
                        HasError = true;
                        ErrorMessage = msg;
                    });
                },

                progress,
                ct
            );
        }
        catch (OperationCanceledException)
        {
            // 如果已经加载了部分数据，保留它们
            if (info.Parts.Count > 0)
            {
                info.Status = "partial";
                VideoInfo = info;
                var first = info.Parts.FirstOrDefault(p => p.HasSubtitles) ?? info.Parts[0];
                SelectPart(first);
                StatusMessage = $"⏹ 已中断 · 已加载 {info.Parts.Count}/{info.TotalParts} 分P";
            }
            else
            {
                StatusMessage = "已取消";
            }
            App.Log("流式加载被取消");
        }
        catch (Exception ex)
        {
            // 如果已经加载了部分数据，保留它们
            if (info.Parts.Count > 0)
            {
                info.Status = "partial";
                VideoInfo = info;
                var first = info.Parts.FirstOrDefault(p => p.HasSubtitles) ?? info.Parts[0];
                SelectPart(first);
                StatusMessage = $"⚠️ 部分加载 · 已加载 {info.Parts.Count}/{info.TotalParts} 分P";
            }

            HasError = true;
            ErrorMessage = ex.Message;
            StatusMessage = "❌ 加载失败";
            App.Log($"流式加载异常: {ex}");
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
            IsStreaming = false;
        }
    }

    private void SelectPart(PartInfo part)
    {
        SelectedPart = part;
    }

    /// <summary>
    /// 历史加载时按需懒加载某分P 的字幕 entries（fetch 流式路径已在 onPart 填充完毕）。
    /// entries 未加载（且该分P 有字幕）时，后台读 parts/NNN.json 填充后刷新筛选。
    /// </summary>
    private async void EnsurePartEntriesLoaded(PartInfo? part)
    {
        if (part == null || part.EntriesLoaded)
            return;

        var bvId = VideoInfo?.BvId;
        if (string.IsNullOrEmpty(bvId))
            return;

        part.EntriesLoaded = true; // 先置位，防并发重入

        var partNumber = part.PartNumber;
        List<SubtitleEntry>? entries;
        try
        {
            entries = await Task.Run(() => HistoryService.LoadPartEntries(bvId, partNumber));
        }
        catch (Exception ex)
        {
            App.Log($"懒加载分P {partNumber} 异常: {ex.Message}");
            entries = null;
        }

        if (entries == null)
        {
            part.EntriesLoaded = false; // 失败允许下次重试
            return;
        }

        // await 后续体在 UI 线程（SynchronizationContext），安全地填充并刷新
        part.Entries.Clear();
        foreach (var e in entries)
            part.Entries.Add(e);

        if (SelectedPart == part)
            ScheduleFilter();
    }

    /// <summary>
    /// 飞书自动同步：AI 整理成功后把该分P 同步为飞书云文档（串行闸门排队，同一时刻一个子进程）。
    /// 配置未启用/不完整时静默跳过，不影响整理结果；失败可经 FeishuRetryCommand 重试。
    /// </summary>
    private async void AutoSyncToFeishuAsync(int partNumber)
    {
        var settings = FeishuSettingsStore.Load();
        if (!settings.IsComplete)
        {
            App.Log($"飞书同步跳过: enabled={settings.Enabled}, 配置不完整");
            return;
        }
        if (VideoInfo == null || _feishuSyncing.Contains(partNumber))
            return;

        var bvId = VideoInfo.BvId;
        var bvDir = HistoryService.FindVideoDirectory(bvId);
        if (bvDir == null)
        {
            App.Log($"飞书同步失败: 找不到历史目录 bvId={bvId}");
            UpdateFeishuUi(partNumber, "⚠ 飞书同步失败", "找不到历史目录");
            return;
        }

        _feishuSyncing.Add(partNumber);
        UpdateFeishuUi(partNumber, "🔄 同步到飞书中...", "");
        OnPropertyChanged(nameof(IsCurrentPartFeishuSyncing));
        var ui = App.Current.Dispatcher;
        try
        {
            await _feishuGate.WaitAsync();
            await Task.Run(async () =>
            {
                await _feishuService.SyncPartAsync(
                    bvDir, partNumber, settings,
                    onStatus: step => ui.Invoke(() => UpdateFeishuUi(partNumber, $"🔄 {step}", "")),
                    onComplete: url =>
                    {
                        App.Log($"飞书同步完成: bvId={bvId}, part={partNumber}, url={url}");
                        ui.Invoke(() => UpdateFeishuUi(partNumber, "✓ 已同步到飞书", ""));
                    },
                    onError: msg =>
                    {
                        App.Log($"飞书同步失败: bvId={bvId}, part={partNumber}, {msg}");
                        ui.Invoke(() => UpdateFeishuUi(partNumber, "⚠ 飞书同步失败", msg));
                    });
            });
        }
        catch (OperationCanceledException)
        {
            UpdateFeishuUi(partNumber, "⚠ 飞书同步失败", "已取消");
        }
        catch (Exception ex)
        {
            App.Log($"飞书同步异常: bvId={bvId}, part={partNumber}, {ex}");
            UpdateFeishuUi(partNumber, "⚠ 飞书同步失败", ex.Message);
        }
        finally
        {
            _feishuGate.Release();
            _feishuSyncing.Remove(partNumber);
            OnPropertyChanged(nameof(IsCurrentPartFeishuSyncing));
        }
    }

    /// <summary>更新某分P 的飞书同步状态；若为当前选中分P 则同步刷新 UI 属性。</summary>
    private void UpdateFeishuUi(int partNumber, string status, string error)
    {
        _feishuStatus[partNumber] = status;
        if (string.IsNullOrEmpty(error))
            _feishuError.Remove(partNumber);
        else
            _feishuError[partNumber] = error;

        if (SelectedPart?.PartNumber != partNumber)
            return;
        FeishuSyncStatus = status;
        FeishuSyncError = error;
        OnPropertyChanged(nameof(FeishuSyncFailed));
    }

    /// <summary>切换分P 时刷新飞书状态属性（当前选中分P 视角）。</summary>
    private void RefreshFeishuUi()
    {
        if (SelectedPart == null)
        {
            FeishuSyncStatus = string.Empty;
            FeishuSyncError = string.Empty;
        }
        else
        {
            FeishuSyncStatus = _feishuStatus.TryGetValue(SelectedPart.PartNumber, out var s) ? s : string.Empty;
            FeishuSyncError = _feishuError.TryGetValue(SelectedPart.PartNumber, out var e) ? e : string.Empty;
        }
        OnPropertyChanged(nameof(FeishuSyncFailed));
        OnPropertyChanged(nameof(IsCurrentPartFeishuSyncing));
    }

    /// <summary>
    /// 重置 AI 整理状态（新加载视频/历史时调用）。
    /// </summary>
    private void ResetAiReadState()
    {
        // 取消所有进行中的 AI 整理
        foreach (var cts in _aiReadCtsMap.Values)
            cts.Cancel();
        _aiReadCtsMap.Clear();
        _aiReadProgress.Clear();
        _aiReadBusyParts.Clear();
        // 清空分P TAB 记忆（加载新视频/历史时避免旧分P 记忆串台）
        _partTabMemory.Clear();
        // 清空 read.json 内存缓存（切换视频/加载历史后旧缓存失效）
        _readPartsCache = null;
        _readPartsCacheLoaded = false;
        _readPartsCacheLoading = false;
        App.Log("AI 整理状态已重置（取消进行中的任务）");
        _aiParagraphs = [];
        AiReadStatus = string.Empty;
        AiReadError = false;
        AiReadErrorMessage = string.Empty;
        // 清空飞书同步状态（加载新视频/历史后旧状态失效）
        _feishuStatus.Clear();
        _feishuError.Clear();
        _feishuSyncing.Clear();
        FeishuSyncStatus = string.Empty;
        FeishuSyncError = string.Empty;
        OnPropertyChanged(nameof(AiParagraphs));
        OnPropertyChanged(nameof(HasAiParagraphs));
        OnPropertyChanged(nameof(ShowAiReadEmptyCard));
        OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
        OnPropertyChanged(nameof(CanAiRead));
    }

    /// <summary>
    /// 切换分P 时从 read.json 加载对应分P 的已整理段落。
    /// 未整理过则显示空状态（等待手动点击 AI 整理）。
    /// </summary>
    private void LoadAiReadForSelectedPart()
    {
        if (SelectedPart == null || VideoInfo == null)
        {
            _aiParagraphs = [];
            AiReadStatus = string.Empty;
            AiReadError = false;
            AiReadErrorMessage = string.Empty;
        }
        else
        {
            var partNumber = SelectedPart.PartNumber;
            var bvId = VideoInfo.BvId;

            // 正在整理中：显示进度，不读缓存
            if (_aiReadBusyParts.Contains(partNumber))
            {
                _aiParagraphs = [];
                AiReadStatus = _aiReadProgress.TryGetValue(partNumber, out var p)
                    ? p
                    : "正在启动 AI 整理...";
                AiReadError = false;
                AiReadErrorMessage = string.Empty;
                App.Log($"AI 阅读数据加载: bvId={bvId}, part={partNumber}, 正在整理中");
            }
            else if (!_readPartsCacheLoaded)
            {
                // 缓存未就绪：首次触发后台读取（只触发一次），期间显示"加载中"占位，
                // 避免每次切换分P 都在 UI 线程同步读磁盘+全量解析（曾导致 UI 卡约 1 秒）。
                if (!_readPartsCacheLoading)
                    LoadReadCacheAsync(partNumber);

                _aiParagraphs = [];
                AiReadStatus = "正在加载 AI 整理记录...";
                AiReadError = false;
                AiReadErrorMessage = string.Empty;
                App.Log($"AI 阅读数据加载: bvId={bvId}, part={partNumber}, 缓存加载中");
            }
            else
            {
                // 缓存已就绪，纯内存查询
                var part = _readPartsCache?.FirstOrDefault(p => p.PartNumber == partNumber);
                _aiParagraphs = part?.Paragraphs ?? [];
                AiReadStatus = part != null
                    ? $"✅ 已整理 · {part.Paragraphs.Count} 个段落"
                    : string.Empty;
                AiReadError = false;
                AiReadErrorMessage = string.Empty;
                App.Log($"AI 阅读数据加载: bvId={bvId}, part={partNumber}, "
                        + $"{(part != null ? $"已缓存 {part.Paragraphs.Count} 段" : "无缓存，待手动整理")}");
            }
        }
        OnPropertyChanged(nameof(AiParagraphs));
        OnPropertyChanged(nameof(HasAiParagraphs));
        OnPropertyChanged(nameof(ShowAiReadEmptyCard));
        OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
        OnPropertyChanged(nameof(CanAiRead));
    }

    /// <summary>
    /// 后台线程读取当前视频 read.json 到内存缓存（整个视频只触发一次）。
    /// 完成后若当前选中分P 仍是发起加载的分P，直接填充展示；否则重新走
    /// <see cref="LoadAiReadForSelectedPart"/> 刷新当前分P（防快速切分P 竞态）。
    /// </summary>
    private async void LoadReadCacheAsync(int requestedPartNumber)
    {
        if (_readPartsCacheLoaded || _readPartsCacheLoading)
            return;

        _readPartsCacheLoading = true;

        var bvId = VideoInfo?.BvId;
        if (string.IsNullOrEmpty(bvId))
        {
            _readPartsCacheLoading = false;
            return;
        }

        List<ReadPartData>? loaded;
        try
        {
            loaded = await Task.Run(() => HistoryService.LoadReadParts(bvId));
        }
        catch (Exception ex)
        {
            App.Log($"加载阅读数据异常: bvId={bvId}, {ex.Message}");
            loaded = null;
        }

        // async void 续体回到 UI 线程（WPF SynchronizationContext），与 LoadAiReadForSelectedPart 同线程，无竞态
        _readPartsCache = loaded;
        _readPartsCacheLoaded = true;
        _readPartsCacheLoading = false;

        if (SelectedPart?.PartNumber == requestedPartNumber)
        {
            // 用户仍停留在发起加载的分P
            var part = loaded?.FirstOrDefault(p => p.PartNumber == requestedPartNumber);
            _aiParagraphs = part?.Paragraphs ?? [];
            AiReadStatus = part != null
                ? $"✅ 已整理 · {part.Paragraphs.Count} 个段落"
                : string.Empty;
            AiReadError = false;
            AiReadErrorMessage = string.Empty;
            App.Log($"AI 阅读数据缓存已加载: bvId={bvId}, part={requestedPartNumber}, "
                    + $"{(part != null ? $"{part.Paragraphs.Count} 段" : "无缓存")}");
        }
        else
        {
            // 加载期间用户切到了其它分P，重新加载当前分P 展示
            App.Log($"AI 阅读数据缓存已加载: bvId={bvId}（期间已切换分P，刷新当前分P）");
            LoadAiReadForSelectedPart();
        }

        OnPropertyChanged(nameof(AiParagraphs));
        OnPropertyChanged(nameof(HasAiParagraphs));
        OnPropertyChanged(nameof(ShowAiReadEmptyCard));
        OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
        OnPropertyChanged(nameof(CanAiRead));
    }

    /// <summary>
    /// AI 整理当前分P：调用 DeepSeek，成功后保存 read.json 并刷新展示。
    /// 期间不阻塞 UI，可随时取消（IsAiReadBusy 期间按钮禁用）。
    /// </summary>
    private async Task GenerateReadAsync()
    {
        var part = SelectedPart;
        if (part == null || VideoInfo == null)
        {
            App.Log($"AI 整理被跳过: part空={part == null}, video空={VideoInfo == null}");
            return;
        }

        var partNumber = part.PartNumber;
        if (_aiReadBusyParts.Contains(partNumber))
        {
            App.Log($"AI 整理被跳过: P{partNumber} 已在整理中");
            return;
        }

        App.Log($"AI 整理开始: bvId={VideoInfo.BvId}, part={partNumber}, subtitleCount={part.SubtitleCount}");

        // 每个分P 独立的 CTS，互不取消，支持并发
        var cts = new CancellationTokenSource();
        _aiReadCtsMap[partNumber] = cts;
        _aiReadBusyParts.Add(partNumber);
        _aiReadProgress[partNumber] = "正在启动 AI 整理...";

        AiReadError = false;
        AiReadErrorMessage = string.Empty;
        AiReadStatus = "正在启动 AI 整理...";
        OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
        OnPropertyChanged(nameof(CanAiRead));

        var ui = App.Current.Dispatcher;
        bool gateHeld = false;

        try
        {
            // 并发闸门：最多 5 个同时跑，其余排队；排队中被取消则不持许可
            await _aiReadGate.WaitAsync(cts.Token);
            gateHeld = true;

            var progress = new Progress<string>();
            progress.ProgressChanged += (_, text) =>
            {
                // 每P 进度存入字典；仅当前选中的分P 刷新状态文本
                _aiReadProgress[partNumber] = text;
                if (SelectedPart?.PartNumber == partNumber)
                    AiReadStatus = text;
            };

            await Task.Run(async () =>
            {
                App.Log($"AI 整理启动子进程: bvId={VideoInfo.BvId}, part={partNumber}");
                await _aiReadService.GenerateReadDataAsync(
                    VideoInfo!.BvId,
                    partNumber,
                    onComplete: paragraphs =>
                    {
                        App.Log($"AI 整理完成回调: bvId={VideoInfo.BvId}, part={partNumber}, 段落数={paragraphs.Count}");

                        // 无条件保存（SaveReadPart 内部加锁，并发安全）
                        var partData = new ReadPartData
                        {
                            PartNumber = partNumber,
                            SubtitleCount = part.SubtitleCount,
                            Paragraphs = paragraphs,
                        };
                        HistoryService.SaveReadPart(VideoInfo!.BvId, partData);
                        App.Log($"AI 阅读数据已保存: bvId={VideoInfo.BvId}, part={partNumber}, "
                                + $"段落数={partData.Paragraphs.Count}, 字幕条数={partData.SubtitleCount}");

                        // 飞书自动同步：整理成功 → 串行队列同步到飞书（配置未启用则静默跳过）
                        ui.Invoke(() => AutoSyncToFeishuAsync(partNumber));

                        // 仅当前选中的分P 才刷新 UI（在 UI 线程内校验，避免切分P 竞态串台）
                        ui.Invoke(() =>
                        {
                            // 无论当前选中哪个分P，都更新内存缓存（经 UI 线程调度，与 LoadAiRead 同线程避免竞态）
                            _readPartsCache ??= new();
                            _readPartsCacheLoaded = true;
                            _readPartsCache.RemoveAll(p => p.PartNumber == partNumber);
                            _readPartsCache.Add(partData);

                            if (SelectedPart?.PartNumber != partNumber)
                                return;
                            _aiParagraphs = paragraphs;
                            OnPropertyChanged(nameof(AiParagraphs));
                            OnPropertyChanged(nameof(HasAiParagraphs));
                            OnPropertyChanged(nameof(ShowAiReadEmptyCard));
                            AiReadStatus = $"✅ 已整理 · {paragraphs.Count} 个段落";
                        });
                    },
                    onError: msg =>
                    {
                        App.Log($"AI 整理失败回调: bvId={VideoInfo.BvId}, part={partNumber}, 错误={msg}");
                        // 仅当前选中的分P 才刷新 UI（在 UI 线程内校验，避免切分P 竞态串台）
                        ui.Invoke(() =>
                        {
                            if (SelectedPart?.PartNumber != partNumber)
                                return;
                            AiReadError = true;
                            AiReadErrorMessage = msg;
                            AiReadStatus = "❌ 整理失败";
                        });
                    },
                    progress: progress,
                    ct: cts.Token);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (SelectedPart?.PartNumber == partNumber)
                AiReadStatus = "已取消";
            App.Log($"AI 整理被取消: bvId={VideoInfo?.BvId}, part={partNumber}");
        }
        catch (Exception ex)
        {
            if (SelectedPart?.PartNumber == partNumber)
            {
                AiReadError = true;
                AiReadErrorMessage = ex.Message;
                AiReadStatus = "❌ 整理失败";
            }
            App.Log($"AI 整理异常: bvId={VideoInfo?.BvId}, part={partNumber}, {ex}");
        }
        finally
        {
            // 并发闸门：释放许可，让排队的分P 补位（未拿到许可不 Release）
            if (gateHeld)
                _aiReadGate.Release();

            // 清理该分P 的并发状态
            _aiReadCtsMap.Remove(partNumber);
            _aiReadBusyParts.Remove(partNumber);
            _aiReadProgress.Remove(partNumber);

            // 若用户仍停留在该分P，刷新按钮/进度并重新加载缓存
            if (SelectedPart?.PartNumber == partNumber)
            {
                OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
                OnPropertyChanged(nameof(CanAiRead));
                LoadAiReadForSelectedPart();
            }

            App.Log($"AI 整理流程结束: bvId={VideoInfo?.BvId}, part={partNumber}");
        }
    }

    /// <summary>
    /// 防抖调度筛选：搜索输入 / 切换分P 后短暂停顿再执行，
    /// 避免连续输入时每次都全量过滤（大分P 数万条字幕会卡 UI）。
    /// </summary>
    private void ScheduleFilter()
    {
        if (_filterDebounce == null)
        {
            _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _filterDebounce.Tick += async (_, _) =>
            {
                _filterDebounce.Stop();
                await ApplyFilterAsync();
            };
        }
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    /// <summary>
    /// 后台线程过滤 + 整体替换集合（避免 UI 线程全量过滤卡顿）。
    /// 捕获当前分P 与搜索词快照，完成时若用户已切走则丢弃结果，由最新一轮调度接管。
    /// </summary>
    private async Task ApplyFilterAsync()
    {
        var part = SelectedPart;
        var keyword = (SearchText ?? string.Empty).Trim();

        if (part == null)
        {
            FilteredEntries = new ObservableCollection<SubtitleEntry>();
            OnPropertyChanged(nameof(FilteredCount));
            return;
        }

        // 整体替换集合（而非逐条 Clear/Add）：只触发一次 CollectionChanged，
        // ListBox 虚拟化下一次重建，避免几千条字幕的通知风暴卡顿 UI。
        // 虚拟化由 XAML 侧 ScrollViewer.CanContentScroll + VirtualizingPanel 四件套承担。
        List<SubtitleEntry> result;
        if (keyword.Length == 0)
        {
            result = new List<SubtitleEntry>(part.Entries);
        }
        else
        {
            var kw = keyword;
            result = await Task.Run(() =>
                part.Entries
                    .Where(e => e.Text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList());
        }

        // 完成时用户已切走（分P 或搜索词变化）则丢弃本次结果
        if (SelectedPart != part || (SearchText ?? string.Empty).Trim() != keyword)
            return;

        FilteredEntries = new ObservableCollection<SubtitleEntry>(result);
        OnPropertyChanged(nameof(FilteredCount));
    }

    public string FilteredCount
    {
        get
        {
            if (SelectedPart == null) return "";
            int total = SelectedPart.Entries.Count;
            int filtered = FilteredEntries.Count;
            return filtered == total
                ? $"共 {total} 条字幕"
                : $"筛选 {filtered} / {total} 条";
        }
    }
}

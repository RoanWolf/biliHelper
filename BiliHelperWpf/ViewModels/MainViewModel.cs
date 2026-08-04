using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliHelperWpf.Models;
using BiliHelperWpf.Services;

namespace BiliHelperWpf.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly BiliService _biliService = new();
    private readonly AiReadService _aiReadService = new();
    private CancellationTokenSource? _cts;

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

                ApplyFilter();
                OnPropertyChanged(nameof(IsOriginalSubtitleVisible));
                OnPropertyChanged(nameof(IsFullTextVisible));
                OnPropertyChanged(nameof(ShowAiReadEmptyCard));
                OnPropertyChanged(nameof(IsCurrentPartAiReadBusy));
                LoadAiReadForSelectedPart();
            }
        }
    }

    // ── 搜索 ────────────────────────────────────────────────
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public ObservableCollection<SubtitleEntry> FilteredEntries { get; } = [];

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
    public ICommand AiReadCommand { get; }

    public MainViewModel()
    {
        FetchCommand = new RelayCommand(async _ => await FetchAsync(), _ => CanFetch);
        ToggleHistoryCommand = new RelayCommand(async _ => await ToggleHistoryAsync());
        LoadFromHistoryCommand = new RelayCommand(async param =>
        {
            if (param is HistoryItem item)
                await LoadHistoryItem(item);
        });
        AiReadCommand = new RelayCommand(async _ => await GenerateReadAsync(), _ => CanAiRead);
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
        FilteredEntries.Clear();
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
        FilteredEntries.Clear();
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
                    };

                    if (ev.Entries != null)
                    {
                        foreach (var entry in ev.Entries)
                            part.Entries.Add(entry);
                    }

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
        App.Log("AI 整理状态已重置（取消进行中的任务）");
        _aiParagraphs = [];
        AiReadStatus = string.Empty;
        AiReadError = false;
        AiReadErrorMessage = string.Empty;
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

            // 正在整理中：显示进度，不读缓存
            if (_aiReadBusyParts.Contains(partNumber))
            {
                _aiParagraphs = [];
                AiReadStatus = _aiReadProgress.TryGetValue(partNumber, out var p)
                    ? p
                    : "正在启动 AI 整理...";
                AiReadError = false;
                AiReadErrorMessage = string.Empty;
                App.Log($"AI 阅读数据加载: bvId={VideoInfo.BvId}, part={partNumber}, 正在整理中");
            }
            else
            {
                var parts = HistoryService.LoadReadParts(VideoInfo.BvId);
                var part = parts?.FirstOrDefault(p => p.PartNumber == partNumber);
                _aiParagraphs = part?.Paragraphs ?? [];
                AiReadStatus = part != null
                    ? $"✅ 已整理 · {part.Paragraphs.Count} 个段落"
                    : string.Empty;
                AiReadError = false;
                AiReadErrorMessage = string.Empty;
                App.Log($"AI 阅读数据加载: bvId={VideoInfo.BvId}, part={partNumber}, "
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

        try
        {
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

                        // 仅当前选中的分P 才刷新 UI（在 UI 线程内校验，避免切分P 竞态串台）
                        ui.Invoke(() =>
                        {
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

    private void ApplyFilter()
    {
        if (SelectedPart == null)
        {
            FilteredEntries.Clear();
            OnPropertyChanged(nameof(FilteredCount));
            return;
        }

        var source = SelectedPart.Entries;

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // 无搜索时直接复用源集合引用，不产生任何 Add 开销
            if (FilteredEntries.Count == source.Count)
            {
                bool same = true;
                for (int i = 0; i < source.Count && same; i++)
                    if (FilteredEntries[i] != source[i])
                        same = false;
                if (same)
                {
                    OnPropertyChanged(nameof(FilteredCount));
                    return;
                }
            }
            FilteredEntries.Clear();
            for (int i = 0; i < source.Count; i++)
                FilteredEntries.Add(source[i]);
        }
        else
        {
            var keyword = SearchText.Trim();
            FilteredEntries.Clear();
            foreach (var e in source)
            {
                if (e.Text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    FilteredEntries.Add(e);
            }
        }

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

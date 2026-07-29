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
            if (SetProperty(ref _selectedPart, value))
                ApplyFilter();
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

    // ── 历史记录 ────────────────────────────────────────────
    private bool _isHistoryOpen;
    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        set => SetProperty(ref _isHistoryOpen, value);
    }

    public ObservableCollection<HistoryItem> HistoryItems { get; } = [];

    // ── 命令 ────────────────────────────────────────────────
    public ICommand FetchCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public ICommand LoadFromHistoryCommand { get; }

    public MainViewModel()
    {
        FetchCommand = new RelayCommand(async _ => await FetchAsync(), _ => CanFetch);
        ToggleHistoryCommand = new RelayCommand(_ => ToggleHistory());
        LoadFromHistoryCommand = new RelayCommand(async param =>
        {
            if (param is HistoryItem item)
                await LoadHistoryItem(item);
        });
    }

    /// <summary>
    /// 打开/关闭历史记录抽屉。
    /// </summary>
    private void ToggleHistory()
    {
        if (!IsHistoryOpen)
        {
            // 打开时刷新列表
            var items = HistoryService.LoadIndex();
            HistoryItems.Clear();
            foreach (var item in items)
                HistoryItems.Add(item);
        }
        IsHistoryOpen = !IsHistoryOpen;
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

    private void ApplyFilter()
    {
        if (SelectedPart == null)
        {
            FilteredEntries.Clear();
            return;
        }

        var source = SelectedPart.Entries;

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            if (FilteredEntries.Count != source.Count ||
                FilteredEntries.Zip(source, (a, b) => a == b).Any(m => !m))
            {
                FilteredEntries.Clear();
                foreach (var e in source)
                    FilteredEntries.Add(e);
            }
        }
        else
        {
            var keyword = SearchText.Trim().ToLower();
            var filtered = source
                .Where(e => e.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

            FilteredEntries.Clear();
            foreach (var e in filtered)
                FilteredEntries.Add(e);
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

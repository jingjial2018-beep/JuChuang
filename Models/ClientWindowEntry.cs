using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace JuChuang.Models;

public enum ClientKind
{
    WeChat,
    WhatsApp
}

/// <summary>
/// 当前客户端账号会话的生命周期状态。Handle 可以变化，但账号身份应尽量保持稳定。
/// </summary>
public enum ClientSessionState
{
    Starting,
    Login,
    Main,
    Rebinding,
    Exited
}

/// <summary>
/// 窗口发现层对客户端顶层窗口用途的分类。
/// </summary>
public enum ClientWindowRole
{
    Unknown,
    Login,
    Main
}

/// <summary>
/// 微信账号资料解析状态。与窗口在线/嵌入状态分离。
/// </summary>
public enum AccountProfileState
{
    NotApplicable,
    Pending,
    Ready,
    ReadyFromCache,
    ReadyWithoutAvatar,
    WaitingForLocalData,
    CompatibilityIssue,
    Error
}

/// <summary>
/// 角标显示形态：决定聚窗左侧头像角标的具体外观。
/// </summary>
public enum AlertDisplayMode
{
    /// <summary>无角标（隐藏整个 UnreadBadge）。</summary>
    None,
    /// <summary>纯红点（找到徽章但数字不清，无法显示准确数字）。</summary>
    Dot,
    /// <summary>显示具体数字（高置信度识别）。</summary>
    Count,
}

public sealed class ClientWindowEntry : INotifyPropertyChanged
{
    private IntPtr _handle;
    private string _displayName;
    private string _status = "在线";
    private bool _isOnline = true;
    private bool _isAttached;
    private string? _identityKey;
    private bool _hasCustomName;
    private string? _avatarPath;
    private string? _clientVersion;
    private int _messageAlertCount;
    private AlertDisplayMode _alertDisplayMode = AlertDisplayMode.None;
    private bool _pendingClearAfterDetection;
    private string? _accountId;
    private ClientSessionState _sessionState;
    private bool _isManuallyDetached;
    private AccountProfileState _profileState;
    private string? _profileDiagnosticCode;
    private string? _profileDiagnosticDetail;

    public ClientWindowEntry(
        IntPtr handle,
        int processId,
        string processName,
        string windowTitle,
        ClientKind kind,
        string displayName,
        string? clientVersion = null,
        ClientSessionState sessionState = ClientSessionState.Main)
    {
        _handle = handle;
        ProcessId = processId;
        ProcessName = processName;
        WindowTitle = windowTitle;
        Kind = kind;
        _displayName = displayName;
        _clientVersion = clientVersion;
        _sessionState = sessionState;
        _profileState = kind == ClientKind.WeChat
            ? AccountProfileState.Pending
            : AccountProfileState.NotApplicable;
        LastSeenUtc = DateTime.UtcNow;
    }

    public IntPtr Handle
    {
        get => _handle;
        set => SetField(ref _handle, value);
    }

    public int ProcessId { get; set; }

    public string ProcessName { get; set; }

    public string WindowTitle { get; set; }

    public ClientKind Kind { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetField(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public bool IsAttached
    {
        get => _isAttached;
        set => SetField(ref _isAttached, value);
    }

    /// <summary>
    /// 聚窗内部稳定身份键，用于保存自定义名称等本地设置。
    /// </summary>
    public string? IdentityKey
    {
        get => _identityKey;
        set => SetField(ref _identityKey, value);
    }

    /// <summary>
    /// 客户端自身的账号 ID（微信为本地解析出的 account/wxid 目录标识）。
    /// 与 HWND 解耦：登录页切主窗口或 HWND 重建时保留。
    /// </summary>
    public string? AccountId
    {
        get => _accountId;
        set => SetField(ref _accountId, value);
    }

    public ClientSessionState SessionState
    {
        get => _sessionState;
        set => SetField(ref _sessionState, value);
    }

    /// <summary>
    /// 用户是否明确要求该账号保持为桌面独立窗口。
    /// 这是账号会话状态，不跟随 HWND；客户端重建窗口时仍保持弹出状态。
    /// </summary>
    public bool IsManuallyDetached
    {
        get => _isManuallyDetached;
        set => SetField(ref _isManuallyDetached, value);
    }

    public AccountProfileState ProfileState
    {
        get => _profileState;
        set
        {
            if (SetField(ref _profileState, value))
            {
                OnPropertyChanged(nameof(ProfileStatusText));
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    public string? ProfileDiagnosticCode
    {
        get => _profileDiagnosticCode;
        set
        {
            if (SetField(ref _profileDiagnosticCode, value))
            {
                OnPropertyChanged(nameof(ProfileDiagnosticText));
            }
        }
    }

    public string? ProfileDiagnosticDetail
    {
        get => _profileDiagnosticDetail;
        set
        {
            if (SetField(ref _profileDiagnosticDetail, value))
            {
                OnPropertyChanged(nameof(ProfileDiagnosticText));
            }
        }
    }

    public string ProfileStatusText => ProfileState switch
    {
        AccountProfileState.Pending => "资料识别中",
        AccountProfileState.Ready => string.Empty,
        AccountProfileState.ReadyFromCache => "使用资料缓存",
        AccountProfileState.ReadyWithoutAvatar => "昵称已识别 · 头像待缓存",
        AccountProfileState.WaitingForLocalData => "等待本地资料",
        AccountProfileState.CompatibilityIssue => "资料解析待适配",
        AccountProfileState.Error => "资料读取失败",
        _ => string.Empty
    };

    public string StatusSummary => Kind == ClientKind.WeChat
        && ProfileState is not AccountProfileState.NotApplicable
        && !string.IsNullOrWhiteSpace(ProfileStatusText)
            ? $"{Status} · {ProfileStatusText}"
            : Status;

    public string ProfileDiagnosticText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProfileDiagnosticCode))
            {
                return string.IsNullOrWhiteSpace(ProfileDiagnosticDetail)
                    ? ProfileStatusText
                    : ProfileDiagnosticDetail!;
            }

            return string.IsNullOrWhiteSpace(ProfileDiagnosticDetail)
                ? ProfileDiagnosticCode!
                : $"{ProfileDiagnosticCode}: {ProfileDiagnosticDetail}";
        }
    }

    /// <summary>最近一次扫描确认当前 HWND 有效的时间。</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// HWND 暂时消失的起始时间。进程仍存活时给予短暂重绑定窗口，
    /// 避免 Login -> Main 或客户端自重建 HWND 时丢失账号卡片。
    /// </summary>
    public DateTime? WindowMissingSinceUtc { get; set; }

    /// <summary>
    /// 账号代际。退出登录/重新进入 Login 状态时递增，用来阻止旧的异步
    /// Profile 读取结果覆盖后来登录的新账号。
    /// </summary>
    public int AccountGeneration { get; set; }

    public bool HasCustomName
    {
        get => _hasCustomName;
        set => SetField(ref _hasCustomName, value);
    }

    public string? AvatarPath
    {
        get => _avatarPath;
        set
        {
            if (SetField(ref _avatarPath, value))
            {
                OnPropertyChanged(nameof(HasAvatar));
            }
        }
    }

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarPath);

    /// <summary>
    /// 从客户端左侧聊天入口角标识别出的总未读数。100 表示“99+”。
    /// </summary>
    public int MessageAlertCount
    {
        get => _messageAlertCount;
        set
        {
            var normalized = Math.Max(0, value);
            if (SetField(ref _messageAlertCount, normalized))
            {
                OnPropertyChanged(nameof(HasMessageAlert));
                OnPropertyChanged(nameof(MessageAlertText));
            }
        }
    }

    public string MessageAlertText => _messageAlertCount >= 100
        ? "99+"
        : _messageAlertCount.ToString();

    /// <summary>
    /// 角标显示形态（None/Dot/Count）。由检测流程根据置信度设置，
    /// 不应从外部直接赋值。
    /// </summary>
    public AlertDisplayMode AlertDisplayMode
    {
        get => _alertDisplayMode;
        set
        {
            if (SetField(ref _alertDisplayMode, value))
            {
                OnPropertyChanged(nameof(HasMessageAlert));
            }
        }
    }

    public bool HasMessageAlert => _alertDisplayMode != AlertDisplayMode.None;

    /// <summary>
    /// 用户点击账号后，是否等待下一次截图确认清零。
    /// true = 等待下一次检测，截图确认已读后才真正清零（避免误清）。
    /// </summary>
    public bool PendingClearAfterDetection
    {
        get => _pendingClearAfterDetection;
        set => SetField(ref _pendingClearAfterDetection, value);
    }

    public string? ClientVersion
    {
        get => _clientVersion;
        set
        {
            if (SetField(ref _clientVersion, value))
            {
                OnPropertyChanged(nameof(ClientLabel));
            }
        }
    }

    public string Glyph => Kind == ClientKind.WeChat ? "微" : "WA";

    public string ClientLabel => Kind == ClientKind.WeChat
        ? string.IsNullOrWhiteSpace(ClientVersion) ? "微信客户端" : $"微信 {ClientVersion}"
        : "桌面客户端";

    private static readonly Brush WeChatAccentBrush = CreateFrozenBrush(22, 185, 85);
    private static readonly Brush WhatsAppAccentBrush = CreateFrozenBrush(37, 211, 102);
    private static readonly Brush OnlineStatusBrush = CreateFrozenBrush(22, 163, 74);
    private static readonly Brush OfflineStatusBrush = CreateFrozenBrush(148, 163, 184);

    public Brush AccentBrush => Kind == ClientKind.WeChat ? WeChatAccentBrush : WhatsAppAccentBrush;

    public Brush StatusBrush => IsOnline ? OnlineStatusBrush : OfflineStatusBrush;

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

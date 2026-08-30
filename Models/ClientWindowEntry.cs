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

    public ClientWindowEntry(
        IntPtr handle,
        int processId,
        string processName,
        string windowTitle,
        ClientKind kind,
        string displayName,
        string? clientVersion = null)
    {
        _handle = handle;
        ProcessId = processId;
        ProcessName = processName;
        WindowTitle = windowTitle;
        Kind = kind;
        _displayName = displayName;
        _clientVersion = clientVersion;
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
        set => SetField(ref _status, value);
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

    public string? IdentityKey
    {
        get => _identityKey;
        set => SetField(ref _identityKey, value);
    }

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

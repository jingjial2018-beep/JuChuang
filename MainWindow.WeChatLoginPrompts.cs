namespace JuChuang;

public partial class MainWindow
{
    // RC5.6: ExternalWindowHost exclusively owns native duplicate-login
    // dialog visibility and position. Retain entry points for scan/call-site
    // compatibility, but NEVER show, hide or position dialogs here.
    private void SyncWeChatLoginPrompts() { }

    private void RestoreWeChatLoginPrompts() { }
}

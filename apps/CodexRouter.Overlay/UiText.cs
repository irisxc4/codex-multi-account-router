using System.Globalization;

namespace CodexRouter.Overlay;

public enum UiLanguage
{
    English,
    SimplifiedChinese,
    TraditionalChinese
}

public static class UiText
{
    public static UiLanguage Language { get; } = ResolveLanguage(CultureInfo.CurrentUICulture);

    public static UiLanguage ResolveLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.TraditionalChinese
                : UiLanguage.SimplifiedChinese;
        }
        return UiLanguage.English;
    }

    private static string T(string simplified, string traditional, string english) => Language switch
    {
        UiLanguage.SimplifiedChinese => simplified,
        UiLanguage.TraditionalChinese => traditional,
        _ => english
    };

    public static string RouterName => "Codex Router";
    public static string ExitApplication => T("退出程序", "結束程式", "Exit app");
    public static string Current => T("当前", "目前", "CURRENT");
    public static string Pin => T("切换", "切換", "SWITCH");
    public static string ModeAuto => T("自动", "自動", "AUTO");
    public static string ModePinned => T("手动", "手動", "MANUAL");
    public static string ModeOff => T("关闭", "關閉", "OFF");
    public static string NoAccountsTitle => T("还没有 Codex 账号", "還沒有 Codex 帳號", "No Codex accounts yet");
    public static string NoAccountsDescription => T(
        "使用 OpenAI 官方 Codex 授权登录。浏览器完成授权后，账号会自动添加，无需复制 Token。",
        "使用 OpenAI 官方 Codex 授權登入。瀏覽器完成授權後，帳號會自動新增，無需複製 Token。",
        "Sign in through official OpenAI Codex authorization. The account is added automatically after browser approval—no token copying.");
    public static string ConnectChatGpt => T("连接 ChatGPT", "連接 ChatGPT", "Connect ChatGPT");
    public static string AddChatGpt => T("+ ChatGPT", "+ ChatGPT", "+ ChatGPT");
    public static string AutoRoute => T("自动路由", "自動路由", "Auto route");
    public static string MigrateCurrent => T("迁移当前会话…", "遷移目前對話…", "Migrate current thread…");
    public static string CancelMigration => T("取消迁移", "取消遷移", "Cancel migration");
    public static string RetryMigration => T("重试失败的迁移", "重試失敗的遷移", "Retry failed migration");
    public static string DesktopIntegration => T("桌面集成", "桌面整合", "Desktop integration");
    public static string RouterOff => T("关闭 Router", "關閉 Router", "Router OFF");
    public static string EnableRouting => T("开启多账户路由", "開啟多帳號路由", "Enable multi-account routing");
    public static string DisableRouting => T("关闭多账户路由", "關閉多帳號路由", "Disable multi-account routing");
    public static string RoutingToggleToolTip => T("新会话将自动选择可用账号；关闭后使用原生单账号模式", "新對話將自動選擇可用帳號；關閉後使用原生單帳號模式", "New threads choose an available account; off keeps native single-account mode");
    public static string RoutingEnabledNeedsRestart => T("已开启多账户路由；桌面集成需要重启 Codex 后生效", "已開啟多帳號路由；桌面整合需要重新啟動 Codex 後生效", "Multi-account routing enabled; restart Codex for Desktop integration to apply");
    public static string AdvancedMaintenance => T("高级 / 维护", "進階 / 維護", "Advanced / maintenance");
    public static string EnableDesktopIntegration => T("配置桌面接管", "設定桌面接管", "Configure Desktop integration");
    public static string ReleaseDesktopIntegration => T("解除桌面接管", "解除桌面接管", "Release Desktop integration");
    public static string IntegrationStatus => T("桌面接管状态", "桌面接管狀態", "Desktop integration status");
    public static string AccountClickToolTip => T("点击切换账号；当前任务在其他账号时会创建续接任务，原任务保持不变", "點擊切換帳號；目前任務在其他帳號時會建立續接任務，原任務保持不變", "Click to switch accounts; a continuation is created when the current task belongs to another account, and the source stays unchanged");
    public static string MigrationHint => T("切换账号会自动续接当前任务；原任务始终保留", "切換帳號會自動續接目前任務；原任務始終保留", "Switching accounts automatically continues the current task; the source task is always preserved");
    public static string MigrationToolTip => T("选择目标账号后创建新对话，原对话保持不变", "選擇目標帳號後建立新對話，原對話保持不變", "Choose a target account to create a new thread; the source stays unchanged");
    public static string IntegrationOn => T("桌面集成 · 已开启", "桌面整合 · 已開啟", "Desktop integration · ON");
    public static string IntegrationOff => T("桌面集成 · 已关闭", "桌面整合 · 已關閉", "Desktop integration · OFF");
    public static string IntegrationConflict => T("桌面集成 · 冲突", "桌面整合 · 衝突", "Desktop integration · Conflict");
    public static string IntegrationShimMissing => T("桌面集成 · 组件缺失", "桌面整合 · 元件缺失", "Desktop integration · Shim missing");
    public static string IntegrationEnabledStatus => T("桌面集成已开启，重启 Codex 后生效", "桌面整合已開啟，重新啟動 Codex 後生效", "Desktop integration enabled; restart Codex to apply it");
    public static string IntegrationDisabledStatus => T("桌面集成已关闭，重启 Codex 后恢复原生模式", "桌面整合已關閉，重新啟動 Codex 後恢復原生模式", "Desktop integration disabled; restart Codex to return to native mode");
    public static string IntegrationEnableFailed(string? error) => T(
        $"桌面集成未开启：{error ?? "未知原因"}",
        $"桌面整合未開啟：{error ?? "未知原因"}",
        $"Desktop integration was not enabled: {error ?? "unknown reason"}");
    public static string IntegrationBinaryMissing => T("缺少 codex-route.exe，无法启用桌面集成。", "缺少 codex-route.exe，無法啟用桌面整合。", "codex-route.exe is missing; Desktop integration cannot be enabled.");
    public static string PinnedNewThreads => T("已选中账号；新任务将使用此账号", "已選中帳號；新任務將使用此帳號", "Account selected; new tasks will use this account");
    public static string AutoEnabled => T("已启用自动路由", "已啟用自動路由", "Auto route enabled");
    public static string RouterNativeMode => T("Router 已关闭 · 使用原生单账号模式", "Router 已關閉 · 使用原生單帳號模式", "Router off · native single-account pass-through");
    public static string QuotaNeverSynced => T("额度未同步", "額度未同步", "Quota not synced");
    public static string QuotaSyncInProgress => T("正在同步额度…", "正在同步額度…", "Syncing quota…");
    public static string QuotaSyncFailed => T("额度同步失败 · 稍后自动重试", "額度同步失敗 · 稍後自動重試", "Quota sync failed · retrying automatically");
    public static string QuotaSyncedAt(string time) => T($"已同步 {time}", $"已同步 {time}", $"Synced {time}");
    public static string QuotaStaleAt(string time) => T($"额度已过期 · {time}", $"額度已過期 · {time}", $"Quota stale · {time}");
    public static string UnknownQuotaLimit => T("专属额度", "專屬額度", "Special quota");
    public static string LoginMethodTitle => T("连接 ChatGPT", "連接 ChatGPT", "Connect ChatGPT");
    public static string LoginMethodDescription => T(
        "全部使用 OpenAI 官方 Codex 登录。Router 只隔离 CODEX_HOME，不读取或保存 ChatGPT Token。",
        "全部使用 OpenAI 官方 Codex 登入。Router 只隔離 CODEX_HOME，不讀取或儲存 ChatGPT Token。",
        "All methods use official OpenAI Codex sign-in. Router only isolates CODEX_HOME and never reads or stores ChatGPT tokens.");
    public static string DesktopLoginTitle => T("官方桌面登录", "官方桌面登入", "Official Desktop sign-in");
    public static string DesktopLoginDescription => T(
        "打开一个临时隔离的官方 Codex/ChatGPT 窗口。登录成功后窗口自动关闭并验证账号。",
        "開啟一個暫時隔離的官方 Codex/ChatGPT 視窗。登入成功後視窗自動關閉並驗證帳號。",
        "Opens a temporary isolated official Codex/ChatGPT window. It closes automatically after sign-in is verified.");
    public static string BrowserLoginTitle => T("官方浏览器登录 · 推荐", "官方瀏覽器登入 · 推薦", "Official browser sign-in · Recommended");
    public static string BrowserLoginDescription => T(
        "由官方 Codex app-server 生成登录地址并完成授权码交换。Router 原样打开地址，不生成、不修改 OAuth 参数。",
        "由官方 Codex app-server 產生登入地址並完成授權碼交換。Router 原樣開啟地址，不產生、不修改 OAuth 參數。",
        "The official Codex app-server creates the login URL and performs code exchange. Router opens the URL unchanged and never generates or rewrites OAuth parameters.");
    public static string DeviceLoginTitle => T("官方设备码登录", "官方裝置碼登入", "Official device-code sign-in");
    public static string DeviceLoginDescription => T(
        "使用官方 Codex app-server 的 chatgptDeviceCode 流程。Router 只显示官方返回的一次性设备码，不读取任何 Token。",
        "使用官方 Codex app-server 的 chatgptDeviceCode 流程。Router 只顯示官方回傳的一次性裝置碼，不讀取任何 Token。",
        "Uses the official Codex app-server chatgptDeviceCode flow. Router displays only the one-time device code returned by Codex and never reads auth tokens.");
    public static string LoginNetworkRouteTitle => T(
        "先明确选择这次登录的网络路径",
        "先明確選擇這次登入的網路路徑",
        "Choose the network route for this sign-in");
    public static string DirectLoginRoute => T("系统直连", "系統直連", "System direct connection");
    public static string LocalProxyRoute(string proxyUrl) => T(
        $"本机代理 {proxyUrl}",
        $"本機代理 {proxyUrl}",
        $"Local proxy {proxyUrl}");
    public static string LoginProxyDescription => T(
        "Router 不会自动切换网络。选择本机代理时，只给这一次官方 Codex 登录使用，不修改 Windows、Clash 或 Codex 全局设置；localhost 回调始终直连。",
        "Router 不會自動切換網路。選擇本機代理時，只給這一次官方 Codex 登入使用，不修改 Windows、Clash 或 Codex 全域設定；localhost 回呼一律直連。",
        "Router never changes routes automatically. If you choose the local proxy, it is used only for this official Codex sign-in; Windows, Clash, and global Codex settings stay unchanged, and localhost callbacks remain direct.");
    public static string LoginProxyOption(string proxyUrl) => T(
        $"本次登录使用本机代理 {proxyUrl}",
        $"本次登入使用本機代理 {proxyUrl}",
        $"Use local proxy {proxyUrl} for this sign-in");
    public static string CancelCurrentLogin => T("取消当前登录", "取消目前登入", "Cancel current sign-in");
    public static string LoginCanceled => T("当前登录已取消，可以重新选择登录方式", "目前登入已取消，可以重新選擇登入方式", "Current sign-in canceled; you can choose another sign-in method");
    public static string LoginCanceling => T("正在取消官方登录…", "正在取消官方登入…", "Canceling official sign-in…");
    public static string OfficialLoginStarting => T("正在向官方 Codex 请求安全登录地址…", "正在向官方 Codex 請求安全登入位址…", "Requesting a secure sign-in URL from official Codex…");
    public static string OfficialLoginSucceeded(string? email, string? planType) => T(
        $"{email ?? "ChatGPT 账号"} 已连接 · {planType ?? "套餐待识别"}",
        $"{email ?? "ChatGPT 帳號"} 已連線 · {planType ?? "方案待識別"}",
        $"{email ?? "ChatGPT account"} connected · {planType ?? "plan pending"}");
    public static string OfficialLoginFailed(string? error) => T(
        $"官方登录失败：{error ?? "未知错误"}",
        $"官方登入失敗：{error ?? "未知錯誤"}",
        $"Official sign-in failed: {error ?? "unknown error"}");
    public static string DesktopLoginOpened => T("已打开隔离的官方 Codex 登录窗口 · 在该窗口完成 ChatGPT 登录", "已開啟隔離的官方 Codex 登入視窗 · 請在該視窗完成 ChatGPT 登入", "Isolated official Codex sign-in window opened · finish ChatGPT sign-in there");
    public static string BrowserLoginOpened => T("已打开官方 Codex 登录页 · 完成授权后会自动验证账号", "已開啟官方 Codex 登入頁 · 完成授權後會自動驗證帳號", "Official Codex sign-in page opened · the account will be verified automatically after authorization");
    public static string DeviceLoginOpened(string userCode) => T(
        $"设备码 {userCode} · 已打开官方验证页，请登录后输入此码",
        $"裝置碼 {userCode} · 已開啟官方驗證頁，請登入後輸入此碼",
        $"Device code {userCode} · official verification page opened; sign in and enter this code");
    public static string DeviceCodeDialogTitle => T("Codex 设备码", "Codex 裝置碼", "Codex device code");
    public static string DeviceCodeDialogDescription => T(
        "官方 Codex 已生成一次性设备码。在刚打开的 OpenAI 验证页登录后输入下面的代码。",
        "官方 Codex 已產生一次性裝置碼。在剛開啟的 OpenAI 驗證頁登入後輸入下面的代碼。",
        "Official Codex generated a one-time device code. Sign in on the OpenAI verification page that just opened, then enter the code below.");
    public static string DeviceCodeSecurityHint => T(
        "代码只保存在当前窗口内存中，登录结束即释放；Router 不保存认证 Token。",
        "代碼只保存在目前視窗記憶體中，登入結束即釋放；Router 不儲存認證 Token。",
        "The code exists only in this window's memory and is released when sign-in ends; Router does not store auth tokens.");
    public static string CopyDeviceCode => T("复制设备码", "複製裝置碼", "Copy device code");
    public static string Close => T("关闭", "關閉", "Close");
    public static string SessionImportTitle => T("导入 ChatGPT 网页账号", "匯入 ChatGPT 網頁帳號", "Import ChatGPT web account");
    public static string SessionImportDescription => T(
        "不再走 Codex OAuth。先在浏览器中使用目标 ChatGPT 账号登录，再把 /api/auth/session 复制到剪贴板。Router 只用其中的 access token 完成一次 AgentIdentity 注册。",
        "不再走 Codex OAuth。先在瀏覽器中使用目標 ChatGPT 帳號登入，再把 /api/auth/session 複製到剪貼簿。Router 只用其中的 access token 完成一次 AgentIdentity 註冊。",
        "This does not use Codex OAuth. Sign in to the target ChatGPT account in your browser, copy /api/auth/session to the clipboard, and Router will use its access token once to register an AgentIdentity.");
    public static string SessionImportStepTitle => T("1. 获取当前 ChatGPT 网页 Session", "1. 取得目前 ChatGPT 網頁 Session", "1. Get the current ChatGPT web session");
    public static string SessionImportStepDescription => T(
        "点击打开 Session 页面，确认浏览器里是你要添加的账号，然后 Ctrl+A、Ctrl+C，再点“从剪贴板读取”。",
        "點擊開啟 Session 頁面，確認瀏覽器裡是你要新增的帳號，然後 Ctrl+A、Ctrl+C，再點「從剪貼簿讀取」。",
        "Open the Session page, confirm the browser is signed in to the account you want, press Ctrl+A and Ctrl+C, then choose Read from clipboard.");
    public static string OpenSessionPage => T("打开 Session 页面", "開啟 Session 頁面", "Open Session page");
    public static string ReadSessionClipboard => T("从剪贴板读取", "從剪貼簿讀取", "Read from clipboard");
    public static string SessionNotLoaded => T("尚未读取 Session JSON", "尚未讀取 Session JSON", "Session JSON not loaded");
    public static string SessionClipboardEmpty => T("剪贴板里没有可用文本", "剪貼簿裡沒有可用文字", "The clipboard does not contain usable text");
    public static string SessionClipboardInvalid(string error) => T(
        $"不是可用的 ChatGPT Session：{error}",
        $"不是可用的 ChatGPT Session：{error}",
        $"Not a usable ChatGPT session: {error}");
    public static string SessionLoaded(string? email, string planType, DateTimeOffset? expiresAt) => T(
        $"已识别：{email ?? "ChatGPT 账号"} · {planType} · 有效期至 {expiresAt?.ToLocalTime().ToString("HH:mm") ?? "未知"}",
        $"已識別：{email ?? "ChatGPT 帳號"} · {planType} · 有效期至 {expiresAt?.ToLocalTime().ToString("HH:mm") ?? "未知"}",
        $"Recognized: {email ?? "ChatGPT account"} · {planType} · expires {expiresAt?.ToLocalTime().ToString("HH:mm") ?? "unknown"}");
    public static string SessionPageOpenFailed(string error) => T($"打开 Session 页面失败：{error}", $"開啟 Session 頁面失敗：{error}", $"Could not open Session page: {error}");
    public static string SessionProxyDescription => T(
        "这里选择的是该账号后续官方 Codex worker 的网络出口，不修改 Windows 或 Clash 全局设置。检测到本机代理时必须明确选择。",
        "這裡選擇的是該帳號後續官方 Codex worker 的網路出口，不修改 Windows 或 Clash 全域設定。偵測到本機代理時必須明確選擇。",
        "This route is used by this account's official Codex worker as well as AgentIdentity registration. It does not change Windows or Clash globally. If a local proxy is detected, choose explicitly.");
    public static string SessionSecurityNotice => T(
        "安全边界：Router 不读取浏览器 Cookie、不保存网页 access token / refresh token / 密码。导入成功后只在该账号的 Windows Credential Manager 中保留官方 Codex 支持的 AgentIdentity 私钥与元数据；若剪贴板仍是这份 Session JSON，会自动清空。",
        "安全邊界：Router 不讀取瀏覽器 Cookie、不儲存網頁 access token / refresh token / 密碼。匯入成功後只在該帳號的 Windows Credential Manager 中保留官方 Codex 支援的 AgentIdentity 私鑰與中繼資料；若剪貼簿仍是這份 Session JSON，會自動清空。",
        "Security boundary: Router never reads browser cookies and does not persist web access tokens, refresh tokens, or passwords. After import, only the official Codex AgentIdentity key and metadata remain in Windows Credential Manager; the clipboard is cleared if it still contains this Session JSON.");
    public static string ImportSessionAccount => T("导入账号", "匯入帳號", "Import account");
    public static string SessionImportWorking => T("正在注册 Codex AgentIdentity 并验证账号…", "正在註冊 Codex AgentIdentity 並驗證帳號…", "Registering Codex AgentIdentity and verifying the account…");
    public static string SessionImportSucceeded(string? email, string? planType) => T(
        $"{email ?? "ChatGPT 账号"} 已导入 · {planType ?? "未知套餐"}",
        $"{email ?? "ChatGPT 帳號"} 已匯入 · {planType ?? "未知方案"}",
        $"{email ?? "ChatGPT account"} imported · {planType ?? "unknown plan"}");
    public static string LoginWaitEnded => T("登录等待已结束", "登入等待已結束", "Login wait ended");
    public static string NoCurrentThread => T("当前没有可迁移的 Router 会话", "目前沒有可遷移的 Router 對話", "No current routed thread is available to migrate");
    public static string NoMigrationTarget => T("没有其他可用账号作为迁移目标", "沒有其他可用帳號作為遷移目標", "No other enabled account is available as a migration target");
    public static string MigrationPollingStopped => T("迁移状态检查已停止", "遷移狀態檢查已停止", "Migration status polling stopped");
    public static string NewThread => T("新会话", "新對話", "new thread");
    public static string UnknownError => T("未知错误", "未知錯誤", "unknown error");
    public static string NoAccountsConfigured => T("尚未配置账号", "尚未設定帳號", "No accounts configured");
    public static string NoActiveAccount => T("没有活动账号", "沒有活動帳號", "No active account");
    public static string NotSignedIn => T("未登录", "未登入", "Not signed in");
    public static string WaitingForCodex => T("等待 Codex", "等待 Codex", "Waiting for Codex");
    public static string PositionSaveFailed => T("位置保存失败", "位置儲存失敗", "Position could not be saved");

    public static string MigrationDialogTitle => T("迁移当前会话", "遷移目前對話", "Migrate current thread");
    public static string MigrationDialogDescription => T(
        "将在目标账号上创建一个新会话，原会话保持不变。",
        "將在目標帳號上建立一個新對話，原對話保持不變。",
        "A new thread will be created on the target account. The source thread stays unchanged.");
    public static string Cancel => T("取消", "取消", "Cancel");
    public static string CreateMigratedThread => T("创建迁移会话", "建立遷移對話", "Create migrated thread");
    public static string AddAccountTitle => T("添加 Codex 账号", "新增 Codex 帳號", "Add Codex account");
    public static string AddAccount => T("添加账号", "新增帳號", "Add account");
    public static string LocalAliasDescription => T("为这个 Codex 配置设置一个本地名称。", "為這個 Codex 設定檔設定一個本機名稱。", "Give this Codex profile a local name.");
    public static string EditDisplayName => T("修改显示名", "修改顯示名稱", "Edit display name");
    public static string EditDisplayNameDescription => T(
        "仅修改 Router 本地显示，不会更改 OpenAI 账户资料。",
        "僅修改 Router 本機顯示，不會更改 OpenAI 帳戶資料。",
        "Changes Router's local label only; your OpenAI profile is unchanged.");
    public static string DisplayNameUpdated => T("显示名已更新", "顯示名稱已更新", "Display name updated");
    public static string Save => T("保存", "儲存", "Save");
    public static string Continue => T("继续", "繼續", "Continue");

    public static string LocalizeHealth(string health) => health switch
    {
        "Healthy" => T("正常", "正常", "Healthy"),
        "Draining" => T("额度偏低", "額度偏低", "Draining"),
        "Cooldown" => T("冷却中", "冷卻中", "Cooldown"),
        "AuthRequired" => T("需要登录", "需要登入", "Auth required"),
        "Degraded" => T("异常", "異常", "Degraded"),
        "Disabled" => T("已禁用", "已停用", "Disabled"),
        _ => T("未知", "未知", "Unknown")
    };

    public static string LoginConnected(string? email) => T(
        $"{email ?? "ChatGPT 账号"} 已连接",
        $"{email ?? "ChatGPT 帳號"} 已連接",
        $"{email ?? "ChatGPT account"} connected");
    public static string LoginFailed(string? error) => T($"登录失败：{error ?? UnknownError}", $"登入失敗：{error ?? UnknownError}", $"Login failed: {error ?? UnknownError}");
    public static string LoginStatusError(string error) => T($"登录状态错误：{error}", $"登入狀態錯誤：{error}", $"Login status error: {error}");
    public static string LocalizeMigrationState(string state) => state switch
    {
        "Pending" => T("等待中", "等待中", "Pending"),
        "Snapshotting" => T("正在读取原会话", "正在讀取原對話", "Snapshotting"),
        "CreatingDestination" => T("正在创建新会话", "正在建立新對話", "Creating destination"),
        "Linking" => T("正在关联", "正在關聯", "Linking"),
        "Completed" => T("已完成", "已完成", "Completed"),
        "Failed" => T("失败", "失敗", "Failed"),
        "Canceled" or "Cancelled" => T("已取消", "已取消", "Canceled"),
        _ => state
    };
    public static string MigrationState(string state) => T($"迁移：{LocalizeMigrationState(state)}", $"遷移：{LocalizeMigrationState(state)}", $"Migration: {LocalizeMigrationState(state)}");
    public static string MigrationStarted(string state) => T($"迁移已开始：{LocalizeMigrationState(state)}", $"遷移已開始：{LocalizeMigrationState(state)}", $"Migration started: {LocalizeMigrationState(state)}");
    public static string MigrationRetry(string state) => T($"迁移重试：{LocalizeMigrationState(state)}", $"遷移重試：{LocalizeMigrationState(state)}", $"Migration retry: {LocalizeMigrationState(state)}");
    public static string MigrationCompleted(string? threadId) => T($"已迁移 → {threadId ?? NewThread}", $"已遷移 → {threadId ?? NewThread}", $"Migrated → {threadId ?? NewThread}");
    public static string MigrationCompletedAndPinned(string? threadId) => T(
        "已切换账号，正在打开续接任务",
        "已切換帳號，正在開啟續接任務",
        "Account switched; opening the continuation task");
    public static string MigrationCompletedOpenFailed(string? threadId, string? error) => T(
        $"迁移已完成并已切换账号，但无法自动打开目标对话 {threadId ?? NewThread}：{error ?? UnknownError}",
        $"遷移已完成並已切換帳號，但無法自動開啟目標對話 {threadId ?? NewThread}：{error ?? UnknownError}",
        $"Migration and account switch completed, but the target thread could not be opened ({threadId ?? NewThread}): {error ?? UnknownError}");
    public static string MigrationPinFailed(string? error) => T(
        $"迁移已完成，但账号切换未提交：{error ?? UnknownError}",
        $"遷移已完成，但帳號切換未提交：{error ?? UnknownError}",
        $"Migration completed, but the account switch was not committed: {error ?? UnknownError}");
    public static string MigrationFailed(string? error) => T($"迁移失败：{error ?? UnknownError}", $"遷移失敗：{error ?? UnknownError}", $"Migration failed: {error ?? UnknownError}");
    public static string MigrationCanceled => T("迁移已取消", "遷移已取消", "Migration canceled");
    public static string MigrationStatusError(string error) => T($"迁移状态错误：{error}", $"遷移狀態錯誤：{error}", $"Migration status error: {error}");
}

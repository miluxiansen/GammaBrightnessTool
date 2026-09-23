using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// 一个可被外部脚本驱动的动作（P5 / L1 注册表的条目）。
/// </summary>
/// <param name="Id">稳定 ID，命名见设计稿 §15.4：&lt;域标签&gt;_&lt;功能&gt;&lt;类型后缀&gt;。</param>
/// <param name="Kind">控件类型（toggle / combo / slider / btn / box / header / nav / label）。</param>
/// <param name="Get">读取当前值（字符串形式）；不可读传 null。</param>
/// <param name="Set">设置值（字符串形式）；不可写传 null。</param>
/// <param name="Click">触发点击/动作；无动作传 null。</param>
/// <param name="Destructive">
/// true = 该动作会弹模态对话框（文件选择 / 确认框）或不可逆，<b>必须</b>带
/// <c>--allow-destructive</c> 才允许执行。
/// 为什么必须区分：模态对话框会占住 UI 线程，而管道命令是经 <c>Control.Invoke</c>
/// 在 UI 线程执行的 —— 脚本点一下「导出设置」就会让这次 Invoke 一直阻塞到用户关掉
/// 对话框为止，接口表现为"卡死"。
/// </param>
internal sealed record AutomationAction(
    string Id,
    string Kind,
    Func<string>? Get = null,
    Action<string>? Set = null,
    Action? Click = null,
    bool Destructive = false);

/// <summary>
/// 应用内动作注册表 + 命名管道服务端（P5 / L1）。
///
/// 要解决的问题（设计稿 §15.1）：本项目的交互控件几乎全是**自绘** ——
/// <c>ToggleSwitch</c> / <c>ThemedComboBox</c> / <c>SettingSlider</c> 都继承 <c>Control</c>
/// 且没有 Name、没有 UIA 模式；托盘菜单（<c>TrayMenuForm</c>）更是完全自绘窗体，
/// 在 Windows UI Automation 树里根本不存在。外部工具（FlaUI / WinAppDriver /
/// PowerShell UIA）拿不到它们，只能退回**坐标点击** —— 这正是自动化测试浪费时间
/// 与失败的根源。
///
/// 本类提供三层：
/// <list type="bullet">
/// <item>L0 控件标识：各控件补 <c>Name</c> + <c>AccessibleName</c>（见各控件注册点）。</item>
/// <item>L1 动作注册表：<c>ID → (get / set / click)</c> 三元组，注册在这里。</item>
/// <item>L2 CLI：<c>--auto &lt;command&gt;</c>，把命令转发进命名管道、结果 JSON 打印到 stdout。</item>
/// </list>
///
/// 线程模型：管道接收在线程池线程；所有动作经 <see cref="Execute"/> 用
/// <c>Control.Invoke</c> 切回 UI 线程执行 —— 自绘控件的状态只能在 UI 线程碰。
///
/// 安全边界（设计稿 §15.6）：
/// <list type="bullet">
/// <item>管道用 <see cref="PipeOptions.CurrentUserOnly"/>，仅当前用户可连，不成为提权面；</item>
/// <item>破坏性命令（<c>app.exit</c> 等）必须显式带 <c>--allow-destructive</c>；</item>
/// <item><b>永不</b>通过本接口重启/结束 explorer.exe（永久红线）。</item>
/// </list>
/// </summary>
internal sealed class AutomationBridge : IDisposable
{
    /// <summary>命名管道名（不含 <c>\\.\pipe\</c> 前缀，客户端会自动加）。</summary>
    internal const string PipeName = "GammaBrightnessTool_Automation";

    /// <summary>连接超时（ms）：够本机管道往返，又不会让脚本卡住。</summary>

    private static AutomationBridge? _instance;

    /// <summary>
    /// 进程内单例。首次访问即创建（不启动服务端 —— 必须先 <see cref="Start"/>）。
    /// </summary>
    internal static AutomationBridge Instance => _instance ??= new AutomationBridge();

    private readonly ConcurrentDictionary<string, AutomationAction> _actions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private Control? _uiAnchor;
    private Thread? _acceptThread;
    private volatile bool _running;

    private AutomationBridge() { }

    /// <summary>
    /// 注册一个动作。同 ID 重复注册覆盖旧项（页面重建后重新注册走这条路）。
    /// 只可在 UI 线程调用。
    /// </summary>
    internal static void Register(AutomationAction action) =>
        Instance._actions[action.Id] = action;

    /// <summary>
    /// P5：从 EDID 实例 ID 派生稳定的短标识（取型号段，非字母数字归一为 '_'），
    /// 供"每屏一行"类动作拼 ID（如 <c>Popup_RowB_SAC2466</c> / <c>Osd_RowB_SAC2466</c>）。
    /// 与设置页显示器页的 <c>Mon_&lt;型号&gt;_*</c> 命名保持同一来源。
    /// </summary>
    internal static string ShortIdFromEdid(string edid)
    {
        if (string.IsNullOrWhiteSpace(edid)) return "UNKNOWN";
        string[] parts = edid.Split('\\');
        string seg = parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : edid;
        var sb = new System.Text.StringBuilder(seg.Length);
        foreach (char ch in seg)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "UNKNOWN";
    }

    /// <summary>
    /// 批量清理某前缀的动作注册。页面重建前调用，避免陈旧 ID 仍指向已 Dispose 的控件。
    /// </summary>
    internal static void ClearActionsByPrefix(string prefix)
    {
        foreach (string key in Instance._actions.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                Instance._actions.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// 给一批控件补稳定 ID（L0）并按控件类型自动注册动作（L1），返回成功注册数。
    ///
    /// 设计原则：**只驱动控件本身，不复制任何业务逻辑** —— 控件的既有事件处理器负责
    /// 落业务。这样自动化路径与用户手点走的是同一条代码，不会出现"两套行为"分叉。
    ///
    /// 已知的「赋值不发事件」陷阱（已在控件侧补齐）：
    ///   * <c>SettingSlider.Value</c> 的 setter 不发事件 → 走 SetValueInteractive；
    ///   * <c>ThemedComboBox.SelectedIndex</c> 赋值会发 SelectedIndexChanged（值不变时不发）。
    /// </summary>
    internal static int Wire(params (Control Control, string Id)[] items) =>
        WireCore(items, destructive: false);

    /// <summary>同 <see cref="Wire"/>，但标为破坏性（需 <c>--allow-destructive</c> 才可执行）。</summary>
    internal static int WireDestructive(params (Control Control, string Id)[] items) =>
        WireCore(items, destructive: true);

    private static int WireCore((Control Control, string Id)[] items, bool destructive)
    {
        int ok = 0;
        foreach ((Control c, string id) in items)
        {
            if (c == null || string.IsNullOrEmpty(id)) continue;
            c.Name = id;
            c.AccessibleName = id;
            if (RegisterByType(c, id, destructive)) ok++;
        }
        return ok;
    }

    private static bool RegisterByType(Control c, string id, bool destructive)
    {
        switch (c)
        {
            case ToggleSwitch t:
                Register(new AutomationAction(id, "toggle",
                    Get: () => t.Checked ? "true" : "false",
                    Set: v => t.Checked = ParseBool(v),
                    Click: () => t.Checked = !t.Checked,
                    Destructive: destructive));
                return true;

            case SettingSlider s:
                Register(new AutomationAction(id, "slider",
                    Get: () => s.Value.ToString(CultureInfo.InvariantCulture),
                    Set: v =>
                    {
                        // 注意：.NET 的 float.TryParse **会**接受 "NaN"/"Infinity"（返回 true），
                        // 而 Math.Clamp(NaN) 结果仍是 NaN —— 会把滑块毒化成 NaN 并可能污染
                        // settings.json（2026-09-14 健壮性排查抓到，回读值 = NaN）。
                        // 所以必须用 IsFinite 拒绝非有限值。
                        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                            && float.IsFinite(f))
                        {
                            s.SetValueInteractive(f);
                        }
                    },
                    Destructive: destructive));
                return true;

            case ThemedComboBox cb:
                Register(new AutomationAction(id, "combo",
                    Get: () => cb.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    Set: v =>
                    {
                        // 2026-09-16：**禁用态不接受赋值** —— 与鼠标路径保持一致。
                        // 设置页的"暂停冻结罩"把色温预设 / 亮度挡位置为 Enabled=false
                        // （SettingsForm._disableLocked），此时用户点不动；但若自动化仍能
                        // 赋值，就会出现"控件外观变了、业务没变"的假象
                        // （接口盘点实测：暂停期间 ui.set:Bri_LevelCombo:3 让控件读数变成
                        //  3，而 Biz_Brightness 未变）。P5 的承诺是自动化与手点走同一条
                        // 路径，故此处必须同样短路。
                        if (!cb.Enabled) return;

                        // 越界值**直接忽略**（不写入）。
                        // 不能依赖 setter 的"越界归一为 -1"：那会把下拉框清成"无选中项"，
                        // 污染 UI 状态（2026-09-14 健壮性排查发现）。setter 的 -1 语义
                        // 保留给内部"重选同项"惯用法（ThemedComboBox.cs:102），不改。
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                            && i >= 0 && i < cb.Items.Count)
                        {
                            cb.SelectedIndex = i;
                        }
                    },
                    Destructive: destructive));
                return true;

            case HotKeyCaptureBox hk:
                // 热键录入框：语义值是 HotKey（格式化后的组合键串），不是 Text。
                // Set 走 CommitCapture（与用户按 √ 同一条路径），Click 进入录入态。
                // 必须放在 RoundedTextBox case 之前 —— 它继承自后者，首个匹配生效。
                Register(new AutomationAction(id, "box",
                    Get: () => hk.HotKey,
                    Set: v => hk.CommitCapture(v),
                    Click: () => hk.StartCapture(),
                    Destructive: destructive));
                return true;

            case RoundedTextBox tb:
                Register(new AutomationAction(id, "box",
                    Get: () => tb.Text,
                    Set: v => tb.Text = v,          // TextBox.Text 的 setter 会发 TextChanged
                    Destructive: destructive));
                return true;

            case RoundedButton b:
                Register(new AutomationAction(id, "btn",
                    Click: () => b.PerformClick(),
                    Destructive: destructive));
                return true;

            default:
                // 其余类型（RoundedCardPanel / HotKeyCaptureBox / ...）留给后续批次按需补
                OpLog.Log($"[auto] Wire: 暂不支持 {c.GetType().Name} (id={id})");
                return false;
        }
    }

    /// <summary>宽松布尔解析："true"/"1"/"on"/"yes" 为真，其余为假。</summary>
    private static bool ParseBool(string v) =>
        !string.IsNullOrEmpty(v) &&
        (v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("1", StringComparison.Ordinal) ||
         v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把 EDID 实例 ID（形如 <c>MONITOR\SAC2466\{GUID}</c>）转成可用于自动化 ID 的稳定短键。
    /// 取型号段（SAC2466）—— 这正是设计稿 §15.4 的示例 <c>Mon_SAC2466_CtrlToggle</c>；
    /// 解析不出时退化到 <c>#&lt;index&gt;</c>（§15.4 允许的回退，见"显示器用 EdidId（或退化到序号）"）。
    /// 非法字符（反斜杠/花括号/连字符等）替换为下划线，保证 ID 只含字母数字下划线。
    /// </summary>
    internal static string MonitorKey(string edidId, int index)
    {
        string key = "";
        if (!string.IsNullOrEmpty(edidId))
        {
            Match m = Regex.Match(edidId, @"MONITOR\\([^\\]+)\\", RegexOptions.IgnoreCase);
            if (m.Success) key = m.Groups[1].Value;
        }
        if (string.IsNullOrEmpty(key)) key = "#" + index;
        return Regex.Replace(key, "[^A-Za-z0-9_]", "_");
    }

    /// <summary>
    /// 启动管道服务端。必须在 UI 线程调用 —— <paramref name="uiAnchor"/> 是用于把
    /// 命令切回 UI 线程的 marshal 锚点（用托盘消息窗即可，它本就归属 UI 线程且有句柄）。
    /// </summary>
    internal void Start(Control? uiAnchor)
    {
        lock (_gate)
        {
            if (_running) return;
            if (uiAnchor == null || !uiAnchor.IsHandleCreated)
            {
                OpLog.Log("[auto] bridge not started: no usable UI anchor");
                return;
            }

            _uiAnchor = uiAnchor;
            _running = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "AutomationPipe"
            };
            _acceptThread.Start();
            OpLog.Log($"[auto] automation bridge listening on pipe '{PipeName}'");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }
        // 唤醒阻塞中的 WaitForConnection：连一次自己的管道，让循环发现 _running=false 退出。
        TryWakeAcceptLoop();
        _actions.Clear();
        _uiAnchor = null;
    }

    private static void TryWakeAcceptLoop()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(200);
        }
        catch
        {
            // 连不上也无妨：线程是后台线程，进程退出时自然回收
        }
    }

    /// <summary>
    /// 服务端接受循环。每轮：等一个连接 → 立刻创建下一个服务实例（保持连接窗口常开）
    /// → 把当前连接丢给线程池处理。maxNumberOfServerInstances=2 才允许这样预创建。
    /// </summary>
    private void AcceptLoop()
    {
        while (_running)
        {
            NamedPipeServerStream? server;
            try
            {
                server = CreateServer();
                server.WaitForConnection();
            }
            catch (Exception ex)
            {
                // 瞬态失败（实测：上一条连接尚未释放时再 CreateServer 会抛
                // "所有的管道范例都在使用中"）**绝不能永久放弃** —— 否则接口从此失联：
                // 进程活着，但再也无法驱动（2026-09-14 D3 场景实测踩到：
                // 平滑动画进行中关设置窗后，接口 TimeoutException 直到进程重启）。
                // 退避 200ms 后重试，直到 _running 翻转（程序退出）。
                OpLog.Log("[auto] pipe accept failed, retry in 200ms: " + ex.Message);
                System.Threading.Thread.Sleep(200);
                continue;
            }

            var current = server;
            ThreadPool.QueueUserWorkItem(_ => HandleConnection(current));
        }
    }

    private static NamedPipeServerStream CreateServer() =>
        // CurrentUserOnly：仅当前用户可连（无需额外的 PipeSecurity 包依赖）
        new(PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);

    private static void HandleConnection(NamedPipeServerStream server)
    {
        try
        {
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            // 约定两行请求：第 1 行 = 标志（浮标，可为空串），第 2 行 = 命令
            string? flags = reader.ReadLine();
            string? command = reader.ReadLine();
            if (command == null) return;

            bool allowDestructive = flags != null
                && flags.Contains("allow-destructive", StringComparison.OrdinalIgnoreCase);

            writer.WriteLine(Instance.Execute(command, allowDestructive));
        }
        catch (Exception ex)
        {
            OpLog.LogEx("[auto] pipe handling failed", ex);
        }
        finally
        {
            try { server.Dispose(); } catch { /* 已断开 */ }
        }
    }

    /// <summary>把命令切到 UI 线程执行并返回 JSON 结果。</summary>
    private string Execute(string command, bool allowDestructive)
    {
        // 全量审计：进来的每条命令、出去的成功/失败都落 OpLog。
        // 自动化接口本身不写日志的话，测试失败时无从复盘"到底发没发出去、收到什么"。
        OpLog.Log($"[auto] <- {command}{(allowDestructive ? "   (allow-destructive)" : "")}");

        Control? anchor = _uiAnchor;
        if (anchor == null || !anchor.IsHandleCreated)
        {
            return Fail(command, "ui anchor not ready");
        }

        try
        {
            return anchor.InvokeRequired
                ? (string)anchor.Invoke(new Func<string>(() => ExecuteCore(command, allowDestructive)))!
                : ExecuteCore(command, allowDestructive);
        }
        catch (Exception ex)
        {
            return Fail(command, "invoke failed: " + ex.Message);
        }
    }

    /// <summary>命令分发（已在 UI 线程）。</summary>
    private string ExecuteCore(string command, bool allowDestructive)
    {
        // 语法：<verb>[:<arg1>[:<arg2>]]；arg2 取余下全部（允许含冒号，如热键串）
        string verb, arg1 = "", arg2 = "";
        int c1 = command.IndexOf(':');
        if (c1 < 0)
        {
            verb = command.Trim();
        }
        else
        {
            verb = command[..c1].Trim();
            int c2 = command.IndexOf(':', c1 + 1);
            if (c2 < 0)
            {
                arg1 = command[(c1 + 1)..].Trim();
            }
            else
            {
                arg1 = command[(c1 + 1)..c2].Trim();
                arg2 = command[(c2 + 1)..];
            }
        }

        switch (verb)
        {
            case "app.ping":
            {
                var d = new Dictionary<string, object?>
                {
                    ["version"] = Application.ProductVersion,
                    ["pid"] = Environment.ProcessId,
                    ["settingsOpen"] = SettingsForm.IsOpen,
                    ["popupOpen"] = Program.Instance?.IsPopupShown == true,
                    ["actionCount"] = _actions.Count
                };
                return Ok(command, d);
            }

            case "ui.list":
            {
                var items = _actions.Values
                    .OrderBy(a => a.Id, StringComparer.Ordinal)
                    .Select(a => new Dictionary<string, object?>
                    {
                        ["id"] = a.Id,
                        ["kind"] = a.Kind,
                        ["canGet"] = a.Get != null,
                        ["canSet"] = a.Set != null,
                        ["canClick"] = a.Click != null
                    })
                    .ToList();
                return Ok(command, new Dictionary<string, object?>
                {
                    ["count"] = items.Count,
                    ["settingsOpen"] = SettingsForm.IsOpen,
                    // Win_* / Nav_* 与各页面域的动作是**随 SettingsForm 创建才注册**的
                    // （控件必须先存在）。窗口没开时列表为空属正常，不是接口坏了 ——
                    // 脚本应先发 settings.open 再 ui.list。这里明确给一句，省得误判。
                    ["hint"] = SettingsForm.IsOpen
                        ? "settings window open"
                        : "settings window closed; send settings.open first, actions register as the window opens",
                    ["items"] = items
                });
            }

            case "ui.get":
                return GetAction(command, arg1);

            case "ui.set":
                return SetAction(command, arg1, arg2, allowDestructive);

            case "ui.click":
                return ClickAction(command, arg1, allowDestructive);

            case "settings.open":
                SettingsForm.ShowOrActivate();
                return Ok(command, new Dictionary<string, object?> { ["open"] = SettingsForm.IsOpen });

            case "settings.close":
                SettingsForm.CloseIfOpen();
                return Ok(command, new Dictionary<string, object?> { ["open"] = SettingsForm.IsOpen });

            case "settings.nav":
            {
                bool ok = SettingsForm.NavigateTo(arg1);
                return ok ? Ok(command, new Dictionary<string, object?> { ["page"] = arg1 })
                          : Fail(command, "unknown page key: " + arg1);
            }

            case "app.exit":
                if (!allowDestructive)
                {
                    return Fail(command, "destructive command requires --allow-destructive");
                }
                OpLog.Log("[auto] app.exit requested via automation bridge");
                Application.Exit();
                return Ok(command, new Dictionary<string, object?> { ["exiting"] = true });

            default:
                return Fail(command, "unknown command: " + verb);
        }
    }

    private string GetAction(string command, string id)
    {
        if (!_actions.TryGetValue(id, out var a)) return Fail(command, "unknown id: " + id);
        if (a.Get == null) return Fail(command, "id not readable: " + id);
        return Ok(command, new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["kind"] = a.Kind,
            ["value"] = a.Get()
        });
    }

    private string SetAction(string command, string id, string value, bool allowDestructive)
    {
        if (!_actions.TryGetValue(id, out var a)) return Fail(command, "unknown id: " + id);
        if (a.Set == null) return Fail(command, "id not writable: " + id);
        if (a.Destructive && !allowDestructive)
        {
            return Fail(command, "destructive action requires --allow-destructive: " + id);
        }

        a.Set(value);

        // 写完回读，让脚本拿到的是"真实生效值"而不是"我要求的值"
        var d = new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["kind"] = a.Kind,
            ["requested"] = value
        };
        if (a.Get != null) d["value"] = a.Get();
        return Ok(command, d);
    }

    private string ClickAction(string command, string id, bool allowDestructive)
    {
        if (!_actions.TryGetValue(id, out var a)) return Fail(command, "unknown id: " + id);
        if (a.Click == null) return Fail(command, "id not clickable: " + id);
        if (a.Destructive && !allowDestructive)
        {
            return Fail(command, "destructive action requires --allow-destructive: " + id);
        }

        a.Click();

        var d = new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["kind"] = a.Kind
        };
        if (a.Get != null) d["value"] = a.Get();
        return Ok(command, d);
    }

    private static string Ok(string command, Dictionary<string, object?> data)
    {
        data["ok"] = true;
        data["cmd"] = command;
        OpLog.Log($"[auto] -> OK   {command}");
        return JsonSerializer.Serialize(data);
    }

    private static string Fail(string command, string error)
    {
        OpLog.Log($"[auto] -> FAIL {command} : {error}");
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["cmd"] = command,
            ["error"] = error
        });
    }
}

/// <summary>
/// P5 / L2：<c>--auto &lt;command&gt;</c> 的客户端侧。
///
/// 关键点（设计稿 §15.5）：本路径必须在 <c>Program.Main</c> 里、**拿单实例互斥之前**
/// 拦截。若走正常启动路径，第二个实例会 <c>KillExistingInstances()</c> 杀掉正在跑的
/// 实例并自己接管 —— 那样"驱动已打开的设置窗"完全做不到，测出来的状态也全错。
/// 这里只连接既有实例的管道转发命令，连不上就明确报错，**绝不杀进程、绝不接管**。
/// </summary>
internal static class AutomationCli
{
    /// <summary>执行一条自动化命令，返回进程退出码（0 = ok，1 = 失败，2 = 用法错误）。</summary>
    internal static int Run(string command, bool allowDestructive, string? outFile)
    {
        string json = Forward(command, allowDestructive);

        // WinExe 默认不挂控制台，不 AttachConsole 的话脚本捕获不到 stdout
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        Console.WriteLine(json);

        if (!string.IsNullOrEmpty(outFile))
        {
            try
            {
                File.WriteAllText(outFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                OpLog.LogEx("[auto] write --out file failed", ex);
            }
        }

        return IsOk(json) ? 0 : 1;
    }

    private static bool IsOk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string Forward(string command, bool allowDestructive)
    {
        // 连接失败重试：服务端在接受循环里对瞬态失败（"所有管道范例都在使用中"）做
        // 200ms 退避重试，客户端若只试一次（1500ms）就会在服务端退避的空档放弃，
        // 表现为"偶发无响应"（2026-09-14 多源干预排查实测）。这里做 3 次 × 1.5s。
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return ForwardOnce(command, allowDestructive);
            }
            catch (Exception ex) when (attempt < 3)
            {
                OpLog.Log($"[auto] connect attempt {attempt} failed, retrying: {ex.Message}");
                System.Threading.Thread.Sleep(250);
            }
        }
    }

    private static string ForwardOnce(string command, bool allowDestructive)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AutomationBridge.PipeName, PipeDirection.InOut);
            client.Connect(1500);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            // 与 AutomationBridge.HandleConnection 的两行协议对齐
            writer.WriteLine(allowDestructive ? "allow-destructive" : "");
            writer.WriteLine(command);

            string? line = reader.ReadLine();
            if (line != null) return line;
            return Error(command, "empty response from instance");
        }
        catch (Exception ex)
        {
            // 最常见：没有实例在跑（管道不存在 → 连接超时）。明确区分，便于脚本判断。
            return Error(command,
                "cannot reach a running instance (pipe '" + AutomationBridge.PipeName +
                "' unavailable): " + ex.GetType().Name);
        }
    }

    private static string Error(string command, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["cmd"] = command,
            ["error"] = message
        });
}

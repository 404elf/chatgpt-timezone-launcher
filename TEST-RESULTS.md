# 测试结果

## 2026-09-29 至 2026-09-30 / 1.1.2 修复与发布验证

- 系统：Windows 11 x64，普通用户权限。
- 实测客户端：`OpenAI.Codex_26.924.2738.0_x64__2p2nqsd0c76g0`，动态发现清单 `App` / `app/ChatGPT.exe`。
- 自动化：合并已有 1.1.1 修复后 26/26 通过，新增包失败与超时不得报成功、非法时区不得启动、更新后入口不存在、恢复默认移除继承 TZ，以及真实 Windows 未注册包错误场景；保留损坏配置下窗口构造回归测试。
- 最终 EXE 在独立目录下的首次启动、损坏配置启动检查均通过。
- 初始单实例测试：进程退出码 0 但立即退出时，新启动器正确报告失败，没有当作成功。

| 真实客户端隔离验证 | 页面实际时区 | UTC 偏移（分钟） | 包身份 |
|---|---|---:|---|
| 纽约覆盖 | America/New_York | -240 | 当前 OpenAI.Codex 包，已验证 |
| 东京覆盖（交付 EXE） | Asia/Tokyo | +540 | 当前 OpenAI.Codex 包，已验证 |
| 无 TZ 覆盖（交付 EXE） | Asia/Taipei | +480 | 当前 OpenAI.Codex 包，已验证 |

页面通过本机隔离实例的调试接口读取 `Intl.DateTimeFormat` 和 `Date.getTimezoneOffset()`，不是从启动参数反推。测试采用新建临时用户目录，不读取或登录用户会话；调试接口只用于验证，不会加入日常启动参数。纽约测试使用相同启动实现的测试辅助宿主；东京和无覆盖测试使用交付 EXE 的实际辅助入口。

无覆盖测试验证了新进程不残留自定义 TZ；UI 默认分支仍通过 Windows AppsFolder 正常激活，其调用参数及继承 TZ 移除由自动化测试覆盖。没有关闭当前已登录客户端，未对其执行完整退出再登录。系统时区仍为 `Taipei Standard Time`，用户/系统 TZ 保持原值，原有客户端进程保留。

测试实例可能在关闭窗口后驻留后台。测试工具只清理它自己创建、使用临时目录的实例；产品关闭流程仍仅请求正常退出，不强制结束用户客户端。初始化检查只覆盖启动后 3 秒，不代表已验证全部联网及账号功能。

### 可选真实客户端回归测试

`build.ps1` 运行 26 项常规测试及最终 EXE 的两项隔离启动检查。以下测试会打开独立临时用户目录的真实客户端，需要 Windows、已安装客户端和构建后的启动器；不会发送聊天消息。一次指定一个时区，`default` 表示不设置 TZ：

```powershell
.\.dotnet-sdk\dotnet.exe run --project tests\ChatGptTimezoneLauncher.Integration -c Release -- Asia/Tokyo .\dist\win-x64\ChatGPT时区启动器.exe
.\.dotnet-sdk\dotnet.exe run --project tests\ChatGptTimezoneLauncher.Integration -c Release -- default .\dist\win-x64\ChatGPT时区启动器.exe
```

若使用系统 SDK，将前面的 `.\.dotnet-sdk\dotnet.exe` 替换为 `dotnet`。临时用户目录留在测试输出目录的 `isolated-browser-*` 中以便排查。

## 历史验证：1.1.1（2026-09-09）

使用 .NET SDK 8.0.425。新增窗口构造回归测试在修复前失败（20/21），修复后 21/21 通过。最终单文件 EXE 在独立中文/空格路径下，首次启动及损坏配置启动均通过，并保留损坏文件原文。未在无 .NET 的干净虚拟机验证，当时未操作真实 ChatGPT。详见 [v1.1.1 修复说明](docs/RELEASE_NOTES_v1.1.1.md)。

## 历史验证：1.1.0

测试日期：2026-09-01  
系统：Windows 11 x64，.NET SDK 8.0.424

## 自动化

```text
PASS  IANA timezone validation
PASS  config save/load and backup
PASS  corrupt config safely defaults
PASS  legacy self-exit cache is discarded
PASS  Cloudflare trace parser
PASS  ChatGPT US route wins while default route is TW
PASS  ChatGPT node switch is re-detected
PASS  default proxy switch cannot change ChatGPT result
PASS  OpenAI trace endpoint fallback
PASS  explicit-IP GeoIP provider fallback
PASS  explicit-IP GeoIP total failure
PASS  ChatGPT trace total failure never uses default exit
PASS  Clash not running does not block trace detection
PASS  non-Clash network works without controller API
PASS  AppX manifest candidate and version selection
PASS  AppX not installed diagnostics
PASS  manual TZ is process-local
PASS  restore/default launch has no TZ injection
PASS  default activation allows an existing ChatGPT instance
PASS  TZ override refuses an existing ChatGPT instance

20/20 tests passed
```

## 人工与本机集成检查

| 场景 | 结果 |
|---|---|
| 旧版普通出口检测 | 复现；GeoIP 自检测返回台湾 Default Proxy |
| ChatGPT trace | 通过；`chatgpt.com/cdn-cgi/trace` 返回美国出口 `134.195.101.58` |
| 指定 IP GeoIP | 通过；IPinfo 查询该 IP 返回 `San Jose, US`、`America/Los_Angeles` |
| 规则分流 | 通过；模拟 ChatGPT→US、Default→TW，自动时区保持美国 |
| 节点切换 | 通过；ChatGPT 节点切换会改变结果，Default Proxy 切换不会改变结果 |
| Clash/Mihomo 依赖 | 无；Clash 停止和非 Clash 网络测试均通过 |
| ChatGPT 当前包定位 | 通过；动态读取 `OpenAI.Codex` 26.825.6671.0 的 `app/ChatGPT.exe` |
| ChatGPT 更新路径变化 | 通过；模拟两个版本，选择新版本及新相对入口 |
| 中文 GUI | 通过；控件、状态、恢复按钮与诊断信息可见 |
| 已运行检测 | 通过；自动化使用现有测试进程验证分支 |
| 真实关闭并重启 ChatGPT | 未执行；ChatGPT 正在承载当前 Codex 任务，避免中断和数据风险 |

恢复默认分支使用标准 AppX AUMID 激活，测试确认启动参数未加入自定义 `TZ`；程序从未写入全局环境变量或注册表。

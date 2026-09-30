# ChatGPT 时区启动器

一个独立、免安装的 Windows 小工具。它不会修改 ChatGPT、Windows 系统时区、注册表或全局环境变量；只在启用时区覆盖时，为这一次新建的 ChatGPT 进程及其子进程设置 `TZ=<IANA timezone>`。

## 灵感来源与借鉴说明

本项目的功能构想和基础交互，参考了 **Opus94 分享的“ChatGPT 时区启动器”截图与使用说明**。感谢原作者验证了“仅向 ChatGPT 进程传入 `TZ`、不修改 Windows 系统时区”这一思路的可行性。

本仓库是根据功能目标从零独立实现的版本，没有复制参考工具的源代码或二进制文件。内部结构、界面、自动检测逻辑、包定位、配置和测试均重新设计。

相较参考方案，本项目主要增加或改进了：

- 自动检测 **ChatGPT/OpenAI 流量实际使用的出口**，而不是普通网络或 GeoIP 服务自身的出口；
- 兼容 Clash/Mihomo 规则分流，但不依赖 Clash、节点名称或 External Controller API；
- `chatgpt.com/cdn-cgi/trace → 明确出口 IP → 指定 IP GeoIP → IANA timezone` 两阶段检测；
- ChatGPT/OpenAI trace 多域名 fallback，以及 IPinfo、ipapi.co、ipwho.is 多 GeoIP fallback；
- 动态解析当前用户的 ChatGPT AppX/MSIX 包、版本和实际入口，不写死 WindowsApps 路径；
- 完整 IANA 时区搜索、配置损坏恢复、旧版错误缓存迁移和一键恢复默认启动；
- 已运行检测与用户确认后的正常关闭重启，不默认强制结束 ChatGPT；
- 自包含单文件 EXE，以及覆盖规则分流、网络失败、包更新和进程环境的自动化测试。

> 如果你知道原分享内容的长期有效原始链接，欢迎提交 Issue 或 PR 补充更精确的出处。

## 使用

1. 运行 `ChatGPT时区启动器.exe`（当前本地修复版为 1.1.1），放在任意普通目录即可使用，无需安装和管理员权限。
2. 选择“自动跟随 ChatGPT 实际出口”或“手动选择时区”。
3. 点击“保存并启动 ChatGPT”。自动模式会在每次启动前重新联网检测，节点变化不会被旧缓存遮盖。
4. 如要停用覆盖，点击醒目的“恢复 ChatGPT 默认启动方式”。此时两个模式均不选中；之后点击“启动 ChatGPT（默认方式）”会使用标准 AppX 激活，不注入 `TZ`。重新点选任一模式即可再次启用。

ChatGPT 已运行时，进程内时区不能动态改变。启用覆盖后，启动器会明确提示，并可在你确认后请求 ChatGPT 正常关闭再重启；不会强制结束进程。

### 两种模式

- **自动跟随 ChatGPT 实际出口**：适合代理、TUN 和规则分流环境；每次启动前重新检测。
- **手动选择时区**：可搜索完整 IANA timezone，例如 `Asia/Shanghai`、`Asia/Tokyo`、`America/Los_Angeles`。

### 恢复默认

点击“恢复 ChatGPT 默认启动方式”后，启动器不再向 ChatGPT 注入 `TZ`。之后点击“启动 ChatGPT（默认方式）”等价于正常 AppX 启动；不会修改或删除 Windows 时区、ChatGPT 文件和用户数据。

## 自动时区与隐私

自动模式不会再把 GeoIP 服务请求自身的出口当成 ChatGPT 出口。规则分流下，`ipapi.co`、`ipinfo.io` 等域名可能命中 Default Proxy，而 ChatGPT 命中另一策略组；两者的调用方 IP 并不等价。

检测顺序：

1. 访问 `https://chatgpt.com/cdn-cgi/trace`，从 `ip=` 字段取得这条 ChatGPT 域名请求的真实出口；请求禁用缓存。
2. 主 trace 失败时，依次尝试 `api.openai.com`、`auth.openai.com`、`chat.openai.com` 上相同的 `/cdn-cgi/trace`。不跟随到其他域名的重定向。
3. 使用明确 URL `https://ipinfo.io/<ChatGPT出口IP>/json` 查询这个指定 IP。
4. IPinfo 失败后，依次使用 `ipapi.co/<IP>/json/` 和 `ipwho.is/<IP>`，仍然只查询同一个明确 IP。

第二步 GeoIP 请求自身经过哪个代理不再影响结果，因为返回 IP 必须与 trace IP 完全一致，否则整项结果会被拒绝。Cloudflare 将 `/cdn-cgi/trace` 定义为域名侧的网络路径诊断端点，见 [Cloudflare 文档](https://developers.cloudflare.com/fundamentals/reference/cdn-cgi-endpoint/)。

实现不依赖 Clash/Mihomo API，也不扫描或读取代理配置，因此 Clash 未运行、使用其他代理软件或无代理时同样可用。Mihomo External Controller 的端口和密钥均可自定义；为避免强绑定和读取敏感控制密钥，当前版本只将代理组、节点显示为“未读取”，不根据节点名称猜测地区。时区的最终依据始终是 ChatGPT trace IP 的 GeoIP。

trace 服务会看到 ChatGPT/OpenAI 请求的公网 IP；GeoIP 服务会收到要查询的明确 IP。启动器没有遥测，不会上传 ChatGPT 数据或配置。相关服务说明见 [IPinfo 文档](https://ipinfo.io/developers/ip-geolocation-api-data)、[ipapi 文档](https://ipapi.co/api/#location-of-clients-ip) 和 [ipwho.is](https://ipwho.is/)。如果 trace 或全部 GeoIP 查询失败，启动器不会猜测、不会回退普通出口，也不会用历史结果启动；上次成功结果只用于界面参考。

## ChatGPT 定位与启动

OpenAI 官方说明 Windows 客户端通过 Microsoft Store 分发，当前官方 Store 产品 ID 为 `9NT1R1C2HH7J`。启动器不写死 `C:\Program Files\WindowsApps` 路径，而是：

1. 查询当前用户的开始菜单 ChatGPT 入口和已注册 AppX/MSIX 包；
2. 读取已注册包清单中的 `InstallLocation`、`Application Id`、`Executable` 和 `Parameters`；
3. 对候选项评分，并优先选择版本号最新的 ChatGPT 入口；
4. 启用覆盖时，通过 Windows 自带的 `Invoke-CommandInDesktopPackage -PreventBreakaway` 在当前 ChatGPT 包身份内启动一个短时辅助进程，再由它创建清单所指的 full-trust 桌面入口，仅在 ChatGPT 子进程环境中加入 `TZ`；
5. 辅助进程和 ChatGPT 都必须通过真实包身份核验；等待 3 秒检查立即退出后才报告初始启动成功。失败、超时会明确报错，不会退回无包身份的直接启动；
6. 默认方式使用 `shell:AppsFolder\<PackageFamilyName>!<AppId>` 标准激活，并从本次调用环境中移除继承的 `TZ`。没有全局时区覆盖需要清理。

2026-09-29 实测包为 `OpenAI.Codex_26.924.2738.0_x64__2p2nqsd0c76g0`，清单入口为 `app/ChatGPT.exe`，`EntryPoint=Windows.FullTrustApplication`。这只是验证样本，不存在于代码常量中；Store 更新后的新版本目录会在每次启动时重新发现。

1.1.0 的带时区分支直接运行 EXE，未保留新版客户端所需的包身份；1.1.1 修复了这条启动路径。辅助进程复用同一个单文件 EXE，通过仅当前用户可连接的随机命名管道收发请求和验证结果。它不注册新包或证书、不启用持久包调试设置，也不修改 ChatGPT 文件。接口保证及适用范围见 [微软 Invoke-CommandInDesktopPackage 文档](https://learn.microsoft.com/en-us/powershell/module/appx/invoke-commandindesktoppackage?view=windowsserver2025-ps)。

## 配置与恢复

配置位于：

```text
%LocalAppData%\ChatGPTTimezoneLauncher\settings.json
```

仅保存当前模式、手动时区、是否启用覆盖和上次成功检测。更新已有配置时先生成 `settings.json.bak`；新文件写完并反序列化验证后才替换旧文件。配置损坏时保留原文件并以安全默认状态启动。

“恢复默认”只把 `TimeZoneOverrideEnabled` 保存为 `false`。它不会修改 Windows 系统时区、系统/用户环境变量、注册表、ChatGPT 文件或用户数据；手动选择和历史检测结果会保留，方便以后重新启用。

## 从源码构建

要求 Windows 10/11 和 .NET 8 SDK。项目使用 WinForms 和 NodaTime（提供完整、可搜索且可验证的 IANA TZDB 列表）。在 PowerShell 中运行：

```powershell
.\build.ps1
```

脚本先运行测试，再发布 `win-x64`、自包含、压缩的单文件 EXE。输出：

```text
dist\win-x64\ChatGPT时区启动器.exe
```

## 验证结果

自动化测试 25/25 通过，覆盖：原有出口检测、配置和包定位场景，以及包启动失败/超时、非法时区、更新后入口缺失、继承 TZ 清除和真实 Windows 未注册包错误。

本机人工验证：

- 动态定位当前 `OpenAI.Codex` 包，在非管理员权限下启动真实客户端，并核验进程持有该包身份；
- 使用独立临时用户目录，真实页面的 `Intl.DateTimeFormat().resolvedOptions().timeZone` 分别返回 `America/New_York`、`Asia/Tokyo`；无覆盖时返回 `Asia/Taipei`，与本机 Windows 默认时区一致；
- 东京及无覆盖场景通过待交付的单文件 EXE 完成，不只使用模拟启动函数；
- Windows 时区、用户/系统 `TZ` 未改变。当前用户原有会话未被关闭；不以独立测试替代对已登录会话的承诺。详细范围见 `TEST-RESULTS.md`。

## 已知限制

- 交付 EXE 为 Windows x64；ARM64 需要将构建运行时改为 `win-arm64` 后重新发布。
- GeoIP 的城市级定位由第三方数据库提供，可能存在误差；时区字段为空或不是有效 IANA ID 时会视为失败。
- 此方案适用于已实测的 packaged full-trust 桌面入口。微软将包内命令接口定位为调试/排障工具，其令牌与标准应用激活不完全相同；未来客户端或 Windows 版本变更仍需重新验证。它不适用于需要 AppContainer 隔离的 UWP 应用。
- 3 秒存活和包身份检查只验证初始启动，不代表联网、登录或后续功能全部正常。切换时区需要彻底退出正在运行的 ChatGPT；仅关闭窗口可能仍驻留后台。启动器仍不会强制结束用户进程。
- 未签名的独立 EXE 可能触发 Windows SmartScreen 提示；源码构建本身不包含代码签名证书。

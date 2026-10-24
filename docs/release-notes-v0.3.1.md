# v0.3.1 — 修复「打开设置页闪退」（StackOverflowException）

> 这是 **必须升级** 的修复版。v0.3.0 的插件在宿主里只要点进「设置 → 插件 → 驿欧无线展台」就会让宿主闪退（0xC00000FD），
> 原因不在宿主，而在插件设置页的按钮构造代码。

## 崩溃原因

`SettingsView.NewButton` 有两个同名重载：

```csharp
private static Button NewButton(StackPanel row, string text, RoutedEventHandler onClick)              // A：普通按钮
private static Button NewButton(StackPanel row, string text, Func<object, RoutedEventArgs, Task> f)  // B：异步按钮
```

重载 B 内部又用 `async` lambda 去调用 `NewButton(...)`。C# 对 async lambda 的重载决议会优先选择
返回 `Task` 的委托（而不是 `void` 的 `RoutedEventHandler`），于是这次调用在编译期被绑定回 **重载 B 自己** ——
一个不抛异常、纯粹的函数级自递归：

```
SettingsView.NewButton(StackPanel, String, Func<Object, RoutedEventArgs, Task>)
  → SettingsView.NewButton(...)   （同一个方法，无限层）
  → StackOverflowException（.NET 无法 catch，进程直接消失）
```

宿主侧表现：`SettingsWindow.LoadPluginSettingsPages` → `PluginSettingsNavigationRegistry.Discover`
→ `GetSettingsView()` → `new SettingsView(...)` → 栈溢出。宿主日志里看不到任何插件抛出的异常，
只有进程消失，很容易误判成宿主或系统问题。

## 修复内容

1. 异步重载改名为 `NewButtonAsync`（3 处调用点同步修改），彻底消除重载决议歧义；
   内部用显式 `RoutedEventHandler` 变量接收 async lambda，不再依赖 async lambda 的决议规则。
2. 新增「设置页冒烟测试」：`tests/SettingsSmoke` 在 **独立进程** 里构造 `PluginRuntime` + `SettingsView`，
   并跑 1.5 秒消息循环（覆盖 provider 帧统计计时器）。`StackOverflowException` 无法捕获，
   所以判据是「进程正常退出 + 输出 `SMOKE-OK`」。
   - 跑法：`pwsh -File tools\run-settings-smoke.ps1`
   - 反向验证：`-PluginDll <v0.3.0 的 dll>` 会稳定复现 0xC00000FD 并判 FAIL（已实测）
   - `tools\build-all.ps1` 已把冒烟测试作为第 4 步，不通过就拒绝出包
3. 顺带清理设置页里 v0.2 时期的过期文案（「自动回退虚拟 DirectShow 摄像头」等，v0.3.0 起已无 vcam）。

## 产物

| 文件 | 大小 | SHA-256 |
| --- | --- | --- |
| `com.yiou.boothcam-0.3.1.icpx` | 77.9 KB | `8933fcc6076a553e13f96dfe84e2be5c1f6c33a08c4e1261c2351b465c456990` |
| `ICCCE-Patched-1.8.0.11-camera-provider-win-x64.zip`（已内置 0.3.1 插件） | 80.6 MB | `b88a547cc76c64e647c3a4e6e330fda5203822de593c966f68d1118ef7e0067f` |

插件包仍只有 3 个文件：`manifest.json` / `YiouCamera.Plugin.dll`（已合并 Core）/ `YiouCamera.Plugin.deps.json`。

## 安装 / 升级

任选其一：

- **宿主内安装**：设置 → 插件 → 从本地安装 → 选 `com.yiou.boothcam-0.3.1.icpx`，然后重启宿主。
- **文件夹安装**：把 3 个文件覆盖到 `<宿主>\Plugins\com.yiou.boothcam\`（先退出宿主）。
- **已经用打补丁宿主**：直接解压新的 `ICCCE-Patched-...zip` 覆盖，插件已在包内。

升级后建议确认：设置 → 插件 → 驿欧无线展台 能正常打开（这是 v0.3.0 的崩溃点）。

## 已知限制（与 v0.3.0 相同）

- 只有带 camera provider（API ≥ 1.14.0）的宿主才能出画面；官方 1.8.0.11 尚无该 API（上游 PR #739）。
  在官方宿主里插件能装、能加载，但不会产生摄像头，设置页会明确提示。
- 取流依赖 ffmpeg（自动探测：插件目录 → 宿主目录 → PATH → 驿欧白板套件自带的 4.0.1，参数按版本自适应）。
- 纯托管、零系统安装：不注册 COM、不写 HKCU、不在磁盘落任何虚拟摄像头 DLL，卸载即消失。

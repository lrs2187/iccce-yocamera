using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ink_Canvas.Plugins;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin.UI
{
    /// <summary>
    /// 插件设置页（纯代码构建，避免插件 ALC 下 XAML 解析的额外依赖风险）。
    /// 宿主在「设置 → 插件 → 驿欧无线展台」里渲染本控件。
    /// 页面元素：
    ///   - 接入模式：provider 激活状态 / 帧统计 / 宿主机位枚举结果 / 刷新 / 日志目录
    ///   - 采集会话：状态 / 连接 / 断开 / 打开宿主视频展台 / 抓取最近帧插入画布
    ///   - 设备与取流：设备 IP / 本机网卡 / 配对码 / 分辨率 / 帧率 / ffmpeg 路径 / 自动行为
    ///   - 日志目录 / 保存配置
    /// </summary>
    internal sealed class SettingsView : UserControl
    {
        private readonly PluginRuntime _rt;

        private TextBlock _modeText;
        private TextBlock _providerStatsText;
        private TextBlock _cameraListText;
        private TextBlock _sessionStatusText;
        private TextBlock _hintText;
        private DispatcherTimer _statsTimer;

        private TextBox _deviceIpBox;
        private ComboBox _bindIpCombo;
        private TextBox _pinCodeBox;
        private ComboBox _resolutionCombo;
        private TextBox _ffmpegPathBox;

        private Button _refreshButton;
        private Button _connectButton;
        private Button _disconnectButton;

        private bool _busy;

        public SettingsView(PluginRuntime runtime)
        {
            _rt = runtime ?? throw new ArgumentNullException(nameof(runtime));
            BuildUi();
            LoadConfigToUi();
            UpdateStatusText();
            _rt.StatusChanged += OnRuntimeStatusChanged;

            // provider 推帧统计每秒刷新（现场诊断：帧率/丢帧/目标格式）。
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += (_, __) => UpdateProviderStatsText();
            _statsTimer.Start();
            Unloaded += (_, __) => { try { _statsTimer?.Stop(); } catch { } };
            Loaded += (_, __) => { try { _statsTimer?.Start(); } catch { } };
        }

        // ---------------------------------------------------------------------
        // UI 构建
        // ---------------------------------------------------------------------
        private void BuildUi()
        {
            Padding = new Thickness(0);
            Background = Brushes.Transparent;

            var root = new StackPanel
            {
                Margin = new Thickness(4, 4, 4, 24),
                MaxWidth = 860,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var header = new TextBlock
            {
                Text = "驿欧无线展台 · ICCCE 插件 v" + _rt.Version,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 2),
            };
            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = "需要宿主支持 camera provider（API ≥ 1.14.0）：画面以纯托管「插件摄像头」形式出现在宿主视频展台里，"
                       + "预览/拍照/旋转都由宿主负责，本插件不注册任何系统组件。官方 1.8.0.11 还没有该 API，"
                       + "请使用带 provider 的宿主构建；点工具栏「驿欧展台」即可带着本设备打开展台。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // ---- 接入模式 / provider 状态 ----
            var modePanel = NewSection(root, "接入模式");
            _modeText = NewStatusText(modePanel);
            _providerStatsText = NewStatusText(modePanel);

            _cameraListText = NewStatusText(modePanel);
            var modeButtons = NewButtonRow(modePanel);
            _refreshButton = NewButtonAsync(modeButtons, "刷新状态", async (_, __) => await RefreshAsync());
            NewButton(modeButtons, "打开日志目录", (_, __) => OpenLogFolder());

            // ---- 采集会话 ----
            var sessionPanel = NewSection(root, "采集会话（发现 → TCP 控制 → RTSP → 共享内存喂帧）");
            _sessionStatusText = NewStatusText(sessionPanel);
            var sessionButtons = NewButtonRow(sessionPanel);
            _connectButton = NewButtonAsync(sessionButtons, "连接设备 / 启动采集", async (_, __) => await ConnectAsync());
            _disconnectButton = NewButtonAsync(sessionButtons, "断开采集", async (_, __) => await DisconnectAsync());
            NewButton(sessionButtons, "打开宿主视频展台", (_, __) => _rt.OpenBooth());
            NewButton(sessionButtons, "抓取最近帧插入画布", (_, __) => _rt.CaptureLatestFrameToCanvas());

            // ---- 设备与取流 ----
            var devicePanel = NewSection(root, "设备与取流设置");

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;

            _deviceIpBox = AddRow(grid, ref row, "设备 IP（留空自动发现）");
            _deviceIpBox.ToolTip = "已知设备 IP 时可跳过 UDP 12811 广播发现，例如 192.168.1.23。";

            var bindRow = AddRowContainer(grid, ref row, "本机网卡 IP");
            var bindStack = new StackPanel { Orientation = Orientation.Horizontal };
            _bindIpCombo = new ComboBox { Width = 220, IsEditable = true };
            FillNetworkInterfaces();
            bindStack.Children.Add(_bindIpCombo);
            var refreshNicButton = NewButton(bindStack, "刷新网卡", (_, __) => FillNetworkInterfaces());
            refreshNicButton.Margin = new Thickness(8, 0, 0, 0);
            bindRow.Children.Add(bindStack);

            var pinRow = AddRowContainer(grid, ref row, "配对码（8 位）");
            var pinStack = new StackPanel { Orientation = Orientation.Horizontal };
            _pinCodeBox = new TextBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            _pinCodeBox.ToolTip = "设备配对码。留空时由 Core 读取 %USERPROFILE%\\Documents\\YOFiles\\YOCamSet.xml 的 PinCode。";
            pinStack.Children.Add(_pinCodeBox);
            NewButton(pinStack, "读取厂商配对码", (_, __) =>
            {
                var code = PluginRuntime.TryReadPairingCodeFromYocamSet();
                if (string.IsNullOrEmpty(code))
                {
                    SetHint("没有在 %USERPROFILE%\\Documents\\YOFiles\\YOCamSet.xml 找到 PinCode，请手动填写。");
                }
                else
                {
                    _pinCodeBox.Text = code;
                    SetHint("已从 YOCamSet.xml 读取配对码（长度 " + code.Length + "）。");
                }
            });
            pinRow.Children.Add(pinStack);

            var resolutionRow = AddRowContainer(grid, ref row, "分辨率 / 帧率");
            _resolutionCombo = new ComboBox { Width = 220 };
            _resolutionCombo.Items.Add(NewResolutionItem("1280×720 @30", "1280x720"));
            _resolutionCombo.Items.Add(NewResolutionItem("1920×1080 @30", "1920x1080"));
            resolutionRow.Children.Add(_resolutionCombo);

            var ffmpegRow = AddRowContainer(grid, ref row, "ffmpeg.exe 路径");
            var ffmpegStack = new StackPanel { Orientation = Orientation.Horizontal };
            _ffmpegPathBox = new TextBox { Width = 380, Margin = new Thickness(0, 0, 8, 0) };
            _ffmpegPathBox.ToolTip = "留空由 Core 自动解析（宿主同目录 / PATH / YIOU_FFMPEG_PATH）。";
            ffmpegStack.Children.Add(_ffmpegPathBox);
            NewButton(ffmpegStack, "浏览...", (_, __) => BrowseFfmpeg());
            ffmpegRow.Children.Add(ffmpegStack);

            devicePanel.Children.Add(grid);

            var deviceButtons = NewButtonRow(devicePanel);
            NewButton(deviceButtons, "保存配置", (_, __) => SaveConfigFromUi());

            _hintText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12,
            };
            devicePanel.Children.Add(_hintText);

            var footer = new TextBlock
            {
                Text = "提示：本插件只使用 camera provider（纯托管）：不注册系统 COM、不写 HKCU、不在磁盘落任何虚拟摄像头 DLL，"
                       + "画面只出现在宿主视频展台里，随插件卸载即消失。"
                       + "首次注册后建议重启宿主。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
            };
            root.Children.Add(footer);

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root,
            };
        }

        private static StackPanel NewSection(StackPanel parent, string title)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            });
            border.Child = panel;
            parent.Children.Add(border);
            return panel;
        }

        private static TextBlock NewStatusText(StackPanel parent)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 13,
            };
            parent.Children.Add(text);
            return text;
        }

        private static StackPanel NewButtonRow(StackPanel parent)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
            };
            parent.Children.Add(row);
            return row;
        }

        private static Button NewButton(StackPanel row, string text, RoutedEventHandler onClick)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 96,
            };
            button.Click += onClick;
            row.Children.Add(button);
            return button;
        }

        // 注意：必须与上面的 RoutedEventHandler 重载区分名字。
        // 若两者同名，async lambda 的重载决议会优先选 Func<...,Task> 版本，
        // 本方法内部的 async lambda 会再次绑定回自身 —— 编译期自递归，
        // 打开设置页时直接 StackOverflowException 带崩宿主（v0.3.0 事故）。
        private static Button NewButtonAsync(StackPanel row, string text, Func<object, RoutedEventArgs, Task> onClickAsync)
        {
            RoutedEventHandler handler = async (sender, args) =>
            {
                try
                {
                    await onClickAsync(sender, args);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[YiouCamera] 设置页按钮异常: " + ex);
                }
            };

            return NewButton(row, text, handler);
        }

        private static TextBox AddRow(Grid grid, ref int row, string label)
        {
            var container = AddRowContainer(grid, ref row, label);
            var box = new TextBox { Width = 380 };
            container.Children.Add(box);
            return box;
        }

        private static StackPanel AddRowContainer(Grid grid, ref int row, string label)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6),
                Opacity = 0.85,
            };
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            var container = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };
            Grid.SetRow(container, row);
            Grid.SetColumn(container, 1);
            grid.Children.Add(container);

            row++;
            return container;
        }

        private static void AddFullRow(Grid grid, ref int row, UIElement element)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(element, row);
            Grid.SetColumn(element, 0);
            Grid.SetColumnSpan(element, 2);
            grid.Children.Add(element);
            row++;
        }

        private static ComboBoxItem NewResolutionItem(string display, string value)
        {
            return new ComboBoxItem { Content = display, Tag = value };
        }

        // ---------------------------------------------------------------------
        // 配置读写
        // ---------------------------------------------------------------------
        private void LoadConfigToUi()
        {
            try
            {
                var config = _rt.Config;
                _deviceIpBox.Text = config.DeviceIp ?? "";
                _bindIpCombo.Text = config.LocalBindIp ?? "";
                _pinCodeBox.Text = config.PairingCode ?? "";
                _ffmpegPathBox.Text = config.FfmpegPath ?? "";

                var target = (config.Resolution ?? "1280x720").Trim().ToLowerInvariant();
                foreach (var item in _resolutionCombo.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Tag as string, target, StringComparison.OrdinalIgnoreCase))
                    {
                        _resolutionCombo.SelectedItem = item;
                        break;
                    }
                }
                if (_resolutionCombo.SelectedItem == null && _resolutionCombo.Items.Count > 0)
                {
                    _resolutionCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                _rt.LogError("设置页加载配置失败", ex);
            }
        }

        private void SaveConfigFromUi()
        {
            try
            {
                var config = _rt.Config;
                config.DeviceIp = (_deviceIpBox.Text ?? "").Trim();
                config.LocalBindIp = (_bindIpCombo.Text ?? "").Trim();
                config.PairingCode = (_pinCodeBox.Text ?? "").Trim();
                config.FfmpegPath = (_ffmpegPathBox.Text ?? "").Trim();
                var item = _resolutionCombo.SelectedItem as ComboBoxItem;
                if (item != null && item.Tag is string resolution) config.Resolution = resolution;

                if (_rt.SaveConfig())
                {
                    SetHint("配置已保存。");
                    _rt.Notify("驿欧无线展台", "配置已保存。", NotificationLevel.Success);
                }
                else
                {
                    SetHint("配置保存失败，详见插件日志。");
                }
                UpdateStatusText();
            }
            catch (Exception ex)
            {
                _rt.LogError("设置页保存配置失败", ex);
            }
        }

        private void FillNetworkInterfaces()
        {
            try
            {
                var current = _bindIpCombo.Text;
                _bindIpCombo.Items.Clear();
                _bindIpCombo.Items.Add("自动（推荐）");
                foreach (var ip in PluginRuntime.GetLocalIpv4Addresses())
                {
                    _bindIpCombo.Items.Add(ip);
                }
                _bindIpCombo.Text = string.IsNullOrWhiteSpace(current) ? "自动（推荐）" : current;
            }
            catch (Exception ex)
            {
                _rt.LogError("枚举本机网卡失败", ex);
            }
        }

        private void BrowseFfmpeg()
        {
            try
            {
                var dialog = _rt.GetService<IFileDialogService>();
                if (dialog == null)
                {
                    SetHint("宿主文件对话框服务不可用，请手动填写 ffmpeg.exe 路径。");
                    return;
                }
                var path = dialog.OpenFile("选择 ffmpeg.exe", "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*", null);
                if (!string.IsNullOrWhiteSpace(path)) _ffmpegPathBox.Text = path;
            }
            catch (Exception ex)
            {
                _rt.LogError("选择 ffmpeg 路径失败", ex);
            }
        }

        // ---------------------------------------------------------------------
        // 操作
        // ---------------------------------------------------------------------
        private async Task RefreshAsync()
        {
            await RunBusyAsync("刷新状态", () => _rt.RefreshStatusAsync());
        }

        private async Task ConnectAsync()
        {
            await RunBusyAsync("启动采集会话", () => _rt.Booth.StartAsync());
            UpdateStatusText();
        }

        private async Task DisconnectAsync()
        {
            await RunBusyAsync("停止采集会话", () => _rt.Booth.StopAsync());
            UpdateStatusText();
        }

        private async Task RunBusyAsync(string name, Func<Task> action)
        {
            if (_busy) return;
            _busy = true;
            SetButtonsEnabled(false);
            SetHint(name + " ...");
            try
            {
                await Task.Run(action);
            }
            catch (Exception ex)
            {
                _rt.LogError(name + "失败", ex);
                SetHint(name + "失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                SetButtonsEnabled(true);
                UpdateStatusText();
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            foreach (var button in new[] { _refreshButton, _connectButton, _disconnectButton })
            {
                if (button != null) button.IsEnabled = enabled;
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                var appInfo = _rt.GetService<IAppInfoService>();
                var root = appInfo != null && !string.IsNullOrWhiteSpace(appInfo.RootPath)
                    ? appInfo.RootPath
                    : Path.GetDirectoryName(Environment.ProcessPath);
                var path = Path.Combine(root ?? "", "PluginLogs", "com.yiou.boothcam");
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                }
                else
                {
                    SetHint("日志目录尚不存在（宿主尚未写日志）：" + path);
                }
            }
            catch (Exception ex)
            {
                _rt.LogError("打开日志目录失败", ex);
            }
        }

        // ---------------------------------------------------------------------
        // 状态刷新
        // ---------------------------------------------------------------------
        private void OnRuntimeStatusChanged()
        {
            try
            {
                if (Dispatcher.CheckAccess()) UpdateStatusText();
                else Dispatcher.BeginInvoke(new Action(UpdateStatusText));
            }
            catch
            {
            }
        }

        private void UpdateStatusText()
        {
            try
            {
                var provider = _rt.CameraProvider;
                if (_modeText != null)
                {
                    _modeText.Text =
                        "接入模式：" + provider.ModeDescription + "\n" +
                        "provider 摄像头名：" + provider.ProviderCameraName + "\n" +
                        (provider.IsProviderMode
                            ? "说明：纯托管 camera provider（API ≥ 1.14.0），不注册系统 COM/虚拟摄像头，随插件卸载即消失。"
                            : "说明：当前宿主不支持 camera provider（需要 API ≥ 1.14.0），本插件无法接入；请升级宿主。")

                        + (string.IsNullOrEmpty(provider.RegistrationError) ? "" : "\n探测信息：" + provider.RegistrationError);
                }
                UpdateProviderStatsText();

                if (_sessionStatusText != null)
                {
                    _sessionStatusText.Text =
                        "后端：" + _rt.Booth.BackendName + "\n" +
                        "会话状态：" + _rt.Booth.StateText + "（可用=" + _rt.Booth.IsAvailable + "，运行中=" + _rt.Booth.IsRunning + "）\n" +
                        "最近错误：" + (string.IsNullOrEmpty(_rt.Booth.LastError) ? "(无)" : _rt.Booth.LastError);
                }
            }
            catch (Exception ex)
            {
                _rt.LogError("设置页刷新状态文本失败", ex);
            }
        }

        /// <summary>provider 推帧统计（每秒刷新，便于现场诊断丢帧/帧率/目标格式）。</summary>
        private void UpdateProviderStatsText()
        {
            try
            {
                if (_providerStatsText == null) return;
                var provider = _rt.CameraProvider;
                if (!provider.IsProviderMode)
                {
                    _providerStatsText.Text = "provider 推流：当前宿主不支持 camera provider（API < 1.14.0），不可用";
                    return;
                }

                var stats = provider.GetStats();
                _providerStatsText.Text =
                    "provider 推流：" + (stats.SinkActive ? "推流中" : "未推流") + "\n" +
                    "目标格式：" + stats.TargetWidth + "×" + stats.TargetHeight + "@" + stats.TargetFrameRate + "fps\n" +
                    "帧统计：提交 " + stats.FramesSubmitted + " / 接收 " + stats.FramesAccepted
                    + " / 拒绝 " + stats.FramesRejected + " / 丢弃 " + stats.FramesDropped
                    + "，实测 ≈" + stats.Fps.ToString("0.0") + " fps"
                    + (stats.BytesAccepted > 0 ? "，累计 " + (stats.BytesAccepted / (1024.0 * 1024.0)).ToString("0.0") + " MB" : "") + "\n" +
                    "最近错误：" + (string.IsNullOrEmpty(stats.LastError) ? "(无)" : stats.LastError);
            }
            catch
            {
            }
        }

        private void SetHint(string text)
        {
            try
            {
                if (_hintText != null) _hintText.Text = text ?? "";
            }
            catch
            {
            }
        }
    }
}

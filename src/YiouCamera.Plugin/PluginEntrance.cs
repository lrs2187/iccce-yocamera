using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;
using YiouCamera.Plugin.Infrastructure;

namespace YiouCamera.Plugin
{
    /// <summary>
    /// ICCCE 插件入口。
    /// - Initialize 必须快速返回：只做配置加载、工具栏注册、后台任务投递；
    /// - 虚拟摄像头安装/状态检查、摄像头枚举、自测全部在后台线程执行；
    /// - 所有异常一律 LogError 吞掉，绝不抛给宿主。
    /// </summary>
    [PluginEntrance]
    public sealed class YiouBoothCameraPlugin : PluginBase
    {
        private PluginRuntime _runtime;
        private int _initialized;

        /// <summary>新版入口（宿主当前版本实际调用这个：IPlugin.Initialize → Initialize(host, services)）。</summary>
        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            InitializeCore(host);
        }

        /// <summary>旧版入口（兼容）：base 的 Initialize(host, services) 也会转到这里。</summary>
        public override void Initialize(IPluginHost host)
        {
            base.Initialize(host);
            InitializeCore(host);
        }

        private void InitializeCore(IPluginHost host)
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            try
            {
                var configFolder = PluginConfigFolder ?? "";
                var pluginFolder = PluginFolder ?? "";

                _runtime = new PluginRuntime(host, configFolder, pluginFolder, Version, Id);

                host.Log("[Yiou] ===== 驿欧无线展台插件初始化 =====");
                host.Log("[Yiou] Id=" + Id + "，Name=" + Name + "，Version=" + Version);
                host.Log("[Yiou] 配置目录=" + (string.IsNullOrEmpty(configFolder) ? "(未分配)" : configFolder)
                         + "；插件目录=" + (string.IsNullOrEmpty(pluginFolder) ? "(未知)" : pluginFolder));


                // camera provider 注册（patched 宿主 API ≥1.14.0）：必须在 Initialize 内完成；
                // 旧宿主上运行时探测会失败（无 provider 接口），插件仅记录“不支持”，不注册任何系统级内容。
                _runtime.InitializeCameraProvider();
                host.Log("[Yiou] 接入模式=" + SafeGet(() => _runtime.CameraProvider.ModeDescription)
                         + (_runtime.CameraProvider.IsProviderMode
                             ? "；provider 摄像头名=" + SafeGet(() => _runtime.CameraProvider.ProviderCameraName)
                             : "；provider 探测失败原因=" + SafeGet(() => _runtime.CameraProvider.RegistrationError)));

                RegisterToolbarItems(host);

                // 后台初始化：查 provider 状态、枚举摄像头、自测。
                // 必须在 Initialize 里只投递，绝不同步等待。
                _runtime.StartBackgroundInit();

                host.Log("[Yiou] 初始化完成（非阻塞）。");
            }
            catch (Exception ex)
            {
                try
                {
                    host?.LogError("[Yiou] 插件初始化失败（已吞掉异常，不影响宿主）", ex);
                }
                catch
                {
                }
            }
        }

        private static string SafeGet(Func<string> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return "(不可用)";
            }
        }

        private void RegisterToolbarItems(IPluginHost host)
        {
            try
            {
                host.RegisterToolbarItem(new PluginToolbarItemInfo
                {
                    Id = "com.yiou.boothcam.open",
                    Surface = PluginToolbarSurface.Floating,
                    DisplayName = "驿欧展台",
                    Description = "打开宿主内置视频展台并预选「驿欧无线展台」虚拟摄像头",
                    IconGeometry = "",
                    ViewFactory = CreateBoothButton,
                });
                host.Log("[Yiou] 已注册工具栏按钮：驿欧展台（Floating）");

                host.RegisterToolbarItem(new PluginToolbarItemInfo
                {
                    Id = "com.yiou.boothcam.capture",
                    Surface = PluginToolbarSurface.Floating,
                    DisplayName = "驿欧截图",
                    Description = "把驿欧展台最近一帧插入当前画布",
                    IconGeometry = "",
                    ViewFactory = CreateCaptureButton,
                });
                host.Log("[Yiou] 已注册工具栏按钮：驿欧截图（Floating）");
            }
            catch (Exception ex)
            {
                try
                {
                    host?.LogError("[Yiou] 注册工具栏按钮失败", ex);
                }
                catch
                {
                }
            }
        }

        private FrameworkElement CreateBoothButton()
        {
            return CreateToolbarButton("驿欧展台", "打开视频展台（预选驿欧无线摄像头）", () => _runtime?.OpenBooth());
        }

        private FrameworkElement CreateCaptureButton()
        {
            return CreateToolbarButton("驿欧截图", "把驿欧展台最近一帧插入画布", () => _runtime?.CaptureLatestFrameToCanvas());
        }

        private FrameworkElement CreateToolbarButton(string content, string tooltip, Action onClick)
        {
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(8, 4, 8, 4),
                MinWidth = 64,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (sender, args) =>
            {
                try
                {
                    onClick?.Invoke();
                }
                catch (Exception ex)
                {
                    _runtime?.LogError("工具栏按钮「" + content + "」处理异常", ex);
                }
            };
            return button;
        }

        public override object GetSettingsView()
        {
            try
            {
                return _runtime?.GetSettingsView();
            }
            catch (Exception ex)
            {
                try
                {
                    Host?.LogError("[Yiou] 创建设置页失败", ex);
                }
                catch
                {
                }
                return null;
            }
        }

        public override void Shutdown()
        {
            try
            {
                Host?.Log("[Yiou] Shutdown：开始清理");
            }
            catch
            {
            }

            try
            {
                _runtime?.Dispose();
            }
            catch (Exception ex)
            {
                try
                {
                    Host?.LogError("[Yiou] Shutdown 清理异常（已吞掉）", ex);
                }
                catch
                {
                }
            }

            try
            {
                base.Shutdown();
            }
            catch
            {
            }

            try
            {
                Host?.Log("[Yiou] Shutdown：完成");
            }
            catch
            {
            }
        }
    }
}

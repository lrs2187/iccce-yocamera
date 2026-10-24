using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace YiouCamera.SettingsSmoke
{
    /// <summary>
    /// 最小假宿主：插件运行时的这 12 个成员都不会真正被用到（只取版本/日志/服务），
    /// 目的是让 PluginRuntime + SettingsView 能在没有 ICCCE 的进程里构造起来。
    /// </summary>
    internal sealed class FakeHost : IPluginHost
    {
        public IServiceProvider ServiceProvider { get { return null; } }
        public IServiceCollection Services { get { return null; } }
        public IPluginIpcBus Ipc { get { return null; } }

        public void Log(string message) { Console.WriteLine("[host.log] " + message); }

        public void LogError(string message, Exception ex)
        {
            Console.WriteLine("[host.err] " + message + " :: " + (ex == null ? "<null>" : ex.GetType().Name));
        }

        public T GetService<T>() where T : class { return null; }

        public void RegisterService<T>(T service) where T : class { }

        public void RegisterToolbarItem(PluginToolbarItemInfo item) { }

        public void RegisterBoardToolbarItem(PluginToolbarItemInfo item) { }

        public void RegisterIpcHandler(string name, Func<JsonElement?, object> handler) { }

        public SecurityVerdict EvaluateTrust(string packagePath, string pluginId, string packageSha256) { return null; }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("SMOKE-FAIL " + ex);
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            string pluginDll = args.Length > 0
                ? Path.GetFullPath(args[0])
                : Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "src", "YiouCamera.Plugin", "bin", "Release", "net10.0-windows10.0.19041.0", "YiouCamera.Plugin.dll"));

            if (!File.Exists(pluginDll))
            {
                Console.WriteLine("SMOKE-FAIL 找不到插件 dll: " + pluginDll);
                return 2;
            }

            Console.WriteLine("[smoke] plugin = " + pluginDll + " (" + new FileInfo(pluginDll).Length + " B)");

            var asm = Assembly.LoadFrom(pluginDll);
            Console.WriteLine("[smoke] assembly = " + asm.FullName);

            var runtimeType = asm.GetType("YiouCamera.Plugin.Infrastructure.PluginRuntime", throwOnError: true);
            var viewType = asm.GetType("YiouCamera.Plugin.UI.SettingsView", throwOnError: true);

            string temp = Path.Combine(Path.GetTempPath(), "yiou-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            const BindingFlags anyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            object runtime = Activator.CreateInstance(
                runtimeType,
                anyCtor,
                binder: null,
                args: new object[]
                {
                    new FakeHost(),
                    Path.Combine(temp, "config"),
                    pluginDll.Substring(0, pluginDll.LastIndexOf(Path.DirectorySeparatorChar)),
                    "0.3.0",
                    "com.yiou.boothcam",
                },
                culture: null);

            Console.WriteLine("[smoke] PluginRuntime 构造完成");

            // 这一步是本次事故的崩溃点：SettingsView 构造函数 → BuildUi()。
            var view = (UserControl)Activator.CreateInstance(
                viewType,
                anyCtor,
                binder: null,
                args: new[] { runtime },
                culture: null);

            Console.WriteLine("[smoke] SettingsView 构造完成");

            // 跑 1.5s 消息循环：让 DispatcherTimer 的统计刷新至少触发一次（现场另一类崩溃点）。
            var frame = new DispatcherFrame();
            var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            stopTimer.Tick += (_, __) =>
            {
                stopTimer.Stop();
                frame.Continue = false;
            };
            stopTimer.Start();
            Dispatcher.PushFrame(frame);

            Console.WriteLine("[smoke] 消息循环 1.5s 正常结束");

            int buttons = Count<Button>(view);
            int texts = Count<TextBlock>(view);
            int combos = Count<ComboBox>(view);
            int boxes = Count<TextBox>(view);

            Console.WriteLine("[smoke] 控件统计: Button=" + buttons + " TextBlock=" + texts + " ComboBox=" + combos + " TextBox=" + boxes);

            if (view.Content == null)
            {
                Console.WriteLine("SMOKE-FAIL 设置页内容为空");
                return 2;
            }

            if (buttons < 10)
            {
                Console.WriteLine("SMOKE-FAIL 按钮数量异常（预期 ≥10，实际 " + buttons + "），设置页可能没构建完整");
                return 2;
            }

            try { (runtime as IDisposable)?.Dispose(); } catch { }
            try { Directory.Delete(temp, true); } catch { }

            Console.WriteLine("SMOKE-OK buttons=" + buttons + " texts=" + texts);
            return 0;
        }

        private static int Count<T>(DependencyObject root) where T : DependencyObject
        {
            int n = root is T ? 1 : 0;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var dep = child as DependencyObject;
                if (dep != null)
                {
                    n += Count<T>(dep);
                }
            }

            return n;
        }
    }
}

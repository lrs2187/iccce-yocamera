// 测试宿主：复刻 ICCCE 视频展台的渲染路径，验证虚拟摄像头能否在宿主同款管线里出图。
//
// 复刻依据（源码确证，见 _research/iccce-integration.md §3.1 路径 B）：
//   MW_VideoPresenter.cs:2354  VideoCaptureDevice = device
//   MW_VideoPresenter.cs:2358  EnableSampleGrabbing = false
//   MW_VideoPresenter.cs:2359  UseYuv = false
//   WPFMediaKit VideoCapturePlayer.SetupGraph(): FilterGraphNoThread + CaptureGraphBuilder2
//                                                + AddFilterByDevicePath + VMR9 Renderless + D3DImage
//
// WPFMediaKit.SetupGraph 有两条互斥分支：
//   m_videoCaptureSourceChanged → AddFilterByName(VideoCaptureSource)
//   else m_videoCaptureDeviceChanged → AddFilterByDevicePath(VideoCaptureDevice.DevicePath)
// 因此这里分两段验证：先按宿主实际用法（只设 VideoCaptureDevice），失败再退回按名字。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DirectShowLib;
using WPFMediaKit.DirectShow.Controls;

namespace YiouCamera.Harness
{
    internal static class Program
    {
        private const string DefaultName = "驿欧无线展台";
        private const string PocName = "YoCamera Virtual Camera";

        private static VideoCaptureElement _element;
        private static Window _window;
        private static bool _mediaOpened;
        private static string _mediaFailed;
        private static int _openedMs = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            // 诊断模式：用宿主 DirectShowCameraService 完全相同的方式探测 pin 分类与 IAMStreamConfig 能力
            if (args.Length > 0 && args[0] == "--caps")
            {
                var logPath0 = System.IO.Path.Combine(AppContext.BaseDirectory, "harness-caps.log");
                var w0 = new System.IO.StreamWriter(logPath0, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(w0);
                string name0 = args.Length > 1 ? args[1] : "驿欧无线展台";
                try { return ProbeCaps(name0); }
                catch (Exception ex) { Console.WriteLine("[caps] EXCEPTION " + ex); return 9; }
            }

            var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "harness.log");
            var logWriter = new System.IO.StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(logWriter);
            Console.WriteLine("[harness] ==== start {0:O} ====", DateTime.Now);

            string want = args.Length > 0 ? args[0] : null;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            _element = new VideoCaptureElement
            {
                EnableSampleGrabbing = false,   // 与宿主特殊模式一致
                UseYuv = false,                 // 与宿主特殊模式一致
                Stretch = Stretch.UniformToFill,
            };
            _window = new Window
            {
                Title = "YiouCamera Harness",
                Width = 960,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 60,
                Topmost = true,
                Background = Brushes.Black,
                Content = _element,
            };

            _element.MediaOpened += (s, e) =>
            {
                _mediaOpened = true;
                _openedMs = (int)_sw.ElapsedMilliseconds;
                Console.WriteLine("[harness] MediaOpened after {0} ms", _openedMs);
            };
            _element.MediaFailed += (s, e) =>
            {
                _mediaFailed = string.Format("{0} / {1}", e.Message, e.Exception?.Message);
                Console.WriteLine("[harness] MediaFailed: " + _mediaFailed);
            };

            int exitCode = 3;
            _window.Loaded += async (s, e) =>
            {
                try { exitCode = await RunAsync(want); }
                catch (Exception ex) { Console.WriteLine("[harness] EXCEPTION " + ex); exitCode = 9; }
                finally { _window.Close(); }
            };

            app.Startup += (s, e) => _window.Show();
            app.Run();
            Console.WriteLine("[harness] EXIT {0}", exitCode);
            return exitCode;
        }

        private static Stopwatch _sw = new Stopwatch();

        private static async Task<int> RunAsync(string want)
        {
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            Console.WriteLine("[harness] VideoInputDevice 共 {0} 个：", devices.Length);
            DsDevice target = null;
            foreach (var d in devices)
            {
                bool hit = want != null
                    ? string.Equals(d.Name, want, StringComparison.OrdinalIgnoreCase)
                    : (d.Name == DefaultName || d.Name == PocName);
                Console.WriteLine("    - '{0}'  DevicePath='{1}'{2}", d.Name,
                    string.IsNullOrEmpty(d.DevicePath) ? "<NULL>" : d.DevicePath,
                    hit ? "   <== 目标" : "");
                if (hit && target == null) target = d;
            }
            if (target == null)
            {
                Console.WriteLine("[harness] FAIL 未找到目标设备");
                return 2;
            }

            var shm = ShmReader.TryOpen();
            shm?.Sample();

            // ---------- 第 1 段：宿主实际用法（只设 VideoCaptureDevice），带 ICCCE 同款重试 ----------
            // 背景：WPFMediaKit.VideoCapturePlayer.SetupGraph() 里
            //   if (m_videoCaptureSourceChanged) { AddFilterByName(VideoCaptureSource); }   ← 首次必然命中
            //   else if (m_videoCaptureDeviceChanged) { AddFilterByDevicePath(...); }
            // 创建播放器时会把 DP 默认值 VideoCaptureSource="" 写进 player，置位 sourceChanged，
            // 于是第一次建图用空名字找设备必然失败；失败分支里该标志被清零，
            // 因此第二次建图才会走 DevicePath 分支。ICCCE 的 1.5s 兜底重试就是为此（MW_VideoPresenter.cs:134-143）。
            Console.WriteLine("\n[harness] === 第1段：VideoCaptureDevice（宿主用法 + 重试） ===");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ResetFlags();
                await StartLikeHostAsync(target);
                bool opened = await WaitForMediaAsync(3000);
                long read = ReadDelta(shm, 2000);
                Console.WriteLine("[harness] 尝试#{0}: reopened={1} opened={2} failed={3} readDelta={4}",
                    attempt, _mediaOpened, opened, _mediaFailed ?? "no", read);
                if (read > 0)
                {
                    Console.WriteLine("[harness] PASS(device-path)：宿主同款路径出图，且滤波器在消费共享内存帧");
                    await Task.Delay(12000);   // 留出截图窗口（脚本按窗口矩形截图）
                    return 0;
                }
            }

            // ---------- 第 2 段：退回按名字 ----------
            Console.WriteLine("\n[harness] === 第2段：VideoCaptureSource（按名字兜底） ===");
            ResetFlags();
            try { _element.VideoCaptureDevice = null; } catch { }
            await Task.Delay(600);
            _sw.Restart();
            _element.VideoCaptureSource = target.Name;
            _element.Play();
            bool ok2 = await WaitForMediaAsync(5000);
            long read2 = ReadDelta(shm, 2000);
            Console.WriteLine("[harness] 段2 结果: opened={0} failed={1} readDelta={2}", ok2, _mediaFailed ?? "no", read2);
            if (read2 > 0)
            {
                Console.WriteLine("[harness] PASS(by-name)：渲染链路通，但宿主路径(DevicePath)不通 —— 需修 DevicePath!");
                await Task.Delay(2000);
                return 1;
            }
            Console.WriteLine("[harness] FAIL：两段都未出图");
            return 3;
        }

        /// <summary>
        /// 完全照抄 ICCCE 的启动序列（MW_VideoPresenter.cs:2320-2387）：
        ///   Stop() → VideoCaptureDevice=null → 250ms → BeginInit → 设设备/参数 → EndInit → Play()
        /// </summary>
        private static async Task StartLikeHostAsync(DsDevice device)
        {
            try { _element.Stop(); } catch { }
            try { _element.VideoCaptureDevice = null; } catch { }
            await Task.Delay(250);

            var init = (System.ComponentModel.ISupportInitialize)_element;
            init.BeginInit();
            _element.VideoCaptureDevice = device;
            _element.EnableSampleGrabbing = false;   // VMR9 Renderless 下必须关闭
            _element.UseYuv = false;
            _element.LoadedBehavior = WPFMediaKit.DirectShow.MediaPlayers.MediaState.Manual;
            _element.DesiredPixelWidth = 1280;
            _element.DesiredPixelHeight = 720;
            _element.FPS = 30;
            init.EndInit();

            _sw.Restart();
            _element.Play();

            // 实验：WPFMediaKit 的 Play() 只有在 m_graph != null 时才会真正 IMediaControl.Run()。
            // 首次 Play 时图可能还没建，导致图停在 Paused（vcam 日志里只有 "Pause done"、没有 "Run"）。
            // 所以建图完成后再补一次 Play()。
            await Task.Delay(700);
            try
            {
                _element.Play();
                Console.WriteLine("[harness] 已补一次 Play()（建图后强制 Run）");
                DumpPlayerState("after-extra-play");
            }
            catch (Exception ex) { Console.WriteLine("[harness] 补 Play 失败: " + ex.Message); }
        }

        /// <summary>
        /// 复刻宿主 DirectShowCameraService 的探测方式：
        ///   moniker.BindToObject → IBaseFilter → DsFindPin.ByCategory(Capture/Preview, 0)
        ///   → QI IAMStreamConfig → GetNumberOfCapabilities/GetStreamCaps
        /// 用来判定「分辨率下拉框为空」到底是 pin 分类不对、还是 GetStreamCaps 返回 0。
        /// </summary>
        private static int ProbeCaps(string want)
        {
            Console.WriteLine("[caps] ==== probe '{0}' ====", want);
            var devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            DsDevice target = null;
            foreach (var d in devs) if (d.Name == want) target = d;
            if (target == null) { Console.WriteLine("[caps] FAIL 未找到设备"); return 2; }

            object obj;
            Guid iidBase = typeof(IBaseFilter).GUID;
            target.Mon.BindToObject(null, null, ref iidBase, out obj);   // moniker → IBaseFilter
            var filter = (IBaseFilter)obj;
            Console.WriteLine("[caps] BindToObject OK");

            int failures = 0;
            foreach (var kv in new[] { Tuple.Create("Capture", PinCategory.Capture), Tuple.Create("Preview", PinCategory.Preview) })
            {
                Console.WriteLine("\n[caps] --- PinCategory.{0} ---", kv.Item1);
                IPin pin = null;
                try { pin = DsFindPin.ByCategory(filter, kv.Item2, 0); }
                catch (Exception ex) { Console.WriteLine("[caps]   DsFindPin 抛异常: " + ex.Message); }
                if (pin == null)
                {
                    Console.WriteLine("[caps]   [FAIL] DsFindPin.ByCategory 找不到该分类的 pin");
                    failures++;
                    continue;
                }
                Console.WriteLine("[caps]   pin found: {0}", pin);

                var cfg = pin as IAMStreamConfig;
                if (cfg == null)
                {
                    Console.WriteLine("[caps]   [FAIL] 该 pin 不支持 IAMStreamConfig");
                    failures++;
                    continue;
                }

                int count = 0, size = 0;
                int hr = cfg.GetNumberOfCapabilities(out count, out size);
                Console.WriteLine("[caps]   GetNumberOfCapabilities hr=0x{0:X8} count={1} size={2}", hr, count, size);
                if (hr < 0 || count <= 0) { Console.WriteLine("[caps]   [FAIL] 能力数为 0"); failures++; continue; }

                // DirectShowLib 签名: GetStreamCaps(int, out AMMediaType, IntPtr pSCC) —— 调用方分配 caps 缓冲
                IntPtr capsBuf = Marshal.AllocCoTaskMem(size > 0 ? size : 128);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        AMMediaType mt;
                        int hr2 = cfg.GetStreamCaps(i, out mt, capsBuf);
                        if (hr2 < 0 || mt == null) { Console.WriteLine("[caps]   #{0} GetStreamCaps hr=0x{1:X8}", i, hr2); failures++; continue; }
                        var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader));
                        Console.WriteLine("[caps]   #{0} {1} {2}x{3} @ {4:F2}fps  (avgTimePerFrame={5}) fmtType={6}",
                            i, mt.subType == MediaSubType.RGB24 ? "RGB24" : mt.subType.ToString(),
                            vih.BmiHeader.Width, vih.BmiHeader.Height,
                            vih.AvgTimePerFrame > 0 ? 10000000.0 / vih.AvgTimePerFrame : 0,
                            vih.AvgTimePerFrame, mt.formatType == FormatType.VideoInfo ? "VideoInfo" : mt.formatType.ToString());
                        DsUtils.FreeAMMediaType(mt);
                    }
                }
                finally { Marshal.FreeCoTaskMem(capsBuf); }
            }

            Console.WriteLine("\n[caps] RESULT: failures={0}", failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>反射拿到 WPFMediaKit 内部的 MediaPlayerBase（VideoCaptureElement 的播放器）。</summary>
        private static object GetPlayer()
        {
            var t = typeof(VideoCaptureElement);
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (var p in t.GetProperties(F))
                if (p.PropertyType.Name == "MediaPlayerBase") { try { return p.GetValue(_element); } catch { } }
            foreach (var f in t.GetFields(F))
                if (f.FieldType.Name == "MediaPlayerBase") { try { return f.GetValue(_element); } catch { } }
            // 往上找基类
            for (var bt = t.BaseType; bt != null; bt = bt.BaseType)
            {
                foreach (var p in bt.GetProperties(F))
                    if (p.PropertyType.Name == "MediaPlayerBase") { try { return p.GetValue(_element); } catch { } }
                foreach (var f in bt.GetFields(F))
                    if (f.FieldType.Name == "MediaPlayerBase") { try { return f.GetValue(_element); } catch { } }
            }
            return null;
        }

        private static void DumpPlayerState(string tag)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var player = GetPlayer();
            if (player == null) { Console.WriteLine("[harness] {0}: 拿不到 player", tag); return; }
            var pt = player.GetType();
            object mc = null, graph = null;
            foreach (var f in pt.GetFields(F))
            {
                if (f.Name == "m_mediaControl") mc = f.GetValue(player);
                if (f.Name == "m_graph") graph = f.GetValue(player);
            }
            Console.WriteLine("[harness] {0}: player={1} m_graph={2} m_mediaControl={3}",
                tag, pt.Name, graph == null ? "<NULL>" : "set", mc == null ? "<NULL>" : "set");
            // 绕过元素包装层，直接让播放器 Run
            if (mc != null)
            {
                try
                {
                    pt.GetMethod("Play", Type.EmptyTypes)?.Invoke(player, null);
                    Console.WriteLine("[harness] {0}: 已直接调用 player.Play()", tag);
                }
                catch (Exception ex) { Console.WriteLine("[harness] {0}: player.Play 失败 {1}", tag, ex.InnerException?.Message ?? ex.Message); }
            }
        }

        private static void ResetFlags()
        {
            _mediaOpened = false;
            _mediaFailed = null;
            _openedMs = -1;
        }

        private static long ReadDelta(ShmReader shm, int ms)
        {
            if (shm == null) return -1;
            shm.Sample();
            Thread.Sleep(ms);
            shm.Sample();
            return shm.ReadDelta;
        }

        private static async Task<bool> WaitForMediaAsync(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (_mediaOpened && _mediaFailed != null) return false;
                if (_mediaOpened) return true;
                if (_mediaFailed != null) return false;
                await Task.Delay(100);
            }
            return _mediaOpened && _mediaFailed == null;
        }
    }

    /// <summary>只读打开虚拟摄像头的共享内存，用于观测消费者进度。</summary>
    internal sealed class ShmReader
    {
        private const string ShmName = @"Local\YoCameraVcamFrames";
        private const int OffWriteSeq = 44;
        private const int OffReadSeq = 48;
        private const int OffWidth = 20;
        private const int OffHeight = 24;
        private const int OffFrameIndex = 60;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private IntPtr _map;
        private int _w0, _r0;
        private int _wd, _rd;

        public int WriteDelta => _wd;
        public int ReadDelta => _rd;

        public static ShmReader TryOpen()
        {
            var h = OpenFileMapping(0x0004, false, ShmName);
            if (h == IntPtr.Zero) { Console.WriteLine("[harness] 共享内存未打开（生产者没跑？err={0}）", Marshal.GetLastWin32Error()); return null; }
            var r = new ShmReader();
            r._map = MapViewOfFile(h, 0x0004, 0, 0, UIntPtr.Zero);
            CloseHandle(h);
            if (r._map == IntPtr.Zero) return null;
            return r;
        }

        public void Sample()
        {
            int w = Marshal.ReadInt32(_map, OffWidth);
            int h = Marshal.ReadInt32(_map, OffHeight);
            int ws = Marshal.ReadInt32(_map, OffWriteSeq);
            int rs = Marshal.ReadInt32(_map, OffReadSeq);
            if (_w0 == 0)
            {
                _w0 = ws; _r0 = rs;
                Console.WriteLine("[harness] SHM {0}x{1} writeSeq={2} readSeq={3} frameIndex={4}",
                    w, h, ws, rs, Marshal.ReadInt64(_map, OffFrameIndex));
            }
            _wd = ws - _w0;
            _rd = rs - _r0;
        }

        ~ShmReader()
        {
            if (_map != IntPtr.Zero) { UnmapViewOfFile(_map); _map = IntPtr.Zero; }
        }
    }
}


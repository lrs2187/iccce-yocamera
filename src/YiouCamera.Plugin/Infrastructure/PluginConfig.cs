using System;
using System.IO;
using System.Text.Json;

namespace YiouCamera.Plugin.Infrastructure
{
    /// <summary>
    /// 插件本地配置（持久化到宿主分配的 PluginConfigFolder，不使用宿主 Settings.json）。
    /// 只写单文件 config.json，不写日志（日志一律走 IPluginHost.Log/LogError）。
    /// <para>本插件只走 camera provider 路径：不注册系统级虚拟摄像头，因此没有 vcam 相关配置项。</para>
    /// </summary>
    internal sealed class PluginConfig
    {
        public const string FileName = "config.json";

        /// <summary>设备 IP。空 = 走 UDP 12811 广播自动发现。</summary>
        public string DeviceIp { get; set; } = "";

        /// <summary>本机网卡 IP（UDP/TCP 绑定用）。空 = 自动挑选非虚拟网卡。</summary>
        public string LocalBindIp { get; set; } = "";

        /// <summary>8 位配对码。空 = Core 自动读 %USERPROFILE%\Documents\YOFiles\YOCamSet.xml。</summary>
        public string PairingCode { get; set; } = "";

        /// <summary>分辨率：1280x720 或 1920x1080。</summary>
        public string Resolution { get; set; } = "1280x720";

        /// <summary>目标帧率。</summary>
        public int FrameRate { get; set; } = 30;

        /// <summary>ffmpeg.exe 路径。空 = Core 自动解析（exe 同目录 / PATH / YIOU_FFMPEG_PATH）。</summary>
        public string FfmpegPath { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public int Width
        {
            get { return ParseResolution(out int w, out _) ? w : 1280; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public int Height
        {
            get { return ParseResolution(out _, out int h) ? h : 720; }
        }

        public bool ParseResolution(out int width, out int height)
        {
            width = 1280;
            height = 720;
            if (string.IsNullOrWhiteSpace(Resolution)) return false;
            var parts = Resolution.Trim().ToLowerInvariant().Split('x');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) return false;
            if (w <= 0 || h <= 0) return false;
            width = w;
            height = h;
            return true;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public int SafeFrameRate
        {
            get { return FrameRate >= 1 && FrameRate <= 60 ? FrameRate : 30; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        public static PluginConfig Load(string folder, Action<string> log, Action<string, Exception> logError)
        {
            var config = new PluginConfig();
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) return config;
                var path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) return config;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return config;
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json, JsonOptions);
                if (loaded != null) config = loaded;
            }
            catch (Exception ex)
            {
                logError?.Invoke("读取插件配置失败，使用默认配置: " + ex.Message, ex);
            }
            return config;
        }

        public bool Save(string folder, Action<string> log, Action<string, Exception> logError)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) return false;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, FileName);
                var json = JsonSerializer.Serialize(this, JsonOptions);
                File.WriteAllText(path, json);
                log?.Invoke("配置已保存: " + path);
                return true;
            }
            catch (Exception ex)
            {
                logError?.Invoke("保存插件配置失败: " + ex.Message, ex);
                return false;
            }
        }
    }
}

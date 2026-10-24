using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ink_Canvas.Plugins;

namespace YiouCamera.Plugin.CameraProvider
{
    /// <summary>
    /// camera provider API 的**运行时反射**适配层。
    /// <para>为什么用反射而不是编译期引用 patched SDK：宿主 <c>PluginManager.FindPluginEntrance</c>
    /// 会对插件程序集调用 <c>Assembly.GetTypes()</c>，只要程序集中存在任何「实现了旧宿主不存在的接口」的类型，
    /// 旧宿主（官方 API 1.13.0）就会在加载期抛 <see cref="ReflectionTypeLoadException"/>，
    /// 插件直接无法加载、也就谈不上优雅降级。因此 provider 实现改为运行时用
    /// <see cref="DispatchProxy"/> 动态实现 <c>IPluginCameraProvider</c>：
    /// 新宿主（API ≥ 1.14.0）能探测到接口就注册；旧宿主探测不到就只标记“不支持”（不做任何系统级安装）。</para>
    /// </summary>
    internal static class ProviderReflectionApi
    {
        public const string CameraId = "yiou-booth";

        private static Type FindSdkType(string fullName)
        {
            try
            {
                var sdkAssembly = typeof(IPluginHost).Assembly;
                var type = sdkAssembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            catch
            {
            }
            try
            {
                return Type.GetType(fullName + ", InkCanvas.PluginSdk", throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        public static bool TryRegister(ICameraProviderHost host, out object providerProxy, out string error)
        {
            providerProxy = null;
            error = null;
            try
            {
                var serviceType = FindSdkType("Ink_Canvas.Plugins.ICameraProviderService");
                var providerInterfaceType = FindSdkType("Ink_Canvas.Plugins.IPluginCameraProvider");
                var sinkType = FindSdkType("Ink_Canvas.Plugins.IPluginCameraFrameSink");

                if (serviceType == null || providerInterfaceType == null || sinkType == null)
                {
                    error = "当前宿主 SDK 不含 camera provider API（需要 API ≥ 1.14.0）";
                    return false;
                }

                var cameraProviderService = GetHostService(host, serviceType);
                if (cameraProviderService == null)
                {
                    error = "宿主未注册 ICameraProviderService（API < 1.14.0）";
                    return false;
                }

                var proxy = ProviderDispatchProxy.CreateProxy(host, providerInterfaceType, sinkType);
                var register = serviceType.GetMethod("RegisterProvider", new[] { providerInterfaceType });
                if (register == null)
                {
                    error = "ICameraProviderService.RegisterProvider 签名不符合预期";
                    return false;
                }

                register.Invoke(cameraProviderService, new[] { proxy });
                providerProxy = proxy;
                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                error = "注册 provider 异常：" + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "注册 provider 异常：" + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public static void TryUnregister(ICameraProviderHost host, object providerProxy)
        {
            if (providerProxy == null) return;
            try
            {
                var serviceType = FindSdkType("Ink_Canvas.Plugins.ICameraProviderService");
                var providerInterfaceType = FindSdkType("Ink_Canvas.Plugins.IPluginCameraProvider");
                if (serviceType == null || providerInterfaceType == null) return;

                var cameraProviderService = GetHostService(host, serviceType);
                var unregister = serviceType.GetMethod("UnregisterProvider", new[] { providerInterfaceType });
                unregister?.Invoke(cameraProviderService, new[] { providerProxy });
            }
            catch (Exception ex)
            {
                host.LogError("注销 camera provider 异常", ex);
            }
        }

        private static object GetHostService(ICameraProviderHost host, Type serviceType)
        {
            try
            {
                var pluginHost = host.Host;
                if (pluginHost == null) return null;

                // IPluginHost.GetService<T>()：Initialize 阶段宿主 DI 容器尚未构建，
                // PluginManager.GetService 会回退到启动时注册的服务字典（ICameraProviderService 在其中）。
                var getService = typeof(IPluginHost)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService"
                                         && m.IsGenericMethodDefinition
                                         && m.GetParameters().Length == 0);
                if (getService != null)
                {
                    try
                    {
                        var result = getService.MakeGenericMethod(serviceType).Invoke(pluginHost, null);
                        if (result != null) return result;
                    }
                    catch (TargetInvocationException tie)
                    {
                        host.Log("GetService<" + serviceType.Name + "> 失败（可忽略）：" + (tie.InnerException ?? tie).Message);
                    }
                }

                var serviceProvider = host.ServiceProvider;
                return serviceProvider?.GetService(serviceType);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 运行时动态实现 <c>IPluginCameraProvider</c> 的代理。
        /// 通过反射构造/读取 patched SDK 的 PluginCameraDescriptor / PluginCameraCapability，
        /// 通过反射调用宿主传入的 <c>IPluginCameraFrameSink.Submit</c> 推帧。
        /// </summary>
        public class ProviderDispatchProxy : DispatchProxy
        {
            private ICameraProviderHost _host;
            private object _sink;
            private MethodInfo _submit;
            private PropertyInfo _sinkIsActive;
            private Type _descriptorType;
            private Type _capabilityType;
            private Type _descriptorListType;
            private Type _capabilityListType;

            internal static object CreateProxy(ICameraProviderHost host, Type providerInterfaceType, Type sinkType)
            {
                var proxy = (ProviderDispatchProxy)DispatchProxy.Create(providerInterfaceType, typeof(ProviderDispatchProxy));
                proxy._host = host ?? throw new ArgumentNullException(nameof(host));
                proxy._submit = sinkType.GetMethod("Submit",
                                    new[] { typeof(byte[]), typeof(int), typeof(int), typeof(int), typeof(long) })
                                ?? sinkType.GetMethod("Submit");
                proxy._sinkIsActive = sinkType.GetProperty("IsActive");

                var getCameras = providerInterfaceType.GetMethod("GetCameras");
                var listInterface = getCameras?.ReturnType;
                proxy._descriptorType = listInterface != null && listInterface.IsGenericType
                    ? listInterface.GetGenericArguments()[0]
                    : null;
                if (proxy._descriptorType != null)
                {
                    proxy._descriptorListType = typeof(List<>).MakeGenericType(proxy._descriptorType);
                    var capabilitiesProperty = proxy._descriptorType.GetProperty("Capabilities");
                    var capabilitiesType = capabilitiesProperty?.PropertyType;
                    proxy._capabilityType = capabilitiesType != null && capabilitiesType.IsGenericType
                        ? capabilitiesType.GetGenericArguments()[0]
                        : null;
                    if (proxy._capabilityType != null)
                    {
                        proxy._capabilityListType = typeof(List<>).MakeGenericType(proxy._capabilityType);
                    }
                }

                host.FrameArrived += proxy.OnFrameArrived;
                host.Log("provider 反射代理已创建（运行时实现 IPluginCameraProvider，不依赖编译期类型）");
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
                switch (targetMethod.Name)
                {
                    case "get_ProviderId":
                        return _host.ProviderId;

                    case "GetCameras":
                        return BuildCameras();

                    case "StartCamera":
                        return StartCamera(args);

                    case "StopCamera":
                        StopCamera();
                        return null;

                    case "SetCameraFormat":
                        return SetCameraFormat(args);

                    default:
                        throw new NotSupportedException("IPluginCameraProvider." + targetMethod.Name);
                }
            }

            private object BuildCameras()
            {
                var list = (IList)Activator.CreateInstance(_descriptorListType);
                if (_descriptorType == null) return list;

                var descriptor = Activator.CreateInstance(_descriptorType);
                SetProperty(descriptor, "Id", CameraId);
                SetProperty(descriptor, "DisplayName", _host.CameraDisplayName);
                SetProperty(descriptor, "SupportsPropertyControl", false);

                if (_capabilityType != null && _capabilityListType != null)
                {
                    var capabilities = (IList)Activator.CreateInstance(_capabilityListType);
                    capabilities.Add(BuildCapability(1920, 1080, 30));
                    capabilities.Add(BuildCapability(1280, 720, 30));
                    SetProperty(descriptor, "Capabilities", capabilities);
                }

                list.Add(descriptor);
                return list;
            }

            private object BuildCapability(int width, int height, int frameRate)
            {
                var capability = Activator.CreateInstance(_capabilityType);
                SetProperty(capability, "Width", width);
                SetProperty(capability, "Height", height);
                SetProperty(capability, "FrameRate", frameRate);
                return capability;
            }

            private static void SetProperty(object target, string name, object value)
            {
                try
                {
                    target?.GetType().GetProperty(name)?.SetValue(target, value);
                }
                catch
                {
                }
            }

            private bool StartCamera(object[] args)
            {
                if (args == null || args.Length < 2) return false;
                var cameraId = args[0] as string;
                if (!string.Equals(cameraId, CameraId, StringComparison.OrdinalIgnoreCase)) return false;
                if (args[1] == null) return false;

                _sink = args[1];
                _host.Log("StartCamera(" + cameraId + ")，等待宿主下发格式后启动采集");
                return _host.BeginCapture(_host.TargetWidth, _host.TargetHeight, _host.TargetFrameRate);
            }

            private void StopCamera()
            {
                _sink = null;
                _host.Log("StopCamera，停止采集");
                _host.EndCapture();
            }

            private bool SetCameraFormat(object[] args)
            {
                if (args == null || args.Length < 4) return false;
                if (!string.Equals(args[0] as string, CameraId, StringComparison.OrdinalIgnoreCase)) return false;
                int width = Convert.ToInt32(args[1]);
                int height = Convert.ToInt32(args[2]);
                int frameRate = Convert.ToInt32(args[3]);
                if (width <= 0 || height <= 0) return false;
                _host.SetTargetFormat(width, height, frameRate > 0 ? frameRate : 30);
                return true;
            }

            private void OnFrameArrived(FrameRef frame)
            {
                var sink = _sink;
                if (sink == null || frame?.Buffer == null || _submit == null) return;

                bool accepted = false;
                int byteCount = frame.Stride > 0
                    ? frame.Stride * frame.Height
                    : ((frame.Width * 3 + 3) & ~3) * frame.Height;
                try
                {
                    bool active = true;
                    try { active = _sinkIsActive == null || (bool)_sinkIsActive.GetValue(sink); } catch { }
                    if (active)
                    {
                        var result = _submit.Invoke(sink, new object[]
                        {
                            frame.Buffer, frame.Width, frame.Height, frame.Stride, frame.TimestampMs,
                        });
                        accepted = result is bool b && b;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    _host.RecordError("Submit 异常：" + (tie.InnerException ?? tie).Message);
                }
                catch (Exception ex)
                {
                    _host.RecordError("Submit 异常：" + ex.Message);
                }

                _host.RecordSubmit(accepted, byteCount);

                if (!accepted)
                {
                    _sink = null;
                    _host.Log("Submit 返回 false，宿主已停止消费，结束推流");
                    _host.EndCapture();
                }
            }
        }
    }
}

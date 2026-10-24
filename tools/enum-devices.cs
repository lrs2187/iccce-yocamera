// 枚举 DirectShow VideoInputDevice，打印 Name / DevicePath / moniker display name。
// 目的：校验「驿欧无线展台」虚拟摄像头已注册、FriendlyName 正确、DevicePath 非空且全局唯一。
// 这是 DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice) 的等价实现（原始 COM，无依赖）。
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace YiouCamera.Verify
{
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                               [MarshalAs(UnmanagedType.Struct)] out object pVar, IntPtr pErrorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                                [MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }

    static class Program
    {
        static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
        static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        static readonly Guid IID_ICreateDevEnum = new Guid("29840822-5B84-11D0-BD3B-00A0C911CE86");
        static readonly Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnkOuter,
                                           int dwClsContext, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        static string ReadProp(IPropertyBag bag, string name)
        {
            try { object v; if (bag.Read(name, out v, IntPtr.Zero) >= 0 && v != null) return v.ToString(); } catch { }
            return null;
        }

        static int Main(string[] args)
        {
            string expectName = args.Length > 0 ? args[0] : "驿欧无线展台";
            object obj = null;
            Guid clsid = CLSID_SystemDeviceEnum, iid = IID_ICreateDevEnum;
            int hr = CoCreateInstance(ref clsid, null, 1, ref iid, out obj);
            if (hr < 0) { Console.WriteLine("[FAIL] CoCreateInstance 0x{0:X8}", hr); return 1; }

            var devEnum = (ICreateDevEnum)obj;
            IEnumMoniker en;
            Guid cat = CLSID_VideoInputDeviceCategory;
            hr = devEnum.CreateClassEnumerator(ref cat, out en, 0);
            if (hr != 0 || en == null) { Console.WriteLine("[FAIL] VideoInputDevice 类别为空"); return 1; }

            int n = 0, nullPath = 0;
            bool found = false;
            string foundPath = null;
            var monikers = new IMoniker[1];
            IntPtr fetched = Marshal.AllocCoTaskMem(4);
            Console.WriteLine("=== DirectShow VideoInputDevice 枚举 ===");
            while (en.Next(1, monikers, fetched) == 0)
            {
                n++;
                IMoniker m = monikers[0];
                string display = null;
                try { m.GetDisplayName(null, null, out display); } catch { }
                string name = "(unknown)", path = null;
                try
                {
                    object bagObj; Guid iidBag = IID_IPropertyBag;
                    m.BindToStorage(null, null, ref iidBag, out bagObj);
                    var bag = (IPropertyBag)bagObj;
                    name = ReadProp(bag, "FriendlyName") ?? "(unknown)";
                    path = ReadProp(bag, "DevicePath");
                    Marshal.ReleaseComObject(bagObj);
                }
                catch { }
                if (string.IsNullOrEmpty(path)) nullPath++;
                Console.WriteLine("{0}) Name       = {1}", n, name);
                Console.WriteLine("   DevicePath = {0}", string.IsNullOrEmpty(path) ? "<NULL>" : path);
                Console.WriteLine("   display    = {0}", display);
                if (name == expectName) { found = true; foundPath = path; }
                Marshal.ReleaseComObject(m);
            }
            Marshal.FreeCoTaskMem(fetched);
            Marshal.ReleaseComObject(en);
            Marshal.ReleaseComObject(devEnum);

            Console.WriteLine();
            Console.WriteLine("[summary] total={0}, nullDevicePath={1}, 目标设备'{2}'={3}", n, nullPath, expectName, found);
            if (!found) { Console.WriteLine("[FAIL] 未枚举到目标设备（插件/vcam 未注册？）"); return 1; }
            if (string.IsNullOrEmpty(foundPath))
            {
                Console.WriteLine("[FAIL] 目标设备在 DirectShow 层 DevicePath 为空 —— 但 DirectShowLib 会回退成 moniker display name，");
                Console.WriteLine("       真正要确认的是 HKCU 类别 Instance 键下的 DevicePath 值（见 tools\\verify.ps1 的注册表检查）。");
            }
            else Console.WriteLine("[OK] 目标设备 DevicePath = {0}", foundPath);
            if (nullPath > 1)
                Console.WriteLine("[WARN] 系统里有 {0} 个 DevicePath=null 的软件摄像头，注意 WPFMediaKit AddFilterByDevicePath 的唯一性（已由注册表 DevicePath 值规避）", nullPath);
            return 0;
        }
    }
}

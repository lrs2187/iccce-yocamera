// verify-enum.cs - reproduces exactly what the ICCCE host does:
//     DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
// which internally is ICreateDevEnum::CreateClassEnumerator(CLSID_VideoInputDeviceCategory)
// + IMoniker enumeration + IPropertyBag("FriendlyName" / "DevicePath") + BindToObject.
//
// In addition to the PoC version it checks the two hard requirements from
// docs/PLAN.md section 13.1:
//   * our device's DevicePath is non-empty (i.e. the registration wrote it)
//   * our DevicePath is globally unique among all enumerated devices
//
// Usage:  verify-enum.exe [expectedFriendlyName] [expectedDevicePath]
// Exit code 0 = PASS.
//
// No NuGet / DirectShow.NET required: raw COM interop against quartz.dll.
// Build:  csc.exe /nologo /platform:x64 /out:verify-enum.exe verify-enum.cs
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VcamVerify
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

    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770")]
    interface IBaseFilter { }

    static class Program
    {
        const string DefaultName = "\u9A7F\u6B27\u65E0\u7EBF\u5C55\u53F0";  // 驿欧无线展台
        const string DefaultPath = "yocam:yiou-wireless-dc";

        static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
        static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        static readonly Guid IID_IBaseFilter = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
        static readonly Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
        static readonly Guid IID_ICreateDevEnum = new Guid("29840822-5B84-11D0-BD3B-00A0C911CE86");
        static readonly Guid CLSID_YoCameraVirtualCam = new Guid("9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31");

        const int CLSCTX_INPROC_SERVER = 1;

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnkOuter,
                                            int dwClsContext, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        static bool ReadBagString(IPropertyBag bag, string name, out string value)
        {
            value = null;
            try
            {
                object v;
                if (bag.Read(name, out v, IntPtr.Zero) >= 0 && v != null)
                {
                    value = v.ToString();
                    return true;
                }
            }
            catch { }
            return false;
        }

        static int Main(string[] args)
        {
            string wantName = args.Length > 0 && args[0].Length > 0 ? args[0] : DefaultName;
            string wantPath = args.Length > 1 && args[1].Length > 0 ? args[1] : DefaultPath;

            Guid clsidSysDevEnum = CLSID_SystemDeviceEnum;
            Guid iidCreateDevEnum = IID_ICreateDevEnum;
            object obj = null;
            int hr = CoCreateInstance(ref clsidSysDevEnum, null, CLSCTX_INPROC_SERVER,
                                      ref iidCreateDevEnum, out obj);
            if (hr < 0) { Console.WriteLine("[FAIL] CoCreateInstance(SystemDeviceEnum) 0x{0:X8}", hr); return 1; }

            var devEnum = (ICreateDevEnum)obj;
            IEnumMoniker enumMoniker = null;
            Guid cat = CLSID_VideoInputDeviceCategory;
            hr = devEnum.CreateClassEnumerator(ref cat, out enumMoniker, 0);
            if (hr != 0 || enumMoniker == null)
            {
                Console.WriteLine("[FAIL] CreateClassEnumerator(VideoInputDevice) hr=0x{0:X8} (no devices)", hr);
                return 1;
            }

            int n = 0;
            int ourIndex = -1;
            bool activated = false;
            int samePath = 0;
            int nullPaths = 0;
            string ourName = null;
            string ourPath = null;
            bool nameOk = false;
            bool pathOk = false;

            IMoniker[] monikers = new IMoniker[1];
            IntPtr fetched = Marshal.AllocCoTaskMem(4);
            try
            {
                while (enumMoniker.Next(1, monikers, fetched) == 0)
                {
                    n++;
                    IMoniker m = monikers[0];
                    string display = null;
                    try { m.GetDisplayName(null, null, out display); } catch { }

                    string friendly = "(unknown)";
                    string devicePath = null;
                    try
                    {
                        object bagObj;
                        Guid iidBag = IID_IPropertyBag;
                        m.BindToStorage(null, null, ref iidBag, out bagObj);
                        var bag = (IPropertyBag)bagObj;
                        ReadBagString(bag, "FriendlyName", out friendly);
                        ReadBagString(bag, "DevicePath", out devicePath);
                        Marshal.ReleaseComObject(bagObj);
                    }
                    catch { }

                    if (devicePath == null || devicePath.Length == 0) nullPaths++;
                    else if (devicePath == wantPath) samePath++;

                    bool isOurs = (display != null &&
                        display.ToUpperInvariant().Contains(CLSID_YoCameraVirtualCam.ToString("B").ToUpperInvariant()));
                    if (isOurs)
                    {
                        ourIndex = n;
                        ourName = friendly;
                        ourPath = devicePath;
                        nameOk = (friendly == wantName);
                        pathOk = (devicePath != null && devicePath.Length > 0 && devicePath == wantPath);
                    }

                    Console.WriteLine("{0}) FriendlyName: {1}", n, friendly);
                    Console.WriteLine("     display:    {0}", display);
                    Console.WriteLine("     DevicePath: {0}", string.IsNullOrEmpty(devicePath) ? "<NULL>" : devicePath);

                    if (isOurs)
                    {
                        try
                        {
                            object filterObj;
                            Guid iidFilter = IID_IBaseFilter;
                            m.BindToObject(null, null, ref iidFilter, out filterObj);
                            if (filterObj != null) { activated = true; Marshal.ReleaseComObject(filterObj); }
                            Console.WriteLine("     BindToObject(IBaseFilter): OK");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("     BindToObject(IBaseFilter): FAILED " + ex.Message);
                        }
                    }
                    Marshal.ReleaseComObject(m);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(fetched);
                if (enumMoniker != null) Marshal.ReleaseComObject(enumMoniker);
                Marshal.ReleaseComObject(devEnum);
            }

            Console.WriteLine("[enum] total VideoInputDevice = {0}", n);
            Console.WriteLine("[enum] devices with DevicePath=<NULL>: {0}", nullPaths);

            bool pass = true;
            if (ourIndex < 0) { Console.WriteLine("[FAIL] our virtual camera is NOT enumerated"); pass = false; }
            else
            {
                Console.WriteLine("[enum] found our device at #{0}: name='{1}' path='{2}'",
                                  ourIndex, ourName, ourPath);
                if (!nameOk)
                {
                    Console.WriteLine("[FAIL] FriendlyName mismatch: expected '{0}' got '{1}'", wantName, ourName);
                    pass = false;
                }
                if (!pathOk)
                {
                    Console.WriteLine("[FAIL] our DevicePath is null or not '{0}' (got '{1}')", wantPath, ourPath);
                    pass = false;
                }
                if (samePath != 1)
                {
                    Console.WriteLine("[FAIL] DevicePath '{0}' is not unique: {1} device(s) report it", wantPath, samePath);
                    pass = false;
                }
                if (!activated)
                {
                    Console.WriteLine("[FAIL] enumerated but BindToObject(IBaseFilter) failed");
                    pass = false;
                }
            }

            if (pass)
            {
                Console.WriteLine("[PASS] host-style enumeration: name + unique DevicePath + BindToObject all OK");
                return 0;
            }
            return 1;
        }
    }
}

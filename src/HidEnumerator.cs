using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PeripheralBattery
{
    internal sealed class HidDeviceInfo
    {
        public string DevicePath;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
        public string ProductName;
        public ushort UsagePage;
        public ushort Usage;
        public int InputReportLength;
        public int OutputReportLength;
        public int FeatureReportLength;

        public string Describe()
        {
            string kind = HidEnumerator.GetUsageDescription(UsagePage, Usage);
            return kind
                + " | VID_" + VendorId.ToString("X4")
                + " PID_" + ProductId.ToString("X4")
                + " | " + ProductName;
        }
    }

    /// <summary>
    /// 枚举 HID 设备并读取 VID/PID、用途页和报告长度。
    /// 对应参考网页中 WebHID 的 getDevices()/collections 信息。
    /// </summary>
    internal static class HidEnumerator
    {
        private static readonly Guid HidInterfaceGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

        internal static List<HidDeviceInfo> Enumerate()
        {
            List<HidDeviceInfo> devices = new List<HidDeviceInfo>();
            Guid interfaceGuid = HidInterfaceGuid;
            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == InvalidHandleValue)
            {
                return devices;
            }

            try
            {
                SP_DEVICE_INTERFACE_DATA interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf(interfaceData);

                for (int index = 0;
                     SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData);
                     index++)
                {
                    uint requiredSize;
                    SetupDiGetDeviceInterfaceDetail(
                        deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);

                    if (requiredSize == 0)
                    {
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(
                                deviceInfoSet, ref interfaceData, detail, requiredSize, out requiredSize, IntPtr.Zero))
                        {
                            continue;
                        }

                        string path = Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
                        HidDeviceInfo info = ReadDevice(path);
                        if (info != null)
                        {
                            devices.Add(info);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return devices;
        }

        private static HidDeviceInfo ReadDevice(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            IntPtr handle = CreateFile(
                path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle == InvalidHandleValue)
            {
                return null;
            }

            try
            {
                HidDeviceInfo info = new HidDeviceInfo();
                info.DevicePath = path;

                HIDD_ATTRIBUTES attributes = new HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(attributes);
                if (!HidD_GetAttributes(handle, ref attributes))
                {
                    return null;
                }

                info.VendorId = attributes.VendorID;
                info.ProductId = attributes.ProductID;
                info.VersionNumber = attributes.VersionNumber;

                StringBuilder productName = new StringBuilder(256);
                if (HidD_GetProductString(handle, productName, productName.Capacity))
                {
                    info.ProductName = productName.ToString().Trim();
                }

                IntPtr preparsedData;
                if (HidD_GetPreparsedData(handle, out preparsedData))
                {
                    try
                    {
                        HIDP_CAPS caps = new HIDP_CAPS();
                        caps.Reserved = new ushort[17];
                        if (HidP_GetCaps(preparsedData, ref caps) >= 0)
                        {
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                            info.FeatureReportLength = caps.FeatureReportByteLength;
                        }
                    }
                    finally
                    {
                        HidD_FreePreparsedData(preparsedData);
                    }
                }

                return info;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        internal static string GetUsageDescription(ushort usagePage, ushort usage)
        {
            if (usagePage >= 0xFF00)
            {
                return "厂商自定义（可承载电量协议）";
            }

            if (usagePage == 0x01)
            {
                if (usage == 0x02)
                {
                    return "鼠标";
                }

                if (usage == 0x06)
                {
                    return "键盘";
                }

                if (usage == 0x05)
                {
                    return "游戏手柄";
                }
            }

            if (usagePage == 0x0C)
            {
                return "消费类控制";
            }

            if (usagePage == 0x0B)
            {
                return "传感器";
            }

            return "UsagePage 0x" + usagePage.ToString("X4");
        }
    }
}

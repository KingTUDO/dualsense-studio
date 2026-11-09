#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DualSenseStudio
{
    internal sealed class DualSenseDevice : IDisposable
    {
        private const string UsbInterfacePrefix = @"\\?\hid#vid_054c&pid_0ce6&mi_";
        private const int UsbInputLength = 64;
        private const int MinimumOutputLength = 48;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareReadWrite = 3;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint PresentDeviceInterfaces = 0x12;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorNoMoreItems = 259;
        private const int HidSuccess = 0x00110000;

        private readonly FileStream input;
        private readonly FileStream output;
        private readonly int outputReportLength;

        internal string DevicePath { get; }
        internal int InputReportLength { get { return UsbInputLength; } }

        private DualSenseDevice(string path, FileStream input, FileStream output, int outputReportLength)
        {
            DevicePath = path;
            this.input = input;
            this.output = output;
            this.outputReportLength = outputReportLength;
        }

        internal static string[] EnumerateDevicePaths()
        {
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr devices = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, PresentDeviceInterfaces);
            if (devices == new IntPtr(-1))
                throw WindowsError("Falha ao listar os dispositivos HID");

            var paths = new List<string>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new DeviceInterfaceData { Size = Marshal.SizeOf(typeof(DeviceInterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hidGuid, index, ref data))
                    {
                        if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                            break;
                        throw WindowsError("Falha ao localizar a interface USB do controle");
                    }

                    string path = GetInterfacePath(devices, ref data);
                    if (IsDualSenseUsbPath(path))
                        paths.Add(path);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devices);
            }

            return paths.ToArray();
        }

        internal static DualSenseDevice OpenFirst()
        {
            IOException? lastError = null;
            foreach (string path in EnumerateDevicePaths())
            {
                try
                {
                    return Open(path);
                }
                catch (IOException error)
                {
                    lastError = error;
                }
            }

            throw new IOException("Conecte um DualSense padrão ao PC por um cabo USB de dados.", lastError);
        }

        internal static DualSenseDevice Open(string path)
        {
            if (!IsDualSenseUsbPath(path))
                throw new ArgumentException("Selecione a interface USB de um DualSense padrão.", nameof(path));

            SafeFileHandle inputHandle = OpenHandle(path, GenericRead);
            SafeFileHandle? outputHandle = null;
            FileStream? inputStream = null;
            try
            {
                HidCapabilities capabilities = ReadCapabilities(inputHandle);
                outputHandle = OpenHandle(path, GenericWrite);

                // Each stream owns a separate overlapped handle. A buffer of 1 disables FileStream buffering.
                inputStream = new FileStream(inputHandle, FileAccess.Read, 1, true);
                var outputStream = new FileStream(outputHandle, FileAccess.Write, 1, true);
                return new DualSenseDevice(path, inputStream, outputStream, capabilities.OutputReportByteLength);
            }
            catch
            {
                if (inputStream != null)
                    inputStream.Dispose();
                else
                    inputHandle.Dispose();
                if (outputHandle != null)
                    outputHandle.Dispose();
                throw;
            }
        }

        internal async Task<int> ReadReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            if (report.Length < UsbInputLength)
                throw new ArgumentException("O buffer de entrada deve ter pelo menos 64 bytes.", nameof(report));

            int count = await input.ReadAsync(report, 0, UsbInputLength, cancellationToken).ConfigureAwait(false);
            if (count != UsbInputLength || report[0] != 0x01)
                throw new IOException("O DualSense retornou um relatório USB de entrada inesperado.");
            return count;
        }

        internal Task SetLightbarAsync(byte red, byte green, byte blue, CancellationToken cancellationToken)
        {
            // USB report 0x02, with only the lightbar control bit set.
            // Layout: Linux hid-playstation.c, dualsense_output_report_common.
            var report = new byte[outputReportLength];
            report[0] = 0x02;
            report[2] = 0x04;
            report[45] = red;
            report[46] = green;
            report[47] = blue;
            return output.WriteAsync(report, 0, report.Length, cancellationToken);
        }

        public void Dispose()
        {
            try
            {
                input.Dispose();
            }
            finally
            {
                output.Dispose();
            }
        }

        private static bool IsDualSenseUsbPath(string? path)
        {
            return path != null && path.StartsWith(UsbInterfacePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetInterfacePath(IntPtr devices, ref DeviceInterfaceData data)
        {
            SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size == 0)
                throw WindowsError("Falha ao obter o tamanho da interface HID");

            IntPtr detail = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                // The Unicode detail structure starts with a DWORD followed by the path.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, size, out _, IntPtr.Zero))
                    throw WindowsError("Falha ao ler o endereço da interface HID");
                return Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(int))) ??
                    throw new IOException("O Windows retornou um endereço de interface HID vazio.");
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        private static SafeFileHandle OpenHandle(string path, uint access)
        {
            SafeFileHandle handle = CreateFile(path, access, ShareReadWrite, IntPtr.Zero,
                OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (!handle.IsInvalid)
                return handle;

            IOException error = WindowsError("Falha ao abrir o DualSense por USB");
            handle.Dispose();
            throw error;
        }

        private static HidCapabilities ReadCapabilities(SafeFileHandle handle)
        {
            var attributes = new HidAttributes { Size = Marshal.SizeOf(typeof(HidAttributes)) };
            if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != 0x054c || attributes.ProductId != 0x0ce6)
                throw new IOException("A interface não corresponde ao DualSense padrão.");
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                throw new IOException("O Windows não conseguiu ler o descritor HID do DualSense.");
            try
            {
                if (HidP_GetCaps(preparsed, out HidCapabilities caps) != HidSuccess ||
                    caps.UsagePage != 0x01 || caps.Usage != 0x05 ||
                    caps.InputReportByteLength != UsbInputLength || caps.OutputReportByteLength < MinimumOutputLength)
                    throw new IOException("A interface USB não tem o formato de relatórios esperado.");
                return caps;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        private static IOException WindowsError(string message)
        {
            var cause = new Win32Exception(Marshal.GetLastWin32Error());
            return new IOException(message + ". " + cause.Message, cause);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        // Reserve the full native HIDP_CAPS structure, including fields this class does not use.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct HidCapabilities
        {
            [FieldOffset(0)] public ushort Usage;
            [FieldOffset(2)] public ushort UsagePage;
            [FieldOffset(4)] public ushort InputReportByteLength;
            [FieldOffset(6)] public ushort OutputReportByteLength;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HidCapabilities capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr deviceInfo, ref Guid interfaceGuid,
            uint index, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterfaceData interfaceData,
            IntPtr detailData, uint detailSize, out uint requiredSize, IntPtr deviceInfo);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
    }
}

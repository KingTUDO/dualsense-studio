#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DualSenseStudio;

internal sealed class InputIsolation : IDisposable
{
    private const string ControlPath = @"\\.\HidHide";
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const int InsufficientBuffer = 122;
    private const int InvalidFunction = 1;
    private const int InvalidParameter = 87;
    private const int NotSupported = 50;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly string journalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualSenseStudio", "hid-isolation.json");
    private FileStream? sessionLock;
    private bool sessionCloak;

    public bool IsAvailable
    {
        get
        {
            using var driver = CreateFile(ControlPath, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (driver.IsInvalid)
                return false;
            try
            {
                ReadFlag(driver, Control.GetActive);
                return true;
            }
            catch (Exception error) when (error is Win32Exception or IOException)
            {
                return false;
            }
        }
    }

    public bool IsActive { get; private set; }

    public string? Recover()
    {
        if (IsActive)
            throw new InvalidOperationException("Desative o isolamento atual antes de tentar restaurar.");
        if (!File.Exists(journalPath))
            return null;

        using var recoveryLock = TakeSessionLock();
        return RestorePending();
    }

    public string Start(string hidDevicePath, params string[] launcherExecutables)
    {
        if (sessionLock != null)
            throw new InvalidOperationException("Encerre a sessão de isolamento atual primeiro.");

        sessionLock = TakeSessionLock();
        try
        {
            RestorePending();
            var device = ResolveDualSense(hidDevicePath);
            var application = GetApplicationImage();
            using var driver = OpenDriver();
            var wasEnabled = ReadFlag(driver, Control.GetActive);
            if (ReadFlag(driver, Control.GetInverse))
                throw new InvalidOperationException("O HidHide está com a lista de aplicativos invertida. Revise a opção no HidHide antes de iniciar.");

            var hidden = ReadList(driver, Control.GetBlacklist);
            if (hidden.Any(entry => entry.Contains('!') && PathComparer.Equals(entry.Split('!')[0], device)))
                throw new InvalidOperationException("O HidHide já tem uma regra de sessão para este controle. Revise a regra no HidHide antes de iniciar.");
            if (!wasEnabled && hidden.Any(entry => !PathComparer.Equals(entry, device)))
                throw new InvalidOperationException("Ativar o HidHide agora também ocultaria outros dispositivos já listados. Revise a lista no HidHide antes de iniciar.");

            var allowed = ReadList(driver, Control.GetWhitelist);
            var addApplication = !allowed.Contains(application, PathComparer);
            var addDevice = !hidden.Contains(device, PathComparer);
            var launcherStatus = string.Concat(launcherExecutables.Select(executable => DescribeLauncher(allowed, executable)));

            // Signed older releases do not expose this optional IOCTL. In newer
            // releases the driver automatically clears these entries on process exit.
            if (wasEnabled && addDevice && !addApplication && TryAddSessionCloak(driver, device))
            {
                sessionCloak = true;
            }
            else if (addDevice || addApplication || !wasEnabled)
            {
                SaveJournal(new IsolationJournal(1, device, application, addDevice, addApplication, !wasEnabled));
                if (addApplication)
                    WriteList(driver, Control.SetWhitelist, allowed.Append(application));
                if (addDevice)
                    WriteList(driver, Control.SetBlacklist, hidden.Append(device));
                if (!wasEnabled)
                    WriteFlag(driver, Control.SetActive, true);
                VerifyActive(driver, device, application);
            }

            IsActive = true;
            return "Isolamento configurado para o DualSense selecionado. Reconecte o controle na mesma porta USB e abra o jogo novamente."
                + launcherStatus;
        }
        catch (Exception startError)
        {
            try
            {
                RestorePending();
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException(
                    "O HidHide não pôde ser restaurado. Feche o configurador HidHide e reabra o DualSense Studio para tentar novamente.",
                    new AggregateException(startError, rollbackError));
            }
            finally
            {
                ReleaseSessionLock();
            }
            throw;
        }
    }

    public void Stop()
    {
        if (sessionLock == null)
            return;

        if (sessionCloak)
        {
            using var driver = OpenDriver();
            Request(driver, Control.ClearSessionBlacklist, null, null);
            sessionCloak = false;
        }

        RestorePending();
        IsActive = false;
        ReleaseSessionLock();
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        finally
        {
            ReleaseSessionLock();
        }
    }

    private FileStream TakeSessionLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        try
        {
            return new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("Outra instância pode estar gerenciando o HidHide. Feche-a e tente novamente.", error);
        }
    }

    private void ReleaseSessionLock()
    {
        sessionLock?.Dispose();
        sessionLock = null;
    }

    private void SaveJournal(IsolationJournal journal)
    {
        var temporary = journalPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, journalPath, overwrite: false);
    }

    private string? RestorePending()
    {
        if (!File.Exists(journalPath))
            return null;

        var journal = JsonSerializer.Deserialize<IsolationJournal>(File.ReadAllText(journalPath));
        if (journal == null || journal.Version != 1 || !IsSonyInstance(journal.Device)
            || !IsNativeExecutable(journal.Application))
            throw new InvalidDataException("O registro de recuperação do HidHide está inválido.");

        using var driver = OpenDriver();
        var hidden = ReadList(driver, Control.GetBlacklist);
        var allowed = ReadList(driver, Control.GetWhitelist);

        // Keep the user's newer cloak configuration if someone added an
        // unrelated device while the app's session was active.
        var keepEnabled = journal.EnabledCloak && hidden.Any(entry => !PathComparer.Equals(entry, journal.Device));
        if (journal.EnabledCloak && !keepEnabled && ReadFlag(driver, Control.GetActive))
            WriteFlag(driver, Control.SetActive, false);
        if (journal.AddedDevice && hidden.RemoveAll(entry => PathComparer.Equals(entry, journal.Device)) != 0)
            WriteList(driver, Control.SetBlacklist, hidden);
        if (journal.AddedApplication && allowed.RemoveAll(entry => PathComparer.Equals(entry, journal.Application)) != 0)
            WriteList(driver, Control.SetWhitelist, allowed);

        if ((journal.AddedDevice && ReadList(driver, Control.GetBlacklist).Contains(journal.Device, PathComparer))
            || (journal.AddedApplication && ReadList(driver, Control.GetWhitelist).Contains(journal.Application, PathComparer))
            || (journal.EnabledCloak && !keepEnabled && ReadFlag(driver, Control.GetActive)))
            throw new IOException("O HidHide não confirmou a restauração da sessão anterior.");

        File.Delete(journalPath);
        return keepEnabled
            ? "Isolamento anterior removido. O HidHide segue ativo para os outros dispositivos da lista."
            : "Configuração do HidHide restaurada. Reconecte o controle se necessário.";
    }

    private static string DescribeLauncher(List<string> allowed, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return "";

        try
        {
            var fullPath = Path.GetFullPath(executable);
            if (!Path.IsPathFullyQualified(executable) || !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return " Escolha o caminho completo do executável do launcher para verificar a lista do HidHide.";

            using var file = CreateFile(fullPath, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (file.IsInvalid)
                return " O executável do launcher selecionado não pôde ser verificado.";

            const uint volumeNameNt = 2;
            var nativePath = new StringBuilder(32768);
            var size = GetFinalPathNameByHandle(file, nativePath, (uint)nativePath.Capacity, volumeNameNt);
            if (size == 0 || size >= nativePath.Capacity)
                return " O caminho do launcher não pôde ser verificado no HidHide.";

            var name = Path.GetFileName(fullPath);
            return allowed.Contains(nativePath.ToString(), PathComparer)
                ? $" {name} já está na lista de exceções do HidHide e pode continuar vendo o controle físico."
                : $" {name} não está na lista de exceções do HidHide.";
        }
        catch (ArgumentException)
        {
            return " O caminho do launcher é inválido.";
        }
    }

    private static string GetApplicationImage()
    {
        var processPath = new StringBuilder(32768);
        var capacity = processPath.Capacity;
        const uint processNameNative = 1;
        if (!QueryFullProcessImageName(GetCurrentProcess(), processNameNative, processPath, ref capacity))
            throw NativeError("identificar o executável do DualSense Studio");
        var path = processPath.ToString();
        var name = Path.GetFileName(path);
        if (!IsNativeExecutable(path) || PathComparer.Equals(name, "dotnet.exe")
            || PathComparer.Equals(name, "testhost.exe"))
            throw new InvalidOperationException("Abra o executável do DualSense Studio para usar o isolamento.");
        return path;
    }

    private static bool IsNativeExecutable(string? path) =>
        path != null && path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Contains('\0');

    private static string ResolveDualSense(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Selecione um DualSense conectado antes de iniciar o isolamento.");

        if (IsSonyInstance(path))
        {
            var hidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
            if (CM_Get_Device_Interface_List_Size(out var chars, ref hidGuid, path, 0) == 0 && chars > 1)
            {
                var buffer = new char[chars];
                if (CM_Get_Device_Interface_List(ref hidGuid, path, buffer, chars, 0) == 0)
                {
                    var selected = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (selected.Any(IsSonyGamepadInterface))
                        return path;
                }
            }
            throw new InvalidOperationException("A instância selecionada não é um DualSense acessível nesta conexão.");
        }

        if (!path.StartsWith(@"\\?\hid#", StringComparison.OrdinalIgnoreCase) || !IsSonyGamepadInterface(path))
            throw new InvalidOperationException("A interface selecionada não é de um DualSense compatível.");

        var devices = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (devices == new IntPtr(-1))
            throw NativeError("abrir a lista de dispositivos do Windows");
        try
        {
            var deviceInterface = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            if (!SetupDiOpenDeviceInterface(devices, path, 0, ref deviceInterface))
                throw NativeError("identificar a interface HID do DualSense");
            var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            if (!SetupDiGetDeviceInterfaceDetail(devices, ref deviceInterface, IntPtr.Zero, 0, out _, ref device)
                && Marshal.GetLastWin32Error() != InsufficientBuffer)
                throw NativeError("identificar o dispositivo físico do DualSense");
            var instance = new StringBuilder(256);
            if (!SetupDiGetDeviceInstanceId(devices, ref device, instance, (uint)instance.Capacity, out _))
                throw NativeError("ler o identificador físico do DualSense");
            var id = instance.ToString();
            if (!IsSonyInstance(id))
                throw new InvalidOperationException("A interface não corresponde a uma instância HID DualSense.");
            return id;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devices);
        }
    }

    private static bool IsSonyInstance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('!') || value.Contains('\0') || value.Length >= 200)
            return false;
        var components = value.Split('\\');
        if (components.Length != 3 || !PathComparer.Equals(components[0], "HID") || components[2].Length == 0)
            return false;
        var hardware = components[1];
        return hardware.StartsWith("VID_054C&PID_0CE6", StringComparison.OrdinalIgnoreCase)
            || hardware.StartsWith("VID_054C&PID_0DF2", StringComparison.OrdinalIgnoreCase)
            || (hardware.StartsWith("{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&",
                    StringComparison.OrdinalIgnoreCase)
                && (hardware.Contains("_PID&0CE6", StringComparison.OrdinalIgnoreCase)
                    || hardware.Contains("_PID&0DF2", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSonyGamepadInterface(string path)
    {
        using var device = CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (device.IsInvalid)
            return false;
        var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(device, ref attributes) || attributes.VendorId != 0x054C
            || (attributes.ProductId != 0x0CE6 && attributes.ProductId != 0x0DF2)
            || !HidD_GetPreparsedData(device, out var data))
            return false;

        try
        {
            // HIDP_CAPS begins with Usage and UsagePage (USHORT each).
            var caps = new byte[64];
            return HidP_GetCaps(data, caps) == 0x00110000 && BitConverter.ToUInt16(caps, 2) == 1
                && BitConverter.ToUInt16(caps, 0) == 5;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    private static bool TryAddSessionCloak(SafeFileHandle driver, string instance)
    {
        try
        {
            Request(driver, Control.AddSessionBlacklist, EncodeList(new[] { instance }), null);
            return true;
        }
        catch (Win32Exception error) when (
            error.NativeErrorCode is InvalidFunction or InvalidParameter or NotSupported)
        {
            return false;
        }
    }

    private static void VerifyActive(SafeFileHandle driver, string device, string application)
    {
        if (!ReadFlag(driver, Control.GetActive) || ReadFlag(driver, Control.GetInverse)
            || !ReadList(driver, Control.GetBlacklist).Contains(device, PathComparer)
            || !ReadList(driver, Control.GetWhitelist).Contains(application, PathComparer))
            throw new IOException("O HidHide não confirmou o isolamento do controle selecionado.");
    }

    private static SafeFileHandle OpenDriver()
    {
        var driver = CreateFile(ControlPath, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!driver.IsInvalid)
            return driver;

        var error = Marshal.GetLastWin32Error();
        driver.Dispose();
        throw new Win32Exception(error, "HidHide indisponível. Confirme se o driver está instalado e feche o configurador HidHide.");
    }

    private static bool ReadFlag(SafeFileHandle driver, Control command)
    {
        var output = new byte[1];
        if (Request(driver, command, null, output) != 1 || output[0] > 1)
            throw new IOException("O HidHide retornou um estado inválido.");
        return output[0] != 0;
    }

    private static void WriteFlag(SafeFileHandle driver, Control command, bool value) =>
        Request(driver, command, new[] { value ? (byte)1 : (byte)0 }, null);

    private static List<string> ReadList(SafeFileHandle driver, Control command)
    {
        var size = Request(driver, command, null, null);
        if (size < 2 || size > 1024 * 1024 || size % 2 != 0)
            throw new IOException("O HidHide retornou uma lista inválida.");
        var output = new byte[size];
        var received = Request(driver, command, null, output);
        if (received != size || output[^1] != 0 || output[^2] != 0)
            throw new IOException("O HidHide retornou uma lista incompleta.");
        return Encoding.Unicode.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static byte[] EncodeList(IEnumerable<string> entries)
    {
        var values = string.Join("\0", entries);
        return Encoding.Unicode.GetBytes(values + "\0\0");
    }

    private static void WriteList(SafeFileHandle driver, Control command, IEnumerable<string> entries) =>
        Request(driver, command, EncodeList(entries), null);

    private static int Request(SafeFileHandle driver, Control command, byte[]? input, byte[]? output)
    {
        // CTL_CODE(32769, function, METHOD_BUFFERED, FILE_READ_DATA).
        var code = 0x80014000u | ((uint)command << 2);
        if (!DeviceIoControl(driver, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out var size, IntPtr.Zero))
            throw NativeError("acessar a configuração do HidHide");
        return size;
    }

    private static Win32Exception NativeError(string action)
    {
        var error = Marshal.GetLastWin32Error();
        var detail = new Win32Exception(error).Message;
        return new Win32Exception(error, $"Falha ao {action}. {detail}");
    }

    private sealed record IsolationJournal(int Version, string Device, string Application,
        bool AddedDevice, bool AddedApplication, bool EnabledCloak);

    private enum Control : uint
    {
        GetWhitelist = 2048,
        SetWhitelist,
        GetBlacklist,
        SetBlacklist,
        GetActive,
        SetActive,
        GetInverse,
        AddSessionBlacklist = 2056,
        ClearSessionBlacklist
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Version;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        [Out] byte[]? output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInterface(IntPtr devices, string path, uint flags, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterfaceData interfaceData,
        IntPtr detail, uint size, out uint needed, ref DeviceInfoData data);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr devices, ref DeviceInfoData device, StringBuilder instance,
        uint size, out uint needed);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint chars, ref Guid classGuid, string instance, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List(ref Guid classGuid, string instance, [Out] char[] paths,
        uint chars, uint flags);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, [Out] byte[] caps);
}

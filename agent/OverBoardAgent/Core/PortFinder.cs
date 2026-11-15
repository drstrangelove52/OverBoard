using System.IO.Ports;
using Microsoft.Win32;

namespace OverBoardAgent.Core;

/// <summary>Findet die COM-Schnittstellen des OverBoard-Geräts (USB-ID 303A:4001) anhand der Windows-Registrierung.</summary>
public static class PortFinder
{
    private const string UsbId = "VID_303A&PID_4001";

    /// <summary>
    /// COM-Schnittstellen der UART-Buchse des Geräts (USB-UART-Chip, z. B. CH340: VID 1A86). Über sie wird die Firmware installiert;
    /// die Buchse "USB 2.0" (303A:4001) ist dagegen die normale Verbindung zum Agent.
    /// </summary>
    public static IReadOnlyList<string> FindUartPorts()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return result;
            foreach (var device in usb.GetSubKeyNames())
            {
                if (!device.StartsWith("VID_1A86&", StringComparison.OrdinalIgnoreCase)) continue;
                using var deviceKey = usb.OpenSubKey(device);
                if (deviceKey == null) continue;
                foreach (var instance in deviceKey.GetSubKeyNames())
                {
                    using var parameters = deviceKey.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && present.Contains(port) && !result.Contains(port))
                        result.Add(port);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Registrierung konnte nicht gelesen werden: {ex.Message}");
        }
        return result;
    }

    public static IReadOnlyList<string> Find()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return result;
            foreach (var device in usb.GetSubKeyNames())
            {
                // Das Gerät ist ein Verbund-Gerät: der COM-Port liegt oft unter "...&MI_00"
                if (!device.StartsWith(UsbId, StringComparison.OrdinalIgnoreCase)) continue;
                using var deviceKey = usb.OpenSubKey(device);
                if (deviceKey == null) continue;
                foreach (var instance in deviceKey.GetSubKeyNames())
                {
                    using var parameters = deviceKey.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && present.Contains(port) && !result.Contains(port))
                        result.Add(port);   // nur Schnittstellen, die gerade wirklich vorhanden sind
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Registrierung konnte nicht gelesen werden: {ex.Message}");
        }
        return result;
    }
}

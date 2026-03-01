using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp.Extensions;

public static class ProcessExtensions
{
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileNameEx(nint hProcess, nint hModule, [Out] StringBuilder lpBaseName, [In][MarshalAs(UnmanagedType.U4)] int nSize);

    public static Process[] ActiveProcesses()
    {
        return Process.GetProcesses();
    }

    public static Process[] ActiveProcessesFiltered()
    {
        return ActiveProcesses().DistinctBy(x => x.ProcessName).ToArray();
    }

    public static Process? FromActiveProcess(string processName)
    {
        var activeProcceses = Process.GetProcesses();
        return activeProcceses.FirstOrDefault(p => p.ProcessName.Equals(Path.GetFileNameWithoutExtension(processName)));
    }

    public static bool IsWindowedProcess(this Process process)
    {
        return process.MainWindowHandle != nint.Zero;
    }

    public static bool IsThisProcess(this Process process)
    {
        return process.Id == Environment.ProcessId;
    }

    public static string GetPathFromProcessId(this Process process)
    {
        nint handle = -1;
        try { handle = process.Handle; } catch (Win32Exception) { }
        if (handle < 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder(1024);
        if (GetModuleFileNameEx(handle, nint.Zero, sb, sb.Capacity) > 0)
        {
            return sb.ToString();
        }
        return string.Empty;
    }

    public static Icon? GetIcon(this Process process)
    {
        string path = process.GetPathFromProcessId();
        return Icon.ExtractAssociatedIcon(path);
    }
}

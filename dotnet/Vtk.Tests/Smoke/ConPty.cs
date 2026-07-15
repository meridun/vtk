using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Runs a child process attached to a Windows pseudo console (ConPTY), so the
/// child's stdout is a genuine console handle and its own isatty check
/// (Console.IsOutputRedirected in vtk) reports interactive. This is the
/// Windows analog of the Unix PTY the deleted Go smoke used (creack/pty in
/// test/smoke/pty_smoke_test.go, which was build-tagged !windows): a real
/// (pseudo) terminal is the only way to exercise vtk's interactive TTY-bypass
/// branch end to end through the real binary.
/// </summary>
internal static class ConPty
{
    /// <summary>Runs commandLine under a pseudo console. Returns everything the child wrote to the terminal (VT sequences included) and its exit code.</summary>
    public static (string output, int exitCode) Run(string commandLine, string workingDir, IDictionary<string, string> extraEnv, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ConPTY is Windows-only");

        // Pipes: we write to the pty's input (unused here) and drain its output.
        if (!CreatePipe(out var ptyInRead, out var ptyInWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        if (!CreatePipe(out var ptyOutRead, out var ptyOutWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        // Tall pseudo-screen so line output is not rewrapped/paged strangely.
        var hr = CreatePseudoConsole(new COORD { X = 160, Y = 500 }, ptyInRead, ptyOutWrite, 0, out var hpc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");
        // ConPTY duplicated its ends; release ours.
        CloseHandle(ptyInRead);
        CloseHandle(ptyOutWrite);

        // Attribute list carrying the pseudoconsole to CreateProcess.
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            // STARTF_USESTDHANDLES with NULL handles: without it the child
            // inherits this process's std handle *values* (under a test
            // runner those are pipes, invalid in the child), and its output
            // never reaches the pseudoconsole. NULL std handles make console
            // initialization bind them to the pseudoconsole instead.
            const int STARTF_USESTDHANDLES = 0x00000100;
            var siEx = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = IntPtr.Zero,
                    hStdError = IntPtr.Zero,
                },
                lpAttributeList = attrList,
            };

            // Child environment: current process env plus the overrides,
            // as a double-NUL-terminated unicode block.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string?)e.Value ?? "";
            foreach (var (k, v) in extraEnv) env[k] = v;
            var envBlock = new StringBuilder();
            foreach (var (k, v) in env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                envBlock.Append(k).Append('=').Append(v).Append('\0');
            envBlock.Append('\0');

            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    envBlock.ToString(), workingDir, ref siEx, out var pi))
                throw new Win32Exception();

            // Drain the pty output concurrently — the child blocks if the
            // pipe buffer fills while we wait on it.
            var buffer = new MemoryStream();
            var reader = new Thread(() =>
            {
                using var fs = new FileStream(new SafeFileHandle(ptyOutRead, ownsHandle: false), FileAccess.Read);
                try { fs.CopyTo(buffer); }
                catch (IOException) { /* pipe closed: clean EOF */ }
            });
            reader.Start();

            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeout.TotalMilliseconds) != 0)
                    throw new TimeoutException($"child did not exit within {timeout}: {commandLine}");
                if (!GetExitCodeProcess(pi.hProcess, out var exitCode))
                    throw new Win32Exception();

                // Closing the pseudoconsole releases its end of the output
                // pipe, unblocking the reader with EOF.
                ClosePseudoConsole(hpc);
                hpc = IntPtr.Zero;
                if (!reader.Join(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("timed out draining the pseudo console output");

                return (Encoding.UTF8.GetString(buffer.ToArray()), (int)exitCode);
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            CloseHandle(ptyOutRead);
            CloseHandle(ptyInWrite);
        }
    }

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, string lpEnvironment,
        string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

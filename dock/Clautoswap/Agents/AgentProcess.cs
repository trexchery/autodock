using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clautoswap;

/// One worker process tree, born inside its own job object, with files (not pipes) as stdin, stdout
/// and stderr. Nothing ties it to the tray: it keeps running if clautoswap exits or crashes, and the
/// next start re-attaches by the root's pid and creation time. The root also inherits a handle to the
/// job, so the job's name stays valid exactly as long as the root runs. The job gives the tree's CPU
/// time (stall detection) and a clean kill of everything the run spawned. A verification command is
/// the exception: it dies with the tray, and the next start runs it again.
public sealed class AgentProcess : IDisposable
{
    readonly SafeFileHandle? _job;
    readonly SafeWaitHandle _process;

    public int Pid { get; }
    public long CreatedTicks { get; }

    AgentProcess(SafeFileHandle? job, SafeWaitHandle process, int pid)
    {
        _job = job;
        _process = process;
        Pid = pid;
        CreatedTicks = CreationTime(process);
    }

    /// Starts `exe args` in `workDir` at below-normal priority. A .cmd/.bat runs through cmd.exe.
    /// `env`: changes to the tray's environment (a null value removes the variable).
    public static AgentProcess Start(string exe, IReadOnlyList<string> args, string workDir, string jobName,
        string stdinPath, string stdoutPath, string stderrPath, IReadOnlyDictionary<string, string?>? env = null)
    {
        var cmdline = CommandLine(exe, args, out var app);
        return Start(app, cmdline, workDir, jobName, stdinPath, stdoutPath, stderrPath, env, outliveTray: true);
    }

    /// Runs a command line through cmd.exe (a verification command as the owner typed it);
    /// stdout and stderr both go to `logPath`.
    public static AgentProcess StartShell(string commandLine, string workDir, string jobName, string stdinPath, string logPath,
        IReadOnlyDictionary<string, string?>? env = null) =>
        Start(Engine.Cmd, $"{Quote(Engine.Cmd)} /d /s /c \"{commandLine}\"", workDir, jobName, stdinPath, logPath, logPath, env, outliveTray: false);

    static AgentProcess Start(string app, string cmdline, string workDir, string jobName,
        string stdinPath, string stdoutPath, string stderrPath, IReadOnlyDictionary<string, string?>? env, bool outliveTray)
    {
        var job = CreateJobObjectW(IntPtr.Zero, jobName);
        if (job.IsInvalid) throw new Win32Exception();
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK | (outliveTray ? 0 : JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) },
            };
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref limits, Marshal.SizeOf(limits)))
                throw new Win32Exception();
            using var stdin = OpenInheritable(stdinPath, FileMode.Open, FileAccess.Read);
            using var stdout = OpenInheritable(stdoutPath, FileMode.Append, FileAccess.Write);
            // One handle when both streams share a file, so their writes cannot overwrite each other.
            using var stderr = stderrPath == stdoutPath ? null : OpenInheritable(stderrPath, FileMode.Append, FileAccess.Write);
            var block = EnvironmentBlock(env);
            PROCESS_INFORMATION pi;
            // A tray started inside someone else's job (an IDE terminal) must not take its agents down with it.
            try { pi = CreateInJob(app, cmdline, workDir, block, job, outliveTray, [stdin, stdout, stderr ?? stdout], breakaway: InAJob()); }
            catch (Win32Exception e) when (e.NativeErrorCode == 5 /* access denied: breakaway not allowed */)
            { pi = CreateInJob(app, cmdline, workDir, block, job, outliveTray, [stdin, stdout, stderr ?? stdout], breakaway: false); }
            CloseHandle(pi.hThread);
            return new AgentProcess(job, new SafeWaitHandle(pi.hProcess, ownsHandle: true), pi.dwProcessId);
        }
        catch
        {
            TerminateJobObject(job, 1);
            job.Dispose();
            throw;
        }
    }

    /// The run started earlier, if its root process (`pid` created at `createdTicks`) still runs.
    public static AgentProcess? Attach(string jobName, int pid, long createdTicks)
    {
        var h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid || CreationTime(h) != createdTicks)
        {
            h.Dispose();
            Reap(jobName);
            return null;
        }
        var job = OpenJobObjectW(JOB_OBJECT_ALL_ACCESS, false, jobName);
        if (job.IsInvalid) { job.Dispose(); job = null; }
        return new AgentProcess(job, h, pid);
    }

    /// Terminates whatever is left in a job whose root is gone (or never confirmed started).
    public static void Reap(string jobName)
    {
        using var job = OpenJobObjectW(JOB_OBJECT_ALL_ACCESS, false, jobName);
        if (!job.IsInvalid) TerminateJobObject(job, 1);
    }

    // Whoever learns of the exit first may dispose this object while others still hold it: once
    // disposed, it reads as exited and empty.
    bool Closed => _process.IsClosed;
    bool JobOpen => _job is { IsClosed: false };
    Task? _exit;

    public bool HasExited => Closed || WaitForSingleObject(_process, 0) == 0;

    public int? ExitCode => !Closed && HasExited && GetExitCodeProcess(_process, out var code) ? (int)code : null;

    /// Completes when the root process exits; no thread waits meanwhile. The wait holds its own
    /// handle, so disposing this object never leaves it waiting on a closed one.
    public Task WaitForExitAsync()
    {
        if (_exit != null) return _exit;
        if (Closed) return _exit = Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var self = Process.GetCurrentProcess().Handle;
        if (!DuplicateHandle(self, _process, self, out var own, SYNCHRONIZE, false, 0)) throw new Win32Exception();
        var wait = new ManualResetEvent(false) { SafeWaitHandle = own };
        ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => { wait.Dispose(); tcs.TrySetResult(); }, null, -1, executeOnlyOnce: true);
        return _exit = tcs.Task;
    }

    /// User + kernel time of every process the job has run.
    public TimeSpan CpuTime =>
        JobOpen && QueryInformationJobObject(_job!, 1 /* JobObjectBasicAccountingInformation */, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
            Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? TimeSpan.FromTicks(info.TotalUserTime + info.TotalKernelTime)
            : TimeSpan.Zero;

    /// Live processes in the job (the root first is not guaranteed).
    public int[] ProcessIds()
    {
        if (!JobOpen) return Closed ? [] : [Pid];
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then pointer-sized ids.
        const int max = 512;
        var size = 8 + max * IntPtr.Size;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_job!, 3 /* JobObjectBasicProcessIdList */, buf, size, IntPtr.Zero)) return [];
            var n = Math.Min(Marshal.ReadInt32(buf, 4), max);
            var pids = new List<int>(n);
            for (var i = 0; i < n; i++)
                if ((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size) is var p and not 0) pids.Add(p);
            return [.. pids];
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// Sum of the working sets of the job's live processes, in bytes.
    public long WorkingSet()
    {
        long total = 0;
        foreach (var pid in ProcessIds())
            try { using var p = Process.GetProcessById(pid); total += p.WorkingSet64; }
            catch (ArgumentException) { } // exited meanwhile
        return total;
    }

    /// Ends the run. Everything below the root goes first, so the `cswap run` wrapper sees Claude exit
    /// and seals its account profile itself; whatever is still in the job after `grace` is terminated.
    public async Task StopAsync(TimeSpan grace)
    {
        if (!HasExited)
        {
            foreach (var pid in ProcessIds().Where(p => p != Pid))
                try { using var p = Process.GetProcessById(pid); p.Kill(); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            await Task.WhenAny(WaitForExitAsync(), Task.Delay(grace));
        }
        Reap();
        if (!HasExited)
            try { using var p = Process.GetProcessById(Pid); p.Kill(entireProcessTree: true); } // re-attached without its job
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    /// Terminates leftovers (background shells, servers) once the run is over.
    public void Reap()
    {
        if (JobOpen) TerminateJobObject(_job!, 1);
    }

    public void Dispose()
    {
        _process.Dispose();
        _job?.Dispose();
    }

    /// Milliseconds since boot, not counting sleep: a laptop lid shut for an hour is not a stall.
    public static long UnbiasedMilliseconds => QueryUnbiasedInterruptTime(out var t) ? t / 10_000 : Environment.TickCount64;

    // -- process creation ------------------------------------------------------

    /// CRT quoting (what the engine's Python and Claude's runtime parse). A batch file needs cmd.exe,
    /// which strips the outer quotes of `/s /c "…"` and keeps the CRT-quoted line inside as is.
    internal static string CommandLine(string exe, IReadOnlyList<string> args, out string app)
    {
        var line = string.Join(' ', new[] { exe }.Concat(args).Select(Quote));
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            app = Engine.Cmd;
            return $"{Quote(app)} /d /s /c \"{line}\"";
        }
        app = exe;
        return line;
    }

    internal static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < a.Length; i++)
        {
            var slashes = 0;
            while (i < a.Length && a[i] == '\\') { slashes++; i++; }
            if (i == a.Length) { sb.Append('\\', slashes * 2); break; }
            if (a[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(a[i]);
        }
        return sb.Append('"').ToString();
    }

    /// The tray's environment with `changes` applied, as a sorted Unicode block.
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string?>? changes)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in changes ?? new Dictionary<string, string?>())
            if (v == null) vars.Remove(k);
            else vars[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        return sb.Append('\0').ToString();
    }

    static SafeFileHandle OpenInheritable(string path, FileMode mode, FileAccess access)
    {
        var h = File.OpenHandle(path, mode, access, FileShare.ReadWrite | FileShare.Delete);
        if (!SetHandleInformation(h, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) { h.Dispose(); throw new Win32Exception(); }
        return h;
    }

    static bool InAJob() => IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var inJob) && inJob;

    /// CreateProcess inheriting exactly the three std handles and, for a run that outlives the tray, a
    /// copy of the job handle (never whatever else the tray holds open: the tray's own job handle stays
    /// private), with the process born inside `job` so nothing it spawns starts outside it.
    static PROCESS_INFORMATION CreateInJob(string app, string cmdline, string workDir, string env, SafeFileHandle job, bool shareJob, SafeFileHandle[] std, bool breakaway)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        var attrs = Marshal.AllocHGlobal((int)size);
        var self = Process.GetCurrentProcess().Handle;
        var jobCopy = IntPtr.Zero;
        if (shareJob && !DuplicateHandle(self, job, self, out jobCopy, 0, true, DUPLICATE_SAME_ACCESS)) { Marshal.FreeHGlobal(attrs); throw new Win32Exception(); }
        var inherit = std.Distinct().Select(h => h.DangerousGetHandle()).ToList();
        if (shareJob) inherit.Add(jobCopy);
        var handles = Marshal.AllocHGlobal(inherit.Count * IntPtr.Size);
        var jobs = Marshal.AllocHGlobal(IntPtr.Size);
        var held = new List<SafeHandle>();
        try
        {
            foreach (var h in std.Append(job).Distinct()) { var ok = false; h.DangerousAddRef(ref ok); if (ok) held.Add(h); }
            for (var i = 0; i < inherit.Count; i++) Marshal.WriteIntPtr(handles, i * IntPtr.Size, inherit[i]);
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            if (!InitializeProcThreadAttributeList(attrs, 2, 0, ref size)) throw new Win32Exception();
            try
            {
                if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (nuint)(inherit.Count * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)
                    || !UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
                var si = new STARTUPINFOEX
                {
                    StartupInfo = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEX>(),
                        dwFlags = STARTF_USESTDHANDLES,
                        hStdInput = std[0].DangerousGetHandle(),
                        hStdOutput = std[1].DangerousGetHandle(),
                        hStdError = std[2].DangerousGetHandle(),
                    },
                    lpAttributeList = attrs,
                };
                var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS;
                if (breakaway) flags |= CREATE_BREAKAWAY_FROM_JOB;
                if (!CreateProcessW(app, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, true, flags, env, workDir, ref si, out var pi))
                    throw new Win32Exception();
                return pi;
            }
            finally { DeleteProcThreadAttributeList(attrs); }
        }
        finally
        {
            Marshal.FreeHGlobal(attrs);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(jobs);
            if (jobCopy != IntPtr.Zero) CloseHandle(jobCopy);
            foreach (var h in held) h.DangerousRelease();
        }
    }

    static long CreationTime(SafeWaitHandle process) =>
        GetProcessTimes(process, out var created, out _, out _, out _) ? created : 0;

    // -- Win32 -----------------------------------------------------------------

    const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, JOB_OBJECT_ALL_ACCESS = 0x1F001F;
    const uint HANDLE_FLAG_INHERIT = 1, STARTF_USESTDHANDLES = 0x100, JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x800,
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000, DUPLICATE_SAME_ACCESS = 2;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000,
        BELOW_NORMAL_PRIORITY_CLASS = 0x4000, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002, PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x2000D;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle OpenJobObjectW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int cls, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int size, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int cls, IntPtr info, int size, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(SafeWaitHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeWaitHandle handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, SafeWaitHandle source, IntPtr targetProcess, out SafeWaitHandle target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    static extern bool QueryUnbiasedInterruptTime(out long time);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmdline, IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags,
        string env, string workDir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
}

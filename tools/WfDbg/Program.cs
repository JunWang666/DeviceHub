using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Minimal read-only WOW64 debugger: breakpoint on 32-bit KERNELBASE!WriteFile in the target,
// log (handle, length, buffer bytes), then single-step over it and re-arm. Never modifies buffers.
// usage: wfdbg <pid> <maxHits> <logFile> <stopFile>
internal static unsafe class P
{
    static StreamWriter log = null!;
    static void L(string s) { var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}"; log.WriteLine(line); log.Flush(); Console.WriteLine(line); }

    static int Main(string[] a)
    {
        log = new StreamWriter(a[2], true, Encoding.ASCII);
        try { return Run(int.Parse(a[0]), int.Parse(a[1]), a[3]); }
        catch (Exception ex) { L("EXC " + ex); return 1; }
    }

    static int Run(int pid, int maxHits, string stopFile)
    {
        var hProc = OpenProcess(0x1F0FFF, false, pid);
        if (hProc == 0) { L($"OpenProcess failed {Marshal.GetLastWin32Error()}"); return 1; }
        ulong kb = FindModule32(hProc, "kernelbase.dll");
        if (kb == 0) { L("kernelbase (32-bit) not found"); return 1; }
        uint rva = ExportRva(@"C:\Windows\SysWOW64\KernelBase.dll", "WriteFile");
        ulong bp = kb + rva;
        L($"KERNELBASE32=0x{kb:X} WriteFile rva=0x{rva:X} bp=0x{bp:X}");

        if (!DebugActiveProcess(pid)) { L($"DebugActiveProcess failed {Marshal.GetLastWin32Error()}"); return 1; }
        DebugSetProcessKillOnExit(false);
        byte orig = ReadByte(hProc, bp);
        L($"orig byte 0x{orig:X2}");
        bool armed = false; int hits = 0; uint steppingTid = 0; bool stopping = false;
        var ev = new byte[256];
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (!stopping && (hits >= maxHits || File.Exists(stopFile) || sw.Elapsed.TotalMinutes > 15)) stopping = true;
            if (stopping && steppingTid == 0)
            {
                if (armed) { WriteByte(hProc, bp, orig); armed = false; }
                DebugActiveProcessStop(pid);
                L($"detached, hits={hits}");
                return 0;
            }
            fixed (byte* e = ev)
            {
                if (!WaitForDebugEvent(e, 200)) continue;
                uint code = *(uint*)e, tid = *(uint*)(e + 8);
                uint cont = 0x00010002; // DBG_CONTINUE
                byte* u = e + 16;
                switch (code)
                {
                    case 3: CloseHandle(*(nint*)u); break;   // CREATE_PROCESS_DEBUG_EVENT: hFile
                    case 6: CloseHandle(*(nint*)u); break;   // LOAD_DLL_DEBUG_EVENT: hFile
                    case 5: L("target exited"); return 0;
                    case 1:                                   // EXCEPTION_DEBUG_EVENT
                    {
                        uint exc = *(uint*)u; ulong addr = *(ulong*)(u + 16);
                        bool isBp = exc == 0x80000003 || exc == 0x4000001F;
                        bool isStep = exc == 0x80000004 || exc == 0x4000001E;
                        if (isBp && addr == bp && armed)
                        {
                            var ht = OpenThread(0x1A, false, tid); // SUSPEND|GET_CONTEXT|SET_CONTEXT
                            var ctx = new byte[0x2CC];
                            fixed (byte* c = ctx)
                            {
                                *(uint*)c = 0x00010007; // WOW64_CONTEXT_FULL
                                Wow64GetThreadContext(ht, c);
                                uint esp = *(uint*)(c + 0xC4);
                                uint h = ReadU32(hProc, esp + 4), buf = ReadU32(hProc, esp + 8), len = ReadU32(hProc, esp + 12);
                                hits++;
                                var data = len is > 0 and <= 0x200 ? ReadBytes(hProc, buf, (int)len) : Array.Empty<byte>();
                                L($"WriteFile#{hits} tid={tid} h=0x{h:X} len={len}\n    " + string.Join(" ", data.Select(b => b.ToString("X2"))));
                                WriteByte(hProc, bp, orig); armed = false;
                                *(uint*)(c + 0xB8) = (uint)bp;   // Eip back onto the original instruction
                                *(uint*)(c + 0xC0) |= 0x100;     // trap flag: single step
                                Wow64SetThreadContext(ht, c);
                            }
                            CloseHandle(ht);
                            steppingTid = tid;
                        }
                        else if (isStep && tid == steppingTid)
                        {
                            if (!stopping) { WriteByte(hProc, bp, 0xCC); armed = true; }
                            steppingTid = 0;
                        }
                        else if (isBp && !armed && steppingTid == 0 && hits == 0)
                        {
                            WriteByte(hProc, bp, 0xCC); armed = true; // attach breakpoint: arm now
                            L($"armed (attach bp exc=0x{exc:X})");
                        }
                        else if (!isBp && !isStep) cont = 0x80010001; // DBG_EXCEPTION_NOT_HANDLED
                        break;
                    }
                }
                ContinueDebugEvent(*(uint*)(e + 4), tid, cont);
            }
        }
    }

    static ulong FindModule32(nint hProc, string name)
    {
        var mods = new nint[1024];
        if (!EnumProcessModulesEx(hProc, mods, mods.Length * 8, out int need, 0x01)) return 0; // LIST_MODULES_32BIT
        var sb = new StringBuilder(260);
        for (int i = 0; i < need / 8; i++)
        {
            sb.Clear(); GetModuleBaseNameW(hProc, mods[i], sb, 260);
            if (sb.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) return (ulong)mods[i];
        }
        return 0;
    }

    static uint ExportRva(string file, string func)
    {
        var b = File.ReadAllBytes(file);
        int pe = BitConverter.ToInt32(b, 0x3C);
        int nsec = BitConverter.ToUInt16(b, pe + 6), optSize = BitConverter.ToUInt16(b, pe + 20);
        int opt = pe + 24; uint expRva = BitConverter.ToUInt32(b, opt + 96); // PE32 data dir 0
        int secs = opt + optSize;
        int Off(uint rva)
        {
            for (int i = 0; i < nsec; i++)
            {
                int s = secs + i * 40;
                uint va = BitConverter.ToUInt32(b, s + 12), vsz = BitConverter.ToUInt32(b, s + 8), rsz = BitConverter.ToUInt32(b, s + 16), raw = BitConverter.ToUInt32(b, s + 20);
                if (rva >= va && rva < va + Math.Max(vsz, rsz)) return (int)(rva - va + raw);
            }
            throw new Exception("rva not in section");
        }
        int ed = Off(expRva);
        int n = BitConverter.ToInt32(b, ed + 24);
        int funcs = Off(BitConverter.ToUInt32(b, ed + 28)), names = Off(BitConverter.ToUInt32(b, ed + 32)), ords = Off(BitConverter.ToUInt32(b, ed + 36));
        for (int i = 0; i < n; i++)
        {
            int no = Off(BitConverter.ToUInt32(b, names + i * 4)); int end = Array.IndexOf(b, (byte)0, no);
            if (Encoding.ASCII.GetString(b, no, end - no) == func)
                return BitConverter.ToUInt32(b, funcs + BitConverter.ToUInt16(b, ords + i * 2) * 4);
        }
        throw new Exception("export not found");
    }

    static byte ReadByte(nint h, ulong a) => ReadBytes(h, a, 1)[0];
    static uint ReadU32(nint h, ulong a) => BitConverter.ToUInt32(ReadBytes(h, a, 4));
    static byte[] ReadBytes(nint h, ulong a, int n) { var r = new byte[n]; ReadProcessMemory(h, (nint)a, r, n, out _); return r; }
    static void WriteByte(nint h, ulong a, byte v)
    {
        VirtualProtectEx(h, (nint)a, 1, 0x40, out uint old);
        WriteProcessMemory(h, (nint)a, new[] { v }, 1, out _);
        VirtualProtectEx(h, (nint)a, 1, old, out _);
        FlushInstructionCache(h, (nint)a, 1);
    }

    [DllImport("kernel32", SetLastError = true)] static extern nint OpenProcess(uint acc, bool inh, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern nint OpenThread(uint acc, bool inh, uint tid);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(nint h);
    [DllImport("kernel32", SetLastError = true)] static extern bool DebugActiveProcess(int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool DebugActiveProcessStop(int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool DebugSetProcessKillOnExit(bool k);
    [DllImport("kernel32", SetLastError = true)] static extern bool WaitForDebugEvent(byte* ev, uint ms);
    [DllImport("kernel32", SetLastError = true)] static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32", SetLastError = true)] static extern bool Wow64GetThreadContext(nint ht, byte* ctx);
    [DllImport("kernel32", SetLastError = true)] static extern bool Wow64SetThreadContext(nint ht, byte* ctx);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(nint h, nint a, byte[] b, int n, out nint r);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(nint h, nint a, byte[] b, int n, out nint w);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtectEx(nint h, nint a, nint n, uint p, out uint old);
    [DllImport("kernel32", SetLastError = true)] static extern bool FlushInstructionCache(nint h, nint a, nint n);
    [DllImport("psapi", SetLastError = true)] static extern bool EnumProcessModulesEx(nint h, nint[] m, int cb, out int need, uint filter);
    [DllImport("psapi", CharSet = CharSet.Unicode)] static extern uint GetModuleBaseNameW(nint h, nint m, StringBuilder sb, int n);
}

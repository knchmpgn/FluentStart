using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using FluentStart.Native;

namespace FluentStart;

/// <summary>
/// Talks to a running Everything instance over its classic WM_COPYDATA IPC (no SDK DLL needed).
/// UNVERIFIED against a live Everything: struct layouts (QUERYW/LISTW), dwData = 2 for the query, the reply arriving as a
/// WM_COPYDATA to our window whose dwData equals the reply id we chose, and that 1.5 builds serve the same IPC.
/// Must be created and used on the UI thread (it owns a message-only window).
/// </summary>
internal sealed class EverythingClient : IDisposable
{
    private const uint EVERYTHING_IPC_COPYDATAQUERYW = 2;
    private const uint FOLDER = 0x1;

    private readonly HwndSource _src;
    private readonly Dictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private uint _nextId = 0x7A00;

    public EverythingClient()
    {
        var p = new HwndSourceParameters("FluentStartEverythingIpc") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };   // HWND_MESSAGE
        _src = new HwndSource(p);
        _src.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != NativeMethods.WM_COPYDATA) return IntPtr.Zero;
        var cds = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
        uint id = (uint)cds.dwData.ToUInt64();
        if (!_pending.Remove(id, out var tcs)) return IntPtr.Zero;

        var bytes = new byte[cds.cbData];
        if (cds.cbData > 0 && cds.lpData != IntPtr.Zero) Marshal.Copy(cds.lpData, bytes, 0, bytes.Length);
        tcs.TrySetResult(bytes);
        handled = true;
        return (IntPtr)1;
    }

    public async Task<List<FileHit>> QueryAsync(IntPtr everythingHwnd, string search, int max, CancellationToken ct)
    {
        uint id = _nextId++;
        var tcs = new TaskCompletionSource<byte[]>();
        _pending[id] = tcs;

        byte[] payload = BuildQuery((uint)_src.Handle.ToInt64(), id, 0, 0, (uint)max, search);
        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var cds = new NativeMethods.COPYDATASTRUCT
            {
                dwData = (UIntPtr)EVERYTHING_IPC_COPYDATAQUERYW,
                cbData = (uint)payload.Length,
                lpData = pin.AddrOfPinnedObject()
            };
            IntPtr r = NativeMethods.SendMessageTimeout(everythingHwnd, NativeMethods.WM_COPYDATA, _src.Handle, ref cds,
                NativeMethods.SMTO_ABORTIFHUNG, 1500, out _);
            if (r == IntPtr.Zero) { _pending.Remove(id); return new List<FileHit>(); }
        }
        finally { pin.Free(); }

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(2500, ct));
        if (finished != tcs.Task)
        {
            _pending.Remove(id);
            ct.ThrowIfCancellationRequested();
            return new List<FileHit>();
        }
        return ParseList(await tcs.Task);
    }

    /// <summary>EVERYTHING_IPC_QUERYW: reply_hwnd, reply_copydata_message, search_flags, offset, max_results, then the NUL-terminated UTF-16 search.</summary>
    internal static byte[] BuildQuery(uint replyHwnd, uint replyMessage, uint searchFlags, uint offset, uint maxResults, string search)
    {
        byte[] text = Encoding.Unicode.GetBytes(search + "\0");
        var buf = new byte[20 + text.Length];
        BitConverter.GetBytes(replyHwnd).CopyTo(buf, 0);
        BitConverter.GetBytes(replyMessage).CopyTo(buf, 4);
        BitConverter.GetBytes(searchFlags).CopyTo(buf, 8);
        BitConverter.GetBytes(offset).CopyTo(buf, 12);
        BitConverter.GetBytes(maxResults).CopyTo(buf, 16);
        text.CopyTo(buf, 20);
        return buf;
    }

    /// <summary>
    /// EVERYTHING_IPC_LISTW: totfolders, totfiles, totitems, numfolders, numfiles, numitems, offset (7 DWORDs), then items of
    /// {flags, filename_offset, path_offset}; offsets are byte offsets from the start of the list to NUL-terminated UTF-16 strings.
    /// </summary>
    internal static List<FileHit> ParseList(byte[] d)
    {
        var hits = new List<FileHit>();
        if (d.Length < 28) return hits;

        uint count = BitConverter.ToUInt32(d, 20);   // numitems
        for (uint i = 0; i < count; i++)
        {
            long p = 28 + (long)i * 12;
            if (p + 12 > d.Length) break;

            uint flags = BitConverter.ToUInt32(d, (int)p);
            string name = ReadWide(d, BitConverter.ToUInt32(d, (int)p + 4));
            string dir = ReadWide(d, BitConverter.ToUInt32(d, (int)p + 8));
            if (name.Length == 0 && dir.Length == 0) continue;

            string full = dir.Length == 0 ? name : name.Length == 0 ? dir : Path.Combine(dir, name);
            hits.Add(new FileHit(full, (flags & FOLDER) != 0));
        }
        return hits;
    }

    private static string ReadWide(byte[] d, uint offset)
    {
        if (offset >= d.Length) return "";
        int start = (int)offset, end = start;
        while (end + 1 < d.Length && !(d[end] == 0 && d[end + 1] == 0)) end += 2;
        return Encoding.Unicode.GetString(d, start, end - start);
    }

    public void Dispose() => _src.Dispose();
}

/// <summary>File search through Everything; reports <see cref="Available"/> = false when no usable instance was found.</summary>
internal sealed class EverythingSearchProvider : IFileSearchProvider
{
    private const string DocumentExtensions = "doc;docx;docm;xls;xlsx;xlsm;ppt;pptx;pdf;txt;md;rtf;odt;ods;odp;csv";

    private readonly Func<bool> _autoStart;
    private EverythingClient? _client;
    private EverythingInfo? _info;
    private DateTime _infoAt, _noneUntil;
    private readonly Func<IReadOnlyList<string>?> _excluded;

    /// <summary>Path fragments that are almost never what a Start-menu search wants (package caches, VCS data, build output...).</summary>
    internal static readonly string[] DefaultExcludedPaths =
    {
        @"\$Recycle.Bin\", @"\System Volume Information\", @"\Windows\WinSxS\", @"\Windows\servicing\", @"\Windows\Installer\",
        @"\node_modules\", @"\.git\", @"\.svn\", @"\__pycache__\", @"\.gradle\", @"\.nuget\", @"\AppData\Local\Temp\",
        @"\AppData\Local\Microsoft\Windows\INetCache\", @"\$WinREAgent\", @"\$WINDOWS.~BT\"
    };

    public EverythingSearchProvider(Func<bool> autoStart) : this(autoStart, () => DefaultExcludedPaths) { }

    /// <param name="excluded">Path fragments to push out of the results (null = no filtering). Excluded hits are only shown when nothing else matched.</param>
    public EverythingSearchProvider(Func<bool> autoStart, Func<IReadOnlyList<string>?> excluded)
    {
        _autoStart = autoStart;
        _excluded = excluded;
    }

    public string Name => _info?.Label ?? "Everything";
    public bool Available { get; private set; }

    public async Task<List<FileHit>> SearchAsync(string query, SearchFilter filter, int max, CancellationToken ct)
    {
        // Detection (and the optional background start) can take a few seconds: keep it off the UI thread.
        bool fresh = _info is { Running: true } && (DateTime.UtcNow - _infoAt).TotalSeconds < 15 && NativeMethods.IsWindow(_info.Hwnd);
        if (!fresh)
        {
            // Not running: do not re-scan processes/windows/disk on every keystroke.
            if (DateTime.UtcNow < _noneUntil) { Available = false; return new List<FileHit>(); }
            _info = await Task.Run(() => EverythingDetector.Resolve(_autoStart()), ct);
            _infoAt = DateTime.UtcNow;
            if (_info is not { Running: true }) _noneUntil = DateTime.UtcNow.AddSeconds(4);
        }
        Available = _info is { Running: true };
        if (!Available) return new List<FileHit>();

        _client ??= new EverythingClient();   // continuation resumes on the UI thread, which the message window needs
        try
        {
            // Everything returns hits in its own sort order (by name), so ask for plenty and rank them here.
            var hits = await _client.QueryAsync(_info!.Hwnd, Decorate(query, filter), 400, ct);
            return Rerank(query, ExcludeNoise(hits, _excluded()), max);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Available = false;
            Log.WarnOnce("everything", "Everything query failed", ex);
            return new List<FileHit>();
        }
    }

    /// <summary>The user's text is passed through raw (Everything's own syntax works); filters append Everything operators.</summary>
    internal static string Decorate(string query, SearchFilter filter) => filter switch
    {
        SearchFilter.Documents => $"{query} ext:{DocumentExtensions}",
        SearchFilter.Folders => $"{query} folder:",
        _ => query
    };

    internal static List<FileHit> ExcludeNoise(List<FileHit> hits, IReadOnlyList<string>? excluded)
    {
        if (excluded == null || excluded.Count == 0 || hits.Count == 0) return hits;
        var clean = hits.Where(h => !excluded.Any(x => !string.IsNullOrEmpty(x) && h.FullPath.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();
        return clean.Count > 0 ? clean : hits;
    }

    /// <summary>Everything has no relevance ranking: order hits by how well the file name matches the first word, then by path depth.</summary>
    internal static List<FileHit> Rerank(string query, IEnumerable<FileHit> hits, int max)
    {
        string token = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => !t.Contains(':')) ?? query;
        return hits
            .Select(h => (hit: h, score: SearchScorer.Score(token, Path.GetFileName(h.FullPath.TrimEnd('\\'))) - h.FullPath.Count(c => c == '\\') * 0.2))
            .OrderByDescending(x => x.score)
            .Take(max)
            .Select(x => x.hit)
            .ToList();
    }
}

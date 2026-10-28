// ApexProbe —— 检测链探针：同时采集 Apex 的系统声音与画面，产出可离线分析的时间轴数据。
//
// 它不判断任何东西，只负责「把原始证据录下来」：
//   音频  <- WASAPI 环路回采（系统渲染设备，游戏声音 + 语音）
//   画面  <- Windows Graphics Capture（每 500ms 一张全分辨率）
//   时间  <- 同一个 Stopwatch 基准，便于对齐
//
// 产物（输出目录下）：
//   audio.wav              16-bit PCM 立体声
//   audio_rms.csv          t_ms,rms_dbfs,peak_dbfs  每 10ms 一行
//   frames/f_<t_ms>.jpg    每 500ms 一张
//   meta.txt               窗口、尺寸、起始时刻、统计
//
// 用法：
//   ApexProbe.exe                          抓 "Apex Legends"，录 600 秒
//   ApexProbe.exe <标题片段> [秒数] [输出目录]
//   ApexProbe.exe --list                   列出可见窗口标题

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using D3D11Device = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace ApexProbe;

internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long NowMs => Clock.ElapsedMilliseconds;

    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _ctx;

    private static int _framesArrived;
    private static int _framesSaved;
    private static int _pendingEncodes;
    private static long _lastSaveMs = long.MinValue / 2;
    private static string _outDir = "";
    private static string _firstError = "";
    private static volatile bool _stop;

    // ---- 音频 ----
    private static FileStream? _wav;
    private static StreamWriter? _rms;
    private static long _audioStartMs;
    private static long _audioBlocks;
    private static byte[] _pend = new byte[1 << 16];
    private static int _pendLen;
    private static int _blockBytes = 1920;
    private static bool _isFloat;
    private static long _audioBytes;
    private static double _lastDb = -120;
    private static int _audioRate = 48000;
    private static int _audioChannels = 2;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--list") { ListWindows(); return 0; }

        var frag = args.Length > 0 ? args[0] : "Apex Legends";
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 600;
        _outDir = args.Length > 2 ? args[2] : Path.Combine(@"D:\played\probe", "s_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        var hwnd = ResolveWindow(frag);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[X] 没找到标题含 \"{frag}\" 的窗口。用 --list 看看有哪些。");
            return 2;
        }
        Console.WriteLine($"[1] 目标窗口: \"{TitleOf(hwnd)}\"");
        Console.WriteLine($"[1] 输出目录: {_outDir}");

        Directory.CreateDirectory(_outDir);
        Directory.CreateDirectory(Path.Combine(_outDir, "frames"));

        GraphicsCaptureItem item;
        try { item = CreateItem(hwnd); }
        catch (Exception ex) { Console.WriteLine($"[X] CreateForWindow 失败: {ex.Message}"); return 3; }
        Console.WriteLine($"[2] CaptureItem OK  {item.Size.Width}x{item.Size.Height}");
        if (item.Size.Width == 0) { Console.WriteLine("[X] 窗口尺寸为 0（最小化了？）"); return 3; }

        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device!, out _ctx!);
        }
        catch (Exception ex) { Console.WriteLine($"[X] D3D11 失败: {ex.Message}"); return 4; }

        using var dev = _device;
        using var ctx = _ctx;

        StartAudio();
        Console.WriteLine("[3] 音频采集已启动（无声音也在录，只是静音）");

        var winrtDevice = ToWinRTDevice(dev);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { }
        pool.FrameArrived += (s, _) => OnFrame(s);
        session.StartCapture();
        Console.WriteLine($"[4] 画面采集已启动，录 {seconds} 秒。Ctrl+C 可提前结束。");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop = true; };

        var deadline = NowMs + seconds * 1000L;
        var nextReport = 5000L;
        while (!_stop && NowMs < deadline)
        {
            Thread.Sleep(200);
            if (NowMs < nextReport) continue;
            nextReport += 5000;
            Console.WriteLine($"    t={NowMs / 1000,4}s  帧={_framesArrived,6} 存图={_framesSaved,5}  " +
                              $"音频={_audioBlocks / 100,4}.{_audioBlocks % 100:D2}s  当前 {_lastDb,6:F1} dBFS");
        }

        _stop = true;
        Thread.Sleep(300);
        try { session.Dispose(); } catch { }
        try { pool.Dispose(); } catch { }

        StopAudio();

        File.WriteAllText(Path.Combine(_outDir, "meta.txt"), string.Join(Environment.NewLine, new[]
        {
            $"window={TitleOf(hwnd)}",
            $"capture_size={item.Size.Width}x{item.Size.Height}",
            $"started={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"duration_ms={NowMs}",
            $"frames_arrived={_framesArrived}",
            $"frames_saved={_framesSaved}",
            $"audio_start_ms={_audioStartMs}",
            $"audio_wav={_audioBytes}",
            $"audio_rate={_audioRate}",
            $"audio_channels={_audioChannels}",
            $"audio_10ms_blocks={_audioBlocks}",
            $"first_error={_firstError}",
        }));

        Console.WriteLine();
        Console.WriteLine($"[5] 结束。帧到达 {_framesArrived}，存图 {_framesSaved}，音频 {_audioBlocks / 100.0:F1}s");
        if (_firstError.Length > 0) Console.WriteLine($"[!] 首个错误: {_firstError}");
        Console.WriteLine($"[5] 产物在 {_outDir}");
        return _framesSaved == 0 ? 10 : 0;
    }

    // ================= 画面 =================

    private static void OnFrame(Direct3D11CaptureFramePool pool)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) return;
            Interlocked.Increment(ref _framesArrived);

            var t = NowMs;
            if (t - _lastSaveMs < 480) return;              // 采样节流：约 2 张/秒
            if (Volatile.Read(ref _pendingEncodes) > 1) return;
            _lastSaveMs = t;

            using var tex = TextureOf(frame);
            if (tex is null) return;

            var sd = tex.Description;
            var w = (int)sd.Width;
            var h = (int)sd.Height;
            if (w == 0 || h == 0) return;

            sd.Usage = ResourceUsage.Staging;
            sd.BindFlags = BindFlags.None;
            sd.CPUAccessFlags = CpuAccessFlags.Read;
            sd.MiscFlags = ResourceOptionFlags.None;

            using var staging = _device!.CreateTexture2D(sd);
            _ctx!.CopyResource(staging, tex);
            var map = _ctx.Map(staging, 0, MapMode.Read);
            byte[] buf;
            try
            {
                buf = new byte[w * h * 4];
                unsafe
                {
                    var src = (byte*)map.DataPointer;
                    var pitch = (int)map.RowPitch;
                    fixed (byte* dst = buf)
                    {
                        for (var y = 0; y < h; y++)
                            Buffer.MemoryCopy(src + (long)y * pitch, dst + (long)y * w * 4, w * 4L, w * 4L);
                    }
                }
            }
            finally { _ctx.Unmap(staging, 0); }

            var path = Path.Combine(_outDir, "frames", $"f_{t:D8}.jpg");
            Interlocked.Increment(ref _pendingEncodes);
            Task.Run(() =>
            {
                try { SaveJpeg(buf, w, h, path); Interlocked.Increment(ref _framesSaved); }
                catch (Exception ex) { RecordError(ex); }
                finally { Interlocked.Decrement(ref _pendingEncodes); }
            });
        }
        catch (Exception ex) { RecordError(ex); }
    }

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static void SaveJpeg(byte[] buf, int w, int h, string path)
    {
        var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        unsafe
        {
            fixed (byte* p = buf)
            {
                using var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppRgb, (IntPtr)p);
                bmp.Save(path, JpegCodec, ps);
            }
        }
    }

    private static ID3D11Texture2D? TextureOf(Direct3D11CaptureFrame frame)
    {
        var surface = frame.Surface;
        if (surface is null) return null;
        var self = WinRT.MarshalInspectable<object>.FromManaged(surface);
        var iid = typeof(ID3D11Texture2D).GUID;
        var hr = GetTextureFromSurface(self, ref iid, out var p);
        return hr < 0 || p == IntPtr.Zero ? null : new ID3D11Texture2D(p);
    }

    // ================= 音频 =================

    private static WasapiLoopbackCapture? _cap;

    private static void StartAudio()
    {
        try
        {
            _cap = new WasapiLoopbackCapture();
            var fmt = _cap.WaveFormat;
            _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
            _audioRate = fmt.SampleRate;
            _audioChannels = fmt.Channels;

            // 统一按 16-bit PCM 存盘
            var wavPath = Path.Combine(_outDir, "audio.wav");
            _wav = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            WriteWavHeader(_wav, _audioRate, _audioChannels);

            _rms = new StreamWriter(Path.Combine(_outDir, "audio_rms.csv"), false, Encoding.UTF8, 1 << 16);
            _rms.WriteLine("t_ms,rms_dbfs,peak_dbfs");

            _blockBytes = _audioRate * _audioChannels * 2 / 100;   // 10ms 的字节数
            if (_blockBytes <= 0) _blockBytes = 1920;

            _cap.DataAvailable += OnAudio;
            _audioStartMs = NowMs;
            _cap.StartRecording();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 音频采集启动失败（只录画面）: {ex.Message}");
            _cap = null;
        }
    }

    private static void OnAudio(object? sender, WaveInEventArgs e)
    {
        try
        {
            var n = e.BytesRecorded;
            if (n <= 0) return;
            byte[] pcm;
            if (_isFloat)
            {
                var samples = n / 4;
                pcm = new byte[samples * 2];
                for (var i = 0; i < samples; i++)
                {
                    var f = BitConverter.ToSingle(e.Buffer, i * 4);
                    var v = (int)Math.Round(Math.Clamp(f, -1f, 1f) * 32767f);
                    pcm[i * 2] = (byte)(v & 0xFF);
                    pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }
            }
            else if (e.Buffer.Length >= n)
            {
                pcm = new byte[n];
                Buffer.BlockCopy(e.Buffer, 0, pcm, 0, n);
            }
            else return;

            Feed(pcm, pcm.Length);

            // 时间戳按「本批数据结束于墙钟 tw」倒推，而不是按块计数累加：
            // 设备空闲时 WASAPI 不送数据，块计数会落后于真实时间。
            if (_blk.Count > 0)
            {
                var baseTs = NowMs - _blk.Count * 10L;
                for (var i = 0; i < _blk.Count; i++)
                    _rms?.WriteLine($"{baseTs + i * 10},{_blk[i].rms:F1},{_blk[i].peak:F1}");
                _blk.Clear();
            }
        }
        catch (Exception ex) { RecordError(ex); }
    }

    private static readonly List<(double rms, double peak)> _blk = new(32);

    private static void Feed(byte[] data, int count)
    {
        if (_pendLen + count > _pend.Length)
        {
            Array.Resize(ref _pend, Math.Max(_pend.Length * 2, _pendLen + count));
        }
        Buffer.BlockCopy(data, 0, _pend, _pendLen, count);
        _pendLen += count;

        var off = 0;
        while (_pendLen - off >= _blockBytes)
        {
            ProcessBlock(_pend, off, _blockBytes);
            off += _blockBytes;
        }
        if (off > 0)
        {
            Buffer.BlockCopy(_pend, off, _pend, 0, _pendLen - off);
            _pendLen -= off;
        }
    }

    private static void ProcessBlock(byte[] buf, int off, int len)
    {
        double sum = 0;
        double peak = 0;
        var samples = len / 2;
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(buf[off + i * 2] | (buf[off + i * 2 + 1] << 8));
            var d = v / 32768.0;
            sum += d * d;
            var a = Math.Abs(d);
            if (a > peak) peak = a;
        }
        var rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;
        var rmsDb = rms > 0 ? 20 * Math.Log10(rms) : -120;
        var peakDb = peak > 0 ? 20 * Math.Log10(peak) : -120;
        _lastDb = peakDb;

        _wav?.Write(buf, off, len);
        _audioBytes += len;

        _audioBlocks++;
        _blk.Add((rmsDb, peakDb));
    }

    private static void WriteWavHeader(FileStream fs, int rate, int channels)
    {
        var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(0);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(rate);
        bw.Write(rate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(0);
        bw.Flush();
    }

    private static void StopAudio()
    {
        try { _cap?.StopRecording(); } catch { }
        try { _cap?.Dispose(); } catch { }
        _cap = null;

        try { _rms?.Flush(); _rms?.Dispose(); } catch { }
        if (_wav is not null)
        {
            try
            {
                var dataBytes = (int)_wav.Length - 44;
                _wav.Seek(4, SeekOrigin.Begin);
                _wav.Write(BitConverter.GetBytes(dataBytes + 36));
                _wav.Seek(40, SeekOrigin.Begin);
                _wav.Write(BitConverter.GetBytes(dataBytes));
                _wav.Flush();
                _wav.Dispose();
            }
            catch { }
        }
    }

    // ================= 互操作（与 WgcTest 相同，已验证可用） =================

    private static D3D11Device ToWinRTDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var raw);
        if (hr != 0) throw new COMException("CreateDirect3D11DeviceFromDXGIDevice 失败", hr);
        return WinRT.MarshalInterface<D3D11Device>.FromAbi(raw);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface(ref Guid iid); }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    private static unsafe GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(name, name.Length, out var hstr);
        try
        {
            Guid iid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hr = RoGetActivationFactory(hstr, ref iid, out var factory);
            if (hr < 0) throw new COMException($"RoGetActivationFactory 失败 0x{hr:X8}");
            try
            {
                Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var p = CallCreateForWindow(factory, hwnd, &itemIid);
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(p);
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    private static unsafe IntPtr CallCreateForWindow(IntPtr self, IntPtr hwnd, Guid* iid)
    {
        var vtbl = (IntPtr*)Marshal.ReadIntPtr(self);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
        IntPtr result;
        var hr = fn(self, hwnd, iid, &result);
        if (hr < 0) throw new COMException($"CreateForWindow 失败 0x{hr:X8}");
        return result;
    }

    private static unsafe int GetTextureFromSurface(IntPtr self, ref Guid iid, out IntPtr result)
    {
        Guid g = iid;
        var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        var vtbl = (IntPtr*)Marshal.ReadIntPtr(self);
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        IntPtr access;
        var hrQi = qi(self, &accessIid, &access);
        if (hrQi < 0) { result = IntPtr.Zero; return hrQi; }
        var avtbl = (IntPtr*)Marshal.ReadIntPtr(access);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)avtbl[3];
        IntPtr r;
        var hr = fn(access, &g, &r);
        Marshal.Release(access);
        result = r;
        return hr;
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // ---- Win32 ----
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

    private static IntPtr ResolveWindow(string frag)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var t = TitleOf(h);
            if (t.Length > 0 && t.Contains(frag, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void ListWindows()
    {
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var t = TitleOf(h);
            if (t.Length > 0) Console.WriteLine($"0x{h:X8}  {t}");
            return true;
        }, IntPtr.Zero);
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void RecordError(Exception ex)
    {
        if (_firstError.Length == 0) _firstError = $"{ex.GetType().Name}: {ex.Message}";
    }
}

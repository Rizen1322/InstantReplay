using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Aura.Core.Encoding;
using Aura.Core.Settings;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
static long Ticks() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
static unsafe void Upload(ID3D11DeviceContext context, ID3D11Texture2D texture, byte[] bytes, int pitch)
{
    fixed (byte* data = bytes) context.UpdateSubresource(texture, 0, null, (IntPtr)data, (uint)pitch, 0);
}

const int width = 640, height = 360, fps = 60;
var prepareProperty = typeof(VideoEncoder).GetProperty("PreparePullFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
MediaFactory.MFStartup();
try
{
    var supported = HardwareEncoders.ProbeSupport().Codecs;
    foreach (bool mft in new[] { false, true })
    foreach (var codec in new[] { VideoCodec.H264, VideoCodec.HEVC, VideoCodec.AV1 })
    {
        D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_0],
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
        using (device)
        using (context)
        {
            using var multithread = device!.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
            VideoEncoder.DirectNvencDisabled = mft;
            var encoder = new VideoEncoder();
            ID3D11Texture2D? texture = null;
            var packets = new ConcurrentQueue<(long Pts, long Dts, int Length, bool Key)>();
            var failures = new ConcurrentQueue<string>();
            int prepared = 0;
            string backend = "";
            bool tenBit = false;
            try
            {
                encoder.FrameEncoded += frame => packets.Enqueue((frame.PtsTicks, frame.Dts, frame.Length, frame.IsKeyframe));
                encoder.Failed += message => failures.Enqueue(message);
                try { encoder.Initialize(device, width, height, fps, 2_000_000, codec, preferTenBit: codec != VideoCodec.H264); }
                catch (NotSupportedException) when (codec == VideoCodec.AV1 && !supported.Contains(codec))
                {
                    Console.WriteLine($"SKIP: AV1, request={(mft ? "MFT" : "native")}: no available backend on this machine");
                    continue;
                }
                backend = encoder.EncoderName;
                tenBit = encoder.TenBit;
                int bpp = tenBit ? 2 : 1;
                texture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                    Format = tenBit ? Format.P010 : Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
                });
                var pixels = new byte[width * height * bpp * 3 / 2];
                if (tenBit) MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan()).Fill(512 << 6);
                else pixels.AsSpan().Fill(128);
                long origin = Ticks();
                prepareProperty.SetValue(encoder, (Action)(() =>
                {
                    int sequence = Interlocked.Increment(ref prepared);
                    if (tenBit) MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan(0, width * height * 2)).Fill((ushort)((64 + sequence % 800) << 6));
                    else pixels.AsSpan(0, width * height).Fill((byte)(16 + sequence % 200));
                    Upload(context!, texture, pixels, width * bpp);
                    long distance = Ticks() - origin;
                    long capture = origin + (distance * 144 / 10_000_000) * 10_000_000 / 144;
                    encoder.SubmitFrame(texture, capture, context!);
                }));
                await Task.Delay(3000);
            }
            finally
            {
                prepareProperty.SetValue(encoder, null);
                encoder.Dispose();
                texture?.Dispose();
            }
            var result = packets.ToArray();
            Require(failures.IsEmpty, "Encoder failed: " + string.Join("; ", failures));
            Require(result.Length >= 140, $"Too few frames: {result.Length}, {backend}");
            Require(prepared <= 190, $"More conversions than output slots: {prepared}");
            Require(result.All(p => p.Length > 0) && result.Any(p => p.Key), "Invalid or keyframe-free bitstream");
            Require(result.Zip(result.Skip(1), (a, b) => b.Dts > a.Dts).All(x => x), "Non-monotonic DTS");
            long[] presentation = result.Select(p => p.Pts).Order().ToArray();
            Require(presentation.Zip(presentation.Skip(1), (a, b) => b - a is >= 166666 and <= 166667).All(x => x),
                "Output is not a contiguous 60fps grid");
            Console.WriteLine($"PASS: {codec} {(tenBit ? 10 : 8)}bit, request={(mft ? "MFT" : "native")}, backend={backend}, encoded={result.Length}, prepared={prepared}, monotonic DTS, contiguous 60fps PTS");
        }
    }
}
finally { MediaFactory.MFShutdown(); }

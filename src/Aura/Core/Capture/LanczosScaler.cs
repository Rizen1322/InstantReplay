using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// Уменьшение кадра фильтром Ланцоша (a = 2) в два прохода — по горизонтали и по
/// вертикали — до того, как кадр попадёт в видеопроцессор драйвера.
///
/// ЗАЧЕМ. Видеопроцессор D3D11 масштабирует тем фильтром, какой выберет драйвер,
/// и выбирает он его для ВОСПРОИЗВЕДЕНИЯ видео, где важна скорость. При записи
/// 1440p-монитора в 1080p тонкие линии интерфейса, текст, сетки и листва рябят и
/// идут лесенкой, а энкодер тратит биты на этот муар. Фильтр Ланцоша с окном,
/// растянутым под коэффициент уменьшения, честно усредняет все исходные пиксели,
/// попадающие в выходной, — так же масштабирует OBS в режиме «Ланцош».
/// Видеопроцессору остаётся только перевод цвета в NV12/P010 без изменения размера.
///
/// ЦЕНА. Веса фильтра зависят только от номера выходного столбца (или строки), а не
/// от второй координаты, поэтому они считаются один раз на процессоре и лежат в
/// буфере; шейдер только складывает отсчёты. Первый вариант считал синусы на каждый
/// отсчёт и стоил около 1 мс видеокарты на кадр 1440p → 1080p (RTX 3070) — для
/// записи игры это много.
///
/// Промежуточный кадр — в половинной точности с плавающей точкой, чтобы
/// отрицательные лепестки фильтра не обрезались между проходами.
/// </summary>
internal sealed class LanczosScaler : IDisposable
{
    private const int A = 2;

    private const string Shader = """
        Texture2D<float4> Source : register(t0);
        StructuredBuffer<float> Weights : register(t1);   // [выход * Taps + k]
        StructuredBuffer<int> First : register(t2);       // первый исходный отсчёт окна
        cbuffer Params : register(b0)
        {
            int Taps;
            int Limit;      // последний допустимый индекс по оси прохода
            int Horizontal;
            int Pad;
        };

        float4 VsMain(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float4 Filter1D(float4 pos)
        {
            int2 p = int2(pos.xy);
            int index = Horizontal ? p.x : p.y;
            int first = First[index];
            int weightBase = index * Taps;
            float4 sum = 0;
            [loop]
            for (int k = 0; k < Taps; k++)
            {
                int c = clamp(first + k, 0, Limit);
                int2 at = Horizontal ? int2(c, p.y) : int2(p.x, c);
                sum += Source.Load(int3(at, 0)) * Weights[weightBase + k];
            }
            return sum;
        }

        float4 PsHorizontal(float4 pos : SV_Position) : SV_Target { return Filter1D(pos); }
        float4 PsVertical(float4 pos : SV_Position) : SV_Target { return saturate(Filter1D(pos)); }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public int Taps, Limit, Horizontal, Pad;
    }

    /// <summary>Окно и нормированные веса фильтра для одной оси.</summary>
    private sealed class Axis : IDisposable
    {
        public int Taps;
        public ID3D11Buffer? Params, Weights, First;
        public ID3D11ShaderResourceView? WeightsView, FirstView;

        public void Dispose()
        {
            WeightsView?.Dispose(); FirstView?.Dispose();
            Weights?.Dispose(); First?.Dispose(); Params?.Dispose();
        }
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psH, _psV;
    private readonly Axis _h = new(), _v = new();
    private ID3D11Texture2D? _middle, _output;
    private ID3D11RenderTargetView? _middleRtv, _outputRtv;
    private ID3D11ShaderResourceView? _middleSrv;
    private readonly int _srcW, _srcH;

    public int OutWidth { get; }
    public int OutHeight { get; }

    private LanczosScaler(ID3D11Device device, ID3D11DeviceContext context, int srcW, int srcH, int outW, int outH)
    {
        _device = device;
        _context = context;
        _srcW = srcW; _srcH = srcH;
        OutWidth = outW; OutHeight = outH;
    }

    /// <summary>
    /// Создать масштабатор; null — уменьшения нет или шейдеры не собрались (тогда
    /// масштабирует видеопроцессор, как раньше).
    /// </summary>
    public static LanczosScaler? TryCreate(ID3D11Device device, ID3D11DeviceContext context,
                                           int srcW, int srcH, int outW, int outH)
    {
        if (outW >= srcW && outH >= srcH) return null;          // только уменьшение
        // Для сравнения «до/после» на живой машине: масштабирует драйвер, как раньше
        if (Environment.GetEnvironmentVariable("AURA_NO_LANCZOS") == "1") return null;
        var scaler = new LanczosScaler(device, context, srcW, srcH, outW, outH);
        try
        {
            scaler.Build();
            Log.Info("Capture", $"Масштабирование {srcW}x{srcH} → {outW}x{outH}: фильтр Ланцоша " +
                                $"(два прохода на GPU, {scaler._h.Taps}+{scaler._v.Taps} отсчётов)");
            return scaler;
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Фильтр Ланцоша не собрался ({ex.Message}) — масштабирует видеопроцессор драйвера");
            scaler.Dispose();
            return null;
        }
    }

    private void Build()
    {
        _vs = _device.CreateVertexShader(Compiler.Compile(Shader, "VsMain", "lanczos.hlsl", "vs_5_0").Span);
        _psH = _device.CreatePixelShader(Compiler.Compile(Shader, "PsHorizontal", "lanczos.hlsl", "ps_5_0").Span);
        _psV = _device.CreatePixelShader(Compiler.Compile(Shader, "PsVertical", "lanczos.hlsl", "ps_5_0").Span);

        BuildAxis(_h, _srcW, OutWidth, horizontal: true);
        BuildAxis(_v, _srcH, OutHeight, horizontal: false);

        _middle = Aura.Core.Diagnostics.GpuResourceLedger.Track(_device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)OutWidth, Height = (uint)_srcH, MipLevels = 1, ArraySize = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        }), "масштабирование");
        _middleRtv = _device.CreateRenderTargetView(_middle);
        _middleSrv = _device.CreateShaderResourceView(_middle);

        _output = Aura.Core.Diagnostics.GpuResourceLedger.Track(_device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)OutWidth, Height = (uint)OutHeight, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        }), "масштабирование");
        _outputRtv = _device.CreateRenderTargetView(_output);
    }

    /// <summary>Посчитать окна и веса для одной оси: <paramref name="source"/> → <paramref name="output"/>.</summary>
    private void BuildAxis(Axis axis, int source, int output, bool horizontal)
    {
        var (first, weights, taps) = LanczosWeights.Compute(source, output, A);
        axis.Taps = taps;
        axis.Params = _device.CreateBuffer(new Params { Taps = taps, Limit = source - 1, Horizontal = horizontal ? 1 : 0 },
            new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Immutable));
        axis.Weights = StructuredBuffer(weights, sizeof(float));
        axis.First = StructuredBuffer(first, sizeof(int));
        axis.WeightsView = _device.CreateShaderResourceView(axis.Weights);
        axis.FirstView = _device.CreateShaderResourceView(axis.First);
    }

    private ID3D11Buffer StructuredBuffer<T>(T[] data, int stride) where T : unmanaged =>
        _device.CreateBuffer(data, new BufferDescription(
            (uint)(data.Length * stride), BindFlags.ShaderResource, ResourceUsage.Immutable,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)stride));

    /// <summary>Уменьшить кадр. Результат живёт до следующего вызова.</summary>
    public ID3D11Texture2D Scale(ID3D11Texture2D source)
    {
        using var sourceView = _device.CreateShaderResourceView(source);
        lock (_device)
        {
            try
            {
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.IASetInputLayout(null);
                _context.VSSetShader(_vs);
                _context.OMSetBlendState(null);

                // По горизонтали: исходник → промежуточный кадр
                _context.OMSetRenderTargets(_middleRtv!);
                _context.RSSetViewport(0, 0, OutWidth, _srcH);
                _context.PSSetShader(_psH);
                _context.PSSetConstantBuffer(0, _h.Params);
                _context.PSSetShaderResources(0, [sourceView, _h.WeightsView!, _h.FirstView!]);
                _context.Draw(3, 0);

                // По вертикали: промежуточный → выход
                _context.PSSetShaderResources(0, [null!, null!, null!]);
                _context.OMSetRenderTargets(_outputRtv!);
                _context.RSSetViewport(0, 0, OutWidth, OutHeight);
                _context.PSSetShader(_psV);
                _context.PSSetConstantBuffer(0, _v.Params);
                _context.PSSetShaderResources(0, [_middleSrv!, _v.WeightsView!, _v.FirstView!]);
                _context.Draw(3, 0);
            }
            finally
            {
                // Выход тут же читает видеопроцессор: привязок не оставляем
                _context.PSSetShaderResources(0, [null!, null!, null!]);
                _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            }
        }
        return _output!;
    }

    public void Dispose()
    {
        _outputRtv?.Dispose(); _output?.Dispose();
        _middleSrv?.Dispose(); _middleRtv?.Dispose(); _middle?.Dispose();
        _h.Dispose(); _v.Dispose();
        _psH?.Dispose(); _psV?.Dispose(); _vs?.Dispose();
    }
}

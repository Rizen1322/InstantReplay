using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Capture;

/// <summary>Поворот поверхности DDA в координаты рабочего стола, до наложения курсора.</summary>
internal sealed class DdaFrameRotator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly int _width, _height;
    private ID3D11Texture2D? _input, _output;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    public int Width { get; }
    public int Height { get; }
    public static (int Width, int Height) OutputSize(int width, int height, ModeRotation rotation) =>
        rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270 ? (height, width) : (width, height);

    public DdaFrameRotator(ID3D11Device device, ID3D11DeviceContext context, int width, int height, ModeRotation rotation)
    {
        _device = device; _context = context; _width = width; _height = height;
        (Width, Height) = OutputSize(width, height, rotation);
        string coordinate = rotation switch
        {
            ModeRotation.Rotate90 => "uint2(p.y, h - 1 - p.x)",
            ModeRotation.Rotate180 => "uint2(w - 1 - p.x, h - 1 - p.y)",
            ModeRotation.Rotate270 => "uint2(w - 1 - p.y, p.x)",
            _ => "p"
        };
        string shader = """
            Texture2D<float4> Src : register(t0);
            float4 Vs(uint id : SV_VertexID) : SV_Position {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            }
            float4 Ps(float4 pos : SV_Position) : SV_Target {
                uint w, h; Src.GetDimensions(w, h);
                uint2 p = uint2(pos.xy);
            """ + "return Src.Load(int3(" + coordinate + ", 0)); }";
        try
        {
            _vs = device.CreateVertexShader(Vortice.D3DCompiler.Compiler.Compile(shader, "Vs", "dda-rotate.hlsl", "vs_5_0").Span);
            _ps = device.CreatePixelShader(Vortice.D3DCompiler.Compiler.Compile(shader, "Ps", "dda-rotate.hlsl", "ps_5_0").Span);
            var description = new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
            };
            _input = Diagnostics.GpuResourceLedger.Track(device.CreateTexture2D(description), "поворот DDA: вход");
            _sourceView = device.CreateShaderResourceView(_input);
            description.Width = (uint)Width; description.Height = (uint)Height;
            description.BindFlags = BindFlags.RenderTarget;
            _output = Diagnostics.GpuResourceLedger.Track(device.CreateTexture2D(description), "поворот DDA: выход");
            _targetView = device.CreateRenderTargetView(_output);
        }
        catch { Dispose(); throw; }
    }

    public ID3D11Texture2D Rotate(ID3D11Texture2D source)
    {
        var description = source.Description;
        if (description.Width != _width || description.Height != _height)
            throw new CaptureFrameSizeMismatchException((int)description.Width, (int)description.Height, _width, _height);
        lock (_device)
        {
            _context.CopyResource(_input!, source);
            _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_vs);
            _context.RSSetState(null);
            _context.OMSetBlendState(null);
            _context.OMSetDepthStencilState(null);
            _context.OMSetRenderTargets(_targetView!);
            _context.RSSetViewport(0, 0, Width, Height);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResource(0, _sourceView!);
            _context.Draw(3, 0);
            _context.PSSetShaderResource(0, null!);
            _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            return _output!;
        }
    }

    public void Dispose()
    {
        _sourceView?.Dispose(); _targetView?.Dispose(); _input?.Dispose(); _output?.Dispose();
        _vs?.Dispose(); _ps?.Dispose();
    }
}

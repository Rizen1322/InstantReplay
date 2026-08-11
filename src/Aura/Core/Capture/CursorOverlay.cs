using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// Дорисовка курсора в кадр Desktop Duplication.
///
/// WGC кладёт курсор в кадр сама, а дупликация отдаёт его ОТДЕЛЬНО: позицию — в
/// метаданных кадра, форму — по запросу. Здесь курсор возвращается в кадр.
///
/// Форм три, и различаются они способом наложения (какая придёт — решает Windows):
///
///   Цветной        BGRA с прозрачностью → смешивание по альфе.
///   Монохромный    две битовые маски, AND и XOR: итог = (фон И A) ИСКЛ.ИЛИ X.
///                  Так работает стрелка и текстовый «I»: он не рисуется своим
///                  цветом, а ИНВЕРТИРУЕТ фон и потому виден на любом.
///   Маскированный  BGRA, где альфа — признак: 0 положить цвет, 255 инвертировать фон.
///
/// ГЛАВНОЕ РЕШЕНИЕ: фон под курсором НЕ читается. Первая версия копировала кусок
/// кадра в отдельную текстуру, чтобы шейдер мог прочитать его для инверсии, и тут же
/// рисовала в тот же кадр — «читаем то, во что только что писали». Каждый кадр GPU
/// выстраивал это в цепочку, цикл захвата замедлялся вдвое: 118 кадров в секунду
/// превращались в 70, запись — 60 fps в 53.
///
/// Вместо чтения фона обе операции выражены РЕЖИМАМИ СМЕШИВАНИЯ, ровно как их делает
/// сама Windows двумя вызовами BitBlt (SRCAND, затем SRCINVERT):
///   И:            итог = 0·исток + фон·исток      → фон·A
///   ИСКЛ.ИЛИ:     итог = исток·(1−фон) + фон·(1−исток)
///                 при X=1 даёт 1−фон, при X=0 оставляет фон — точное ИСКЛ.ИЛИ для
///                 масок 0/1, а 1−цвет для восьми бит равно ИСКЛ.ИЛИ с 255.
/// Положение задаётся областью вывода, поэтому нет ни буфера констант, ни его
/// обновления на каждый кадр.
/// </summary>
internal sealed class CursorOverlay : IDisposable
{
    private const int ShapeMonochrome = 1;
    private const int ShapeColor = 2;
    private const int ShapeMaskedColor = 4;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psAnd;      // выдаёт маску AND
    private ID3D11PixelShader? _psXor;      // выдаёт маску XOR
    private ID3D11PixelShader? _psColor;    // выдаёт цвет с прозрачностью
    /// <summary>Готов ли курсор рисоваться. false — шейдер не собрался, наложения не будет.</summary>
    public bool IsReady => _ready;

    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _blendAnd;
    private ID3D11BlendState? _blendXor;
    private ID3D11BlendState? _blendAlpha;
    private bool _ready;

    private ID3D11Texture2D? _shape;
    private ID3D11ShaderResourceView? _shapeView;
    private int _shapeWidth, _shapeHeight, _shapeType;
    private int _hotspotX, _hotspotY;

    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;

    private int _x, _y;
    private bool _visible;

    public CursorOverlay(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _ready = TryBuildPipeline();
    }

    /// <summary>Забрать из кадра позицию курсора и, если сменилась, его форму.</summary>
    public void Update(IDXGIOutputDuplication duplication, in OutduplFrameInfo info)
    {
        if (!_ready) return;

        // Позиция приходит только когда курсор двигался или менял видимость;
        // в остальных кадрах поле нулевое и трогать состояние нельзя.
        if (info.LastMouseUpdateTime != 0)
        {
            _visible = info.PointerPosition.Visible;
            _x = info.PointerPosition.Position.X;
            _y = info.PointerPosition.Position.Y;
        }

        if (info.PointerShapeBufferSize <= 0) return;

        try
        {
            var buffer = new byte[(int)info.PointerShapeBufferSize];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                duplication.GetFramePointerShape((uint)buffer.Length, handle.AddrOfPinnedObject(),
                    out uint _, out OutduplPointerShapeInfo shape).CheckError();
                BuildShapeTexture(buffer, shape);
            }
            finally { handle.Free(); }
        }
        catch (Exception ex) { Log.Warn("Capture", $"Форма курсора не прочитана: {ex.Message}"); }
    }

    /// <summary>Наложить курсор на кадр. Вызывается после того, как кадр возвращён системе.</summary>
    public void Draw(ID3D11Texture2D frame)
    {
        if (!_ready || !_visible || _shapeView is null || _shapeWidth <= 0) return;

        var desc = frame.Description;
        int frameWidth = (int)desc.Width, frameHeight = (int)desc.Height;

        int left = _x - _hotspotX, top = _y - _hotspotY;
        if (left + _shapeWidth <= 0 || top + _shapeHeight <= 0 || left >= frameWidth || top >= frameHeight) return;

        try
        {
            EnsureTargetView(frame);
            _context.OMSetRenderTargets(_targetView!);
            // Область вывода задаёт положение курсора: за краями экрана она может
            // уходить в минус, растеризатор обрежет сам. Так не нужен ни буфер
            // констант, ни его обновление на каждый кадр.
            _context.RSSetViewport(left, top, _shapeWidth, _shapeHeight);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_vs);
            _context.PSSetShaderResources(0, [_shapeView]);
            _context.PSSetSampler(0, _sampler);

            if (_shapeType == ShapeColor)
            {
                _context.PSSetShader(_psColor);
                _context.OMSetBlendState(_blendAlpha);
                _context.Draw(4, 0);
            }
            else
            {
                // Два прохода, как у Windows: сначала И с маской, потом ИСКЛ.ИЛИ
                _context.PSSetShader(_psAnd);
                _context.OMSetBlendState(_blendAnd);
                _context.Draw(4, 0);

                _context.PSSetShader(_psXor);
                _context.OMSetBlendState(_blendXor);
                _context.Draw(4, 0);
            }

            // Снимаем привязки: тот же кадр следом читает видеопроцессор
            _context.PSSetShaderResources(0, [null!]);
            _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            _context.OMSetBlendState(null);
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Курсор не отрисован: {ex.Message}");
            _ready = false; // одной ошибки достаточно: не сыпать её каждый кадр
        }
    }

    // ---------------- Форма курсора ----------------

    private void BuildShapeTexture(byte[] buffer, OutduplPointerShapeInfo shape)
    {
        _shapeType = (int)shape.Type;
        _hotspotX = shape.HotSpot.X;
        _hotspotY = shape.HotSpot.Y;

        int width = (int)shape.Width;
        // У монохромного курсора в буфере две маски одна под другой
        int height = _shapeType == ShapeMonochrome ? (int)shape.Height / 2 : (int)shape.Height;
        if (width <= 0 || height <= 0) return;

        var pixels = _shapeType switch
        {
            ShapeMonochrome => ExpandMonochrome(buffer, width, height, (int)shape.Pitch),
            ShapeMaskedColor => ExpandMaskedColor(buffer, width, height, (int)shape.Pitch),
            _ => CopyColor(buffer, width, height, (int)shape.Pitch)
        };

        _shapeView?.Dispose();
        _shape?.Dispose();

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource
        };

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * 4));
            _shape = _device.CreateTexture2D(description, [data]);
        }
        finally { handle.Free(); }

        _shapeView = _device.CreateShaderResourceView(_shape);
        _shapeWidth = width;
        _shapeHeight = height;
    }

    /// <summary>
    /// Монохромный курсор: две битовые маски подряд. Раскладываем так, чтобы каждый
    /// проход отрисовки просто взял свой канал: синий — маска AND, зелёный — XOR.
    /// </summary>
    private static byte[] ExpandMonochrome(byte[] buffer, int width, int height, int pitch)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int andRow = y * pitch;
            int xorRow = (y + height) * pitch;
            for (int x = 0; x < width; x++)
            {
                int mask = 0x80 >> (x & 7);
                int index = x >> 3;

                bool andBit = andRow + index < buffer.Length && (buffer[andRow + index] & mask) != 0;
                bool xorBit = xorRow + index < buffer.Length && (buffer[xorRow + index] & mask) != 0;

                int p = (y * width + x) * 4;
                pixels[p + 0] = andBit ? (byte)255 : (byte)0; // B — маска AND
                pixels[p + 1] = xorBit ? (byte)255 : (byte)0; // G — маска XOR
                pixels[p + 2] = 0;
                pixels[p + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>
    /// Маскированный цветной приводим к тем же двум маскам: альфа 0 — положить цвет
    /// (маска AND = 0, XOR = цвет), альфа 255 — инвертировать фон (AND = 1, XOR = 1).
    /// </summary>
    private static byte[] ExpandMaskedColor(byte[] buffer, int width, int height, int pitch)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int row = y * pitch;
            for (int x = 0; x < width; x++)
            {
                int source = row + x * 4;
                if (source + 3 >= buffer.Length) continue;

                bool invert = buffer[source + 3] != 0;
                int p = (y * width + x) * 4;
                pixels[p + 0] = invert ? (byte)255 : (byte)0;              // AND
                pixels[p + 1] = invert ? (byte)255 : buffer[source + 0];   // XOR (цвет как есть)
                pixels[p + 2] = invert ? (byte)255 : buffer[source + 1];
                pixels[p + 3] = invert ? (byte)255 : buffer[source + 2];
            }
        }
        return pixels;
    }

    private static byte[] CopyColor(byte[] buffer, int width, int height, int pitch)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int source = y * pitch;
            int target = y * width * 4;
            int length = Math.Min(width * 4, Math.Max(0, buffer.Length - source));
            if (length > 0) Buffer.BlockCopy(buffer, source, pixels, target, length);
        }
        return pixels;
    }

    private void EnsureTargetView(ID3D11Texture2D frame)
    {
        if (ReferenceEquals(_target, frame) && _targetView is not null) return;
        _targetView?.Dispose();
        _targetView = _device.CreateRenderTargetView(frame);
        _target = frame;
    }

    // ---------------- Шейдеры и смешивание ----------------

    // ВНИМАНИЕ: в тексте шейдера только латиница и никаких комментариев по-русски.
    // Кириллица в UTF-8 занимает два байта на букву, и компилятор получал буфер
    // короче, чем текст: на Windows 10 у друга сборка обрывалась на 26-й строке с
    // «unexpected end of file», хотя на машине разработчика всё компилировалось.
    // Пояснения к шейдеру — здесь, в коде:
    //   PsAnd   — выдаёт маску AND (синий канал текстуры формы)
    //   PsXor   — выдаёт маску XOR (зелёный; для маскированного цветного там же цвет)
    //   PsColor — обычный цветной курсор с прозрачностью
    private const string ShaderSource = """
        struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VsOut VsMain(uint id : SV_VertexID)
        {
            float2 corner = float2(id & 1, id >> 1);
            VsOut o;
            o.pos = float4(corner.x * 2 - 1, 1 - corner.y * 2, 0, 1);
            o.uv  = corner;
            return o;
        }

        Texture2D Shape : register(t0);
        SamplerState Point : register(s0);

        float4 PsAnd(VsOut i) : SV_Target
        {
            float a = Shape.Sample(Point, i.uv).b;
            return float4(a, a, a, 1);
        }

        float4 PsXor(VsOut i) : SV_Target
        {
            float3 x = Shape.Sample(Point, i.uv).gra;
            return float4(x, 1);
        }

        float4 PsColor(VsOut i) : SV_Target
        {
            return Shape.Sample(Point, i.uv);
        }

        """;

    private bool TryBuildPipeline()
    {
        try
        {
            _vs = _device.CreateVertexShader(Compiler.Compile(ShaderSource, "VsMain", "cursor.hlsl", "vs_4_0").Span);
            _psAnd = _device.CreatePixelShader(Compiler.Compile(ShaderSource, "PsAnd", "cursor.hlsl", "ps_4_0").Span);
            _psXor = _device.CreatePixelShader(Compiler.Compile(ShaderSource, "PsXor", "cursor.hlsl", "ps_4_0").Span);
            _psColor = _device.CreatePixelShader(Compiler.Compile(ShaderSource, "PsColor", "cursor.hlsl", "ps_4_0").Span);

            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,       // курсор нельзя размывать
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MaxLOD = float.MaxValue
            });

            // фон · маска
            _blendAnd = _device.CreateBlendState(Blend(Vortice.Direct3D11.Blend.Zero, Vortice.Direct3D11.Blend.SourceColor));
            // маска·(1−фон) + фон·(1−маска) — точное ИСКЛ.ИЛИ для масок 0/1
            _blendXor = _device.CreateBlendState(Blend(Vortice.Direct3D11.Blend.InverseDestinationColor, Vortice.Direct3D11.Blend.InverseSourceColor));
            // обычная прозрачность для цветных курсоров
            _blendAlpha = _device.CreateBlendState(Blend(Vortice.Direct3D11.Blend.SourceAlpha, Vortice.Direct3D11.Blend.InverseSourceAlpha));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Курсор для дупликации недоступен: {ex.Message}");
            return false;
        }
    }

    private static BlendDescription Blend(Blend source, Blend destination)
    {
        var description = new BlendDescription();
        ref var target = ref description.RenderTarget[0];
        target.BlendEnable = true;
        target.SourceBlend = source;
        target.DestinationBlend = destination;
        target.BlendOperation = BlendOperation.Add;
        target.SourceBlendAlpha = Vortice.Direct3D11.Blend.One;
        target.DestinationBlendAlpha = Vortice.Direct3D11.Blend.Zero;
        target.BlendOperationAlpha = BlendOperation.Add;
        target.RenderTargetWriteMask = ColorWriteEnable.All;
        return description;
    }

    public void Dispose()
    {
        _targetView?.Dispose(); _target = null;
        _shapeView?.Dispose(); _shape?.Dispose();
        _blendAlpha?.Dispose(); _blendXor?.Dispose(); _blendAnd?.Dispose();
        _sampler?.Dispose();
        _psColor?.Dispose(); _psXor?.Dispose(); _psAnd?.Dispose(); _vs?.Dispose();
    }
}

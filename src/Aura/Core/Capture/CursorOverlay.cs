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
/// Монохромная форма обходится без чтения фона: обе операции выражены режимами
/// смешивания, ровно как два вызова BitBlt (SRCAND, затем SRCINVERT):
///   И:            итог = 0·исток + фон·исток      → фон·A
///   ИСКЛ.ИЛИ:     исток·(1−фон) + фон·(1−исток), точно для битов 0/1.
/// Маскированная цветная форма умеет XOR произвольного RGB. Только для неё копируем
/// маленький прямоугольник фона размером с курсор и считаем XOR в шейдере. Обычные
/// цветные и монохромные курсоры этого дополнительного копирования не платят.
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
    private ID3D11PixelShader? _psMasked;   // masked-color с точным XOR копии фона
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
    private long _shapeRevision = long.MinValue;
    private DdaCursorShape? _uploadedShape;

    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11Texture2D? _maskedBackground;
    private ID3D11ShaderResourceView? _maskedBackgroundView;

    public CursorOverlay(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _ready = TryBuildPipeline();
    }

    /// <summary>Копирует чистый кадр и накладывает только проверенный снимок курсора.</summary>
    public void Compose(
        ID3D11Texture2D clean,
        ID3D11Texture2D output,
        in DdaCursorSnapshot cursor)
    {
        _context.CopyResource(output, clean);

        try
        {
            if (!_ready || !cursor.Visible || cursor.Shape is null) return;

            EnsureShapeTexture(cursor.Revision, cursor.Shape);
            if (_shapeView is null || _shapeWidth <= 0) return;

            var desc = output.Description;
            int frameWidth = (int)desc.Width, frameHeight = (int)desc.Height;
            int left = cursor.X, top = cursor.Y;
            if (left + _shapeWidth <= 0 || top + _shapeHeight <= 0 ||
                left >= frameWidth || top >= frameHeight) return;

            if (_shapeType == ShapeMaskedColor)
                CopyMaskedBackground(output, left, top, frameWidth, frameHeight);

            EnsureTargetView(output);
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
            else if (_shapeType == ShapeMaskedColor)
            {
                _context.PSSetShaderResources(0, [_shapeView, _maskedBackgroundView!]);
                _context.PSSetShader(_psMasked);
                _context.OMSetBlendState(null);
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

        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Курсор не отрисован: {ex.Message}");
            _ready = false; // одной ошибки достаточно: не сыпать её каждый кадр
        }
        finally
        {
            // Тот же output сразу читает видеопроцессор: никаких D3D-привязок не оставляем.
            _context.PSSetShaderResources(0, [null!, null!]);
            _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            _context.OMSetBlendState(null);
        }
    }

    // ---------------- Форма курсора ----------------

    private void EnsureShapeTexture(long revision, DdaCursorShape shape)
    {
        if (_shapeRevision == revision) return;
        _shapeRevision = revision;
        if (ReferenceEquals(_uploadedShape, shape)) return;

        _shapeType = (int)shape.Kind;
        int width = shape.Width;
        int height = shape.Height;

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

        var handle = GCHandle.Alloc(shape.Pixels, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * 4));
            _shape = Aura.Core.Diagnostics.GpuResourceLedger.Track(_device.CreateTexture2D(description, [data]), "курсор");
        }
        finally { handle.Free(); }

        _shapeView = _device.CreateShaderResourceView(_shape);
        _shapeWidth = width;
        _shapeHeight = height;
        _uploadedShape = shape;
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
    //   PsAnd   — выдаёт маску AND (alpha текстуры формы)
    //   PsXor   — выдаёт монохромную маску XOR
    //   PsMasked — заменяет цвет или XOR-ит его с маленькой копией фона
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
        Texture2D Background : register(t1);
        SamplerState Point : register(s0);

        float4 PsAnd(VsOut i) : SV_Target
        {
            float a = Shape.Sample(Point, i.uv).a;
            return float4(a, a, a, 1);
        }

        float4 PsXor(VsOut i) : SV_Target
        {
            float3 x = Shape.Sample(Point, i.uv).rgb;
            return float4(x, 1);
        }

        float4 PsColor(VsOut i) : SV_Target
        {
            return Shape.Sample(Point, i.uv);
        }

        float4 PsMasked(VsOut i) : SV_Target
        {
            float4 shape = Shape.Sample(Point, i.uv);
            uint3 rgb = (uint3)round(shape.rgb * 255.0);
            if (shape.a >= 0.5)
            {
                uint3 background = (uint3)round(Background.Sample(Point, i.uv).rgb * 255.0);
                rgb ^= background;
            }
            return float4((float3)rgb / 255.0, 1);
        }

        """;

    private bool TryBuildPipeline()
    {
        try
        {
            _vs = _device.CreateVertexShader(Compiler.Compile(ShaderSource, "VsMain", "cursor.hlsl", "vs_4_0").Span);
            _psAnd = _device.CreatePixelShader(Compiler.Compile(ShaderSource, "PsAnd", "cursor.hlsl", "ps_4_0").Span);
            _psXor = _device.CreatePixelShader(Compiler.Compile(ShaderSource, "PsXor", "cursor.hlsl", "ps_4_0").Span);
            _psMasked = _device.CreatePixelShader(Compiler.Compile(ShaderSource, "PsMasked", "cursor.hlsl", "ps_4_0").Span);
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
            // Для монохромного XOR 0/1 арифметическое смешивание даёт точный битовый результат.
            _blendXor = _device.CreateBlendState(Blend(
                Vortice.Direct3D11.Blend.InverseDestinationColor,
                Vortice.Direct3D11.Blend.InverseSourceColor));
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

    /// <summary>
    /// Копирует только пересечение курсора с кадром. Позиция назначения сохраняет
    /// координаты внутри полной формы, поэтому UV шейдера совпадает и у краёв экрана.
    /// </summary>
    private void CopyMaskedBackground(ID3D11Texture2D frame, int left, int top,
                                      int frameWidth, int frameHeight)
    {
        if (_maskedBackground is null ||
            _maskedBackground.Description.Width != (uint)_shapeWidth ||
            _maskedBackground.Description.Height != (uint)_shapeHeight)
        {
            _maskedBackgroundView?.Dispose();
            _maskedBackground?.Dispose();
            _maskedBackground = Aura.Core.Diagnostics.GpuResourceLedger.Track(_device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_shapeWidth,
                Height = (uint)_shapeHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = frame.Description.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            }), "курсор");
            _maskedBackgroundView = _device.CreateShaderResourceView(_maskedBackground);
        }

        int sourceLeft = Math.Max(0, left);
        int sourceTop = Math.Max(0, top);
        int sourceRight = Math.Min(frameWidth, left + _shapeWidth);
        int sourceBottom = Math.Min(frameHeight, top + _shapeHeight);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop) return;

        var sourceBox = new Box(sourceLeft, sourceTop, 0, sourceRight, sourceBottom, 1);
        _context.CopySubresourceRegion(
            _maskedBackground, 0,
            (uint)(sourceLeft - left), (uint)(sourceTop - top), 0,
            frame, 0, sourceBox);
    }

    public void Dispose()
    {
        _maskedBackgroundView?.Dispose(); _maskedBackground?.Dispose();
        _targetView?.Dispose(); _target = null;
        _shapeView?.Dispose(); _shape?.Dispose();
        _blendAlpha?.Dispose(); _blendXor?.Dispose(); _blendAnd?.Dispose();
        _sampler?.Dispose();
        _psColor?.Dispose(); _psMasked?.Dispose(); _psXor?.Dispose(); _psAnd?.Dispose(); _vs?.Dispose();
    }
}

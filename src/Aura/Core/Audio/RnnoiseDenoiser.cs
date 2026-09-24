using System.Runtime.InteropServices;
using Aura.Core.Logging;

namespace Aura.Core.Audio;

/// <summary>
/// Шумоподавление микрофона нейросетью RNNoise (Xiph, BSD-3; та же, что в OBS),
/// собранной в Aura.Media64.dll.
///
/// ЗАЧЕМ. Шумовой гейт умеет только одно — молчать, когда тихо. Вентилятор, гул
/// блока питания и клавиатура никуда не деваются, пока человек говорит, а в паузах
/// гейт хлопает. RNNoise вычитает шум из самого голоса и в паузах оставляет
/// ровную тишину без щелчков. Работает кадрами по 10 мс при 48 кГц — ровно блок
/// микшера — и стоит около процента одного ядра.
/// </summary>
internal sealed partial class RnnoiseDenoiser : IDisposable
{
    private const string Dll = "Aura.Media64.dll";
    public const int FrameSize = 480;

    [LibraryImport(Dll, EntryPoint = "rnnoise_create")]
    private static partial IntPtr Create(IntPtr model);

    [LibraryImport(Dll, EntryPoint = "rnnoise_destroy")]
    private static partial void Destroy(IntPtr state);

    [LibraryImport(Dll, EntryPoint = "rnnoise_process_frame")]
    private static unsafe partial float Process(IntPtr state, float* output, float* input);

    private IntPtr _state;
    private readonly float[] _in = new float[FrameSize];
    private readonly float[] _out = new float[FrameSize];

    /// <summary>Вероятность голоса в последнем блоке (0..1).</summary>
    public float VoiceProbability { get; private set; }

    private RnnoiseDenoiser(IntPtr state) => _state = state;

    /// <summary>Создать; null — библиотеки нет (тогда микрофон пишется без шумодава).</summary>
    public static RnnoiseDenoiser? TryCreate()
    {
        try
        {
            IntPtr state = Create(IntPtr.Zero);
            return state == IntPtr.Zero ? null : new RnnoiseDenoiser(state);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Warn("Audio", $"Шумоподавление RNNoise недоступно: {ex.Message}");
            return null;
        }
    }

    /// <summary>Обработать блок моно 10 мс на месте (значения в диапазоне ±1).</summary>
    public unsafe void ProcessBlock(Span<float> mono)
    {
        if (_state == IntPtr.Zero || mono.Length != FrameSize) return;
        // RNNoise обучена на отсчётах в шкале 16-битного звука
        for (int i = 0; i < FrameSize; i++) _in[i] = mono[i] * 32768f;
        fixed (float* input = _in)
        fixed (float* output = _out)
            VoiceProbability = Process(_state, output, input);
        for (int i = 0; i < FrameSize; i++) mono[i] = _out[i] / 32768f;
    }

    public void Dispose()
    {
        var state = Interlocked.Exchange(ref _state, IntPtr.Zero);
        if (state != IntPtr.Zero) Destroy(state);
    }
}

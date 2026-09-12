namespace Aura.Core.Encoding;

/// <summary>
/// Проверка, можно ли продолжать один RAM-буфер кадрами новой сессии энкодера.
/// Одинаковых размеров и codec enum недостаточно: H.264/HEVC кадры ссылаются на
/// параметры из sequence header, который затем попадёт в MP4.
/// </summary>
internal static class EncodedStreamCompatibility
{
    public static bool SameSequenceHeader(byte[]? previous, byte[]? current) =>
        previous is { Length: > 0 } && current is { Length: > 0 } &&
        previous.AsSpan().SequenceEqual(current);
}

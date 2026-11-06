using Aura.Core.Buffering;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Выдача и возврат блоков арены. Раньше проход по страницам нового блока шёл под
/// тем же замком, что и выдача: пока фон прогревал 16 МБ, поток, забирающий кадры
/// у энкодера, стоял в EnsureChunk на всё это время.
/// </summary>
public sealed class AuraArenaChunkTests
{
    private const long TwoChunks = ArenaStorage.ChunkBytes * 2;

    [Fact]
    public unsafe void Page_warmup_preserves_bytes_already_written_by_the_encoder()
    {
        var storage = new NativeArenaStorage(TwoChunks);
        try
        {
            storage.EnsureChunk(0);
            var bytes = new Span<byte>((void*)storage.Base, 8192);
            bytes.Fill(94);
            ArenaStorage.Touch(storage.Base, bytes.Length);
            Assert.All(bytes.ToArray(), value => Assert.Equal((byte)94, value));
        }
        finally { storage.Release(); }
    }

    [Fact]
    public void Chunk_committed_by_writer_can_be_released_without_background_warmup()
    {
        var storage = new NativeArenaStorage(TwoChunks);
        try
        {
            storage.EnsureChunk(0);
            storage.ReleaseChunk(0);
            Assert.Equal(0, storage.ResidentChunks);
        }
        finally { storage.Release(); }
    }

    [Fact]
    public void Prepared_chunk_is_writable_and_release_returns_it()
    {
        var storage = new NativeArenaStorage(TwoChunks);
        try
        {
            storage.Prepare(1);
            Assert.Equal(1, storage.ResidentChunks);

            // Блок уже выдан: повторная выдача ничего не делает.
            storage.EnsureChunk(1);
            Assert.Equal(1, storage.ResidentChunks);

            storage.ReleaseChunk(1);
            Assert.Equal(0, storage.ResidentChunks);
        }
        finally { storage.Release(); }
    }

    [Fact]
    public async Task Release_during_prepare_does_not_leave_the_chunk_stuck()
    {
        var storage = new NativeArenaStorage(TwoChunks);
        try
        {
            Task prepare = Task.Run(() => storage.Prepare(0));
            for (int i = 0; i < 50; i++) storage.ReleaseChunk(0);
            await prepare;

            // Проход закончился — следующий возврат обязан снять блок.
            storage.ReleaseChunk(0);
            Assert.Equal(0, storage.ResidentChunks);

            // И блок снова выдаётся обычным путём записи кадра.
            storage.EnsureChunk(0);
            Assert.Equal(1, storage.ResidentChunks);
        }
        finally { storage.Release(); }
    }
}

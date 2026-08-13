using System.Runtime.InteropServices;
using Aura.Core.Saving;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Владение буферами арены.
///
/// Главная проверка — <see cref="СчётчикиПартийНеЗависятДругОтДруга"/>: раньше счётчик
/// живых буферов был один статический на процесс, и второе сохранение, начатое пока
/// первое ждёт разгрузки писателя, поднимало его своими буферами. Первое видело
/// «писатель ещё держит», высиживало 30 секунд и оставляло блоки арены закреплёнными
/// навсегда.
///
/// Заодно тесты фиксируют рукописную vtable: порядок методов взят из mfobjects.h
/// вручную, и ошибка там проявилась бы не исключением, а падением процесса.
/// </summary>
public class ArenaBufferBatchTests
{
    private static readonly Guid IidMediaBuffer = new("045FA593-8799-42B8-BC8D-8968C6453507");

    [Fact]
    public void СчётчикиПартийНеЗависятДругОтДруга()
    {
        var first = new ArenaBufferBatch();
        var second = new ArenaBufferBatch();
        IntPtr data = Marshal.AllocHGlobal(16);
        try
        {
            IntPtr a = first.Create(data, 16);
            Assert.Equal(1, first.Alive);
            Assert.Equal(0, second.Alive);

            IntPtr b = second.Create(data, 16);
            Assert.Equal(1, first.Alive);
            Assert.Equal(1, second.Alive);

            // Чужая партия разгрузилась — на нашу это влиять не должно
            Marshal.Release(a);
            Assert.Equal(0, first.Alive);
            Assert.Equal(1, second.Alive);

            Marshal.Release(b);
            Assert.Equal(0, second.Alive);
        }
        finally
        {
            first.Free();
            second.Free();
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void ПартияЗанятаПокаПисательДержитБуфер()
    {
        var batch = new ArenaBufferBatch();
        IntPtr data = Marshal.AllocHGlobal(16);
        try
        {
            IntPtr buffer = batch.Create(data, 16);

            Marshal.AddRef(buffer);      // «писатель» взял свою ссылку
            Marshal.Release(buffer);     // мы отдали свою
            Assert.Equal(1, batch.Alive);

            Marshal.Release(buffer);     // писатель отпустил
            Assert.Equal(0, batch.Alive);
        }
        finally
        {
            batch.Free();
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void БуферОтдаётсяТолькоКакIMFMediaBuffer()
    {
        var batch = new ArenaBufferBatch();
        IntPtr data = Marshal.AllocHGlobal(16);
        try
        {
            IntPtr buffer = batch.Create(data, 16);

            Guid wanted = IidMediaBuffer;
            Assert.Equal(0, Marshal.QueryInterface(buffer, in wanted, out IntPtr asMediaBuffer));
            Assert.Equal(buffer, asMediaBuffer);
            Marshal.Release(asMediaBuffer);

            // IMF2DBuffer и IMFDXGIBuffer мы не изображаем — писатель обязан
            // обойтись линейным буфером, и отказ должен быть честным
            Guid other = new("11111111-2222-3333-4444-555555555555");
            Assert.NotEqual(0, Marshal.QueryInterface(buffer, in other, out _));
            Assert.Equal(1, batch.Alive);

            Marshal.Release(buffer);
            Assert.Equal(0, batch.Alive);
        }
        finally
        {
            batch.Free();
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void ДлинаБуфераЧитаетсяЧерезVtable()
    {
        var batch = new ArenaBufferBatch();
        IntPtr data = Marshal.AllocHGlobal(64);
        try
        {
            IntPtr buffer = batch.Create(data, 64);
            Assert.Equal(64u, CurrentLength(buffer));
            Assert.Equal(64u, MaxLength(buffer));
            Marshal.Release(buffer);
        }
        finally
        {
            batch.Free();
            Marshal.FreeHGlobal(data);
        }
    }

    // Слоты vtable по mfobjects.h: 0-2 IUnknown, 3 Lock, 4 Unlock,
    // 5 GetCurrentLength, 6 SetCurrentLength, 7 GetMaxLength.
    private static unsafe uint CurrentLength(IntPtr buffer) => ReadUInt32Slot(buffer, 5);
    private static unsafe uint MaxLength(IntPtr buffer) => ReadUInt32Slot(buffer, 7);

    private static unsafe uint ReadUInt32Slot(IntPtr buffer, int slot)
    {
        var vtable = *(void***)buffer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtable[slot];
        uint value;
        int hr = method(buffer, &value);
        Assert.Equal(0, hr);
        return value;
    }
}

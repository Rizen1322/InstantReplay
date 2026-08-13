using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aura.Core.Saving;

/// <summary>
/// Буфер Media Foundation поверх ЧУЖОЙ памяти — без копирования.
///
/// Зачем. Раньше на каждый кадр вызывался MFCreateMemoryBuffer, и байты кадра
/// копировались в свежую нативную память. Замер показал, во что это обходится:
/// из 11207 сэмплов клипа 8732 на момент нашего освобождения имели вторую ссылку —
/// писателя. То есть IMFSinkWriter держит у себя почти весь клип разом, и вместе
/// с сэмплами держит их буферы. Отсюда прирост нативной памяти ровно на размер
/// клипа при каждом сохранении.
///
/// Копировать незачем: кадры уже лежат в арене кольцевого буфера, и снимок держит
/// их замороженными до конца записи файла (см. ReplayVideoBuffer.ReleaseSnapshot).
/// Время жизни совпадает идеально, поэтому буфер может просто ссылаться на арену.
/// Писатель волен удерживать сколько угодно сэмплов — новой памяти это не стоит.
///
/// Реализация — вручную собранный COM-объект: указатель на таблицу методов плюс
/// поля. Готовой обёртки нет: Vortice умеет вызывать интерфейсы Media Foundation,
/// но не реализовывать их на стороне .NET.
///
/// ВАЖНО: память, на которую ссылается буфер, обязана быть закреплённой и живой,
/// пока писатель не отпустит сэмпл. За это отвечает вызывающий (ReplaySaver
/// закрепляет блоки арены на время записи файла).
/// </summary>
internal static unsafe class ArenaMediaBuffer
{
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidMediaBuffer = new("045FA593-8799-42B8-BC8D-8968C6453507");

    private const int SOk = 0;
    private const int EPointer = unchecked((int)0x80004003);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EInvalidArg = unchecked((int)0x80070057);

    /// <summary>Сам COM-объект: первым полем обязан идти указатель на таблицу методов.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public IntPtr Vtbl;
        public int RefCount;
        public IntPtr Data;
        public uint MaxLength;
        public uint CurrentLength;
        /// <summary>Счётчик живых буферов своей партии (см. <see cref="ArenaBufferBatch"/>).</summary>
        public IntPtr Alive;
    }

    private static readonly IntPtr SharedVtbl = BuildVtbl();

    private static IntPtr BuildVtbl()
    {
        // Порядок строго по mfobjects.h: три метода IUnknown, затем пять своих.
        var slots = (IntPtr*)NativeMemory.Alloc(8, (nuint)sizeof(IntPtr));
        slots[0] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, Guid*, IntPtr*, int>)&QueryInterface;
        slots[1] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
        slots[2] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint>)&Release;
        slots[3] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, byte**, uint*, uint*, int>)&Lock;
        slots[4] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, int>)&Unlock;
        slots[5] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint*, int>)&GetCurrentLength;
        slots[6] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint, int>)&SetCurrentLength;
        slots[7] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint*, int>)&GetMaxLength;
        return (IntPtr)slots;
    }

    /// <summary>
    /// Создать буфер поверх готовых байт. Счётчик ссылок = 1, эта ссылка переходит
    /// вызывающему. <paramref name="alive"/> — счётчик партии: поднимается здесь,
    /// опускается в <see cref="Release"/>, когда буфер действительно умер.
    /// </summary>
    public static IntPtr Create(int* alive, IntPtr data, int length)
    {
        var instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
        instance->Vtbl = SharedVtbl;
        instance->RefCount = 1;
        instance->Data = data;
        instance->MaxLength = (uint)length;
        instance->CurrentLength = (uint)length;
        instance->Alive = (IntPtr)alive;
        Interlocked.Increment(ref *alive);
        return (IntPtr)instance;
    }

    // ---------------- IUnknown ----------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Instance* self, Guid* iid, IntPtr* result)
    {
        if (result is null) return EPointer;
        *result = IntPtr.Zero;
        if (iid is null) return EPointer;

        if (*iid == IidUnknown || *iid == IidMediaBuffer)
        {
            Interlocked.Increment(ref self->RefCount);
            *result = (IntPtr)self;
            return SOk;
        }
        // Ни IMF2DBuffer, ни IMFDXGIBuffer мы не изображаем: писатель MP4 их
        // спрашивает, но прекрасно обходится обычным линейным буфером.
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Instance* self)
    {
        int left = Interlocked.Decrement(ref self->RefCount);
        if (left == 0)
        {
            // Счётчик читаем ДО освобождения объекта: после Free поле уже мусор.
            int* alive = (int*)self->Alive;
            NativeMemory.Free(self); // саму арену не трогаем — она не наша
            if (alive is not null) Interlocked.Decrement(ref *alive);
        }
        return (uint)Math.Max(left, 0);
    }

    // ---------------- IMFMediaBuffer ----------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Lock(Instance* self, byte** buffer, uint* maxLength, uint* currentLength)
    {
        if (buffer is null) return EPointer;
        *buffer = (byte*)self->Data;
        // Оба размера необязательны — писатель вправе передать null.
        if (maxLength is not null) *maxLength = self->MaxLength;
        if (currentLength is not null) *currentLength = self->CurrentLength;
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unlock(Instance* self) => SOk; // память закреплена снаружи, освобождать нечего

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetCurrentLength(Instance* self, uint* length)
    {
        if (length is null) return EPointer;
        *length = self->CurrentLength;
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetCurrentLength(Instance* self, uint length)
    {
        if (length > self->MaxLength) return EInvalidArg;
        self->CurrentLength = length;
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetMaxLength(Instance* self, uint* length)
    {
        if (length is null) return EPointer;
        *length = self->MaxLength;
        return SOk;
    }
}

/// <summary>
/// Партия буферов ОДНОГО сохранения: знает, сколько её буферов ещё живо.
///
/// Пока счётчик не ноль, память арены, на которую они смотрят, трогать нельзя:
/// писатель отпускает сэмплы асинхронно и вполне может додержать часть после того,
/// как его самого освободили.
///
/// Счётчик обязан быть СВОЙ на каждое сохранение. Раньше он был один статический на
/// весь процесс, и ломалось это так: движок возвращает состояние Running сразу после
/// закрытия писателя, а ожидание разгрузки продолжается в фоне. Следующее сохранение,
/// начатое в эту щель, поднимало общий счётчик своими буферами — предыдущее видело
/// «писатель ещё держит» там, где держать было уже нечего, высиживало все 30 секунд
/// и уходило в аварийную ветку, оставляя блоки арены закреплёнными НАВСЕГДА.
///
/// Счётчик нативный, а не обычное поле: его правит Release, который прилетает из
/// потоков Media Foundation в произвольный момент — в том числе когда управляемая
/// часть сохранения давно закончилась.
/// </summary>
internal sealed unsafe class ArenaBufferBatch
{
    private IntPtr _counter = (IntPtr)NativeMemory.AllocZeroed(1, (nuint)sizeof(int));

    /// <summary>Сколько буферов партии ещё живо; 0 — писатель отпустил всё.</summary>
    public int Alive
    {
        get
        {
            IntPtr p = Volatile.Read(ref _counter);
            return p == IntPtr.Zero ? 0 : Volatile.Read(ref *(int*)p);
        }
    }

    /// <summary>Буфер Media Foundation поверх памяти арены, отнесённый к этой партии.</summary>
    public IntPtr Create(IntPtr data, int length) =>
        ArenaMediaBuffer.Create((int*)_counter, data, length);

    /// <summary>
    /// Отпустить счётчик. Звать ТОЛЬКО когда <see cref="Alive"/> == 0: буферы правят
    /// его из Release, и освобождение раньше времени — это запись по мёртвому адресу.
    /// При зависшем писателе счётчик намеренно утекает вместе с закреплёнными блоками
    /// арены: несколько байт безопаснее краха.
    /// </summary>
    public void Free()
    {
        IntPtr p = Interlocked.Exchange(ref _counter, IntPtr.Zero);
        if (p != IntPtr.Zero) NativeMemory.Free((void*)p);
    }
}

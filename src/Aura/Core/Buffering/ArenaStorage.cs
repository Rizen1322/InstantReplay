using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Aura.Core.Logging;

namespace Aura.Core.Buffering;

/// <summary>
/// Непрерывное адресное пространство под арену кольцевого буфера видео.
///
/// ПОЧЕМУ НЕ byte[]. Раньше арена была набором 16-МБ массивов в куче больших
/// объектов .NET. Отсюда росло всё расследование памяти в docs/ПАМЯТЬ-*.md:
/// • сборщик мусора с включённым SustainedLowLatency месяцами не видел этих блоков,
///   и «частная память» процесса никак не сходилась с тем, что держит буфер;
/// • на время записи файла блоки приходилось закреплять (GCHandle.Pinned), иначе
///   Media Foundation читала бы сдвинутую память;
/// • потолок одного массива (2 ГБ) заставлял резать арену на блоки и следить, чтобы
///   кадр не лёг на стык.
///
/// Здесь память берётся у системы напрямую, одним непрерывным диапазоном адресов.
/// Сборщик мусора её не видит и не двигает, закреплять нечего, кадр может лежать
/// где угодно. Физическая память выдаётся блоками (<see cref="ChunkBytes"/>) по
/// мере того, как кольцо до них доходит, и возвращается системе, когда блок
/// позади кольца опустел.
///
/// Два вида хранилища:
/// • <see cref="NativeArenaStorage"/> — оперативная память (VirtualAlloc);
/// • <see cref="FileArenaStorage"/> — файл на диске, отображённый в память. Так
///   делает ShadowPlay: буфер на 20–30 минут в оперативную память не помещается,
///   а на диске его место есть. Страницы файла пишет на диск сама система, и в
///   памяти процесса остаётся только то, что сейчас пишется.
///
/// Время жизни — по счётчику ссылок: хранилище живёт, пока его держит буфер ИЛИ
/// снимок, который в этот момент пишется в файл. Буфер может перейти на новое
/// хранилище (смена настроек), а старое освободится, когда писатель закончит.
/// </summary>
public abstract class ArenaStorage
{
    /// <summary>Гранулярность выдачи физической памяти.</summary>
    public const int ChunkBytes = 16 << 20;

    private int _refs = 1;

    protected ArenaStorage(long capacity)
    {
        Capacity = capacity;
    }

    /// <summary>Размер диапазона, кратен <see cref="ChunkBytes"/>.</summary>
    public long Capacity { get; }

    /// <summary>Начало диапазона.</summary>
    public abstract IntPtr Base { get; }

    public int ChunkCount => (int)(Capacity / ChunkBytes);

    /// <summary>Сколько блоков сейчас занимают физическую память.</summary>
    public abstract int ResidentChunks { get; }

    /// <summary>Хранилище на диске — для диагностики и подписей.</summary>
    public virtual bool OnDisk => false;

    /// <summary>
    /// Подготовить блок к записи. Зовётся на горячем пути, поэтому обязан быть
    /// дешёвым для уже готового блока; тяжёлую часть делает <see cref="Prepare"/>.
    /// </summary>
    public abstract void EnsureChunk(int index);

    /// <summary>Подготовить блок заранее, в фоне: выдать память и пройти её страницы.</summary>
    public abstract void Prepare(int index);

    /// <summary>Блок опустел: можно вернуть его память системе.</summary>
    public abstract void ReleaseChunk(int index);

    /// <summary>Блок дописан до конца: хранилище на диске отпускает его из памяти.</summary>
    public virtual void Seal(int index) { }

    public void AddRef()
    {
        if (Interlocked.Increment(ref _refs) <= 1)
            throw new ObjectDisposedException(nameof(ArenaStorage));
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Free();
    }

    protected abstract void Free();

    // ---------------- Win32 ----------------

    protected const uint MemCommit = 0x1000;
    protected const uint MemReserve = 0x2000;
    protected const uint MemDecommit = 0x4000;
    protected const uint MemRelease = 0x8000;
    protected const uint PageReadWrite = 0x04;
    protected const uint PageNoAccess = 0x01;

    [DllImport("kernel32.dll", SetLastError = true)]
    protected static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    protected static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    protected static extern bool VirtualUnlock(IntPtr address, nuint size);

    /// <summary>Пройти страницы блока, чтобы система выдала их сейчас, а не на горячем пути.</summary>
    protected static unsafe void Touch(IntPtr start, int bytes)
    {
        byte* p = (byte*)start;
        for (int offset = 0; offset < bytes; offset += 4096)
            p[offset] = 0;
    }
}

/// <summary>Арена в оперативной памяти: диапазон резервируется целиком, память выдаётся блоками.</summary>
public sealed class NativeArenaStorage : ArenaStorage
{
    private IntPtr _base;
    // 0 — блок не выдан, 1 — выдан. Меняется под замком буфера или в Prepare.
    private readonly int[] _committed;
    private int _resident;

    public NativeArenaStorage(long capacity) : base(capacity)
    {
        _base = VirtualAlloc(IntPtr.Zero, (nuint)capacity, MemReserve, PageNoAccess);
        if (_base == IntPtr.Zero)
            throw new OutOfMemoryException(
                $"Не удалось зарезервировать {capacity >> 20} МБ адресов под буфер повтора " +
                $"(ошибка {Marshal.GetLastWin32Error()})");
        _committed = new int[ChunkCount];
    }

    public override IntPtr Base => _base;

    public override int ResidentChunks => Volatile.Read(ref _resident);

    public override void EnsureChunk(int index)
    {
        if (Volatile.Read(ref _committed[index]) != 0) return;
        Commit(index);
    }

    public override void Prepare(int index)
    {
        if (Volatile.Read(ref _committed[index]) != 0) return;
        // Проход по страницам — под тем же замком, что и возврат блока системе:
        // иначе параллельный ReleaseChunk снял бы память прямо под рукой, и запись
        // в неё уронила бы процесс.
        lock (_committed)
        {
            if (Commit(index))
                Touch(_base + index * (nint)ChunkBytes, ChunkBytes);
        }
    }

    private bool Commit(int index)
    {
        lock (_committed)
        {
            if (_committed[index] != 0) return false;
            if (_base == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeArenaStorage));
            IntPtr at = _base + index * (nint)ChunkBytes;
            if (VirtualAlloc(at, ChunkBytes, MemCommit, PageReadWrite) == IntPtr.Zero)
                throw new OutOfMemoryException(
                    $"Система не выдала {ChunkBytes >> 20} МБ под буфер повтора " +
                    $"(ошибка {Marshal.GetLastWin32Error()})");
            Volatile.Write(ref _committed[index], 1);
            Interlocked.Increment(ref _resident);
            return true;
        }
    }

    public override void ReleaseChunk(int index)
    {
        lock (_committed)
        {
            if (_committed[index] == 0) return;
            VirtualFree(_base + index * (nint)ChunkBytes, ChunkBytes, MemDecommit);
            _committed[index] = 0;
            Interlocked.Decrement(ref _resident);
        }
    }

    protected override void Free()
    {
        lock (_committed)
        {
            var at = Interlocked.Exchange(ref _base, IntPtr.Zero);
            if (at != IntPtr.Zero) VirtualFree(at, 0, MemRelease);
            Array.Clear(_committed);
            Volatile.Write(ref _resident, 0);
        }
    }
}

/// <summary>
/// Арена в файле на диске, отображённом в память целиком.
///
/// Файл удаляется системой при закрытии (в том числе при падении процесса),
/// поэтому на диске после себя ничего не оставляет. Место на диске занимается
/// сразу на весь размер: у разреженного файла место выделялось бы, когда система
/// сбрасывает страницы, и если диск к тому времени заполнил кто-то другой, данные
/// молча терялись бы, а чтение такой страницы при сохранении роняло бы процесс
/// (EXCEPTION_IN_PAGE_ERROR). Нулями файл при этом не заполняется: NTFS выделяет
/// кластеры, не записывая их.
///
/// Дописанный блок сбрасывается на диск в фоне и выталкивается из рабочего набора
/// процесса: страницы остаются в файловом кэше системы, который она отдаёт другим
/// по первому требованию. В памяти приложения держится лишь блок, в который идёт
/// запись прямо сейчас.
/// </summary>
public sealed class FileArenaStorage : ArenaStorage
{
    private readonly SafeFileHandle _file;
    private readonly IntPtr _mapping;
    private IntPtr _view;
    private readonly string _path;

    public FileArenaStorage(string directory, long capacity) : base(capacity)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"replay-{Environment.ProcessId}-{Guid.NewGuid():N}.buffer");

        _file = CreateFileW(_path, GenericRead | GenericWrite, 0, IntPtr.Zero, CreateAlways,
                            FileAttributeNotContentIndexed | FileFlagDeleteOnClose, IntPtr.Zero);
        if (_file.IsInvalid)
            throw new IOException($"Не удалось создать файл буфера {_path} (ошибка {Marshal.GetLastWin32Error()})");

        try
        {
            _mapping = CreateFileMappingW(_file, IntPtr.Zero, PageReadWrite,
                                          (uint)(capacity >> 32), (uint)capacity, null);
            if (_mapping == IntPtr.Zero)
                throw new IOException($"Не удалось отобразить файл буфера (ошибка {Marshal.GetLastWin32Error()})");

            _view = MapViewOfFile(_mapping, FileMapWrite | FileMapRead, 0, 0, (nuint)capacity);
            if (_view == IntPtr.Zero)
                throw new IOException($"Не удалось отобразить файл буфера (ошибка {Marshal.GetLastWin32Error()})");
        }
        catch
        {
            if (_mapping != IntPtr.Zero) CloseHandle(_mapping);
            _file.Dispose();
            throw;
        }
        Log.Info("Buffer", $"Буфер на диске: {_path}");
    }

    public override IntPtr Base => _view;

    public override bool OnDisk => true;

    // Файл занимает память только блоком, куда идёт запись.
    public override int ResidentChunks => 1;

    public override void EnsureChunk(int index) { }

    public override void Prepare(int index) { }

    public override void ReleaseChunk(int index) { }

    public override void Seal(int index)
    {
        IntPtr at = _view + index * (nint)ChunkBytes;
        // Фоновая задача держит хранилище живым: иначе снос буфера отобразил бы
        // файл обратно раньше, чем она дойдёт до этих адресов.
        AddRef();
        Task.Run(() =>
        {
            try
            {
                // Сначала на диск, потом из рабочего набора: VirtualUnlock на
                // незаблокированных страницах выталкивает их из рабочего набора, и
                // чистые (уже записанные) страницы уходят в резервный список кэша.
                FlushViewOfFile(at, ChunkBytes);
                VirtualUnlock(at, ChunkBytes);
            }
            catch (Exception ex) { Log.Info("Buffer", $"Блок буфера на диске не сброшен: {ex.Message}"); }
            finally { Release(); }
        });
    }

    protected override void Free()
    {
        var view = Interlocked.Exchange(ref _view, IntPtr.Zero);
        if (view != IntPtr.Zero) UnmapViewOfFile(view);
        CloseHandle(_mapping);
        _file.Dispose(); // FILE_FLAG_DELETE_ON_CLOSE убирает файл
    }

    /// <summary>
    /// Убрать файлы буфера, оставшиеся от прошлых запусков. Обычно их удаляет сама
    /// система (DELETE_ON_CLOSE), но при отказе питания файл может остаться.
    /// </summary>
    public static void CleanupStale(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "replay-*.buffer"))
            {
                try { File.Delete(file); } catch { /* занят живой копией — не наш */ }
            }
        }
        catch { }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNotContentIndexed = 0x00002000;
    private const uint FileFlagDeleteOnClose = 0x04000000;
    private const uint FileMapWrite = 0x0002;
    private const uint FileMapRead = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(SafeFileHandle file, IntPtr security, uint protect,
        uint sizeHigh, uint sizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushViewOfFile(IntPtr address, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

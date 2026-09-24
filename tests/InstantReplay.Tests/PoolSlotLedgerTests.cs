using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Учёт слотов пула текстур энкодера. Главное правило: слот, который держит
/// энкодер, очередь или пейсер, не выдаётся под новую копию. Раньше кольцо шло
/// по кругу вслепую и переписывало текстуру, которую NVENC ещё читал, — конвейер
/// вставал целиком.
/// </summary>
public class PoolSlotLedgerTests
{
    [Fact]
    public void Busy_slot_is_never_handed_out_again()
    {
        var ledger = new PoolSlotLedger(4);
        int held = ledger.TryTake();          // этот кадр держит энкодер
        var taken = new HashSet<int>();
        for (int round = 0; round < 50; round++)
        {
            int slot = ledger.TryTake();
            Assert.NotEqual(held, slot);
            taken.Add(slot);
            ledger.Release(slot);
        }
        Assert.Equal(3, taken.Count);         // остальные три ходят по кругу
    }

    [Fact]
    public void Exhausted_pool_refuses_instead_of_overwriting()
    {
        var ledger = new PoolSlotLedger(3);
        int a = ledger.TryTake(), b = ledger.TryTake(), c = ledger.TryTake();
        Assert.Equal(-1, ledger.TryTake());
        Assert.Equal(3, ledger.Busy);

        ledger.Release(b);
        Assert.Equal(b, ledger.TryTake());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Slot_stays_busy_until_every_reference_is_released()
    {
        var ledger = new PoolSlotLedger(2);
        int slot = ledger.TryTake();          // очередь
        ledger.AddRef(slot);                  // и пейсер как «последний кадр»
        ledger.Release(slot);                 // энкодер выдал кадр
        int other = ledger.TryTake();
        Assert.NotEqual(slot, other);
        Assert.Equal(-1, ledger.TryTake());

        ledger.Release(slot);                 // пейсер перешёл на новый кадр
        Assert.Equal(slot, ledger.TryTake());
    }

    [Fact]
    public void Freed_slot_is_reused_last()
    {
        var ledger = new PoolSlotLedger(4);
        int first = ledger.TryTake();
        ledger.Release(first);
        // Только что отпущенный слот не берётся сразу: видеокарта ещё может
        // дочитывать его, пока свободны другие.
        Assert.NotEqual(first, ledger.TryTake());
    }

    [Fact]
    public void AddRef_and_extra_release_on_free_slot_do_nothing()
    {
        var ledger = new PoolSlotLedger(1);
        ledger.AddRef(0);
        ledger.Release(0);
        Assert.Equal(0, ledger.Busy);
        Assert.Equal(0, ledger.TryTake());
        Assert.Equal(-1, ledger.TryTake());
    }
}

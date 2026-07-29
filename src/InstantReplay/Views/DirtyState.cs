using Microsoft.UI.Xaml.Controls;

namespace InstantReplay.Views;

/// <summary>
/// Состояние «есть несохранённые изменения» для страниц настроек.
///
/// Общая модель для всех вкладок с кнопкой «Применить»: до нажатия ничего не
/// сохраняется и конвейер не перезапускается, а строка состояния прямо говорит, что
/// изменения не применены. Раньше часть настроек (пресеты качества, выбор папки)
/// применялась молча и мгновенно, а часть ждала кнопку — по виду страницы понять,
/// что уже сработало, было нельзя.
/// </summary>
internal sealed class DirtyState(TextBlock status, Button apply, Button revert)
{
    private int _savedToken;

    public bool IsDirty { get; private set; }

    /// <summary>Не отмечать изменения (страница сама раскладывает значения по контролам).</summary>
    public bool Suspended { get; set; }

    /// <summary>Настройки нельзя применить сейчас (идёт запись) — кнопка гаснет.</summary>
    private bool _locked;
    public bool Locked
    {
        get => _locked;
        set { _locked = value; UpdateButtons(); }
    }

    public void Mark()
    {
        if (Suspended || IsDirty) return;
        IsDirty = true;
        _savedToken++; // отменяем возможное «Применено ✓»
        status.Text = "Есть несохранённые изменения";
        UpdateButtons();
    }

    public void Clear()
    {
        IsDirty = false;
        status.Text = "";
        UpdateButtons();
    }

    /// <summary>После успешного «Применить»: подтверждение, которое само гаснет.</summary>
    public void MarkSaved()
    {
        Clear();
        status.Text = "Применено ✓";
        _ = HideSavedLaterAsync(++_savedToken);
    }

    private async Task HideSavedLaterAsync(int token)
    {
        await Task.Delay(1600);
        if (token == _savedToken && !IsDirty) status.Text = "";
    }

    private void UpdateButtons()
    {
        apply.IsEnabled = IsDirty && !Locked;
        revert.IsEnabled = IsDirty;
    }
}

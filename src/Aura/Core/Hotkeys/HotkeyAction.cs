namespace Aura.Core.Hotkeys;

/// <summary>Что делает горячая клавиша. Отдельный файл — им пользуются и хук, и проверки, и тесты.</summary>
public enum HotkeyAction
{
    SaveReplay,
    SaveLast30,
    StartRecording,
    StopRecording,
    ToggleInstantReplay,
    Screenshot,
    ScreenshotRegion,
    OpenFolder
}

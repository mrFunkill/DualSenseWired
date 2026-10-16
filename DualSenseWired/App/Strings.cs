// Strings of the tray and message boxes in English and Russian: English by default, Russian when
// Windows' UI language is Russian (the same rule as the Steam Deck plugin). The main window's
// strings are in App/ui/index.html.

using System.Globalization;

namespace DualSenseWired.App;

public sealed class Strings
{
    public static readonly Strings Current =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? Russian() : English();

    public bool IsRussian { get; private init; }
    public string AppName { get; private init; } = "DualSense Wired";
    public string Open { get; private init; } = "Open";
    public string Exit { get; private init; } = "Exit";
    public string Emulation { get; private init; } = "Wired connection emulation";
    public string NoController { get; private init; } = "No DualSense over Bluetooth";
    public string Connected { get; private init; } = "Emulated as wired";
    public string EmulationOff { get; private init; } = "Emulation is off";
    public string Battery { get; private init; } = "Battery";
    public string Charging { get; private init; } = "charging";
    public string Full { get; private init; } = "charged";
    public string LowBatteryTitle { get; private init; } = "DualSense: low battery";
    public string MissingUsbIp { get; private init; } = "usbip-win2 is not installed. It is needed for the virtual wired controller.";

    static Strings English() => new();

    static Strings Russian() => new()
    {
        IsRussian = true,
        Open = "Открыть",
        Exit = "Выход",
        Emulation = "Эмуляция проводного подключения",
        NoController = "DualSense по Bluetooth не найден",
        Connected = "Эмулируется как проводной",
        EmulationOff = "Эмуляция выключена",
        Battery = "Батарея",
        Charging = "заряжается",
        Full = "заряжен",
        LowBatteryTitle = "DualSense: низкий заряд",
        MissingUsbIp = "usbip-win2 не установлен. Он нужен для виртуального проводного контроллера.",
    };
}

using System;
using System.Collections.Generic;

namespace FluentStart;

/// <summary>
/// Windows Settings pages reachable from search. UNVERIFIED: every ms-settings: URI and title below is recalled from memory
/// of the documented URI list; none was checked on a real system. A wrong/unsupported URI makes Windows open Settings
/// (or show an error) instead of the page - fix the entry, do not assume it exists. Keywords widen matching only.
/// </summary>
public static class SettingsCatalog
{
    public sealed record Entry(string Title, string Category, string Uri, string Keywords = "");

    public static readonly IReadOnlyList<Entry> All = new List<Entry>
    {
        new("Display", "System", "ms-settings:display", "screen resolution brightness scale monitor refresh rate"),
        new("Sound", "System", "ms-settings:sound", "volume audio speakers microphone output input"),
        new("Notifications", "System", "ms-settings:notifications", "alerts focus do not disturb"),
        new("Focus", "System", "ms-settings:quiethours", "focus assist quiet hours"),
        new("Power & battery", "System", "ms-settings:powersleep", "sleep screen timeout energy saver"),
        new("Storage", "System", "ms-settings:storagesense", "disk space cleanup storage sense"),
        new("Multitasking", "System", "ms-settings:multitasking", "snap windows virtual desktops"),
        new("Clipboard", "System", "ms-settings:clipboard", "clipboard history paste"),
        new("Remote Desktop", "System", "ms-settings:remotedesktop", "rdp remote access"),
        new("Optional features", "System", "ms-settings:optionalfeatures", "windows features add feature"),
        new("About", "System", "ms-settings:about", "device name specs version pc info rename pc"),
        new("Troubleshoot", "System", "ms-settings:troubleshoot", "fix problems diagnostics"),
        new("Recovery", "System", "ms-settings:recovery", "reset this pc go back advanced startup"),
        new("Activation", "System", "ms-settings:activation", "product key license"),
        new("Night light", "System", "ms-settings:nightlight", "blue light warm color"),
        new("Bluetooth & devices", "Bluetooth & devices", "ms-settings:bluetooth", "pair device wireless"),
        new("Printers & scanners", "Bluetooth & devices", "ms-settings:printers", "printer scanner add printer"),
        new("Mouse", "Bluetooth & devices", "ms-settings:mousetouchpad", "pointer speed cursor buttons"),
        new("Touchpad", "Bluetooth & devices", "ms-settings:devices-touchpad", "gestures scrolling tap"),
        new("Typing", "Bluetooth & devices", "ms-settings:typing", "autocorrect spelling keyboard suggestions"),
        new("AutoPlay", "Bluetooth & devices", "ms-settings:autoplay", "removable drives media"),
        new("USB", "Bluetooth & devices", "ms-settings:usb", "usb connected notifications"),
        new("Network & internet", "Network & internet", "ms-settings:network", "connection status"),
        new("Wi-Fi", "Network & internet", "ms-settings:network-wifi", "wireless networks"),
        new("Ethernet", "Network & internet", "ms-settings:network-ethernet", "wired network cable"),
        new("VPN", "Network & internet", "ms-settings:network-vpn", "virtual private network"),
        new("Airplane mode", "Network & internet", "ms-settings:network-airplanemode", "flight radio"),
        new("Mobile hotspot", "Network & internet", "ms-settings:network-mobilehotspot", "share internet"),
        new("Proxy", "Network & internet", "ms-settings:network-proxy", "proxy server"),
        new("Personalization", "Personalization", "ms-settings:personalization", "customize appearance"),
        new("Background", "Personalization", "ms-settings:personalization-background", "wallpaper desktop picture"),
        new("Colors", "Personalization", "ms-settings:colors", "accent color dark mode light mode transparency"),
        new("Themes", "Personalization", "ms-settings:themes", "theme contrast"),
        new("Lock screen", "Personalization", "ms-settings:lockscreen", "lock screen picture"),
        new("Start", "Personalization", "ms-settings:personalization-start", "start menu layout recommended folders"),
        new("Taskbar", "Personalization", "ms-settings:taskbar", "taskbar behaviors alignment icons"),
        new("Fonts", "Personalization", "ms-settings:fonts", "typefaces install fonts"),
        new("Installed apps", "Apps", "ms-settings:appsfeatures", "uninstall programs apps and features"),
        new("Default apps", "Apps", "ms-settings:defaultapps", "file associations default browser"),
        new("Startup apps", "Apps", "ms-settings:startupapps", "run at startup login items"),
        new("Offline maps", "Apps", "ms-settings:maps", "maps download"),
        new("Your info", "Accounts", "ms-settings:yourinfo", "account profile picture name"),
        new("Sign-in options", "Accounts", "ms-settings:signinoptions", "password pin windows hello fingerprint"),
        new("Email & accounts", "Accounts", "ms-settings:emailandaccounts", "add account"),
        new("Other users", "Accounts", "ms-settings:otherusers", "family add user"),
        new("Windows backup", "Accounts", "ms-settings:backup", "backup onedrive sync"),
        new("Date & time", "Time & language", "ms-settings:dateandtime", "clock time zone"),
        new("Language & region", "Time & language", "ms-settings:regionlanguage", "display language keyboard region format"),
        new("Speech", "Time & language", "ms-settings:speech", "voice speech language"),
        new("Game Bar", "Gaming", "ms-settings:gaming-gamebar", "xbox overlay capture"),
        new("Game Mode", "Gaming", "ms-settings:gaming-gamemode", "performance games"),
        new("Accessibility", "Accessibility", "ms-settings:easeofaccess", "ease of access"),
        new("Text size", "Accessibility", "ms-settings:easeofaccess-display", "larger text magnify"),
        new("Narrator", "Accessibility", "ms-settings:easeofaccess-narrator", "screen reader"),
        new("Magnifier", "Accessibility", "ms-settings:easeofaccess-magnifier", "zoom"),
        new("Privacy & security", "Privacy & security", "ms-settings:privacy", "permissions"),
        new("Windows Security", "Privacy & security", "ms-settings:windowsdefender", "antivirus defender firewall virus"),
        new("Camera privacy", "Privacy & security", "ms-settings:privacy-webcam", "camera access"),
        new("Microphone privacy", "Privacy & security", "ms-settings:privacy-microphone", "microphone access"),
        new("Location privacy", "Privacy & security", "ms-settings:privacy-location", "location services"),
        new("Windows Update", "Windows Update", "ms-settings:windowsupdate", "updates check for updates restart"),
        new("Update history", "Windows Update", "ms-settings:windowsupdate-history", "installed updates"),
    };

    /// <summary>Best score (0-100) of <paramref name="query"/> against an entry's title, category and keywords.</summary>
    public static double Score(string query, Entry e)
    {
        double title = SearchScorer.Score(query, e.Title);
        if (title >= 50) return title;
        double kw = 0;
        foreach (var word in e.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            kw = Math.Max(kw, SearchScorer.Score(query, word));
        double cat = SearchScorer.Score(query, e.Category);
        return Math.Max(title, Math.Max(kw * 0.6, cat * 0.4));
    }
}

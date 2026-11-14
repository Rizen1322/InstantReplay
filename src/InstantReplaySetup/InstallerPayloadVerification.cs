using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace InstantReplaySetup;

/// <summary>Проверка именно payload, встроенного в готовый установщик; ничего не устанавливает.</summary>
public static class InstallerPayloadVerification
{
    public sealed record Report(string Version, Dictionary<string, string> Files, long TotalBytes);

    public static Report Verify(Stream payload, string expectedVersion)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Contains('\0') ||
                name.Split('/').Any(p => p is "." or ".."))
                throw new InvalidDataException("Недопустимый путь в поставке: " + name);
            if (name.EndsWith('/')) continue;
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("Повторный файл: " + name);
            total = checked(total + entry.Length);
            if (total > 300L * 1024 * 1024) throw new InvalidDataException("Поставка превышает 300 МБ.");
            if (name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("libvlc/win-x86/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("libvlc/win-arm64/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Лишний файл в поставке: " + name);
        }
        foreach (string required in new[] { "Aura.exe", "Aura.dll", "Aura.runtimeconfig.json", "Aura.deps.json",
            "Aura.Media64.dll", "Aura.EncoderHost.exe", "AuraUninstall.exe", "Aura.Identity.msix",
            "Aura.GameCaptureHook64.dll", "Aura.GameCaptureHook64.sha256", "libvlc/win-x64/libvlc.dll",
            "libvlc/win-x64/libvlccore.dll", "libvlc/win-x64/plugins/codec/libavcodec_plugin.dll",
            "libvlc/win-x64/plugins/demux/libmp4_plugin.dll", "libvlc/win-x64/plugins/mux/libmux_mp4_plugin.dll" })
            if (!entries.TryGetValue(required, out var found) || found.Length == 0)
                throw new InvalidDataException("Нет обязательного файла: " + required);

        using var dll = new MemoryStream();
        using (var input = entries["Aura.dll"].Open()) input.CopyTo(dll);
        dll.Position = 0;
        using var pe = new PEReader(dll, PEStreamOptions.LeaveOpen);
        string version = pe.GetMetadataReader().GetAssemblyDefinition().Version.ToString();
        if (version != expectedVersion) throw new InvalidDataException($"Версия payload {version}, ожидалась {expectedVersion}.");

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entries)
        {
            using var input = pair.Value.Open();
            hashes.Add(pair.Key, Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
        }
        using var manifestStream = entries["Aura.GameCaptureHook64.sha256"].Open();
        using var reader = new StreamReader(manifestStream);
        string expectedHook = reader.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!expectedHook.Equals(hashes["Aura.GameCaptureHook64.dll"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Подпись capture hook не соответствует DLL.");
        return new Report(version, hashes, total);
    }
}

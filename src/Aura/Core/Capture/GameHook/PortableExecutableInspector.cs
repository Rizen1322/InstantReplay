using System.Reflection;
using System.Security.Cryptography;

namespace Aura.Core.Capture.GameHook;

internal enum PortableExecutableKind
{
    Invalid,
    WrongArchitecture,
    NotDynamicLibrary,
    X64Dll
}

internal enum GameHookBinaryValidation
{
    Valid,
    PathNotAbsolute,
    FileMissing,
    InvalidExpectedHash,
    NotX64Dll,
    HashMismatch,
    ReadFailed
}

internal static class PortableExecutableInspector
{
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileDll = 0x2000;

    public static PortableExecutableKind Inspect(ReadOnlySpan<byte> image)
    {
        if (image.Length < 64 || image[0] != (byte)'M' || image[1] != (byte)'Z')
            return PortableExecutableKind.Invalid;

        int peOffset = BitConverter.ToInt32(image[0x3c..0x40]);
        if (peOffset < 0 || peOffset > image.Length - 24)
            return PortableExecutableKind.Invalid;
        if (image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' ||
            image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
        {
            return PortableExecutableKind.Invalid;
        }

        ushort machine = BitConverter.ToUInt16(image.Slice(peOffset + 4, 2));
        if (machine != ImageFileMachineAmd64)
            return PortableExecutableKind.WrongArchitecture;

        ushort characteristics = BitConverter.ToUInt16(image.Slice(peOffset + 22, 2));
        return (characteristics & ImageFileDll) != 0
            ? PortableExecutableKind.X64Dll
            : PortableExecutableKind.NotDynamicLibrary;
    }
}

internal static class GameHookBinaryValidator
{
    public const string EmbeddedHashResourceName = "Aura.GameCaptureHook64.sha256";

    public static GameHookBinaryValidation Validate(string path, string expectedSha256)
    {
        if (!Path.IsPathFullyQualified(path))
            return GameHookBinaryValidation.PathNotAbsolute;
        if (!File.Exists(path))
            return GameHookBinaryValidation.FileMissing;

        byte[] expected;
        try
        {
            if (expectedSha256.Length != 64)
                return GameHookBinaryValidation.InvalidExpectedHash;
            expected = Convert.FromHexString(expectedSha256);
        }
        catch (FormatException)
        {
            return GameHookBinaryValidation.InvalidExpectedHash;
        }

        try
        {
            byte[] image = File.ReadAllBytes(path);
            if (PortableExecutableInspector.Inspect(image) != PortableExecutableKind.X64Dll)
                return GameHookBinaryValidation.NotX64Dll;

            byte[] actual = SHA256.HashData(image);
            return CryptographicOperations.FixedTimeEquals(actual, expected)
                ? GameHookBinaryValidation.Valid
                : GameHookBinaryValidation.HashMismatch;
        }
        catch (IOException)
        {
            return GameHookBinaryValidation.ReadFailed;
        }
        catch (UnauthorizedAccessException)
        {
            return GameHookBinaryValidation.ReadFailed;
        }
    }

    public static string LoadEmbeddedExpectedSha256(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using Stream stream = assembly.GetManifestResourceStream(EmbeddedHashResourceName) ??
            throw new InvalidOperationException(
                $"В Aura не встроен ресурс {EmbeddedHashResourceName}");
        using var reader = new StreamReader(stream);
        string firstToken = (reader.ReadLine() ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        if (firstToken.Length != 64)
            throw new InvalidDataException("Встроенный SHA-256 hook DLL повреждён");
        _ = Convert.FromHexString(firstToken);
        return firstToken.ToLowerInvariant();
    }
}

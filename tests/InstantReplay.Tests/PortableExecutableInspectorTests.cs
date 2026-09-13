using System.Security.Cryptography;
using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class PortableExecutableInspectorTests
{
    [Fact]
    public void Accepts_only_x64_dynamic_libraries()
    {
        Assert.Equal(
            PortableExecutableKind.X64Dll,
            PortableExecutableInspector.Inspect(MinimalPe(machine: 0x8664, dll: true)));
        Assert.Equal(
            PortableExecutableKind.WrongArchitecture,
            PortableExecutableInspector.Inspect(MinimalPe(machine: 0x014c, dll: true)));
        Assert.Equal(
            PortableExecutableKind.NotDynamicLibrary,
            PortableExecutableInspector.Inspect(MinimalPe(machine: 0x8664, dll: false)));
        Assert.Equal(
            PortableExecutableKind.Invalid,
            PortableExecutableInspector.Inspect([0x4D, 0x5A]));
        Assert.Equal(
            PortableExecutableKind.Invalid,
            PortableExecutableInspector.Inspect(new byte[512]));
    }

    [Fact]
    public void Binary_validator_requires_exact_embedded_sha256_and_absolute_path()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AuraHookPeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Aura.GameCaptureHook64.dll");
        try
        {
            byte[] pe = MinimalPe(machine: 0x8664, dll: true);
            File.WriteAllBytes(path, pe);
            string hash = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant();

            Assert.Equal(GameHookBinaryValidation.Valid, GameHookBinaryValidator.Validate(path, hash));
            Assert.Equal(
                GameHookBinaryValidation.HashMismatch,
                GameHookBinaryValidator.Validate(path, new string('0', 64)));
            Assert.Equal(
                GameHookBinaryValidation.PathNotAbsolute,
                GameHookBinaryValidator.Validate("Aura.GameCaptureHook64.dll", hash));

            File.WriteAllBytes(path, MinimalPe(machine: 0x014c, dll: true));
            string x86Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Equal(
                GameHookBinaryValidation.NotX64Dll,
                GameHookBinaryValidator.Validate(path, x86Hash));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] MinimalPe(ushort machine, bool dll)
    {
        var bytes = new byte[512];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        BitConverter.GetBytes(128).CopyTo(bytes, 0x3c);
        bytes[128] = 0x50;
        bytes[129] = 0x45;
        BitConverter.GetBytes(machine).CopyTo(bytes, 132);
        BitConverter.GetBytes((ushort)(dll ? 0x2000 : 0)).CopyTo(bytes, 150);
        return bytes;
    }
}

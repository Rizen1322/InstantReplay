const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.resolveTargetQuery(.{
        .cpu_arch = .x86_64,
        .os_tag = .windows,
        .abi = .gnu,
    });
    const optimize = b.standardOptimizeOption(.{ .preferred_optimize_mode = .ReleaseFast });

    const module = b.createModule(.{
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });
    module.addIncludePath(b.path("../../../third_party/nvenc"));
    module.addIncludePath(b.path("../../../third_party/rnnoise/include"));
    module.addIncludePath(b.path("../../../third_party/rnnoise/src"));
    module.addCSourceFiles(.{
        .files = &.{"nvenc_shim.c"},
        .flags = &.{ "-std=c11", "-DNDEBUG", "-Wall", "-Wno-unused-function" },
    });
    // RNNoise (Xiph, BSD-3): шумоподавление микрофона нейросетью, блок 10 мс при 48 кГц
    module.addCSourceFiles(.{
        .files = &.{
            "../../../third_party/rnnoise/src/denoise.c",
            "../../../third_party/rnnoise/src/rnn.c",
            "../../../third_party/rnnoise/src/rnn_data.c",
            "../../../third_party/rnnoise/src/rnn_reader.c",
            "../../../third_party/rnnoise/src/pitch.c",
            "../../../third_party/rnnoise/src/kiss_fft.c",
            "../../../third_party/rnnoise/src/celt_lpc.c",
        },
        .flags = &.{ "-std=c99", "-DNDEBUG", "-DRNNOISE_BUILD", "-DDLL_EXPORT", "-DWIN32", "-D_USE_MATH_DEFINES", "-DM_PI=3.14159265358979323846", "-w" },
    });
    module.linkSystemLibrary("kernel32", .{});

    const lib = b.addLibrary(.{
        .name = "Aura.Media64",
        .linkage = .dynamic,
        .root_module = module,
    });
    b.installArtifact(lib);
}

const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.resolveTargetQuery(.{
        .cpu_arch = .x86_64,
        .os_tag = .windows,
        .abi = .gnu,
    });
    const optimize = b.standardOptimizeOption(.{ .preferred_optimize_mode = .ReleaseFast });

    const hook_module = b.createModule(.{
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });
    hook_module.addIncludePath(b.path("."));
    hook_module.addIncludePath(b.path("../../../third_party/minhook/include"));
    hook_module.addIncludePath(b.path("../../../third_party/minhook/src"));
    hook_module.addIncludePath(b.path("../../../third_party/minhook/src/hde"));
    hook_module.addCSourceFiles(.{
        .files = &.{
            "hook_exports.c",
            "dllmain.c",
            "ipc.c",
            "present_hooks.c",
            "hook_lifetime.c",
            "../../../third_party/minhook/src/buffer.c",
            "../../../third_party/minhook/src/hook.c",
            "../../../third_party/minhook/src/trampoline.c",
            "../../../third_party/minhook/src/hde/hde64.c",
        },
        .flags = &.{
            "-std=c11",
            "-DNDEBUG",
            "-DWIN32_LEAN_AND_MEAN",
            "-DNOMINMAX",
        },
    });
    hook_module.linkSystemLibrary("kernel32", .{});
    hook_module.linkSystemLibrary("user32", .{});
    hook_module.linkSystemLibrary("gdi32", .{});
    hook_module.linkSystemLibrary("opengl32", .{});

    const hook = b.addLibrary(.{
        .name = "Aura.GameCaptureHook64",
        .linkage = .dynamic,
        .root_module = hook_module,
    });
    b.installArtifact(hook);

    const protocol_module = b.createModule(.{
        .target = target,
        .optimize = .ReleaseFast,
        .link_libc = true,
    });
    protocol_module.addIncludePath(b.path("."));
    protocol_module.addCSourceFiles(.{
        .files = &.{"protocol_layout_test.c"},
        .flags = &.{ "-std=c11", "-Wall", "-Wextra", "-Werror" },
    });
    const protocol_test = b.addExecutable(.{
        .name = "protocol_layout_test",
        .root_module = protocol_module,
    });
    const run_protocol_test = b.addRunArtifact(protocol_test);
    const test_step = b.step("test", "Compile and run cross-process ABI assertions");
    test_step.dependOn(&run_protocol_test.step);
}

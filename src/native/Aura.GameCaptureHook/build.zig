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
            "gl_capture.c",
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
    const test_step = b.step("test", "Compile and run IPC ABI and retirement assertions");
    test_step.dependOn(&run_protocol_test.step);

    const cleanup_module = b.createModule(.{
        .target = target,
        .optimize = .ReleaseFast,
        .link_libc = true,
    });
    cleanup_module.addIncludePath(b.path("."));
    cleanup_module.addIncludePath(b.path("../../../third_party/minhook/include"));
    cleanup_module.addCSourceFiles(.{
        .files = &.{
            "present_cleanup_test.c",
            "../../../third_party/minhook/src/buffer.c",
            "../../../third_party/minhook/src/hook.c",
            "../../../third_party/minhook/src/trampoline.c",
            "../../../third_party/minhook/src/hde/hde64.c",
        },
        .flags = &.{ "-std=c11", "-DNDEBUG", "-DWIN32_LEAN_AND_MEAN", "-DNOMINMAX" },
    });
    cleanup_module.linkSystemLibrary("kernel32", .{});
    cleanup_module.linkSystemLibrary("user32", .{});
    cleanup_module.linkSystemLibrary("gdi32", .{});
    cleanup_module.linkSystemLibrary("opengl32", .{});
    const cleanup_test = b.addExecutable(.{
        .name = "present_cleanup_test",
        .root_module = cleanup_module,
    });
    const run_cleanup_test = b.addRunArtifact(cleanup_test);
    const gl_test_step = b.step("test-gl", "Run hardware-backed OpenGL cleanup assertions");
    gl_test_step.dependOn(&run_cleanup_test.step);

    const retirement_module = b.createModule(.{
        .target = target,
        .optimize = .ReleaseFast,
        .link_libc = true,
    });
    retirement_module.addIncludePath(b.path("."));
    retirement_module.addCSourceFiles(.{
        .files = &.{"hook_retirement_test.c"},
        .flags = &.{ "-std=c11", "-Wall", "-Wextra", "-Werror" },
    });
    retirement_module.linkSystemLibrary("kernel32", .{});
    const retirement_test = b.addExecutable(.{
        .name = "hook_retirement_test",
        .root_module = retirement_module,
    });
    const run_retirement_test = b.addRunArtifact(retirement_test);
    test_step.dependOn(&run_retirement_test.step);
}

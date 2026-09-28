using System.Runtime.InteropServices;
using Silk.NET.Shaderc;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>Compiles GLSL shaders to SPIR-V at startup using shaderc.</summary>
internal static class ShaderCompiler
{
    public static unsafe byte[] Compile(string source, ShaderKind kind, string name)
    {
        var shaderc = Shaderc.GetApi();
        var compiler = shaderc.CompilerInitialize();
        var options = shaderc.CompileOptionsInitialize();
        try
        {
            shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            var result = shaderc.CompileIntoSpv(compiler, source, (nuint)System.Text.Encoding.UTF8.GetByteCount(source), kind, name, "main", options);
            try
            {
                if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    string message = Marshal.PtrToStringUTF8((nint)shaderc.ResultGetErrorMessage(result)) ?? "unknown error";
                    throw new InvalidOperationException($"Failed to compile shader {name}: {message}");
                }

                var length = (int)shaderc.ResultGetLength(result);
                var bytes = new byte[length];
                new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), length).CopyTo(bytes);
                return bytes;
            }
            finally
            {
                shaderc.ResultRelease(result);
            }
        }
        finally
        {
            shaderc.CompileOptionsRelease(options);
            shaderc.CompilerRelease(compiler);
        }
    }
}

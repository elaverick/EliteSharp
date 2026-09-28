using System.Numerics;
using System.Runtime.InteropServices;
using EliteSharp.Rendering.Scene;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// Draws the 3D world: the ships as wireframes (with hidden line removal on
/// the GPU), the planet as a sphere with circles on its surface, the sun as a
/// disc, and particles and laser beams, all with real 3D transforms, a
/// perspective projection and a depth buffer.
/// </summary>
public sealed unsafe class WorldRenderer : IDisposable
{
    /// <summary>The per-frame uniforms (see WorldShaders.Common).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Viewport;
        public Vector4 ViewportSize;
    }

    /// <summary>The per-draw push constants (see WorldShaders.Common).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 Model;
        public Vector4 CameraModel;
        public Vector4 Parameters;
        public uint Colour0, Colour1, Colour2, Colour3;
    }

    /// <summary>A particle as sent to the GPU (one instance per particle).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleInstance
    {
        public Vector3 Position;
        public Vector2 Size;
        public uint Colour0, Colour1, Colour2, Colour3;
    }

    /// <summary>The per-frame resources for each frame in flight.</summary>
    private sealed class FrameResources(GpuDevice gpu)
    {
        public GpuBuffer Uniforms = null!;
        public DescriptorSet DescriptorSet;
        public DynamicBuffer Lines = new(gpu, BufferUsageFlags.VertexBufferBit);
        public DynamicBuffer Particles = new(gpu, BufferUsageFlags.VertexBufferBit);
    }

    /// <summary>
    /// The planet's sphere is drawn this much smaller than the planet (into the
    /// depth buffer only), so the circles on the planet's surface pass the
    /// depth test on the near side of the planet and fail it on the far side.
    /// </summary>
    private const float PlanetOccluderScale = 0.995f;

    /// <summary>The crater's distance from the planet's centre, and its radius, as fractions of the planet's radius (PL26).</summary>
    private const float CraterDistance = 222f / 256f;
    private const float CraterRadius = 0.5f;

    /// <summary>The original's pattern of red and yellow pixels for the sun (the first row of <c>Orange</c> in SUN).</summary>
    private const int SunColourByte = 0b10100101;

    private static readonly uint Black = 0xFF000000;

    private readonly GpuDevice _gpu;
    private readonly MeshLibrary _meshes;
    private readonly DescriptorSetLayout _descriptorSetLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _wirePipeline;
    private readonly Pipeline _solidPipeline;
    private readonly Pipeline _sunPipeline;
    private readonly Pipeline _particlePipeline;
    private readonly FrameResources[] _frames;

    public WorldRenderer(GpuDevice gpu, RenderPass renderPass, int framesInFlight)
    {
        _gpu = gpu;
        var vk = gpu.Vk;
        _meshes = new MeshLibrary(gpu);

        // The per-frame uniforms are in a uniform buffer, bound as descriptor set 0
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        GpuDevice.Check(vk.CreateDescriptorSetLayout(gpu.Device, in setLayoutInfo, null, out _descriptorSetLayout), "vkCreateDescriptorSetLayout");

        var poolSize = new DescriptorPoolSize(DescriptorType.UniformBuffer, (uint)framesInFlight);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)framesInFlight,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        GpuDevice.Check(vk.CreateDescriptorPool(gpu.Device, in poolInfo, null, out _descriptorPool), "vkCreateDescriptorPool");

        var pushRange = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(DrawConstants));
        var descriptorSetLayout = _descriptorSetLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        GpuDevice.Check(vk.CreatePipelineLayout(gpu.Device, in layoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        // The pipelines
        var wireVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.WireVertex, ShaderKind.VertexShader, "wire.vert"));
        var patternFragment = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.PatternFragment, ShaderKind.FragmentShader, "pattern.frag"));
        var sunVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.SunVertex, ShaderKind.VertexShader, "sun.vert"));
        var sunFragment = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.SunFragment, ShaderKind.FragmentShader, "sun.frag"));
        var particleVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.ParticleVertex, ShaderKind.VertexShader, "particle.vert"));

        VertexInputBindingDescription[] meshBinding = [new(0, MeshVertex.SizeInBytes, VertexInputRate.Vertex)];
        var meshAttributes = MeshVertex.Attributes(0);

        _wirePipeline = PipelineFactory.Create(gpu, renderPass, new PipelineDescription
        {
            VertexShader = wireVertex,
            FragmentShader = patternFragment,
            Topology = PrimitiveTopology.LineList,
            Layout = _pipelineLayout,
            Bindings = meshBinding,
            Attributes = meshAttributes,
            Depth = DepthMode.Test,
        });

        _solidPipeline = PipelineFactory.Create(gpu, renderPass, new PipelineDescription
        {
            VertexShader = wireVertex,
            FragmentShader = patternFragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = meshBinding,
            Attributes = meshAttributes,
            Depth = DepthMode.TestAndWrite,
        });

        _sunPipeline = PipelineFactory.Create(gpu, renderPass, new PipelineDescription
        {
            VertexShader = sunVertex,
            FragmentShader = sunFragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = meshBinding,
            Attributes = meshAttributes,
            Depth = DepthMode.TestAndWrite,
        });

        _particlePipeline = PipelineFactory.Create(gpu, renderPass, new PipelineDescription
        {
            VertexShader = particleVertex,
            FragmentShader = patternFragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = [new(0, (uint)sizeof(ParticleInstance), VertexInputRate.Instance)],
            Attributes =
            [
                new(0, 0, Format.R32G32B32Sfloat, 0),
                new(1, 0, Format.R32G32Sfloat, 12),
                new(2, 0, Format.R32G32B32A32Uint, 20),
            ],
            Depth = DepthMode.Test,
        });

        foreach (var module in new[] { wireVertex, patternFragment, sunVertex, sunFragment, particleVertex })
        {
            vk.DestroyShaderModule(gpu.Device, module, null);
        }

        // The per-frame resources
        _frames = new FrameResources[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            var frame = new FrameResources(gpu)
            {
                Uniforms = gpu.CreateHostBuffer((ulong)sizeof(FrameUniforms), BufferUsageFlags.UniformBufferBit),
            };

            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &descriptorSetLayout,
            };
            GpuDevice.Check(vk.AllocateDescriptorSets(gpu.Device, in allocInfo, out frame.DescriptorSet), "vkAllocateDescriptorSets");

            var bufferInfo = new DescriptorBufferInfo(frame.Uniforms.Buffer, 0, (ulong)sizeof(FrameUniforms));
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.DescriptorSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufferInfo,
            };
            vk.UpdateDescriptorSets(gpu.Device, 1, in write, 0, null);
            _frames[i] = frame;
        }
    }

    /// <summary>
    /// Record the commands to draw the world into the given viewport, where
    /// pixelSize is the size of one of the original's pixels (1/192 of the
    /// space view's height), and palette is the current space view palette.
    /// </summary>
    public void Draw(CommandBuffer commandBuffer, int frameIndex, SceneFrame scene, Rect2D viewport, float pixelSize, float lineWidth, int[] palette)
    {
        var vk = _gpu.Vk;
        var frame = _frames[frameIndex];
        float aspect = (float)viewport.Extent.Width / viewport.Extent.Height;
        var viewProjection = scene.Camera.ViewMatrix * Camera.Projection(aspect);

        *(FrameUniforms*)frame.Uniforms.Mapped = new FrameUniforms
        {
            ViewProjection = viewProjection,
            Viewport = new Vector4(viewport.Offset.X, viewport.Offset.Y, pixelSize, 0),
            ViewportSize = new Vector4(viewport.Extent.Width, viewport.Extent.Height, 0, 0),
        };

        var vkViewport = new Viewport(viewport.Offset.X, viewport.Offset.Y, viewport.Extent.Width, viewport.Extent.Height, 0, 1);
        vk.CmdSetViewport(commandBuffer, 0, 1, in vkViewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, in viewport);
        vk.CmdSetLineWidth(commandBuffer, lineWidth);

        var descriptorSet = frame.DescriptorSet;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in descriptorSet, 0, null);

        // Solid objects first, so they are in the depth buffer before the
        // lines and particles are tested against it
        DrawPlanetOccluders(commandBuffer, scene);
        DrawSuns(commandBuffer, scene, pixelSize, palette);
        DrawShips(commandBuffer, scene, palette);
        DrawPlanetLines(commandBuffer, scene, palette);
        DrawLines(commandBuffer, frame, scene, palette);
        DrawParticles(commandBuffer, frame, scene, palette, aspect);
    }

    private void BindMeshes(CommandBuffer commandBuffer, Pipeline pipeline)
    {
        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        ulong offset = 0;
        var buffer = _meshes.VertexBuffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
    }

    private void DrawMesh(CommandBuffer commandBuffer, Mesh mesh, in DrawConstants constants)
    {
        fixed (DrawConstants* p = &constants)
        {
            _gpu.Vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(DrawConstants), p);
        }

        _gpu.Vk.CmdDraw(commandBuffer, mesh.VertexCount, 1, mesh.FirstVertex, 0);
    }

    /// <summary>Draw constants for geometry with no faces (every edge is drawn).</summary>
    private static DrawConstants Unculled(Matrix4x4 model, uint[] colours) => new()
    {
        Model = model,
        Parameters = new Vector4(0, 1, 0, 0),
        Colour0 = colours[0],
        Colour1 = colours[1],
        Colour2 = colours[2],
        Colour3 = colours[3],
    };

    /// <summary>A transform that maps the unit circle (or disc) onto a circle with the given centre and axes.</summary>
    private static Matrix4x4 CircleTransform(Vector3 centre, Vector3 axisX, Vector3 axisY)
    {
        var normal = Vector3.Cross(axisX, axisY);
        return new Matrix4x4(
            axisX.X, axisX.Y, axisX.Z, 0,
            axisY.X, axisY.Y, axisY.Z, 0,
            normal.X, normal.Y, normal.Z, 0,
            centre.X, centre.Y, centre.Z, 1);
    }

    /// <summary>
    /// The outline of a sphere as seen from the camera (at the origin): the
    /// circle where the lines of sight touch the sphere, which is centred on
    /// the line to the sphere's centre, and a pair of axes for it.
    /// </summary>
    private static bool Silhouette(Vector3 centre, float radius, out Vector3 circleCentre, out float circleRadius, out Vector3 axisX, out Vector3 axisY)
    {
        float distanceSquared = centre.LengthSquared();
        float ratio = radius * radius / distanceSquared;
        circleCentre = default;
        circleRadius = 0;
        axisX = axisY = default;
        if (ratio >= 1)
        {
            // We are inside the sphere
            return false;
        }

        circleCentre = centre * (1 - ratio);
        circleRadius = radius * MathF.Sqrt(1 - ratio);
        var forward = Vector3.Normalize(centre);
        var reference = MathF.Abs(forward.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        axisX = Vector3.Normalize(Vector3.Cross(reference, forward));
        axisY = Vector3.Cross(forward, axisX);
        return true;
    }

    /// <summary>Draw each planet's sphere into the depth buffer (in black), so it hides whatever is behind it.</summary>
    private void DrawPlanetOccluders(CommandBuffer commandBuffer, SceneFrame scene)
    {
        if (scene.Planets.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _solidPipeline);
        uint[] black = [Black, Black, Black, Black];
        foreach (var planet in scene.Planets)
        {
            var model = Matrix4x4.CreateScale(planet.Radius * PlanetOccluderScale) * Matrix4x4.CreateTranslation(planet.Centre);
            DrawMesh(commandBuffer, _meshes.UnitSphere, Unculled(model, black));
        }
    }

    /// <summary>Draw the outline of each planet, plus its meridian and equator, or its crater.</summary>
    private void DrawPlanetLines(CommandBuffer commandBuffer, SceneFrame scene, int[] palette)
    {
        if (scene.Planets.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _wirePipeline);
        foreach (var planet in scene.Planets)
        {
            var colours = ColourPattern.Resolve(planet.Colour, palette);
            if (Silhouette(planet.Centre, planet.Radius, out var centre, out float radius, out var axisX, out var axisY))
            {
                DrawMesh(commandBuffer, _meshes.UnitCircle, Unculled(CircleTransform(centre, axisX * radius, axisY * radius), colours));
            }

            if (!planet.ShowFeatures)
            {
                continue;
            }

            // The game rotates the planet's orientation vectors without keeping
            // them exactly at right angles, which doesn't matter to the original
            // (it just draws the ellipses they project to), but here the circles
            // need to lie on the planet's surface, so we straighten them up
            var nose = Vector3.Normalize(planet.Nose);
            var roof = Vector3.Normalize(planet.Roof - nose * Vector3.Dot(nose, planet.Roof));
            var side = Vector3.Normalize(planet.Side - nose * Vector3.Dot(nose, planet.Side) - roof * Vector3.Dot(roof, planet.Side));

            float r = planet.Radius;
            if (planet.HasCrater)
            {
                // The crater is a circle on the planet's surface, around roofv
                var craterCentre = planet.Centre + roof * (r * CraterDistance);
                var model = CircleTransform(craterCentre, nose * (r * CraterRadius), side * (r * CraterRadius));
                DrawMesh(commandBuffer, _meshes.UnitCircle, Unculled(model, colours));
            }
            else
            {
                // The meridian is the great circle through nosev and roofv, and
                // the equator is the great circle through nosev and sidev
                DrawMesh(commandBuffer, _meshes.UnitCircle, Unculled(CircleTransform(planet.Centre, nose * r, roof * r), colours));
                DrawMesh(commandBuffer, _meshes.UnitCircle, Unculled(CircleTransform(planet.Centre, nose * r, side * r), colours));
            }
        }
    }

    /// <summary>Draw each sun as a disc facing the camera, with the original's colours and fringe.</summary>
    private void DrawSuns(CommandBuffer commandBuffer, SceneFrame scene, float pixelSize, int[] palette)
    {
        if (scene.Suns.Count == 0)
        {
            return;
        }

        var colours = ColourPattern.Resolve(SunColourByte, palette);
        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _sunPipeline);
        ulong offset = 0;
        var buffer = _meshes.VertexBuffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);

        foreach (var sun in scene.Suns)
        {
            if (!Silhouette(sun.Centre, sun.Radius, out var centre, out float radius, out var axisX, out var axisY))
            {
                continue;
            }

            // The sun's radius in original pixels, given that the original
            // projects with a scale of 256 pixels per unit of x / z
            float radiusInPixels = 256 * radius / MathF.Max(centre.Length(), 1);

            // Make the disc big enough to include the fringe
            float discScale = 1 + (sun.FringeMask + 1) / MathF.Max(radiusInPixels, 1);
            var model = CircleTransform(centre, axisX * (radius * discScale), axisY * (radius * discScale));
            var constants = new DrawConstants
            {
                Model = model,
                Parameters = new Vector4(discScale, sun.Seed & 0xFFFF, sun.FringeMask, radiusInPixels),
                Colour0 = colours[0],
                Colour1 = colours[1],
            };
            DrawMesh(commandBuffer, _meshes.UnitDisc, constants);
        }
    }

    /// <summary>Draw the ships' wireframes, with the GPU working out which edges are hidden.</summary>
    private void DrawShips(CommandBuffer commandBuffer, SceneFrame scene, int[] palette)
    {
        if (scene.Ships.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _wirePipeline);
        foreach (var ship in scene.Ships)
        {
            if (!Matrix4x4.Invert(ship.Transform, out var inverse))
            {
                continue;
            }

            // The camera is at the origin of world space
            var camera = Vector3.Transform(Vector3.Zero, inverse);
            var colours = ColourPattern.Resolve(ship.Colour, palette);
            var constants = new DrawConstants
            {
                Model = ship.Transform,
                CameraModel = new Vector4(camera, ship.LodDistance),
                Parameters = new Vector4(ship.NormalOffsetScale, 0, 0, 0),
                Colour0 = colours[0],
                Colour1 = colours[1],
                Colour2 = colours[2],
                Colour3 = colours[3],
            };
            DrawMesh(commandBuffer, _meshes.Ship(ship.Model), constants);
        }
    }

    /// <summary>Draw the lines in space, such as laser beams.</summary>
    private void DrawLines(CommandBuffer commandBuffer, FrameResources frame, SceneFrame scene, int[] palette)
    {
        if (scene.Lines.Count == 0)
        {
            return;
        }

        var vertices = new MeshVertex[scene.Lines.Count * 2];
        for (int i = 0; i < scene.Lines.Count; i++)
        {
            vertices[i * 2] = MeshVertex.Plain(scene.Lines[i].Start);
            vertices[i * 2 + 1] = MeshVertex.Plain(scene.Lines[i].End);
        }

        frame.Lines.Write<MeshVertex>(vertices);
        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _wirePipeline);
        ulong offset = 0;
        var buffer = frame.Lines.Buffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);

        for (int i = 0; i < scene.Lines.Count; i++)
        {
            DrawMesh(commandBuffer, new Mesh((uint)i * 2, 2), Unculled(Matrix4x4.Identity, ColourPattern.Resolve(scene.Lines[i].Colour, palette)));
        }
    }

    /// <summary>
    /// Draw the particles, as one instanced draw. The stardust is spread out
    /// horizontally to fill views that are wider than the original's 4:3 (the
    /// game simulates it within that field of view, and each particle moves
    /// directly away from the centre in the front and rear views, so this
    /// keeps its motion looking the same).
    /// </summary>
    private void DrawParticles(CommandBuffer commandBuffer, FrameResources frame, SceneFrame scene, int[] palette, float aspect)
    {
        if (scene.Particles.Count == 0)
        {
            return;
        }

        float spread = MathF.Max(1, aspect / (4f / 3f));
        var view = scene.Camera.ViewMatrix;
        var patterns = new Dictionary<int, uint[]>();
        var instances = new ParticleInstance[scene.Particles.Count];
        for (int i = 0; i < instances.Length; i++)
        {
            var particle = scene.Particles[i];
            if (!patterns.TryGetValue(particle.Colour, out var colours))
            {
                colours = ColourPattern.Resolve(particle.Colour, palette);
                patterns[particle.Colour] = colours;
            }

            var position = particle.Position;
            if (particle.Stardust && spread > 1)
            {
                var inView = Vector3.TransformNormal(position, view);
                position = Camera.ViewToWorld(scene.Camera.View, inView with { X = inView.X * spread });
            }

            instances[i] = new ParticleInstance
            {
                Position = position,
                Size = new Vector2(particle.Width, particle.Height),
                Colour0 = colours[0],
                Colour1 = colours[1],
                Colour2 = colours[2],
                Colour3 = colours[3],
            };
        }

        frame.Particles.Write<ParticleInstance>(instances);
        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _particlePipeline);
        ulong offset = 0;
        var buffer = frame.Particles.Buffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
        _gpu.Vk.CmdDraw(commandBuffer, 6, (uint)instances.Length, 0, 0);
    }

    public void Dispose()
    {
        var vk = _gpu.Vk;
        foreach (var frame in _frames)
        {
            frame.Uniforms.Dispose();
            frame.Lines.Dispose();
            frame.Particles.Dispose();
        }

        vk.DestroyPipeline(_gpu.Device, _wirePipeline, null);
        vk.DestroyPipeline(_gpu.Device, _solidPipeline, null);
        vk.DestroyPipeline(_gpu.Device, _sunPipeline, null);
        vk.DestroyPipeline(_gpu.Device, _particlePipeline, null);
        vk.DestroyPipelineLayout(_gpu.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_gpu.Device, _descriptorPool, null);
        vk.DestroyDescriptorSetLayout(_gpu.Device, _descriptorSetLayout, null);
        _meshes.Dispose();
    }
}

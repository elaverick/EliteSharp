using System.Numerics;
using System.Runtime.InteropServices;
using EliteSharp.Rendering.Geometry;
using EliteSharp.Rendering.Scene;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// Draws the 3D world: the ships as wireframes over solid surfaces (so the
/// depth buffer hides whatever is behind them, including their own far
/// sides), the planet as a sphere with circles on its surface, the sun as a
/// disc, and particles and laser beams, all with real 3D transforms, a
/// perspective projection and a depth buffer.
///
/// The solid surfaces are drawn first, into the depth buffer only (a depth
/// pre-pass, with their back faces culled), then everything that is seen is
/// drawn and tested against them.
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

        /// <summary>The colours of each ink (see <see cref="Palette.Patterns"/>).</summary>
        public fixed uint Inks[Rendering.Inks.Count * Palette.PatternLength];
    }

    /// <summary>The per-draw push constants (see WorldShaders.Common).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 Model;
        public Vector4 Parameters;
        public uint Ink, SecondInk, Unused1, Unused2;
    }

    /// <summary>A particle as sent to the GPU (one instance per particle).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleInstance
    {
        public Vector3 Position;
        public Vector2 Size;
        public uint Ink;
    }

    /// <summary>A ship to draw this frame, at the level of detail chosen for it.</summary>
    private readonly record struct ShipDraw(ShipMeshes Meshes, int Level, Matrix4x4 Transform, Ink Colour);

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

    /// <summary>
    /// How far a ship's surface is pushed back from the camera (in the model's
    /// units), so the edges on it are in front of it. This is kept small, as the
    /// hidden edges within this distance of the surface show through it.
    /// </summary>
    private const float SurfacePushBack = 1;

    /// <summary>
    /// The slope-scaled depth bias for surfaces, which pushes them further back
    /// where they are seen almost edge-on, so the lines on them still pass the
    /// depth test at those angles.
    /// </summary>
    private const float SurfaceDepthBiasSlope = -2;

    private readonly GpuDevice _gpu;
    private readonly MeshLibrary _meshes;
    private readonly DescriptorSetLayout _descriptorSetLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _wirePipeline;
    private readonly Pipeline _depthPipeline;
    private readonly Pipeline _sunPipeline;
    private readonly Pipeline _particlePipeline;
    private readonly FrameResources[] _frames;

    /// <summary>The ships to draw this frame (reused from frame to frame).</summary>
    private readonly List<ShipDraw> _ships = [];

    public WorldRenderer(GpuDevice gpu, RenderTargetFormats targets, int framesInFlight, IEnumerable<string> shipModelPaths)
    {
        _gpu = gpu;
        var vk = gpu.Vk;
        _meshes = new MeshLibrary(gpu, shipModelPaths);

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
        var depthVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.DepthVertex, ShaderKind.VertexShader, "depth.vert"));
        var inkFragment = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.InkFragment, ShaderKind.FragmentShader, "ink.frag"));
        var sunVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.SunVertex, ShaderKind.VertexShader, "sun.vert"));
        var sunFragment = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.SunFragment, ShaderKind.FragmentShader, "sun.frag"));
        var particleVertex = gpu.CreateShaderModule(ShaderCompiler.Compile(WorldShaders.ParticleVertex, ShaderKind.VertexShader, "particle.vert"));

        VertexInputBindingDescription[] meshBinding = [new(0, MeshVertex.SizeInBytes, VertexInputRate.Vertex)];
        var meshAttributes = MeshVertex.Attributes(0);

        _wirePipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = wireVertex,
            FragmentShader = inkFragment,
            Topology = PrimitiveTopology.LineList,
            Layout = _pipelineLayout,
            Bindings = meshBinding,
            Attributes = meshAttributes,
            Depth = DepthMode.Test,
        });

        _depthPipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = depthVertex,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = meshBinding,
            Attributes = meshAttributes,
            Depth = DepthMode.TestAndWrite,
            CullBackFaces = true,
            DepthBiasSlope = SurfaceDepthBiasSlope,
        });

        _sunPipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = sunVertex,
            FragmentShader = sunFragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = meshBinding,
            Attributes = meshAttributes,
            Depth = DepthMode.TestAndWrite,
        });

        _particlePipeline = PipelineFactory.Create(gpu, targets, new PipelineDescription
        {
            VertexShader = particleVertex,
            FragmentShader = inkFragment,
            Topology = PrimitiveTopology.TriangleList,
            Layout = _pipelineLayout,
            Bindings = [new(0, (uint)sizeof(ParticleInstance), VertexInputRate.Instance)],
            Attributes =
            [
                new(0, 0, Format.R32G32B32Sfloat, 0),
                new(1, 0, Format.R32G32Sfloat, 12),
                new(2, 0, Format.R32Uint, 20),
            ],
            Depth = DepthMode.Test,
        });

        foreach (var module in new[] { wireVertex, depthVertex, inkFragment, sunVertex, sunFragment, particleVertex })
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
    /// space view's height), and palette says what the inks look like.
    /// </summary>
    public void Draw(CommandBuffer commandBuffer, int frameIndex, SceneFrame scene, Rect2D viewport, float pixelSize, float lineWidth, Palette palette)
    {
        var vk = _gpu.Vk;
        var frame = _frames[frameIndex];
        float aspect = (float)viewport.Extent.Width / viewport.Extent.Height;
        var viewProjection = scene.Camera.ViewMatrix * Camera.Projection(aspect);

        var uniforms = (FrameUniforms*)frame.Uniforms.Mapped;
        uniforms->ViewProjection = viewProjection;
        uniforms->Viewport = new Vector4(viewport.Offset.X, viewport.Offset.Y, pixelSize, 0);
        uniforms->ViewportSize = new Vector4(viewport.Extent.Width, viewport.Extent.Height, 0, 0);
        palette.Patterns.CopyTo(new Span<uint>(uniforms->Inks, Rendering.Inks.Count * Palette.PatternLength));

        var vkViewport = new Viewport(viewport.Offset.X, viewport.Offset.Y, viewport.Extent.Width, viewport.Extent.Height, 0, 1);
        vk.CmdSetViewport(commandBuffer, 0, 1, in vkViewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, in viewport);
        vk.CmdSetLineWidth(commandBuffer, lineWidth);

        var descriptorSet = frame.DescriptorSet;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in descriptorSet, 0, null);
        vk.CmdBindIndexBuffer(commandBuffer, _meshes.IndexBuffer, 0, IndexType.Uint32);

        ChooseShipLevels(scene, new ViewFrustum(aspect), viewport.Extent.Height);

        // The depth pre-pass, then everything that is seen, tested against it
        DrawDepth(commandBuffer, scene);
        DrawSuns(commandBuffer, scene);
        DrawShipWireframes(commandBuffer);
        DrawPlanetLines(commandBuffer, scene);
        DrawLines(commandBuffer, frame, scene);
        DrawParticles(commandBuffer, frame, scene, aspect);
    }

    /// <summary>
    /// Decide which ships can be seen (those whose bounding spheres are in the
    /// camera's view), and choose each one's level of detail from its size on
    /// the screen, in pixels.
    /// </summary>
    private void ChooseShipLevels(SceneFrame scene, ViewFrustum frustum, float viewportHeight)
    {
        _ships.Clear();
        var view = scene.Camera.ViewMatrix;
        foreach (var ship in scene.Ships)
        {
            var meshes = _meshes.Ship(ship.Model);
            var centre = ship.Transform.Translation;
            if (!frustum.Intersects(Vector3.Transform(centre, view), meshes.Radius))
            {
                continue;
            }

            float screenRadius = ViewFrustum.ProjectedRadius(meshes.Radius, centre.Length(), viewportHeight);
            _ships.Add(new ShipDraw(meshes, ShipLevelOfDetail.LevelFor(screenRadius), ship.Transform, ship.Colour));
        }
    }

    private void BindMeshes(CommandBuffer commandBuffer, Pipeline pipeline)
    {
        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        ulong offset = 0;
        var buffer = _meshes.VertexBuffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
    }

    private void PushConstants(CommandBuffer commandBuffer, in DrawConstants constants)
    {
        fixed (DrawConstants* p = &constants)
        {
            _gpu.Vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(DrawConstants), p);
        }
    }

    private void DrawMesh(CommandBuffer commandBuffer, Mesh mesh, in DrawConstants constants)
    {
        PushConstants(commandBuffer, constants);
        _gpu.Vk.CmdDrawIndexed(commandBuffer, mesh.IndexCount, 1, mesh.FirstIndex, mesh.VertexOffset, 0);
    }

    /// <summary>Draw constants for geometry in the given ink.</summary>
    private static DrawConstants Constants(Matrix4x4 model, Ink ink = Ink.None, Vector4 parameters = default) => new()
    {
        Model = model,
        Parameters = parameters,
        Ink = (uint)ink,
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

    /// <summary>
    /// The depth pre-pass: draw each planet's sphere and each ship's surface
    /// into the depth buffer, so they hide whatever is behind them.
    /// </summary>
    private void DrawDepth(CommandBuffer commandBuffer, SceneFrame scene)
    {
        if (scene.Planets.Count == 0 && _ships.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _depthPipeline);
        foreach (var planet in scene.Planets)
        {
            var model = Matrix4x4.CreateScale(planet.Radius * PlanetOccluderScale) * Matrix4x4.CreateTranslation(planet.Centre);
            DrawMesh(commandBuffer, _meshes.UnitSphere, Constants(model));
        }

        var pushBack = new Vector4(SurfacePushBack, 0, 0, 0);
        foreach (var ship in _ships)
        {
            DrawMesh(commandBuffer, ship.Meshes.Surfaces[ship.Level], Constants(ship.Transform, parameters: pushBack));
        }
    }

    /// <summary>Draw each sun as a disc facing the camera, with the original's colours and fringe.</summary>
    private void DrawSuns(CommandBuffer commandBuffer, SceneFrame scene)
    {
        if (scene.Suns.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _sunPipeline);
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
            DrawMesh(commandBuffer, _meshes.UnitDisc, new DrawConstants
            {
                Model = model,
                Parameters = new Vector4(discScale, sun.Seed & 0xFFFF, sun.FringeMask, radiusInPixels),
                Ink = (uint)Ink.Red,
                SecondInk = (uint)Ink.Yellow,
            });
        }
    }

    /// <summary>Draw the ships' wireframes, whose hidden parts fail the depth test against the surfaces.</summary>
    private void DrawShipWireframes(CommandBuffer commandBuffer)
    {
        if (_ships.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _wirePipeline);
        foreach (var ship in _ships)
        {
            DrawMesh(commandBuffer, ship.Meshes.Wireframes[ship.Level], Constants(ship.Transform, ship.Colour));
        }
    }

    /// <summary>Draw the outline of each planet, plus its meridian and equator, or its crater.</summary>
    private void DrawPlanetLines(CommandBuffer commandBuffer, SceneFrame scene)
    {
        if (scene.Planets.Count == 0)
        {
            return;
        }

        BindMeshes(commandBuffer, _wirePipeline);
        foreach (var planet in scene.Planets)
        {
            var ink = planet.Colour;
            if (Silhouette(planet.Centre, planet.Radius, out var centre, out float radius, out var axisX, out var axisY))
            {
                DrawMesh(commandBuffer, _meshes.UnitCircle, Constants(CircleTransform(centre, axisX * radius, axisY * radius), ink));
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
                // The crater is a circle on the planet's surface, around roofv.
                // The original only draws it when roofv points away from us
                // (roofv_z >= 0), which puts it on the far side of its
                // see-through planet; here the planet hides its far side, so
                // we reflect the crater front-to-back along the line of sight
                // onto the near side, where it covers the same part of the
                // screen as in the original
                var lineOfSight = Vector3.Normalize(planet.Centre);
                Vector3 Reflect(Vector3 v) => v - 2 * Vector3.Dot(v, lineOfSight) * lineOfSight;
                var craterCentre = planet.Centre + Reflect(roof) * (r * CraterDistance);
                var model = CircleTransform(craterCentre, Reflect(nose) * (r * CraterRadius), Reflect(side) * (r * CraterRadius));
                DrawMesh(commandBuffer, _meshes.UnitCircle, Constants(model, ink));
            }
            else
            {
                // The meridian is the great circle through nosev and roofv, and
                // the equator is the great circle through nosev and sidev
                DrawMesh(commandBuffer, _meshes.UnitCircle, Constants(CircleTransform(planet.Centre, nose * r, roof * r), ink));
                DrawMesh(commandBuffer, _meshes.UnitCircle, Constants(CircleTransform(planet.Centre, nose * r, side * r), ink));
            }
        }
    }

    /// <summary>Draw the lines in space, such as laser beams.</summary>
    private void DrawLines(CommandBuffer commandBuffer, FrameResources frame, SceneFrame scene)
    {
        var lines = CollectionsMarshal.AsSpan(scene.Lines);
        if (lines.IsEmpty)
        {
            return;
        }

        var vertices = frame.Lines.Map<MeshVertex>(lines.Length * 2);
        for (int i = 0; i < lines.Length; i++)
        {
            vertices[i * 2] = new MeshVertex(lines[i].Start);
            vertices[i * 2 + 1] = new MeshVertex(lines[i].End);
        }

        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _wirePipeline);
        ulong offset = 0;
        var buffer = frame.Lines.Buffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
        for (int i = 0; i < lines.Length; i++)
        {
            PushConstants(commandBuffer, Constants(Matrix4x4.Identity, lines[i].Colour));
            _gpu.Vk.CmdDraw(commandBuffer, 2, 1, (uint)i * 2, 0);
        }
    }

    /// <summary>
    /// Draw the particles, as one instanced draw. The stardust is spread out
    /// horizontally to fill views that are wider than the original's 4:3 (the
    /// game simulates it within that field of view, and each particle moves
    /// directly away from the centre in the front and rear views, so this
    /// keeps its motion looking the same).
    /// </summary>
    private void DrawParticles(CommandBuffer commandBuffer, FrameResources frame, SceneFrame scene, float aspect)
    {
        var particles = CollectionsMarshal.AsSpan(scene.Particles);
        if (particles.IsEmpty)
        {
            return;
        }

        float spread = MathF.Max(1, aspect / (4f / 3f));
        var view = scene.Camera.ViewMatrix;
        var instances = frame.Particles.Map<ParticleInstance>(particles.Length);
        for (int i = 0; i < particles.Length; i++)
        {
            ref readonly var particle = ref particles[i];
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
                Ink = (uint)particle.Colour,
            };
        }

        _gpu.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _particlePipeline);
        ulong offset = 0;
        var buffer = frame.Particles.Buffer;
        _gpu.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, in buffer, in offset);
        _gpu.Vk.CmdDraw(commandBuffer, 6, (uint)particles.Length, 0, 0);
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
        vk.DestroyPipeline(_gpu.Device, _depthPipeline, null);
        vk.DestroyPipeline(_gpu.Device, _sunPipeline, null);
        vk.DestroyPipeline(_gpu.Device, _particlePipeline, null);
        vk.DestroyPipelineLayout(_gpu.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_gpu.Device, _descriptorPool, null);
        vk.DestroyDescriptorSetLayout(_gpu.Device, _descriptorSetLayout, null);
        _meshes.Dispose();
    }
}

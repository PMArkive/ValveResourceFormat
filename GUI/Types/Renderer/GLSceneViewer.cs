using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.Renderer.UniformBuffers;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat;

namespace GUI.Types.Renderer
{
    internal abstract class GLSceneViewer : GLViewerControl, IDisposable
    {
        public Scene Scene { get; }
        public Scene SkyboxScene { get; protected set; }
        public SceneSkybox2D Skybox2D { get; protected set; }
        public VrfGuiContext GuiContext => Scene.GuiContext;

        private bool ShowBaseGrid;
        private bool ShowLightBackground;
        public bool ShowSkybox { get; set; } = true;
        public bool IsWireframe { get; set; }

        public float Uptime { get; private set; }

        private bool showStaticOctree;
        private bool showDynamicOctree;
        private Frustum lockedCullFrustum;

        protected UniformBuffer<ViewConstants> viewBuffer;
        private UniformBuffer<LightingConstants> lightingBuffer;
        public List<IBuffer> Buffers { get; private set; }
        public List<(ReservedTextureSlots Slot, string Name, RenderTexture Texture)> Textures { get; } = [];

        private bool skipRenderModeChange;
        private ComboBox renderModeComboBox;
        private InfiniteGrid baseGrid;
        private SceneBackground baseBackground;
        private OctreeDebugRenderer<SceneNode> staticOctreeRenderer;
        private OctreeDebugRenderer<SceneNode> dynamicOctreeRenderer;
        protected SelectedNodeRenderer selectedNodeRenderer;
        private Shader depthOnlyShader;
        private GLTextureViewer depthViewer;
        private GLViewerTrackBarControl sunYawTrackbar;
        private GLViewerTrackBarControl sunPitchTrackbar;
        private GLViewerTrackBarControl sunRollTrackbar;
        private float SunPitch = 45f;
        private float SunYaw = 200f;
        private float SunRoll;
        public Framebuffer ShadowDepthBuffer { get; private set; }

        protected GLSceneViewer(VrfGuiContext guiContext, Frustum cullFrustum) : base(guiContext)
        {
            Scene = new Scene(guiContext);
            lockedCullFrustum = cullFrustum;

            InitializeControl();
            AddWireframeToggleControl();

            GLLoad += OnLoad;

#if DEBUG
            guiContext.ShaderLoader.ShaderHotReload.ReloadShader += OnHotReload;
#endif
        }

        protected GLSceneViewer(VrfGuiContext guiContext) : base(guiContext)
        {
            Scene = new Scene(guiContext);

            InitializeControl();
            AddCheckBox("Lock Cull Frustum", false, (v) =>
            {
                lockedCullFrustum = v ? Camera.ViewFrustum.Clone() : null;
            });
            AddCheckBox("Show Static Octree", showStaticOctree, (v) =>
            {
                showStaticOctree = v;

                if (showStaticOctree)
                {
                    staticOctreeRenderer.StaticBuild();
                }
            });
            AddCheckBox("Show Dynamic Octree", showDynamicOctree, (v) => showDynamicOctree = v);
            AddCheckBox("Show Tool Materials", Scene.ShowToolsMaterials, (v) =>
            {
                Scene.ShowToolsMaterials = v;

                if (SkyboxScene != null)
                {
                    SkyboxScene.ShowToolsMaterials = v;
                }
            });

            AddWireframeToggleControl();

            GLLoad += OnLoad;

#if DEBUG
            guiContext.ShaderLoader.ShaderHotReload.ReloadShader += OnHotReload;
#endif
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                viewBuffer?.Dispose();
                lightingBuffer?.Dispose();

                GLPaint -= OnPaint;

#if DEBUG
                GuiContext.ShaderLoader.ShaderHotReload.ReloadShader -= OnHotReload;
#endif
            }

            base.Dispose(disposing);
        }

        protected abstract void InitializeControl();

        private void CreateBuffers()
        {
            viewBuffer = new(ReservedBufferSlots.View);
            lightingBuffer = new(ReservedBufferSlots.Lighting);

            Buffers = [viewBuffer, lightingBuffer];
        }

        void UpdateSceneBuffersGpu(Scene scene, Camera camera)
        {
            camera.SetViewConstants(viewBuffer.Data);
            scene.SetFogConstants(viewBuffer.Data);
            viewBuffer.Update();
        }

        public virtual void PreSceneLoad()
        {
            const string vtexFileName = "ggx_integrate_brdf_lut_schlick.vtex_c";
            var assembly = Assembly.GetExecutingAssembly();

            // Load brdf lut, preferably from game.
            var brdfLutResource = GuiContext.LoadFile("textures/dev/" + vtexFileName);

            try
            {
                Stream brdfStream; // Will be used by LoadTexture, and disposed by resource

                if (brdfLutResource == null)
                {
                    brdfStream = assembly.GetManifestResourceStream("GUI.Utils." + vtexFileName);

                    brdfLutResource = new Resource() { FileName = vtexFileName };
                    brdfLutResource.Read(brdfStream);
                }

                // TODO: add annoying force clamp for lut
                Textures.Add(new(ReservedTextureSlots.BRDFLookup, "g_tBRDFLookup", GuiContext.MaterialLoader.LoadTexture(brdfLutResource)));
            }
            finally
            {
                brdfLutResource?.Dispose();
            }

            // Load default cube fog texture.
            using var cubeFogStream = assembly.GetManifestResourceStream("GUI.Utils.sky_furnace.vtex_c");
            using var cubeFogResource = new Resource() { FileName = "default_cube.vtex_c" };
            cubeFogResource.Read(cubeFogStream);

            Scene.FogInfo.DefaultFogTexture = GuiContext.MaterialLoader.LoadTexture(cubeFogResource);
        }

        public virtual void PostSceneLoad()
        {
            Scene.CalculateLightProbeBindings();
            Scene.CalculateEnvironmentMaps();

            if (SkyboxScene != null)
            {
                SkyboxScene.CalculateLightProbeBindings();
                SkyboxScene.CalculateEnvironmentMaps();
            }

            if (Scene.AllNodes.Any() && this is not GLWorldViewer)
            {
                var first = true;
                var bbox = new AABB();

                foreach (var node in Scene.AllNodes)
                {
                    if (first)
                    {
                        first = false;
                        bbox = node.BoundingBox;
                        continue;
                    }

                    bbox = bbox.Union(node.BoundingBox);
                }

                // If there is no bbox, LookAt will break camera, so +1 to location
                var offset = Math.Max(bbox.Max.X, bbox.Max.Z) + 1f * 1.5f;
                var location = new Vector3(offset, 0, offset);

                Camera.SetLocation(location);
                Camera.LookAt(bbox.Center);
            }

            staticOctreeRenderer = new OctreeDebugRenderer<SceneNode>(Scene.StaticOctree, Scene.GuiContext, false);
            dynamicOctreeRenderer = new OctreeDebugRenderer<SceneNode>(Scene.DynamicOctree, Scene.GuiContext, true);

            SetAvailableRenderModes();

            AddControl(new Label
            {
                Text = "Sun Angle",
            });

            sunYawTrackbar = AddTrackBar(value =>
            {
                SunYaw = value;
            });
            sunYawTrackbar.TrackBar.TickFrequency = 5;
            sunYawTrackbar.TrackBar.Minimum = 0;
            sunYawTrackbar.TrackBar.Maximum = 360;
            sunYawTrackbar.TrackBar.Value = (int)SunYaw;

            sunPitchTrackbar = AddTrackBar(value =>
            {
                SunPitch = value;
            });
            sunPitchTrackbar.TrackBar.TickFrequency = 5;
            sunPitchTrackbar.TrackBar.Minimum = 0;
            sunPitchTrackbar.TrackBar.Maximum = 360;
            sunPitchTrackbar.TrackBar.Value = (int)SunPitch;

            sunRollTrackbar = AddTrackBar(value =>
            {
                SunRoll = value;
            });
            sunRollTrackbar.TrackBar.TickFrequency = 5;
            sunRollTrackbar.TrackBar.Minimum = 0;
            sunRollTrackbar.TrackBar.Maximum = 360;
            sunRollTrackbar.TrackBar.Value = (int)SunRoll;
        }

        protected abstract void LoadScene();

        protected abstract void OnPicked(object sender, PickingTexture.PickingResponse pixelInfo);

        protected virtual void OnLoad(object sender, EventArgs e)
        {
            baseGrid = new InfiniteGrid(Scene);
            baseBackground = new SceneBackground(Scene);
            selectedNodeRenderer = new(Scene);

            Picker = new PickingTexture(Scene.GuiContext, OnPicked);

            ShadowDepthBuffer = Framebuffer.Prepare(512, 512, 0, null, Framebuffer.DepthAttachmentFormat.Depth32F);
            ShadowDepthBuffer.Initialize();
            ShadowDepthBuffer.ClearMask = ClearBufferMask.DepthBufferBit;
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            Textures.Add(new(ReservedTextureSlots.ShadowDepthBufferDepth, "g_tShadowDepthBufferDepth", ShadowDepthBuffer.Depth));

            GL.TextureParameter(ShadowDepthBuffer.Depth.Handle, TextureParameterName.TextureBaseLevel, 0);
            GL.TextureParameter(ShadowDepthBuffer.Depth.Handle, TextureParameterName.TextureMaxLevel, 0);
            depthOnlyShader = GuiContext.ShaderLoader.LoadShader("vrf.depth_only", new Dictionary<string, byte>()
            {
                ["D_ANIMATED"] = 1,
            });

            MainFramebuffer.Bind(FramebufferTarget.Framebuffer);
            CreateBuffers();

            var timer = Stopwatch.StartNew();
            PreSceneLoad();
            LoadScene();
            timer.Stop();
            Log.Debug(GetType().Name, $"Loading scene time: {timer.Elapsed}, shader variants: {GuiContext.ShaderLoader.ShaderCount}, materials: {GuiContext.MaterialLoader.MaterialCount}");

            PostSceneLoad();

            depthViewer = new GLTextureViewer(this, Scene.GuiContext);
            Scene.LightingInfo.LightingData.SunLightColor = Vector4.One;

            GLLoad -= OnLoad;
            GLPaint += OnPaint;

            GuiContext.ClearCache();
        }

        protected virtual void OnPaint(object sender, RenderEventArgs e)
        {
            Uptime += e.FrameTime;
            viewBuffer.Data.Time = Uptime;

            Scene.LightingInfo.LightingData.SunLightPosition = Matrix4x4.CreateFromYawPitchRoll(SunYaw * MathF.PI / 180f, SunPitch * MathF.PI / 180f, SunRoll * MathF.PI / 180f);

            var renderContext = new Scene.RenderContext
            {
                View = this,
                Camera = Camera,
                Framebuffer = MainFramebuffer,
            };

            using (new GLDebugGroup("Update Loop"))
            {
                Scene.Update(e.FrameTime);
                SkyboxScene?.Update(e.FrameTime);

                selectedNodeRenderer.Update(new Scene.UpdateContext(e.FrameTime));

                Scene.CollectSceneDrawCalls(Camera, lockedCullFrustum);
                SkyboxScene?.CollectSceneDrawCalls(Camera, lockedCullFrustum);
            }

            using (new GLDebugGroup("Scenes Render"))
            {
                if (Picker.ActiveNextFrame)
                {
                    renderContext.ReplacementShader = Picker.Shader;
                    renderContext.Framebuffer = Picker;

                    RenderScenesWithView(renderContext);
                    Picker.Finish();
                }

                if (Picker.DebugShader is not null)
                {
                    renderContext.ReplacementShader = Picker.DebugShader;
                }

                RenderSceneShadows(renderContext);
                RenderScenesWithView(renderContext);
            }

            using (new GLDebugGroup("Lines Render"))
            {
                selectedNodeRenderer.Render(renderContext);

                if (showStaticOctree)
                {
                    staticOctreeRenderer.Render();
                }

                if (showDynamicOctree)
                {
                    dynamicOctreeRenderer.Render();
                }

                if (ShowBaseGrid)
                {
                    baseGrid.Render();
                }
            }

            depthViewer.DrawLowerCorner(ShadowDepthBuffer.Depth);
        }

        protected void DrawMainScene()
        {
            var renderContext = new Scene.RenderContext
            {
                View = this,
                Camera = Camera,
                Framebuffer = MainFramebuffer,
                Scene = Scene,
            };

            UpdateSceneBuffersGpu(Scene, Camera);
            lightingBuffer.Data = Scene.LightingInfo.LightingData;

            Scene.RenderOpaqueLayer(renderContext);
            Scene.RenderTranslucentLayer(renderContext);
        }

        private void RenderSceneShadows(Scene.RenderContext renderContext)
        {
            GL.Viewport(0, 0, ShadowDepthBuffer.Width, ShadowDepthBuffer.Height);
            ShadowDepthBuffer.Bind(FramebufferTarget.Framebuffer);
            GL.ClearDepth(1);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthRange(0, 1);
            GL.Clear(ClearBufferMask.DepthBufferBit);

            renderContext.Framebuffer = ShadowDepthBuffer;
            renderContext.ReplacementShader = depthOnlyShader;
            renderContext.Scene = Scene;

            var sunMatrix = Scene.LightingInfo.LightingData.SunLightPosition;
            var sunDir = Vector3.Normalize(Vector3.Transform(-Vector3.UnitX, sunMatrix)); // why is sun dir calculated like so?.

            var bbox = 2048f;
            var sunPos = sunDir * bbox * 0.5f;
            var sunCameraProj = Matrix4x4.CreateOrthographicOffCenter(-bbox, bbox, -bbox, bbox, 1f, bbox * 2 - 1f);
            var sunCameraView = Matrix4x4.CreateLookAt(sunPos, Vector3.Zero, Vector3.UnitZ);

            var sunViewProj = sunCameraView * sunCameraProj;
            viewBuffer.Data.ViewToProjection = sunViewProj;
            viewBuffer.Data.WorldToShadow = sunViewProj;
            viewBuffer.Data.CameraPosition = sunPos;
            viewBuffer.Update();

            Scene.RenderOpaqueShadows(renderContext);
            GL.ClearDepth(0);
            GL.DepthFunc(DepthFunction.Greater);
        }

        private void RenderScenesWithView(Scene.RenderContext renderContext)
        {
            GL.Viewport(0, 0, renderContext.Framebuffer.Width, renderContext.Framebuffer.Height);
            renderContext.Framebuffer.Clear();

            // TODO: check if renderpass allows wireframe mode
            if (IsWireframe)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }

            GL.DepthRange(0.05, 1);
            UpdateSceneBuffersGpu(Scene, Camera);
            lightingBuffer.Data = Scene.LightingInfo.LightingData;

            using (new GLDebugGroup("Main Scene Opaque Render"))
            {
                renderContext.Scene = Scene;
                Scene.RenderOpaqueLayer(renderContext);
            }

            // 3D Sky
            GL.DepthRange(0, 0.05);
            if (ShowSkybox && SkyboxScene != null)
            {
                using (new GLDebugGroup("3D Sky Scene Render"))
                {
                    lightingBuffer.Data = SkyboxScene.LightingInfo.LightingData;
                    renderContext.Scene = SkyboxScene;
                    renderContext.ReplacementShader?.SetUniform1("isSkybox", 1u);

                    SkyboxScene.RenderOpaqueLayer(renderContext);
                    SkyboxScene.RenderTranslucentLayer(renderContext);

                    lightingBuffer.Data = Scene.LightingInfo.LightingData;
                    renderContext.Scene = Scene;
                    renderContext.ReplacementShader?.SetUniform1("isSkybox", 0u);
                }
            }

            // 2D Sky
            if (Skybox2D is not null)
            {
                using (new GLDebugGroup("2D Sky Render"))
                {
                    Skybox2D.Render();
                }
            }
            else
            {
                baseBackground.Render();
            }

            GL.DepthRange(0.05, 1);

            using (new GLDebugGroup("Main Scene Translucent Render"))
            {
                Scene.RenderTranslucentLayer(renderContext);
            }

            if (IsWireframe)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }
        }

        protected void AddBaseGridControl()
        {
            ShowBaseGrid = true;

            AddCheckBox("Show Grid", ShowBaseGrid, (v) => ShowBaseGrid = v);
            AddCheckBox("Show Light Background", ShowLightBackground, (v) =>
            {
                ShowLightBackground = v;
                baseBackground.SetLightBackground(ShowLightBackground);
            });
        }

        protected void AddWireframeToggleControl()
        {
            AddCheckBox("Show Wireframe", false, (v) => IsWireframe = v);
        }

        protected void AddRenderModeSelectionControl()
        {
            renderModeComboBox ??= AddSelection("Render Mode", (renderMode, _) =>
            {
                if (skipRenderModeChange)
                {
                    skipRenderModeChange = false;
                    return;
                }

                SetRenderMode(renderMode);
            });
        }

        private void SetAvailableRenderModes(bool keepCurrentSelection = false)
        {
            if (renderModeComboBox != null)
            {
                var selectedIndex = 0;
                var supportedRenderModes = Scene.AllNodes
                    .SelectMany(r => r.GetSupportedRenderModes())
                    .Concat(Picker.Shader.RenderModes)
                    .Distinct()
                    .Prepend("Default Render Mode")
                    .ToArray();

                if (keepCurrentSelection)
                {
                    selectedIndex = Array.IndexOf(supportedRenderModes, renderModeComboBox.SelectedItem.ToString());

                    if (selectedIndex < 0)
                    {
                        selectedIndex = 0;
                    }
                }

                renderModeComboBox.BeginUpdate();
                renderModeComboBox.Items.Clear();
                renderModeComboBox.Enabled = true;
                renderModeComboBox.Items.AddRange(supportedRenderModes);
                skipRenderModeChange = true;
                renderModeComboBox.SelectedIndex = selectedIndex;
                renderModeComboBox.EndUpdate();
            }
        }

        protected void SetEnabledLayers(HashSet<string> layers)
        {
            Scene.SetEnabledLayers(layers);
            SkyboxScene?.SetEnabledLayers(layers);

            staticOctreeRenderer = new OctreeDebugRenderer<SceneNode>(Scene.StaticOctree, Scene.GuiContext, false);
        }

        private void SetRenderMode(string renderMode)
        {
            var title = Program.MainForm.Text;
            Program.MainForm.Text = "Source 2 Viewer - Reloading shaders…";

            try
            {
                Picker.SetRenderMode(renderMode);
                selectedNodeRenderer.SetRenderMode(renderMode);

                foreach (var node in Scene.AllNodes)
                {
                    node.SetRenderMode(renderMode);
                }

                if (SkyboxScene != null)
                {
                    foreach (var node in SkyboxScene.AllNodes)
                    {
                        node.SetRenderMode(renderMode);
                    }
                }
            }
            finally
            {
                Program.MainForm.Text = title;
            }
        }

        protected override void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyData == Keys.Delete)
            {
                selectedNodeRenderer.DisableSelectedNodes();
                return;
            }

            base.OnKeyDown(sender, e);
        }

#if DEBUG
        private void OnHotReload(object sender, string e)
        {
            if (renderModeComboBox != null)
            {
                SetAvailableRenderModes(true);
            }

            foreach (var node in Scene.AllNodes)
            {
                node.UpdateVertexArrayObjects();
            }

            if (SkyboxScene != null)
            {
                foreach (var node in SkyboxScene.AllNodes)
                {
                    node.UpdateVertexArrayObjects();
                }
            }
        }
#endif
    }
}

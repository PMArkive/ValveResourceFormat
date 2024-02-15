using System.Reflection;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat;

namespace GUI.Types.Renderer
{
    /// <summary>
    /// GL Render control with model controls (render mode, animation panels).
    /// </summary>
    class GLSingleNodeViewer : GLSceneViewer, IDisposable
    {
        private Framebuffer SaveAsFbo;
        private GLViewerTrackBarControl sunYawTrackbar;
        private GLViewerTrackBarControl sunPitchTrackbar;
        private GLViewerTrackBarControl sunRollTrackbar;
        private float SunPitch;
        private float SunYaw = 200f;
        private float SunRoll = 45f;

        public GLSingleNodeViewer(VrfGuiContext guiContext)
            : base(guiContext, Frustum.CreateEmpty())
        {
            //
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SaveAsFbo?.Dispose();

                sunYawTrackbar.Dispose();
                sunPitchTrackbar.Dispose();
                sunRollTrackbar.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void InitializeControl()
        {
            AddRenderModeSelectionControl();
            AddBaseGridControl();
        }

        public override void PreSceneLoad()
        {
            base.PreSceneLoad();
            LoadDefaultEnviromentMap();
        }

        protected override void LoadScene()
        {
            MainFramebuffer.ChangeFormat(new(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedInt), MainFramebuffer.DepthFormat);
        }

        public override void PostSceneLoad()
        {
            base.PostSceneLoad();

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

        private void LoadDefaultEnviromentMap()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("GUI.Utils.industrial_sunset_puresky.vtex_c");

            using var resource = new Resource()
            {
                FileName = "vrf_default_cubemap.vtex_c"
            };
            resource.Read(stream);

            var texture = Scene.GuiContext.MaterialLoader.LoadTexture(resource);
            var environmentMap = new SceneEnvMap(Scene, new AABB(new Vector3(float.MinValue), new Vector3(float.MaxValue)))
            {
                Transform = Matrix4x4.Identity,
                EdgeFadeDists = Vector3.Zero,
                HandShake = 0,
                ProjectionMode = 0,
                EnvMapTexture = texture,
            };

            Scene.LightingInfo.AddEnvironmentMap(environmentMap);
        }

        protected override void OnPaint(object sender, RenderEventArgs e)
        {
            Scene.LightingInfo.LightingData.SunLightPosition = Matrix4x4.CreateFromYawPitchRoll(SunYaw * MathF.PI / 180f, SunPitch * MathF.PI / 180f, SunRoll * MathF.PI / 180f);
            Scene.LightingInfo.LightingData.SunLightColor = Vector4.One;

            base.OnPaint(sender, e);
        }

        protected override void OnPicked(object sender, PickingTexture.PickingResponse pickingResponse)
        {
            //
        }

        // Render only the main scene nodes into a transparent framebuffer
        protected override SKBitmap ReadPixelsToBitmap()
        {
            var (w, h) = (MainFramebuffer.Width, MainFramebuffer.Height);

            MainFramebuffer.Bind(FramebufferTarget.Framebuffer);
            GL.ClearColor(new OpenTK.Graphics.Color4(0, 0, 0, 0));
            GL.Clear(MainFramebuffer.ClearMask);

            DrawMainScene();

            if (SaveAsFbo == null)
            {
                SaveAsFbo = Framebuffer.Prepare(w, h, 0, new(PixelInternalFormat.Rgba8, PixelFormat.Bgra, PixelType.UnsignedByte), MainFramebuffer.DepthFormat);
                SaveAsFbo.ClearColor = new OpenTK.Graphics.Color4(0, 0, 0, 0);
                SaveAsFbo.Initialize();
            }
            else
            {
                SaveAsFbo.Resize(w, h);
            }

            SaveAsFbo.Clear();
            GL.BlitNamedFramebuffer(MainFramebuffer.FboHandle, SaveAsFbo.FboHandle, 0, h, w, 0, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

            GL.Flush();
            GL.Finish();

            SaveAsFbo.Bind(FramebufferTarget.ReadFramebuffer);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pixels = bitmap.GetPixels(out var length);

            GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);

            return bitmap;
        }
    }
}

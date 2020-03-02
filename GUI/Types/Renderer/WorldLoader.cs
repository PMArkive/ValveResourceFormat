using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GUI.Utils;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace GUI.Types.Renderer
{
    internal class WorldLoader
    {
        private readonly World world;
        private readonly VrfGuiContext guiContext;

        // Contains metadata that can't be captured by manipulating the scene itself. Returned from Load().
        public class LoadResult
        {
            public HashSet<string> DefaultEnabledLayers { get; } = new HashSet<string>();

            public IDictionary<string, Matrix4x4> CameraMatrices { get; } = new Dictionary<string, Matrix4x4>();

            public Vector3? GlobalLightPosition { get; set; }
        }

        public WorldLoader(VrfGuiContext vrfGuiContext, World world)
        {
            this.world = world;
            guiContext = vrfGuiContext;
        }

        public LoadResult Load(Scene scene)
        {
            var result = new LoadResult();

            // Output is World_t we need to iterate m_worldNodes inside it.
            var worldNodes = world.GetWorldNodeNames();
            foreach (var worldNode in worldNodes)
            {
                if (worldNode != null)
                {
                    var newResource = guiContext.LoadFileByAnyMeansNecessary(worldNode + ".vwnod_c");
                    if (newResource == null)
                    {
                        throw new Exception("WTF");
                    }

                    var subloader = new WorldNodeLoader(guiContext, new WorldNode(newResource));
                    subloader.Load(scene);
                }
            }

            foreach (var lumpName in world.GetEntityLumpNames())
            {
                if (lumpName == null)
                {
                    return result;
                }

                var newResource = guiContext.LoadFileByAnyMeansNecessary(lumpName + "_c");

                if (newResource == null)
                {
                    return result;
                }

                var entityLump = new EntityLump(newResource);
                LoadEntitiesFromLump(scene, result, entityLump, "world_layer_base"); // TODO
            }

            return result;
        }

        private void LoadEntitiesFromLump(Scene scene, LoadResult result, EntityLump entityLump, string layerName = null)
        {
            var childEntities = entityLump.GetChildEntityNames();

            foreach (var childEntityName in childEntities)
            {
                var newResource = guiContext.LoadFileByAnyMeansNecessary(childEntityName + "_c");

                if (newResource == null)
                {
                    continue;
                }

                var childLump = new EntityLump(newResource);
                var childName = childLump.GetData().GetProperty<string>("m_name");

                LoadEntitiesFromLump(scene, result, childLump, childName);
            }

            var worldEntities = entityLump.GetEntities();

            foreach (var entity in worldEntities)
            {
                var classname = entity.GetProperty<string>("classname");

                if (classname == "info_world_layer")
                {
                    var spawnflags = entity.GetProperty<uint>("spawnflags");
                    var layername = entity.GetProperty<string>("layername");

                    // Visible on spawn flag
                    if ((spawnflags & 1) == 1)
                    {
                        result.DefaultEnabledLayers.Add(layername);
                    }

                    continue;
                }

                var scale = entity.GetProperty<string>("scales");
                var position = entity.GetProperty<string>("origin");
                var angles = entity.GetProperty<string>("angles");
                var model = entity.GetProperty<string>("model");
                var skin = entity.GetProperty<string>("skin");
                var colour = entity.GetProperty<byte[]>("rendercolor");
                var particle = entity.GetProperty<string>("effect_name");
                var animation = entity.GetProperty<string>("defaultanim");

                if (scale == null || position == null || angles == null)
                {
                    continue;
                }

                var isGlobalLight = classname == "env_global_light";
                var isCamera =
                    classname == "sky_camera" ||
                    classname == "point_devshot_camera" ||
                    classname == "point_camera";

                var scaleMatrix = Matrix4x4.CreateScale(VectorExtensions.ParseVector(scale));

                var positionVector = VectorExtensions.ParseVector(position);
                var positionMatrix = Matrix4x4.CreateTranslation(positionVector);

                var pitchYawRoll = VectorExtensions.ParseVector(angles);
                var rollMatrix = Matrix4x4.CreateRotationX(OpenTK.MathHelper.DegreesToRadians(pitchYawRoll.Z)); // Roll
                var pitchMatrix = Matrix4x4.CreateRotationY(OpenTK.MathHelper.DegreesToRadians(pitchYawRoll.X)); // Pitch
                var yawMatrix = Matrix4x4.CreateRotationZ(OpenTK.MathHelper.DegreesToRadians(pitchYawRoll.Y)); // Yaw

                var rotationMatrix = rollMatrix * pitchMatrix * yawMatrix;
                var transformationMatrix = scaleMatrix * rotationMatrix * positionMatrix;

                if (particle != null)
                {
                    var particleResource = guiContext.LoadFileByAnyMeansNecessary(particle + "_c");

                    if (particleResource != null)
                    {
                        var particleSystem = new ParticleSystem(particleResource);
                        var origin = new Vector3(positionVector.X, positionVector.Y, positionVector.Z);

                        var particleNode = new ParticleSceneNode(scene, particleSystem)
                        {
                            Transform = Matrix4x4.CreateTranslation(origin),
                            LayerName = layerName,
                        };
                        scene.Add(particleNode, true);
                    }

                    continue;
                }

                if (isCamera)
                {
                    var name = entity.GetProperty<string>("name") ?? string.Empty;
                    var cameraName = name == string.Empty
                        ? classname
                        : name;

                    result.CameraMatrices.Add(cameraName, transformationMatrix);

                    continue;
                }
                else if (isGlobalLight)
                {
                    result.GlobalLightPosition = positionVector;

                    continue;
                }
                else if (model == null)
                {
                    continue;
                }

                var objColor = Vector4.One;

                // Parse colour if present
                if (colour != default && colour.Length == 4)
                {
                    objColor.X = colour[0] / 255.0f;
                    objColor.Y = colour[1] / 255.0f;
                    objColor.Z = colour[2] / 255.0f;
                    objColor.W = colour[3] / 255.0f;
                }

                var newEntity = guiContext.LoadFileByAnyMeansNecessary(model + "_c");

                if (newEntity == null)
                {
                    var errorModelResource = guiContext.LoadFileByAnyMeansNecessary("models/dev/error.vmdl_c");

                    if (errorModelResource != null)
                    {
                        var errorModel = new ModelSceneNode(scene, new Model(errorModelResource), skin, false)
                        {
                            Transform = transformationMatrix,
                            LayerName = layerName,
                        };
                        scene.Add(errorModel, false);
                    }
                    else
                    {
                        Console.WriteLine("Unable to load error.vmdl_c. Did you add \"core/pak_001.dir\" to your game paths?");
                    }

                    continue;
                }

                var newModel = new Model(newEntity);

                var modelNode = new ModelSceneNode(scene, newModel, skin, false)
                {
                    Transform = transformationMatrix,
                    Tint = objColor,
                    LayerName = layerName,
                };

                if (animation != default)
                {
                    modelNode.LoadAnimation(animation); // Load only this animation
                    modelNode.SetAnimation(animation);
                }

                var bodyHash = EntityLumpKeyLookup.Get("body");
                if (entity.Properties.ContainsKey(bodyHash))
                {
                    var groups = modelNode.GetMeshGroups();
                    var body = entity.Properties[bodyHash].Data;
                    int bodyGroup = -1;

                    if (body is ulong bodyGroupLong)
                    {
                        bodyGroup = (int)bodyGroupLong;
                    }
                    else if (body is string bodyGroupString)
                    {
                        int.TryParse(bodyGroupString, out bodyGroup);
                    }

                    modelNode.SetActiveMeshGroups(groups.Skip(bodyGroup).Take(1));
                }

                scene.Add(modelNode, false);
            }
        }
    }
}

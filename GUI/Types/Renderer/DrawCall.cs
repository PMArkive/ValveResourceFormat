using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;

#nullable disable

namespace GUI.Types.Renderer
{
    class DrawCall
    {
        public PrimitiveType PrimitiveType { get; set; }
        public int BaseVertex { get; set; }
        //public uint VertexCount { get; set; }
        public nint StartIndex { get; set; } // pointer for GL call
        public int IndexCount { get; set; }
        //public float UvDensity { get; set; }     //TODO
        //public string Flags { get; set; }        //TODO
        public Vector4 TintColor { get; set; } = Vector4.One;

        public AABB? DrawBounds { get; set; }

        public int MeshId { get; set; }
        public int FirstMeshlet { get; set; }
        public int NumMeshlets { get; set; }
        public RenderMaterial Material { get; set; }

        public string MeshName { get; set; } = string.Empty;
        public int VertexArrayObject { get; set; } = -1;
        public VertexDrawBuffer[] VertexBuffers { get; set; }
        public DrawElementsType IndexType { get; set; }
        public IndexDrawBuffer IndexBuffer { get; set; }
        public int VertexIdOffset { get; set; }


        public void SetNewMaterial(RenderMaterial newMaterial)
        {
            DeleteVertexArrayObject();
            Material = newMaterial;
        }

        public void UpdateVertexArrayObject(GPUMeshBufferCache meshBuffers)
        {
            DeleteVertexArrayObject();

            VertexArrayObject = meshBuffers.GetVertexArrayObject(
                   MeshName,
                   VertexBuffers,
                   Material,
                   IndexBuffer.Handle);

#if DEBUG
            if (!string.IsNullOrEmpty(MeshName))
            {
                GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, VertexArrayObject, MeshName.Length, MeshName);
            }
#endif
        }

        public void DeleteVertexArrayObject()
        {
            if (VertexArrayObject != -1)
            {
                GL.DeleteVertexArray(VertexArrayObject);
                VertexArrayObject = -1;
            }
        }
    }

    internal struct IndexDrawBuffer
    {
        public int Handle;
        public uint Offset;
    }

    internal struct VertexDrawBuffer
    {
        public int Handle;
        public uint Offset;
        public uint ElementSizeInBytes;
        public VBIB.RenderInputLayoutField[] InputLayoutFields;
    }
}

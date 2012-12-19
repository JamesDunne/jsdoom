using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using System.Threading;
using OpenTK.Graphics;
using System.Diagnostics;
using System.IO;
using OpenTK;
using System.Drawing;

namespace jsdoom
{
    public static class StringExtensions
    {
        public static string ASCIIZ(this string zstr)
        {
            int z = zstr.IndexOf('\0');
            if (z < 0) return zstr;
            return zstr.Substring(0, z);
        }

        public static int AddReturnIndex<T>(this List<T> list, T value)
        {
            list.Add(value);
            return list.Count - 1;
        }
    }

    class MainWindow : OpenTK.GameWindow
    {
        Random rnd = new Random();

        class Frame
        {
            public Color4 bg;
        }

        Frame rendering = null;
        Frame ready = null;
        Frame next = null;
        AutoResetEvent rendered = new AutoResetEvent(false);

        Matrix4 matrixProjection, matrixModelview;
        Vector3 centerPos;
        float cameraRotation = 0f;
        const float cameraDistance = 0.55f;
        const float cameraHeight = 0.4f;

        int[] vboIds;
        bool setup;

        Vertex[] vertexes;
        LineDef[] linedefs;
        SideDef[] sidedefs;
        Sector[] sectors;
        LineSeg[] segs;
        SubSector[] subsectors;

        Quad[] quadIndices;
        Vector3[] quadVerts;

        Polygon[] floorPolys;
        int[] floorIndices;
        Vector3[] floorVerts;

        public MainWindow()
            : base()
        {
            Task.Factory.StartNew(async () => await Game());
        }

        struct Lump
        {
            public readonly string Name;
            public readonly int Position;
            public readonly int Size;
            public readonly byte[] Data;

            public Lump(string name, int pos, int size, byte[] data)
            {
                Name = name;
                Position = pos;
                Size = size;
                Data = data;
            }
        }

        enum MapLump
        {
            LABEL,		// A separator, name, ExMx or MAPxx
            THINGS,		// Monsters, items..
            LINEDEFS,	// LineDefs, from editing
            SIDEDEFS,	// SideDefs, from editing
            VERTEXES,	// Vertices, edited and BSP splits generated
            SEGS,		// LineSegs, from LineDefs split by BSP
            SSECTORS,	// SubSectors, list of LineSegs
            NODES,		// BSP nodes
            SECTORS,	// Sectors, from editing
            REJECT,		// LUT, sector-sector visibility	
            BLOCKMAP	// LUT, motion clipping, walls/grid element
        };

        static int FindLump(Lump[] lumps, string name)
        {
            // Search backwards:
            for (int i = lumps.Length - 1; i >= 0; --i)
                if (lumps[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            // Not found.
            return -1;
        }

        struct Vertex
        {
            public readonly int X, Y;

            public Vertex(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        struct Vertex3
        {
            public readonly int X, Y, Z;

            public Vertex3(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public Vertex3(Vertex v, int z)
                : this(v.X, v.Y, z)
            {
            }
        }

        struct Quad
        {
            public readonly int A, B, C, D;

            public Quad(int a, int b, int c, int d)
            {
                A = a;
                B = b;
                C = c;
                D = d;
            }
        }

        struct Polygon
        {
            public readonly int[] V;
            public readonly int StartIndex;

            public Polygon(int[] v, int startIndex)
            {
                V = v;
                StartIndex = startIndex;
            }
        }

        class Sector
        {
            public readonly int Floorheight;
            public readonly int Ceilingheight;
            public readonly string Floorpic;
            public readonly string Ceilingpic;
            public readonly int Lightlevel;
            public readonly int Special;
            public readonly int Tag;

            public Sector(int floorheight, int ceilingheight, string floorpic, string ceilingpic, int lightlevel, int special, int tag)
            {
                Floorheight = floorheight;
                Ceilingheight = ceilingheight;
                Floorpic = floorpic;
                Ceilingpic = ceilingpic;
                Lightlevel = lightlevel;
                Special = special;
                Tag = tag;
            }
        }

        class SubSector
        {
            public readonly LineSeg[] Segs;

            public SubSector(LineSeg[] segs)
            {
                Segs = segs;
            }
        }

        class SideDef
        {
            public readonly int Textureoffset;
            public readonly int Rowoffset;
            public readonly string Toptexture;
            public readonly string Bottomtexture;
            public readonly string Midtexture;
            public readonly Sector Sector;

            public SideDef(int textureoffset, int rowoffset, string toptexture, string bottomtexture, string midtexture, Sector sector)
            {
                Textureoffset = textureoffset;
                Rowoffset = rowoffset;
                Toptexture = toptexture;
                Bottomtexture = bottomtexture;
                Midtexture = midtexture;
                Sector = sector;
            }
        }

        class LineDef
        {
            public readonly int V1, V2;
            public readonly int Flags;
            public readonly int Special;
            public readonly int Tag;
            public readonly SideDef Side0, Side1;

            public LineDef(int v1, int v2, int flags, int special, int tag, SideDef side0, SideDef side1)
            {
                V1 = v1;
                V2 = v2;
                Flags = flags;
                Special = special;
                Tag = tag;
                Side0 = side0;
                Side1 = side1;
            }
        }

        class LineSeg
        {
            public readonly int V1, V2;
            public readonly int Angle;
            public readonly LineDef Line;
            public readonly SideDef Side;
            public readonly int Offset;

            public LineSeg(int v1, int v2, int angle, LineDef line, SideDef side, int offset)
            {
                V1 = v1;
                V2 = v2;
                Angle = angle;
                Line = line;
                Side = side;
                Offset = offset;
            }
        }

        const float scale = 4096f;

        static void MapCoordToVector3(int x, int y, int z, out Vector3 vec)
        {
            vec.X = x / scale;
            vec.Y = z / scale;
            vec.Z = -y / scale;
        }

        static void MapCoordToVector3(List<Vertex3> verts, int i, out Vector3 vec)
        {
            MapCoordToVector3(verts[i].X, verts[i].Y, verts[i].Z, out vec);
        }

        static void MapCoordToVector3(Vertex[] verts, int i, int z, out Vector3 vec)
        {
            MapCoordToVector3(verts[i].X, verts[i].Y, z, out vec);
        }

        async Task Game()
        {
            Lump[] lumps;

            try
            {
                using (var iwad = File.Open(@"E:\Steam\steamapps\common\DOOM 3 BFG Edition\base\wads\DOOM2.WAD", FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] rec = new byte[16];
                    await iwad.ReadAsync(rec, 0, 12);
                    int num = BitConverter.ToInt32(rec, 4);
                    int ofs = BitConverter.ToInt32(rec, 8);
                    iwad.Seek((long)ofs, SeekOrigin.Begin);

                    // Read the lump table:
                    byte[] dir = new byte[16 * num];
                    await iwad.ReadAsync(dir, 0, 16 * num);

                    lumps = new Lump[num];
                    for (int i = 0; i < num; ++i)
                    {
                        // Read the lump data from the table:
                        int position = BitConverter.ToInt32(dir, i * 16 + 0);
                        int size = BitConverter.ToInt32(dir, i * 16 + 4);
                        string name = Encoding.ASCII.GetString(dir, i * 16 + 8, 8).TrimEnd(new char[] { '\0' });

                        byte[] data;
                        if (size > 0)
                        {
                            data = new byte[size];

                            iwad.Seek((long)position, SeekOrigin.Begin);
                            await iwad.ReadAsync(data, 0, size);
                        }
                        else
                        {
                            data = null;
                        }

                        // Record the lump and its data:
                        lumps[i] = new Lump(name, position, size, data);
                    }
                }

                int ml = FindLump(lumps, "map26");

                // Read vertices:
                var vertexesLump = lumps[ml + (int)MapLump.VERTEXES];
                int numVertexes = vertexesLump.Size / (2 + 2);
                vertexes = new Vertex[numVertexes];
                for (int i = 0; i < numVertexes; ++i)
                {
                    short x = BitConverter.ToInt16(vertexesLump.Data, i * 2 * sizeof(Int16));
                    short y = BitConverter.ToInt16(vertexesLump.Data, i * 2 * sizeof(Int16) + sizeof(Int16));

                    vertexes[i] = new Vertex(x, y);
                }

                // Read sectors:
                var sectorsLump = lumps[ml + (int)MapLump.SECTORS];

                const int sectorSize = (sizeof(short) * 5) + (8 * 2);
                int numSectors = sectorsLump.Size / sectorSize;

                sectors = new Sector[numSectors];
                for (int i = 0; i < numSectors; ++i)
                {
                    short floorheight = BitConverter.ToInt16(sectorsLump.Data, (i * sectorSize) + (sizeof(short) * 0));
                    short ceilingheight = BitConverter.ToInt16(sectorsLump.Data, (i * sectorSize) + (sizeof(short) * 1));
                    string floorpic = Encoding.ASCII.GetString(sectorsLump.Data, (i * sectorSize) + 0 + (sizeof(short) * 2), 8).ASCIIZ();
                    string ceilingpic = Encoding.ASCII.GetString(sectorsLump.Data, (i * sectorSize) + 8 + (sizeof(short) * 2), 8).ASCIIZ();
                    short lightlevel = BitConverter.ToInt16(sectorsLump.Data, (i * sectorSize) + 16 + (sizeof(short) * 2));
                    short special = BitConverter.ToInt16(sectorsLump.Data, (i * sectorSize) + 16 + (sizeof(short) * 3));
                    short tag = BitConverter.ToInt16(sectorsLump.Data, (i * sectorSize) + 16 + (sizeof(short) * 4));

                    sectors[i] = new Sector(floorheight, ceilingheight, floorpic, ceilingpic, lightlevel, special, tag);
                }

                // Read sidedefs:
                var sidedefsLump = lumps[ml + (int)MapLump.SIDEDEFS];

                const int sidedefSize = (sizeof(short) * 3) + (8 * 3);
                int numSidedefs = sidedefsLump.Size / sidedefSize;

                sidedefs = new SideDef[numSidedefs];
                for (int i = 0; i < numSidedefs; ++i)
                {
                    short textureoffset = BitConverter.ToInt16(sidedefsLump.Data, (i * sidedefSize) + (sizeof(short) * 0));
                    short rowoffset = BitConverter.ToInt16(sidedefsLump.Data, (i * sidedefSize) + (sizeof(short) * 1));
                    string toptexture = Encoding.ASCII.GetString(sidedefsLump.Data, (i * sidedefSize) + 0 + (sizeof(short) * 2), 8).ASCIIZ();
                    string bottomtexture = Encoding.ASCII.GetString(sidedefsLump.Data, (i * sidedefSize) + 8 + (sizeof(short) * 2), 8).ASCIIZ();
                    string midtexture = Encoding.ASCII.GetString(sidedefsLump.Data, (i * sidedefSize) + 16 + (sizeof(short) * 2), 8).ASCIIZ();
                    // Front sector, towards viewer.
                    short sector = BitConverter.ToInt16(sidedefsLump.Data, (i * sidedefSize) + 24 + (sizeof(short) * 2));

                    sidedefs[i] = new SideDef(textureoffset, rowoffset, toptexture, bottomtexture, midtexture, sectors[sector]);
                }

                // Read linedefs:
                var linedefsLump = lumps[ml + (int)MapLump.LINEDEFS];

                const int linedefSize = sizeof(short) * 7;
                int numLineDefs = linedefsLump.Size / linedefSize;

                linedefs = new LineDef[numLineDefs];

                var verts = new List<Vertex3>(numVertexes * 6);
                var quads = new List<Quad>(numLineDefs * 3);
                for (int i = 0; i < numLineDefs; ++i)
                {
                    short v1 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 0));
                    short v2 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 1));
                    short flags = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 2));
                    short special = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 3));
                    short tag = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 4));
                    short sidenum0 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 5));
                    short sidenum1 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 6));

                    // Look up SideDefs:
                    SideDef side0, side1;
                    if (sidenum0 >= 0)
                    {
                        side0 = sidedefs[sidenum0];

                        if (sidenum1 < 0)
                        {
                            // One-sided:
                            side1 = null;

                            quads.Add(new Quad(
                                verts.AddReturnIndex(new Vertex3(vertexes[v1], side0.Sector.Floorheight)),
                                verts.AddReturnIndex(new Vertex3(vertexes[v1], side0.Sector.Ceilingheight)),
                                verts.AddReturnIndex(new Vertex3(vertexes[v2], side0.Sector.Ceilingheight)),
                                verts.AddReturnIndex(new Vertex3(vertexes[v2], side0.Sector.Floorheight))
                            ));
                        }
                        else
                        {
                            // Two-sided:
                            side1 = sidedefs[sidenum1];

                            if (side1.Sector.Floorheight < side0.Sector.Floorheight)
                            {
                                quads.Add(new Quad(
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side0.Sector.Floorheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side1.Sector.Floorheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side1.Sector.Floorheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side0.Sector.Floorheight))
                                ));
                            }
                            else if (side1.Sector.Floorheight >= side0.Sector.Floorheight)
                            {
                                quads.Add(new Quad(
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side0.Sector.Floorheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side1.Sector.Floorheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side1.Sector.Floorheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side0.Sector.Floorheight))
                                ));
                            }

                            if (side1.Sector.Ceilingheight < side0.Sector.Ceilingheight)
                            {
                                quads.Add(new Quad(
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side1.Sector.Ceilingheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side0.Sector.Ceilingheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side0.Sector.Ceilingheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side1.Sector.Ceilingheight))
                                ));
                            }
                            else if (side1.Sector.Ceilingheight >= side0.Sector.Ceilingheight)
                            {
                                quads.Add(new Quad(
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side0.Sector.Ceilingheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v1], side1.Sector.Ceilingheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side1.Sector.Ceilingheight)),
                                    verts.AddReturnIndex(new Vertex3(vertexes[v2], side0.Sector.Ceilingheight))
                                ));
                            }
                        }
                    }
                    else
                    {
                        throw new Exception("FAIL");
                    }

                    linedefs[i] = new LineDef(v1, v2, flags, special, tag, side0, side1);
                }


                // Generate vectors and quad indices for OpenGL:
                quadVerts = new Vector3[verts.Count];
                for (int i = 0; i < verts.Count; ++i)
                {
                    MapCoordToVector3(verts, i, out quadVerts[i]);
                }
                quadIndices = quads.ToArray();


                // Read segs:
                const int segSize = sizeof(short) * 6;

                var segsLump = lumps[ml + (int)MapLump.SEGS];
                int numSegs = segsLump.Size / segSize;

                segs = new LineSeg[numSegs];
                for (int i = 0; i < numSegs; ++i)
                {
                    short v1 = BitConverter.ToInt16(segsLump.Data, (i * segSize) + (sizeof(short) * 0));
                    short v2 = BitConverter.ToInt16(segsLump.Data, (i * segSize) + (sizeof(short) * 1));
                    short angle = BitConverter.ToInt16(segsLump.Data, (i * segSize) + (sizeof(short) * 2));
                    short linedef = BitConverter.ToInt16(segsLump.Data, (i * segSize) + (sizeof(short) * 3));
                    short side = BitConverter.ToInt16(segsLump.Data, (i * segSize) + (sizeof(short) * 4));
                    short offset = BitConverter.ToInt16(segsLump.Data, (i * segSize) + (sizeof(short) * 5));

                    var ldef = linedefs[linedef];
                    var sdef = ldef.Side0;
                    if (side != 0) sdef = ldef.Side1;

                    segs[i] = new LineSeg(v1, v2, angle, ldef, sdef, offset);
                }

                // Read subsectors:
                const int subsectorSize = sizeof(short) * 2;

                var subsectorsLump = lumps[ml + (int)MapLump.SSECTORS];
                var numSubsectors = subsectorsLump.Size / subsectorSize;

                subsectors = new SubSector[numSubsectors];
                for (int i = 0; i < numSubsectors; ++i)
                {
                    short numsegs = BitConverter.ToInt16(subsectorsLump.Data, (i * subsectorSize) + (sizeof(short) * 0));
                    short firstseg = BitConverter.ToInt16(subsectorsLump.Data, (i * subsectorSize) + (sizeof(short) * 1));

                    // Pull out all the LineSegs for this subsector:
                    var mysegs = new LineSeg[numsegs];
                    Array.Copy(segs, firstseg, mysegs, 0, numsegs);
                    subsectors[i] = new SubSector(mysegs);

                    // Assert that all line segs in this subsector are in the same sector:
                    var sector = subsectors[i].Segs[0].Side.Sector;
                    Debug.Assert(subsectors[i].Segs.All(s => s.Side.Sector == sector));
                }


                // Build floor polygons from subsectors:
                var fVerts = new List<Vertex3>(numVertexes);
                var fPolys = new List<Polygon>(numSubsectors);
                int fIndices = 0;
                for (int i = 0; i < numSubsectors; ++i)
                {
                    //if (subsectors[i].Segs.Length < 2) continue;

                    List<int> pind = new List<int>(subsectors[i].Segs.Length * 2);
                    for (int j = 0; j < subsectors[i].Segs.Length; ++j)
                    {
                        var seg = subsectors[i].Segs[j];
                        if (seg.Side == seg.Line.Side0)
                        {
                            pind.Add(fVerts.AddReturnIndex(new Vertex3(vertexes[seg.V1], seg.Side.Sector.Floorheight)));
                            pind.Add(fVerts.AddReturnIndex(new Vertex3(vertexes[seg.V2], seg.Side.Sector.Floorheight)));
                        }
                        else
                        {
                            pind.Add(fVerts.AddReturnIndex(new Vertex3(vertexes[seg.V2], seg.Side.Sector.Floorheight)));
                            pind.Add(fVerts.AddReturnIndex(new Vertex3(vertexes[seg.V1], seg.Side.Sector.Floorheight)));
                        }
                    }

                    fPolys.Add(new Polygon(pind.ToArray(), fIndices));
                    fIndices += pind.Count;
                }

                floorVerts = new Vector3[fVerts.Count];
                for (int i = 0; i < fVerts.Count; ++i)
                {
                    MapCoordToVector3(fVerts, i, out floorVerts[i]);
                }

                floorPolys = fPolys.ToArray();
                floorIndices = new int[fIndices];
                int n = 0;
                for (int i = 0; i < floorPolys.Length; ++i)
                {
                    var v = floorPolys[i].V;
                    Debug.Assert(floorPolys[i].StartIndex == n);
                    Array.Copy(v, 0, floorIndices, n, v.Length);
                    n += v.Length;
                }


                // Read things:
                var thingsLump = lumps[ml + (int)MapLump.THINGS];
                const int thingSize = sizeof(short) * 5;

                int numThings = thingsLump.Size / thingSize;
                for (int i = 0; i < numThings; ++i)
                {
                    short x = BitConverter.ToInt16(thingsLump.Data, (i * thingSize) + (sizeof(short) * 0));
                    short y = BitConverter.ToInt16(thingsLump.Data, (i * thingSize) + (sizeof(short) * 1));
                    short angle = BitConverter.ToInt16(thingsLump.Data, (i * thingSize) + (sizeof(short) * 2));
                    short type = BitConverter.ToInt16(thingsLump.Data, (i * thingSize) + (sizeof(short) * 3));
                    short options = BitConverter.ToInt16(thingsLump.Data, (i * thingSize) + (sizeof(short) * 4));

                    // Player #1 start:
                    if (type == 1)
                    {
                        MapCoordToVector3(x, y, 0, out centerPos);
                    }
                }

                // Game loop:
                while (true)
                {
                    // Create a new Frame to update:
                    next = new Frame();

                    // Set up the frame state to render:
                    next.bg = new Color4((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble(), 1.0f);

                    // Set this as the next frame ready to be rendered:
                    Interlocked.Exchange(ref ready, next);
                    // Wait for the frame to be rendered:
                    rendered.WaitOne(16);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Exit();
                return;
            }
        }

        protected override void OnUpdateFrame(OpenTK.FrameEventArgs e)
        {
            // Set the ready frame to render:
            Interlocked.Exchange(ref rendering, ready);
        }

        protected override void OnResize(EventArgs e)
        {
            GL.Viewport(0, 0, Width, Height);
            matrixProjection = Matrix4.CreatePerspectiveFieldOfView((float)Math.PI / 4, Width / (float)Height, 0.01f, 4.0f);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref matrixProjection);
        }

        protected override void OnRenderFrame(OpenTK.FrameEventArgs e)
        {
            if (rendering == null) return;
            if (!setup)
            {
                GL.Enable(EnableCap.PointSmooth);
                GL.Enable(EnableCap.PointSprite);
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.AlphaTest);
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);

                GL.FrontFace(FrontFaceDirection.Cw);
                //GL.CullFace(CullFaceMode.Back);
                // Don't fill back faces:
                GL.PolygonMode(MaterialFace.Back, PolygonMode.Line);

                GL.TexEnv(TextureEnvTarget.PointSprite, TextureEnvParameter.CoordReplace, 1 /* GL_TRUE */);

                GL.EnableClientState(ArrayCap.VertexArray);

                vboIds = new int[4];
                GL.GenBuffers(4, vboIds);

                GL.BindBuffer(BufferTarget.ArrayBuffer, vboIds[0]);
                GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(quadVerts.Length * Vector3.SizeInBytes), quadVerts, BufferUsageHint.StaticDraw);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, vboIds[1]);
                GL.BufferData(BufferTarget.ElementArrayBuffer, new IntPtr(quadIndices.Length * (sizeof(int) * 4)), quadIndices, BufferUsageHint.StaticDraw);

                GL.BindBuffer(BufferTarget.ArrayBuffer, vboIds[2]);
                GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(floorVerts.Length * Vector3.SizeInBytes), floorVerts, BufferUsageHint.StaticDraw);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, vboIds[3]);
                GL.BufferData(BufferTarget.ElementArrayBuffer, new IntPtr(floorIndices.Length * sizeof(int)), floorIndices, BufferUsageHint.StaticDraw);

                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                setup = true;
            }
            rendered.Set();

            cameraRotation = (cameraRotation < 360f) ? (cameraRotation + 15f * (float)e.Time) : 0f;

            //GL.ClearColor(rendering.bg);
            GL.ClearColor(Color4.Black);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            //Matrix4.CreateRotationY(cameraRotation, out matrixModelview);
            var eye = centerPos + new Vector3(cameraDistance * (float)Math.Cos(cameraRotation * Math.PI / 180.0), cameraHeight, cameraDistance * (float)Math.Sin(cameraRotation * Math.PI / 180.0));
            var up = new Vector3(0f, 1f, 0f);
            matrixModelview = Matrix4.LookAt(eye, centerPos, up);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref matrixModelview);

#if true
            GL.Color4(Color4.White);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vboIds[0]);
            GL.VertexPointer(3, VertexPointerType.Float, 0, 0);

            GL.DrawArrays(BeginMode.Points, 0, quadVerts.Length);
#endif

#if true
            // Draw transparent walls so we can see a bit more:
            var wallColor = Color4.Blue;
            wallColor.A = 0.25f;
            GL.Color4(wallColor);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, vboIds[1]);
            GL.DrawElements(BeginMode.Quads, quadIndices.Length * 4, DrawElementsType.UnsignedInt, 0);
#endif

            // Draw floor polygons:
#if true
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboIds[2]);
            GL.VertexPointer(3, VertexPointerType.Float, 0, 0);

            GL.Color4(Color4.White);

            GL.DrawArrays(BeginMode.Points, 0, floorVerts.Length);

            var floorColor = Color4.Red;
            floorColor.A = 0.25f;
            GL.Color4(floorColor);

#if false
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, vboIds[3]);
            for (int i = 0; i < 24; ++i)
            {
                GL.DrawElements(BeginMode.Lines, floorPolys[i].V.Length, DrawElementsType.UnsignedInt, floorPolys[i].V);
            }
#else
            for (int i = 0; i < floorPolys.Length; ++i)
            {
                GL.Begin(BeginMode.Lines);
                for (int j = 0; j < floorPolys[i].V.Length; ++j) {
                    GL.Vertex3(floorVerts[j + floorPolys[i].StartIndex]);
                }
                GL.End();
            }
#endif
#endif

            Context.SwapBuffers();
        }
    }
}

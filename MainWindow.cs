﻿using System;
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
        float cameraRotation = 0f;
        float cameraDistance = 0.2f;
        private int[] vboIds;
        private Vector3[] verts;
        private bool setup;
        private ushort[] lines;

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
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Exit();
                return;
            }

            int ml = FindLump(lumps, "map01");

            // Read vertices:
            var vertexesLump = lumps[ml + (int)MapLump.VERTEXES];
            int numVertexes = vertexesLump.Size / (2 + 2);
            verts = new Vector3[numVertexes];
            for (int i = 0; i < numVertexes; ++i)
            {
                verts[i].X = (float)BitConverter.ToInt16(vertexesLump.Data, i * 2 * sizeof(Int16)) / 16384.0f;
                verts[i].Z = (float)BitConverter.ToInt16(vertexesLump.Data, i * 2 * sizeof(Int16) + sizeof(Int16)) / 16384.0f;
                verts[i].Y = 0.0f;
            }

            // Read linedefs:
            var linedefsLump = lumps[ml + (int)MapLump.LINEDEFS];

            const int linedefSize = sizeof(short) * 7;
            int numLineDefs = linedefsLump.Size / linedefSize;

            lines = new ushort[numLineDefs * 2];
            for (int i = 0; i < numLineDefs; ++i)
            {
                short v1 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 0));
                short v2 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 1));
                short flags = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 2));
                //if ((flags & 128) == 128) continue;

                short special = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 3));
                short tag = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 4));
                short sidenum0 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 5));
                short sidenum1 = BitConverter.ToInt16(linedefsLump.Data, (i * linedefSize) + (sizeof(short) * 6));

                unchecked
                {
                    lines[i * 2 + 0] = (ushort)v1;
                    lines[i * 2 + 1] = (ushort)v2;
                }
            }

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

        protected override void OnUpdateFrame(OpenTK.FrameEventArgs e)
        {
            // Set the ready frame to render:
            Interlocked.Exchange(ref rendering, ready);
        }

        protected override void OnResize(EventArgs e)
        {
            GL.Viewport(0, 0, Width, Height);
            matrixProjection = Matrix4.CreatePerspectiveFieldOfView((float)Math.PI / 4, Width / (float)Height, 0.001f, 8.0f);
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

                GL.TexEnv(TextureEnvTarget.PointSprite, TextureEnvParameter.CoordReplace, 1 /* GL_TRUE */);

                GL.EnableClientState(ArrayCap.VertexArray);

                vboIds = new int[2];
                GL.GenBuffers(2, vboIds);

                GL.BindBuffer(BufferTarget.ArrayBuffer, vboIds[0]);
                GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(verts.Length * Vector3.SizeInBytes), verts, BufferUsageHint.StaticDraw);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, vboIds[1]);
                GL.BufferData(BufferTarget.ElementArrayBuffer, new IntPtr(lines.Length * sizeof(ushort)), lines, BufferUsageHint.StaticDraw);

                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                setup = true;
            }
            rendered.Set();

            //GL.ClearColor(rendering.bg);
            GL.Enable(EnableCap.DepthTest);

            GL.ClearColor(Color4.Black);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            cameraRotation = (cameraRotation < 360f) ? (cameraRotation + 0.25f * (float)e.Time) : 0f;
            Matrix4.CreateRotationY(cameraRotation, out matrixModelview);
            matrixModelview *= Matrix4.LookAt(cameraDistance, 0.15f, -cameraDistance, 0f, 0f, 0f, 0f, 1f, 0f);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref matrixModelview);

            GL.Color4(Color4.White);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vboIds[0]);
            GL.VertexPointer(3, VertexPointerType.Float, 0, 0);

            GL.DrawArrays(BeginMode.Points, 0, verts.Length);

            GL.Color4(Color4.Red);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, vboIds[1]);
            GL.DrawElements(BeginMode.Lines, lines.Length, DrawElementsType.UnsignedShort, 0);

            Context.SwapBuffers();
        }
    }
}

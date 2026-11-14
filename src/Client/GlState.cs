using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Client memory to a texture with the GL state the game may have left: saves the active unit's 2D texture binding,
/// the pixel-unpack buffer and the unpack alignment, row length and skips; sets the defaults (no buffer, alignment 4,
/// tight rows) for the upload; and puts them all back when disposed. Render thread.
/// </summary>
/// <summary>
/// A plain draw's GL state (0.5.3, WM-0040: written out by every draw): culling and blending off and a vertex array bound
/// while it lasts; puts back what it found when disposed. Render thread.
/// </summary>
public readonly struct GlDraw : IDisposable
{
    readonly bool cull, blend;
    readonly int vao;
    public GlDraw(int bind)
    {
        cull = GL.IsEnabled(EnableCap.CullFace); blend = GL.IsEnabled(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.GetInteger(GetPName.VertexArrayBinding, out vao);
        GL.BindVertexArray(bind);
    }
    public void Dispose()
    {
        GL.BindVertexArray(vao);
        if (cull) GL.Enable(EnableCap.CullFace);
        if (blend) GL.Enable(EnableCap.Blend);
    }
}

public readonly struct GlUnpack : IDisposable
{
    readonly int texture, buffer, alignment, rowLength, skipPixels, skipRows, imageHeight, skipImages;
    public GlUnpack()
    {
        GL.GetInteger(GetPName.TextureBinding2D, out texture);
        GL.GetInteger(GetPName.PixelUnpackBufferBinding, out buffer);
        GL.GetInteger(GetPName.UnpackAlignment, out alignment);
        GL.GetInteger(GetPName.UnpackRowLength, out rowLength);
        GL.GetInteger(GetPName.UnpackSkipPixels, out skipPixels);
        GL.GetInteger(GetPName.UnpackSkipRows, out skipRows);
        GL.GetInteger(GetPName.UnpackImageHeight, out imageHeight);
        GL.GetInteger(GetPName.UnpackSkipImages, out skipImages);
        if (buffer != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
        GL.PixelStore(PixelStoreParameter.UnpackImageHeight, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipImages, 0);
    }
    public void Dispose()
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        if (buffer != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, buffer);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, alignment);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, rowLength);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, skipPixels);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, skipRows);
        GL.PixelStore(PixelStoreParameter.UnpackImageHeight, imageHeight);
        GL.PixelStore(PixelStoreParameter.UnpackSkipImages, skipImages);
    }
}

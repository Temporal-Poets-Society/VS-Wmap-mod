using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>The mid-distance view's distant lights, drawn after the game's chunks (the far terrain renderer runs before
/// them) so the real terrain in front hides them too.</summary>
sealed class FarLights : IRenderer
{
    readonly FarTerrain far;
    public FarLights(FarTerrain far) { this.far = far; }
    public double RenderOrder => 0.9;
    public int RenderRange => 99999;
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage) { if (far.DrawnThisFrame) far.DrawLights(); }
    public void Dispose() { }
}

using System;
using System.IO;

using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace BlurBuddy.Rendering;

/// <summary>
/// Render thread: a premultiplied plate layer (UpscaleBuddy's UI layer, copied) over our own copy of the scene, for scene
/// and nameplates under frame generation; unblended, a copy between formats. Draws only into our textures, in a context
/// state of its own so the game's bindings and state cache stay
/// </summary>
public sealed class PlateOverlay: IDisposable
{
	private ID3D11DeviceContext1? context1;
	private ID3DDeviceContextState? state;
	private ID3D11VertexShader? vertexShader;
	private ID3D11PixelShader? pixelShader;
	private ID3D11BlendState? blendState;

	public void Draw(ID3D11DeviceContext gameContext, ID3D11RenderTargetView target, ID3D11ShaderResourceView plates, uint width, uint height,
		bool blend = true)
	{
		if (this.state == null)
			this.CreateResources(gameContext);

		ID3D11DeviceContext1 context = this.context1!;
		ID3DDeviceContextState previous = context.SwapDeviceContextState(this.state!);
		context.OMSetRenderTargets(target, null);
		context.OMSetBlendState(blend ? this.blendState : null);
		context.RSSetViewport(0, 0, width, height, 0, 1);
		context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		context.VSSetShader(this.vertexShader);
		context.PSSetShader(this.pixelShader);
		context.PSSetShaderResource(0, plates);
		context.Draw(3, 0);

		// Bindings held in an inactive state would keep the textures referenced
		context.PSUnsetShaderResource(0);
		context.UnsetRenderTargets();
		context.SwapDeviceContextState(previous).Dispose();
		previous.Dispose();
	}

	private void CreateResources(ID3D11DeviceContext gameContext)
	{
		ID3D11Device device = gameContext.Device; // cached by the wrapper, released when it is repointed
		using ID3D11Device1 device1 = device.QueryInterface<ID3D11Device1>();
		this.context1 = gameContext.QueryInterface<ID3D11DeviceContext1>();
		this.vertexShader = device.CreateVertexShader(Load("layer_vs"));
		this.pixelShader = device.CreatePixelShader(Load("layer_ps"));
		this.blendState = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
		// Has to match a single threaded device
		CreateDeviceContextStateFlags flags = (device.CreationFlags & DeviceCreationFlags.Singlethreaded) != 0
			? CreateDeviceContextStateFlags.Singlethreaded
			: CreateDeviceContextStateFlags.None;
		this.state = device1.CreateDeviceContextState<ID3D11Device>(flags, [device.FeatureLevel], out _);
	}

	private static byte[] Load(string name)
	{
		using Stream stream = typeof(PlateOverlay).Assembly.GetManifestResourceStream($"Shaders.{name}.cso") ??
		                      throw new InvalidOperationException($"missing resource Shaders.{name}.cso");
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	public void Dispose()
	{
		this.blendState?.Dispose();
		this.pixelShader?.Dispose();
		this.vertexShader?.Dispose();
		this.state?.Dispose();
		this.context1?.Dispose();
	}
}

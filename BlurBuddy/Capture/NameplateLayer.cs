using System;
using System.IO;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.Interop;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace BlurBuddy.Capture;

/// <summary>
/// Nameplates are one bind of the final target with the display depth buffer, after the post effects and before the 2D UI
/// (view 30, keys 0xCE/0xCF..., ffxiv_rev nameplate-layer-handoff). That bind gets our layer as render target 0 with the depth
/// kept, so the plates land in it occluded exactly as on screen; their blends are rewritten to leave it premultiplied.
/// Right before the 2D UI's bind (a callback queued in front of it) the scene is saved and the layer drawn back over the
/// 2D UI's target, so the plates go wherever the UI goes and the player's screen is unchanged.
/// With UpscaleBuddy (frame generation) the UI's target is its UI layer: whichever plugin's hook runs first, the plate bind
/// shows the final target or that layer, and the plates stay off the scene it interpolates.
/// </summary>
public sealed unsafe class NameplateLayer: IDisposable
{
	// RenderTargetManager.Initialize's targets: an RTV and an SRV, allocation class 3
	private const TextureFlags LayerFlags = TextureFlags.TextureRenderTarget | TextureFlags.ReadWrite | TextureFlags.UserManaged;
	private const uint AllocationClass = 3;

	private delegate void SetTargetDelegate(ImmediateContext* context, RenderCommandSetTarget* command);

	private delegate void SetBlendStateDelegate(ImmediateContext* context, uint state);

	private static NameplateLayer? instance;

	private readonly Hook<SetTargetDelegate> setTargetHook;
	private readonly Hook<SetBlendStateDelegate> setBlendStateHook;

	// Main thread at the UI pass: the final target (as the gate picks it, see UiCapture.FinalTarget) and the layer of its size
	private volatile nint finalTarget;
	private volatile nint uiTarget; // what the 2D UI's bind renders into: the final target, or another plugin's UI layer
	// Per frame final and UI targets and the set for the flush, a ring like FrameSlots: only compared through uiTarget, drawn
	// into from here; the set is the frame's, the render thread runs frames behind a change
	private const int FlushSlots = 4;
	private const int FlushArguments = 3;
	private readonly nint* flushTargets = (nint*)NativeMemory.AllocZeroed(FlushSlots * FlushArguments, (nuint)sizeof(nint));
	private int nextFlush;
	private volatile nint layer;
	private volatile bool stopped;
	private (uint, uint) failedSize;

	// Render thread
	private nint drawn; // layer the plates went into, until consumed
	private nint cleared; // layer cleared since it was last drawn back; a new one has undefined content
	private bool pending; // plates drawn, not yet back on the final target
	private readonly ID3D11DeviceContext gameContext = new(0);
	private readonly ID3D11RenderTargetView targetView = new(0);
	private readonly ID3D11ShaderResourceView layerView = new(0);
	private ID3D11DeviceContext1? context1;
	private ID3DDeviceContextState? state;
	private ID3D11VertexShader? vertexShader;
	private ID3D11PixelShader? pixelShader;
	private ID3D11BlendState? blendState;

	public NameplateLayer()
	{
		instance = this;
		this.setTargetHook = Service.GameInteropProvider.HookFromAddress<SetTargetDelegate>(
			(nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand, this.SetTargetDetour);
		this.setBlendStateHook = Service.GameInteropProvider.HookFromAddress<SetBlendStateDelegate>(
			(nint)ImmediateContext.MemberFunctionPointers.SetBlendState, this.SetBlendStateDetour);
		this.setTargetHook.Enable();
		this.setBlendStateHook.Enable();
	}

	/// <summary>Render thread: the scene is in the final target, before the plates are drawn back; with the frame's set</summary>
	public Action<nint, CaptureSet>? SceneReady { get; set; }

	/// <summary>Render thread: the layer with this frame's plates, 0 when none were drawn; valid until the next frame's plates</summary>
	public nint Drawn => this.drawn;

	/// <summary>Main thread, AtkServer.Draw: the layer for this final target, recreated when its size changes</summary>
	public void Prepare(KernelTexture* final)
	{
		if (this.stopped || final == null)
		{
			this.finalTarget = 0;
			return;
		}

		KernelTexture* current = (KernelTexture*)this.layer;
		if (current == null || current->ActualWidth != final->ActualWidth || current->ActualHeight != final->ActualHeight)
		{
			// The game releases it once the frames using it are done
			this.layer = 0;
			if (current != null)
				current->DecRef();

			current = KernelTexture.CreateTexture2D((int)final->ActualWidth, (int)final->ActualHeight, 1, TextureFormat.B8G8R8A8_UNORM,
				LayerFlags, AllocationClass);
			if (current == null && this.failedSize != (final->ActualWidth, final->ActualHeight))
			{
				this.failedSize = (final->ActualWidth, final->ActualHeight);
				Service.PluginLog.Error($"Nameplate layer {final->ActualWidth}x{final->ActualHeight} couldn't be created, plates stay on the scene");
			}

			this.layer = (nint)current;
		}

		this.finalTarget = (nint)final;
	}

	/// <summary>
	/// Main thread, at the 2D UI's bind (its target is RTM+0x570 during AtkServer.Draw): the argument of the
	/// <see cref="OnUiBegin"/> callback queued in front of it
	/// </summary>
	public nint FlushArgument(KernelTexture* final, KernelTexture* ui, CaptureSet set)
	{
		nint* targets = this.flushTargets + (this.nextFlush * FlushArguments);
		this.nextFlush = (this.nextFlush + 1) % FlushSlots;
		targets[0] = (nint)final;
		targets[1] = (nint)ui;
		targets[2] = (nint)set;
		this.uiTarget = (nint)ui;
		return (nint)targets;
	}

	[UnmanagedCallersOnly]
	public static void OnUiBegin(nint targets)
	{
		try
		{
			instance?.Flush(((nint*)targets)[0], ((nint*)targets)[1], (CaptureSet)((nint*)targets)[2]);
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "BlurBuddy nameplate layer failed");
		}
	}

	/// <summary>Render thread, before the 2D UI: scene saved, plates drawn back over it</summary>
	public void Flush(nint final, nint ui, CaptureSet set)
	{
		if (this.stopped || final == 0)
			return;

		this.Frame++;
		this.SceneReady?.Invoke(final, set);
		if (this.pending)
		{
			// Every capture this frame reads it, the next frame's plate bind clears it
			this.pending = false;
			this.cleared = 0;
			KernelTexture* target = (KernelTexture*)(ui != 0 ? ui : final);
			this.Draw(RenderTargetView(target), target->ActualWidth, target->ActualHeight, (KernelTexture*)this.drawn);
		}
		else
		{
			this.drawn = 0; // no plates this frame
		}
	}

	/// <summary>Render thread, from <see cref="SceneReady"/>: this frame's plates over another render target of their size</summary>
	public void DrawOver(nint renderTargetView, uint width, uint height)
	{
		if (this.pending)
			this.Draw(renderTargetView, width, height, (KernelTexture*)this.drawn);
	}

	/// <summary>Render thread: counts UI passes, captures with the same value ran in one frame</summary>
	public uint Frame { get; private set; }

	/// <summary>Render thread: plates drawn but no 2D UI bind came after them</summary>
	public void FlushPending(nint final, CaptureSet set)
	{
		if (this.pending)
			this.Flush(final, final, set); // no UI pass, no UI target
	}

	/// <summary>Render thread: the plate bind draws into the layer, the depth stays bound so the occlusion is unchanged</summary>
	private void SetTargetDetour(ImmediateContext* context, RenderCommandSetTarget* command)
	{
		nint final = this.finalTarget;
		nint ui = this.uiTarget;
		nint layer = this.layer;
		KernelTexture** target = (KernelTexture**)command->RenderTargets.GetPointer(0);
		KernelTexture* bound = target[0];
		bool plates = final != 0 && ((nint)bound == final || (ui != 0 && (nint)bound == ui)) && command->DepthBuffer != null;
		if (this.stopped || layer == 0 || !plates)
		{
			this.setTargetHook.Original(context, command);
			return;
		}

		if (this.cleared != layer)
		{
			this.Clear((KernelTexture*)layer);
			this.cleared = layer;
		}

		this.drawn = layer;
		this.pending = true;
		target[0] = (KernelTexture*)layer;
		this.setTargetHook.Original(context, command);
		target[0] = bound;
	}

	/// <summary>Render thread, every draw state change: blends into the layer accumulate coverage</summary>
	private void SetBlendStateDetour(ImmediateContext* context, uint state)
	{
		nint layer = this.layer;
#pragma warning disable CS0618 // render target 0, set by the bind; ClientStructs has no current render targets yet
		if (layer != 0 && (nint)context->BackBufferReference == layer)
#pragma warning restore CS0618
			state = Premultiplied(state);

		this.setBlendStateHook.Original(context, state);
	}

	/// <summary>
	/// Layer = colour + layer * (1 - coverage) for every blend the plates use, so it composites as premultiplied alpha.
	/// Coverage follows the colour's destination term: kept for additive, replaced for opaque, (1 - src alpha) otherwise.
	/// The kernel caches blend states by key, rewritten keys get their own D3D state.
	/// </summary>
	private static uint Premultiplied(uint key)
	{
		PackedBlendStateDesc desc = *(PackedBlendStateDesc*)&key;
		if (!desc.BlendEnable)
		{
			desc.BlendEnable = true;
			desc.BlendOp = (byte)BlendOperation.Add;
			desc.SrcBlend = (byte)Blend.One;
			desc.DestBlend = (byte)Blend.Zero;
		}

		bool additive = desc.DestBlend == (byte)Blend.One;
		bool replace = desc.DestBlend == (byte)Blend.Zero;
		desc.BlendOpAlpha = (byte)BlendOperation.Add;
		desc.SrcBlendAlpha = (byte)(additive ? Blend.Zero : Blend.One);
		desc.DestBlendAlpha = (byte)(additive ? Blend.One : replace ? Blend.Zero : Blend.InverseSourceAlpha);
		desc.RenderTargetWriteMask |= (byte)ColorWriteEnable.Alpha;
		return *(uint*)&desc;
	}

	private void Clear(KernelTexture* texture)
	{
		this.gameContext.NativePointer = (nint)Device.Instance()->D3D11DeviceContext;
		this.targetView.NativePointer = (nint)texture->GetMipRenderTarget(0, 0)->D3D11RenderTargetViewOrDepthStencilView;
		try
		{
			this.gameContext.ClearRenderTargetView(this.targetView, new Color4(0, 0, 0, 0));
		}
		finally
		{
			this.targetView.NativePointer = 0;
			this.gameContext.NativePointer = 0;
		}
	}

	/// <summary>Layer over the target, premultiplied; in a context state of its own so the game's bindings and state cache stay</summary>
	private static nint RenderTargetView(KernelTexture* texture) => (nint)texture->GetMipRenderTarget(0, 0)->D3D11RenderTargetViewOrDepthStencilView;

	private void Draw(nint renderTargetView, uint width, uint height, KernelTexture* layer)
	{
		if (layer == null || renderTargetView == 0 || width != layer->ActualWidth || height != layer->ActualHeight)
			return;

		this.gameContext.NativePointer = (nint)Device.Instance()->D3D11DeviceContext;
		this.targetView.NativePointer = renderTargetView;
		this.layerView.NativePointer = (nint)layer->D3D11ShaderResourceView;
		try
		{
			if (this.state == null)
				this.CreateResources();

			ID3D11DeviceContext1 context = this.context1!;
			ID3DDeviceContextState previous = context.SwapDeviceContextState(this.state!);
			context.OMSetRenderTargets(this.targetView, null);
			context.OMSetBlendState(this.blendState);
			context.RSSetViewport(0, 0, width, height, 0, 1);
			context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
			context.VSSetShader(this.vertexShader);
			context.PSSetShader(this.pixelShader);
			context.PSSetShaderResource(0, this.layerView);
			context.Draw(3, 0);

			// Bindings held in an inactive state would keep the targets referenced, through ResizeBuffers too
			context.PSUnsetShaderResource(0);
			context.UnsetRenderTargets();
			context.SwapDeviceContextState(previous).Dispose();
			previous.Dispose();
		}
		finally
		{
			this.layerView.NativePointer = 0;
			this.targetView.NativePointer = 0;
			this.gameContext.NativePointer = 0;
		}
	}

	private void CreateResources()
	{
		ID3D11Device device = this.gameContext.Device; // cached by the wrapper, released when it is repointed
		using ID3D11Device1 device1 = device.QueryInterface<ID3D11Device1>();
		this.context1 = this.gameContext.QueryInterface<ID3D11DeviceContext1>();
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
		using Stream stream = typeof(NameplateLayer).Assembly.GetManifestResourceStream($"Shaders.{name}.cso") ??
		                      throw new InvalidOperationException($"missing resource Shaders.{name}.cso");
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	/// <summary>Framework thread: no redirects from here, queued callbacks do nothing; then wait for the render thread before Dispose</summary>
	public void Stop()
	{
		this.stopped = true;
		this.finalTarget = 0;
	}

	public void Dispose()
	{
		this.stopped = true;
		this.setTargetHook.Dispose();
		this.setBlendStateHook.Dispose();
		KernelTexture* current = (KernelTexture*)this.layer;
		this.layer = 0;
		if (current != null)
			current->DecRef();

		this.blendState?.Dispose();
		this.pixelShader?.Dispose();
		this.vertexShader?.Dispose();
		this.state?.Dispose();
		this.context1?.Dispose();
		NativeMemory.Free(this.flushTargets);
		instance = null;
	}
}

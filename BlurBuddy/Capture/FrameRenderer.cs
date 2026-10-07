using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using Sheep.OBSHookLibrary.Devices;

using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlurBuddy.Capture;

/// <summary>
/// Render thread: copies the final target, blurs, writes the shared output texture
/// </summary>
public sealed unsafe class FrameRenderer: IDisposable
{
	private static FrameRenderer? instance;

	private readonly object resourceLock = new();
	// Game owned, pointed at per use and cleared after: the wrappers' finalizers would release them
	private readonly ID3D11DeviceContext gameContext = new(0);
	private readonly ID3D11Texture2D gameTexture = new(0);
	private Texture2DDescription captureDesc;
	private ID3D11Texture2D? capture;
	private ID3D11ShaderResourceView? captureSrv;
	private ID3D11Texture2D? output;
	private ID3D11UnorderedAccessView? outputUav;
	private ID3D11ShaderResourceView? outputSrv;

	public FrameRenderer() => instance = this;

	private readonly Sheep.OBSHookLibrary.Capture obs = new();
	private D3D11GraphicsDevice? obsDevice;
	private D3D11GraphicsTexture? obsTexture;

	public string ObsStatus { get; private set; } = "Waiting for OBS";

	/// <summary>Raw capture until a composite writes the output</summary>
	public nint PreviewSrv => (this.Composite == null ? this.captureSrv : this.outputSrv)?.NativePointer ?? 0;

	public uint OutputWidth => this.captureDesc.Width;

	public uint OutputHeight => this.captureDesc.Height;

	/// <summary>Context, capture SRV, output UAV, width, height, frame slot</summary>
	public Action<ID3D11DeviceContext, ID3D11ShaderResourceView, ID3D11UnorderedAccessView, uint, uint, nint>? Composite { get; set; }

	[UnmanagedCallersOnly]
	public static void OnRender(nint slot)
	{
		try
		{
			instance?.Render(slot);
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "BlurBuddy render callback failed");
		}
	}

	private void Render(nint slot)
	{
		// The render thread can see the next frame's target already, use the one the UI pass bound
		Texture* source = (Texture*)((FrameSlot*)slot)->Target;
		if (source == null || source->D3D11Texture2D == null)
			return;

		lock (this.resourceLock)
		{
			this.gameContext.NativePointer = (nint)Device.Instance()->D3D11DeviceContext;
			this.gameTexture.NativePointer = (nint)source->D3D11Texture2D;
			try
			{
				this.EnsureTextures(this.gameTexture);
				this.gameContext.CopyResource(this.capture, this.gameTexture);
				if (this.Composite != null)
					this.Composite(this.gameContext, this.captureSrv!, this.outputUav!, this.captureDesc.Width, this.captureDesc.Height, slot);
				else
					this.gameContext.CopyResource(this.output, this.capture);
				this.PresentToObs();
			}
			finally
			{
				this.gameContext.NativePointer = 0;
				this.gameTexture.NativePointer = 0;
			}
		}
	}

	private void EnsureTextures(ID3D11Texture2D source)
	{
		Texture2DDescription desc = source.Description;
		if (this.capture != null && desc.Width == this.captureDesc.Width && desc.Height == this.captureDesc.Height &&
		    desc.Format == this.captureDesc.Format)
			return;

		this.ReleaseTextures();
		using ID3D11Device device = source.Device;
		this.capture = device.CreateTexture2D(new Texture2DDescription(desc.Format, desc.Width, desc.Height, 1, 1, BindFlags.ShaderResource));
		this.captureSrv = device.CreateShaderResourceView(this.capture);
		// RGBA: typed UAV stores on BGRA are optional in D3D11
		this.output = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, desc.Width, desc.Height, 1, 1,
			BindFlags.ShaderResource | BindFlags.UnorderedAccess));
		this.outputUav = device.CreateUnorderedAccessView(this.output);
		this.outputSrv = device.CreateShaderResourceView(this.output);
		this.captureDesc = desc;
	}

	/// <summary>Render thread, takes the capture from OBS's own hook if it is loaded</summary>
	private void PresentToObs()
	{
		if (!this.obs.TryInit(takeOver: true))
			return;

		if (this.obsDevice == null)
		{
			using ID3D11Device device = this.gameContext.Device;
			this.obsDevice = new D3D11GraphicsDevice(device.NativePointer); // takes its own reference
		}

		this.obsTexture ??= new D3D11GraphicsTexture(this.output!); // takes its own reference
		this.obs.Present(this.obsDevice, this.obsTexture, (nint)Device.Instance()->hWnd);
		this.ObsStatus = !this.obs.IsCapturing
			? "Waiting for OBS"
			: this.obs.UsesSharedMemory
				? "Streaming to OBS (compatibility mode, shared memory)"
				: this.obs.TookOver ? "Streaming to OBS (took over OBS's capture)" : "Streaming to OBS";
	}

	/// <summary>Framework thread after queued callbacks finished; OBS's own hook takes the capture back</summary>
	public void StopObs()
	{
		lock (this.resourceLock)
		{
			this.obs.Dispose();
			this.obsTexture?.Dispose();
			this.obsTexture = null;
			this.obsDevice?.Dispose();
			this.obsDevice = null;
		}
	}

	private void ReleaseTextures()
	{
		// A resized output is wrapped again, the library starts a new capture on size change
		this.obsTexture?.Dispose();
		this.obsTexture = null;
		this.outputSrv?.Dispose();
		this.outputUav?.Dispose();
		this.output?.Dispose();
		this.captureSrv?.Dispose();
		this.capture?.Dispose();
		this.outputSrv = null;
		this.outputUav = null;
		this.output = null;
		this.captureSrv = null;
		this.capture = null;
		this.captureDesc = default;
	}

	public void Dispose()
	{
		lock (this.resourceLock)
			this.ReleaseTextures();
		instance = null;
	}
}

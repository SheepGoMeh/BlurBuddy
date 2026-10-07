using System;
using System.Runtime.InteropServices;

using BlurBuddy.Graphics;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using Sheep.OBSHookLibrary.Devices;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace BlurBuddy.Capture;

/// <summary>
/// Render thread: copies the final target, blurs, writes the shared output texture
/// </summary>
public sealed unsafe class FrameRenderer: IDisposable
{
	private static FrameRenderer? instance;

	private readonly object resourceLock = new();
	private nint capture;
	private nint captureSrv;
	private nint output;
	private nint outputUav;
	private D3D11.Texture2DDesc captureDesc;

	public FrameRenderer() => instance = this;

	private readonly Sheep.OBSHookLibrary.Capture obs = new();
	private D3D11GraphicsDevice? obsDevice;
	private D3D11GraphicsTexture? obsTexture;

	public string ObsStatus { get; private set; } = "Waiting for OBS";

	public nint OutputSrv { get; private set; }

	/// <summary>Raw capture until a composite writes the output</summary>
	public nint PreviewSrv => this.Composite == null ? this.captureSrv : this.OutputSrv;

	public uint OutputWidth => this.captureDesc.Width;

	public uint OutputHeight => this.captureDesc.Height;

	/// <summary>Context, capture SRV, output UAV, width, height, frame slot</summary>
	public Action<nint, nint, nint, uint, uint, nint>? Composite { get; set; }

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

		nint context = (nint)Device.Instance()->D3D11DeviceContext;
		nint sourceTexture = (nint)source->D3D11Texture2D;
		lock (this.resourceLock)
		{
			this.EnsureTextures(sourceTexture);
			D3D11.CopyResource(context, this.capture, sourceTexture);
			if (this.Composite != null)
				this.Composite(context, this.captureSrv, this.outputUav, this.captureDesc.Width, this.captureDesc.Height, slot);
			else
				D3D11.CopyResource(context, this.output, this.capture);
			this.PresentToObs(sourceTexture);
		}
	}

	private void EnsureTextures(nint source)
	{
		D3D11.Texture2DDesc desc = D3D11.GetDesc(source);
		if (this.capture != 0 && desc.Width == this.captureDesc.Width && desc.Height == this.captureDesc.Height &&
		    desc.Format == this.captureDesc.Format)
			return;

		this.ReleaseTextures();
		nint device = D3D11.GetDevice(source);
		try
		{
			this.capture = D3D11.CreateTexture2D(device, desc.Width, desc.Height, desc.Format, D3D11.BindShaderResource, 0);
			this.captureSrv = D3D11.CreateSrv(device, this.capture);
			// RGBA: typed UAV stores on BGRA are optional in D3D11
			this.output = D3D11.CreateTexture2D(device, desc.Width, desc.Height, D3D11.FormatR8G8B8A8Unorm,
				D3D11.BindShaderResource | D3D11.BindUnorderedAccess, 0);
			this.outputUav = D3D11.CreateUav(device, this.output, D3D11.FormatR8G8B8A8Unorm);
			this.OutputSrv = D3D11.CreateSrv(device, this.output);
			this.captureDesc = desc;
		}
		finally
		{
			D3D11.Release(device);
		}
	}

	/// <summary>Render thread, takes the capture from OBS's own hook if it is loaded</summary>
	private void PresentToObs(nint sourceTexture)
	{
		if (!this.obs.TryInit(takeOver: true))
			return;

		if (this.obsDevice == null)
		{
			nint device = D3D11.GetDevice(sourceTexture);
			this.obsDevice = new D3D11GraphicsDevice(device); // takes its own reference
			D3D11.Release(device);
		}

		if (this.obsTexture == null)
		{
			using ID3D11Texture2D output = new(this.output);
			output.AddRef(); // balanced by the using, the wrapper keeps its own reference
			this.obsTexture = new D3D11GraphicsTexture(output);
		}

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
		D3D11.Release(this.OutputSrv);
		D3D11.Release(this.outputUav);
		D3D11.Release(this.output);
		D3D11.Release(this.captureSrv);
		D3D11.Release(this.capture);
		this.OutputSrv = this.outputUav = this.output = this.captureSrv = this.capture = 0;
		this.captureDesc = default;
	}

	public void Dispose()
	{
		lock (this.resourceLock)
			this.ReleaseTextures();
		instance = null;
	}
}

using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using BlurBuddy.Rendering;
using BlurBuddy.Tracking;

using Sheep.OBSHookLibrary.Devices;

using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BlurBuddy.Capture;

/// <summary>
/// Render thread: copies of the frame where each set needs it, blurred, into the shared output texture. Only read from the
/// game: the scene at the plate bind, the scene with the plates (or another plugin's plate layer) at the 2D UI's bind, the
/// finished frame after ToneAdjust
/// </summary>
public sealed unsafe class FrameRenderer: IDisposable
{
	/// <summary>Where a frame's plates are: unknown (whole rectangles), scene against plated, or the plate layer</summary>
	public enum Coverage: uint
	{
		None,
		Difference,
		Layer,
	}

	public delegate void CompositeDelegate(ID3D11DeviceContext context, ID3D11ShaderResourceView capture, Coverage coverage,
		ID3D11ShaderResourceView? first, ID3D11ShaderResourceView? second, ID3D11UnorderedAccessView output, uint width, uint height,
		nint slot);

	private static FrameRenderer? instance;

	private readonly object resourceLock = new();
	// Game owned, pointed at per use and cleared after: the wrappers' finalizers would release them
	private readonly ID3D11DeviceContext gameContext = new(0);
	private readonly ID3D11Texture2D gameTexture = new(0);
	private readonly PlateCapture plates;
	private readonly PlateOverlay overlay = new();
	private uint savedFrame = uint.MaxValue; // UI pass the copies below are of
	private Coverage coverage; // how that pass's plates are found
	private Texture2DDescription captureDesc;
	private ID3D11Texture2D? capture; // what the set shows, blurred into the output
	private ID3D11ShaderResourceView? captureSrv;
	private ID3D11RenderTargetView? captureRtv;
	private ID3D11Texture2D? scene; // at the plate bind: no plates, no UI
	private ID3D11ShaderResourceView? sceneSrv;
	private ID3D11Texture2D? plated; // at the 2D UI's bind: the scene with the plates
	private ID3D11ShaderResourceView? platedSrv;
	private Texture2DDescription layerDesc;
	private ID3D11Texture2D? layerCopy; // another plugin's UI layer at the 2D UI's bind: only the plates, premultiplied
	private ID3D11ShaderResourceView? layerSrv;
	private ID3D11Texture2D? output;
	private ID3D11UnorderedAccessView? outputUav;
	private ID3D11ShaderResourceView? outputSrv;

	public FrameRenderer(PlateCapture plates)
	{
		this.plates = plates;
		plates.BeforePlates = this.TakeScene;
		plates.AtUi = this.TakePlates;
		instance = this;
	}

	private readonly Sheep.OBSHookLibrary.Capture obs = new();
	private D3D11GraphicsDevice? obsDevice;
	private D3D11GraphicsTexture? obsTexture;
	private VkCapture? vulkan;

	public string ObsStatus { get; private set; } = "Waiting for OBS";

	public string? VulkanStatus => this.vulkan?.Status;

	/// <summary>Raw capture until a composite writes the output</summary>
	public nint PreviewSrv => (this.Composite == null ? this.captureSrv : this.outputSrv)?.NativePointer ?? 0;

	public uint OutputWidth => this.captureDesc.Width;

	public uint OutputHeight => this.captureDesc.Height;

	public CompositeDelegate? Composite { get; set; }

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
		FrameSlot* frame = (FrameSlot*)slot;
		Texture* source = (Texture*)frame->Target;
		if (source == null || source->D3D11Texture2D == null)
			return;

		lock (this.resourceLock)
		{
			this.Bind(source);
			try
			{
				this.EnsureTextures(this.gameTexture);
				// Copies of this frame, else (UI pass skipped) only the finished frame, plates in whole rectangles
				bool copied = frame->AfterUiBind != 0 && this.savedFrame == this.plates.Frame;
				Coverage coverage = copied ? this.coverage : Coverage.None;
				switch (copied ? frame->Set : CaptureSet.Ui)
				{
					case CaptureSet.None:
						this.gameContext.CopyResource(this.capture, this.scene);
						break;
					case CaptureSet.Nameplates when coverage == Coverage.Layer:
						this.gameContext.CopyResource(this.capture, this.scene);
						this.overlay.Draw(this.gameContext, this.captureRtv!, this.layerSrv!, this.captureDesc.Width, this.captureDesc.Height);
						break;
					case CaptureSet.Nameplates:
						this.gameContext.CopyResource(this.capture, this.plated);
						break;
					default:
						this.gameContext.CopyResource(this.capture, this.gameTexture);
						break;
				}

				// The copies have no UI: the loading indicator from the finished frame
				if (copied && frame->Set != CaptureSet.Ui && !frame->Indicator.IsEmpty)
				{
					ScreenRect r = frame->Indicator;
					this.gameContext.CopySubresourceRegion(this.capture, 0, (uint)r.Left, (uint)r.Top, 0, this.gameTexture, 0,
						new Box((int)r.Left, (int)r.Top, 0, (int)r.Right, (int)r.Bottom, 1));
				}

				if (this.Composite != null)
				{
					this.Composite(this.gameContext, this.captureSrv!, coverage,
						coverage == Coverage.Layer ? this.layerSrv : this.sceneSrv, this.platedSrv, this.outputUav!,
						this.captureDesc.Width, this.captureDesc.Height, slot);
				}
				else
				{
					this.gameContext.CopyResource(this.output, this.capture);
				}

				this.PresentToObs();
			}
			finally
			{
				this.Unbind();
			}
		}
	}

	private void Bind(Texture* texture)
	{
		this.gameContext.NativePointer = (nint)Device.Instance()->D3D11DeviceContext;
		this.gameTexture.NativePointer = (nint)texture->D3D11Texture2D;
	}

	private void Unbind()
	{
		this.gameContext.NativePointer = 0;
		this.gameTexture.NativePointer = 0;
	}

	/// <summary>Render thread, at the plate bind: the scene, before the game draws the plates</summary>
	private void TakeScene(nint final)
	{
		Texture* texture = (Texture*)final;
		if (texture->D3D11Texture2D == null)
			return;

		lock (this.resourceLock)
		{
			this.Bind(texture);
			try
			{
				this.EnsureTextures(this.gameTexture);
				this.gameContext.CopyResource(this.scene, this.gameTexture);
			}
			finally
			{
				this.Unbind();
			}
		}
	}

	/// <summary>
	/// Render thread, before the 2D UI: the plates are drawn. On the final target it's the scene with them (the scene alone
	/// when no plate bind came); in another plugin's UI layer (UpscaleBuddy) that layer holds only them and the final target
	/// only the scene
	/// </summary>
	private void TakePlates(nint final, nint ui, bool sceneTaken)
	{
		Texture* texture = (Texture*)final;
		Texture* layer = (Texture*)ui;
		if (texture->D3D11Texture2D == null)
			return;

		lock (this.resourceLock)
		{
			this.Bind(texture);
			try
			{
				this.EnsureTextures(this.gameTexture);
				if (!sceneTaken)
					this.gameContext.CopyResource(this.scene, this.gameTexture);

				if (layer == null || layer == texture || layer->D3D11Texture2D == null)
				{
					this.gameContext.CopyResource(this.plated, this.gameTexture);
					this.coverage = Coverage.Difference;
				}
				else
				{
					this.gameTexture.NativePointer = (nint)layer->D3D11Texture2D;
					this.coverage = this.EnsureLayer(this.gameTexture) ? Coverage.Layer : Coverage.None;
					if (this.coverage == Coverage.Layer)
						this.gameContext.CopyResource(this.layerCopy, this.gameTexture);
				}

				this.savedFrame = this.plates.Frame;
			}
			finally
			{
				this.Unbind();
			}
		}
	}

	/// <summary>A copy of the other plugin's layer, false when it isn't the frame's size</summary>
	private bool EnsureLayer(ID3D11Texture2D source)
	{
		Texture2DDescription desc = source.Description;
		if (desc.Width != this.captureDesc.Width || desc.Height != this.captureDesc.Height)
			return false;
		if (this.layerCopy != null && desc.Format == this.layerDesc.Format)
			return true;

		this.layerSrv?.Dispose();
		this.layerCopy?.Dispose();
		ID3D11Device device = source.Device; // cached by the wrapper, released when it is repointed
		this.layerCopy = device.CreateTexture2D(new Texture2DDescription(Typed(desc.Format), desc.Width, desc.Height, 1, 1,
			BindFlags.ShaderResource));
		this.layerSrv = device.CreateShaderResourceView(this.layerCopy);
		this.layerDesc = desc;
		return true;
	}

	private void EnsureTextures(ID3D11Texture2D source)
	{
		Texture2DDescription desc = source.Description;
		if (this.capture != null && desc.Width == this.captureDesc.Width && desc.Height == this.captureDesc.Height &&
		    desc.Format == this.captureDesc.Format)
			return;

		this.vulkan?.SetSource(0);
		this.ReleaseTextures();
		ID3D11Device device = source.Device; // cached by the wrapper, released when it is repointed
		Format format = Typed(desc.Format);
		if (format != desc.Format)
			Service.PluginLog.Information($"Final target is {desc.Format}, copied as {format}");
		this.capture = device.CreateTexture2D(new Texture2DDescription(format, desc.Width, desc.Height, 1, 1,
			BindFlags.ShaderResource | BindFlags.RenderTarget));
		this.captureSrv = device.CreateShaderResourceView(this.capture);
		this.captureRtv = device.CreateRenderTargetView(this.capture);
		this.scene = device.CreateTexture2D(new Texture2DDescription(format, desc.Width, desc.Height, 1, 1, BindFlags.ShaderResource));
		this.sceneSrv = device.CreateShaderResourceView(this.scene);
		this.plated = device.CreateTexture2D(new Texture2DDescription(format, desc.Width, desc.Height, 1, 1, BindFlags.ShaderResource));
		this.platedSrv = device.CreateShaderResourceView(this.plated);
		// RGBA: typed UAV stores on BGRA are optional in D3D11
		this.output = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, desc.Width, desc.Height, 1, 1,
			BindFlags.ShaderResource | BindFlags.UnorderedAccess));
		this.outputUav = device.CreateUnorderedAccessView(this.output);
		this.outputSrv = device.CreateShaderResourceView(this.output);
		this.captureDesc = desc;
		this.savedFrame = uint.MaxValue; // new copies hold nothing yet
		this.vulkan?.SetSource(this.output.NativePointer);
	}

	/// <summary>
	/// A copy's format: the source's, made viewable. Typeless targets (a swapchain or tool that views them as sRGB) take their
	/// UNORM member, sRGB ones too: the copy keeps the stored bytes, which are what the screen shows
	/// </summary>
	private static Format Typed(Format format) => format switch
	{
		Format.R8G8B8A8_Typeless or Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
		Format.B8G8R8A8_Typeless or Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
		Format.B8G8R8X8_Typeless or Format.B8G8R8X8_UNorm_SRgb => Format.B8G8R8X8_UNorm,
		Format.R10G10B10A2_Typeless => Format.R10G10B10A2_UNorm,
		Format.R16G16B16A16_Typeless => Format.R16G16B16A16_Float,
		_ => format,
	};

	/// <summary>Framework thread, null before disposing the capture</summary>
	public void SetVulkan(VkCapture? capture)
	{
		lock (this.resourceLock)
		{
			this.vulkan?.SetSource(0);
			this.vulkan = capture;
			capture?.SetSource(this.output?.NativePointer ?? 0);
		}
	}

	/// <summary>Render thread, takes the capture from OBS's own hook if it is loaded</summary>
	private void PresentToObs()
	{
		if (!this.obs.TryInit(takeOver: true))
			return;

		if (this.obsDevice == null)
		{
			ID3D11Device device = this.gameContext.Device; // cached by the wrapper, released when it is repointed
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
		this.captureRtv?.Dispose();
		this.captureSrv?.Dispose();
		this.capture?.Dispose();
		this.sceneSrv?.Dispose();
		this.scene?.Dispose();
		this.platedSrv?.Dispose();
		this.plated?.Dispose();
		this.layerSrv?.Dispose();
		this.layerCopy?.Dispose();
		this.outputSrv = null;
		this.outputUav = null;
		this.output = null;
		this.captureRtv = null;
		this.captureSrv = null;
		this.capture = null;
		this.sceneSrv = null;
		this.scene = null;
		this.platedSrv = null;
		this.plated = null;
		this.layerSrv = null;
		this.layerCopy = null;
		this.layerDesc = default;
		this.captureDesc = default;
	}

	public void Dispose()
	{
		lock (this.resourceLock)
		{
			this.ReleaseTextures();
			this.overlay.Dispose();
		}

		instance = null;
	}
}

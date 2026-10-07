using System;
using System.IO;
using System.Runtime.InteropServices;

using BlurBuddy.Capture;
using BlurBuddy.Graphics;

namespace BlurBuddy.Rendering;

/// <summary>
/// Render thread: half resolution Gaussian of the capture, composited inside the frame's rectangles
/// </summary>
public sealed unsafe class Blur(BlurBuddyConfiguration configuration): IDisposable
{
	private const float Feather = 6.0f;
	private const int MaxRadius = 32;
	private const float PixelsPerStrength = 4.0f; // pixelate block size

	[StructLayout(LayoutKind.Sequential)]
	private struct DownsampleConstants
	{
		public float InvWidth, InvHeight;
		public uint Width, Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct GaussianConstants
	{
		public int DirectionX, DirectionY;
		public uint Width, Height;
		public float Sigma;
		public int Radius;
		public float Padding0, Padding1;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CompositeConstants
	{
		public uint Width, Height;
		public uint RectCount;
		public uint WholeFrame;
		public float Feather;
		public uint Style;
		public float BlockSize;
		public float Strength;
	}

	private nint device;
	private nint downsample, gaussian, composite, sampler;
	private nint downsampleCb, gaussianCb, compositeCb;
	private nint rects, rectsSrv;
	private nint half, halfSrv, halfUav, temp, tempSrv, tempUav;
	private uint halfWidth, halfHeight;

	public void Run(nint context, nint captureSrv, nint outputUav, uint width, uint height, nint slotPointer)
	{
		FrameSlot* slot = (FrameSlot*)slotPointer;
		this.EnsureResources(context, (width + 1) / 2, (height + 1) / 2);

		BlurStyle style = configuration.Style;
		float strength = configuration.BlurStrength;
		nint* none = stackalloc nint[3] { 0, 0, 0 };
		nint* samplers = stackalloc nint[1] { this.sampler };
		D3D11.SetSamplers(context, samplers, 1);

		// Pixelate and crystallize sample the capture directly
		if (style is BlurStyle.Gaussian or BlurStyle.Diamond)
			this.Gaussian(context, captureSrv, strength);

		// Capture + half + rectangles -> output
		uint count = (uint)Math.Min(slot->RectCount, FrameSlots.MaxRects);
		if (count > 0)
			D3D11.UploadBytes(context, this.rects, slot->Rects, count * 16);
		D3D11.Upload(context, this.compositeCb, new CompositeConstants
		{
			Width = width, Height = height, RectCount = count, WholeFrame = (uint)slot->WholeFrame, Feather = Feather,
			Style = (uint)style, BlockSize = MathF.Max(2.0f, strength * PixelsPerStrength), Strength = strength,
		});
		this.Pass(context, this.composite, this.compositeCb, captureSrv, this.halfSrv, this.rectsSrv, outputUav, width, height);

		D3D11.SetShaderResources(context, none, 3);
		D3D11.SetUnorderedAccessViews(context, none, 1);
		D3D11.SetShader(context, 0);
	}

	/// <summary>Capture -> half, horizontal half -> temp, vertical temp -> half</summary>
	private void Gaussian(nint context, nint captureSrv, float strength)
	{
		float sigma = Math.Clamp(strength, 0.5f, MaxRadius / 3.0f);
		int radius = Math.Min(MaxRadius, (int)MathF.Ceiling(sigma * 3));

		D3D11.Upload(context, this.downsampleCb, new DownsampleConstants
		{
			InvWidth = 1.0f / this.halfWidth, InvHeight = 1.0f / this.halfHeight, Width = this.halfWidth, Height = this.halfHeight,
		});
		this.Pass(context, this.downsample, this.downsampleCb, captureSrv, 0, 0, this.halfUav, this.halfWidth, this.halfHeight);

		D3D11.Upload(context, this.gaussianCb, new GaussianConstants
		{
			DirectionX = 1, Width = this.halfWidth, Height = this.halfHeight, Sigma = sigma, Radius = radius,
		});
		this.Pass(context, this.gaussian, this.gaussianCb, this.halfSrv, 0, 0, this.tempUav, this.halfWidth, this.halfHeight);
		D3D11.Upload(context, this.gaussianCb, new GaussianConstants
		{
			DirectionY = 1, Width = this.halfWidth, Height = this.halfHeight, Sigma = sigma, Radius = radius,
		});
		this.Pass(context, this.gaussian, this.gaussianCb, this.tempSrv, 0, 0, this.halfUav, this.halfWidth, this.halfHeight);
	}

	private void Pass(nint context, nint shader, nint constants, nint srv0, nint srv1, nint srv2, nint uav, uint width, uint height)
	{
		nint* none = stackalloc nint[3] { 0, 0, 0 };
		nint* srvs = stackalloc nint[3] { srv0, srv1, srv2 };
		nint* uavs = stackalloc nint[1] { uav };
		nint* buffers = stackalloc nint[1] { constants };
		D3D11.SetShaderResources(context, none, 3); // unbind before reusing a texture as UAV
		D3D11.SetUnorderedAccessViews(context, uavs, 1);
		D3D11.SetShaderResources(context, srvs, 3);
		D3D11.SetConstantBuffers(context, buffers, 1);
		D3D11.SetShader(context, shader);
		D3D11.Dispatch(context, (width + 7) / 8, (height + 7) / 8);
		D3D11.SetUnorderedAccessViews(context, none, 1);
	}

	private void EnsureResources(nint context, uint width, uint height)
	{
		if (this.device == 0)
		{
			this.device = D3D11.GetDevice(context);
			this.downsample = D3D11.CreateComputeShader(this.device, Load("downsample"));
			this.gaussian = D3D11.CreateComputeShader(this.device, Load("gaussian"));
			this.composite = D3D11.CreateComputeShader(this.device, Load("composite"));
			this.sampler = D3D11.CreateSampler(this.device, true);
			this.downsampleCb = D3D11.CreateConstantBuffer(this.device, (uint)sizeof(DownsampleConstants));
			this.gaussianCb = D3D11.CreateConstantBuffer(this.device, (uint)sizeof(GaussianConstants));
			this.compositeCb = D3D11.CreateConstantBuffer(this.device, (uint)sizeof(CompositeConstants));
			this.rects = D3D11.CreateStructuredBuffer(this.device, FrameSlots.MaxRects, out this.rectsSrv);
		}

		if (width == this.halfWidth && height == this.halfHeight)
			return;

		this.ReleaseTargets();
		const uint bind = D3D11.BindShaderResource | D3D11.BindUnorderedAccess;
		this.half = D3D11.CreateTexture(this.device, width, height, D3D11.FormatR16G16B16A16Float, 1, bind);
		this.halfSrv = D3D11.CreateSrv(this.device, this.half);
		this.halfUav = D3D11.CreateUav(this.device, this.half, D3D11.FormatR16G16B16A16Float);
		this.temp = D3D11.CreateTexture(this.device, width, height, D3D11.FormatR16G16B16A16Float, 1, bind);
		this.tempSrv = D3D11.CreateSrv(this.device, this.temp);
		this.tempUav = D3D11.CreateUav(this.device, this.temp, D3D11.FormatR16G16B16A16Float);
		this.halfWidth = width;
		this.halfHeight = height;
	}

	private static byte[] Load(string name)
	{
		using Stream stream = typeof(Blur).Assembly.GetManifestResourceStream($"Shaders.{name}.cso") ??
		                      throw new InvalidOperationException($"missing resource Shaders.{name}.cso");
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	private void ReleaseTargets()
	{
		foreach (nint o in new[] { this.halfUav, this.halfSrv, this.half, this.tempUav, this.tempSrv, this.temp })
			D3D11.Release(o);
		this.halfUav = this.halfSrv = this.half = this.tempUav = this.tempSrv = this.temp = 0;
		this.halfWidth = this.halfHeight = 0;
	}

	public void Dispose()
	{
		this.ReleaseTargets();
		foreach (nint o in new[]
		         {
			         this.rectsSrv, this.rects, this.compositeCb, this.gaussianCb, this.downsampleCb, this.sampler, this.composite,
			         this.gaussian, this.downsample, this.device,
		         })
			D3D11.Release(o);
	}
}

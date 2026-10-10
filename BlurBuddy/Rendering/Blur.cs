using System;
using System.IO;
using System.Runtime.InteropServices;

using BlurBuddy.Capture;

using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlurBuddy.Rendering;

/// <summary>
/// Render thread: half resolution Gaussian of the capture, composited inside the frame's rectangles
/// </summary>
public sealed unsafe class Blur(BlurBuddyConfiguration configuration): IDisposable
{
	private const float Feather = 6.0f;
	private const int MaxRadius = 32;
	private const float PixelsPerStrength = 4.0f; // pixelate block size
	private const float CoverageRadius = 8.0f; // nameplate glyph coverage grows by this, so the blur hides the words' shapes

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
		public uint PlateCount;
		public uint HasLayer;
		public float CoverageRadius;
		public float Padding0;
	}

	private Pipeline? pipeline;
	private Targets? targets;

	public void Run(ID3D11DeviceContext context, ID3D11ShaderResourceView capture, ID3D11ShaderResourceView? layer,
		ID3D11UnorderedAccessView output, uint width, uint height, nint slotPointer)
	{
		FrameSlot* slot = (FrameSlot*)slotPointer;
		uint halfWidth = (width + 1) / 2, halfHeight = (height + 1) / 2;
		if (this.pipeline == null)
		{
			ID3D11Device device = context.Device; // cached by the wrapper, released when it is repointed
			this.pipeline = new Pipeline(device);
		}

		if (this.targets == null || this.targets.Width != halfWidth || this.targets.Height != halfHeight)
		{
			this.targets?.Dispose();
			ID3D11Device device = context.Device; // cached by the wrapper, released when it is repointed
			this.targets = new Targets(device, halfWidth, halfHeight);
		}

		Pipeline p = this.pipeline;
		Targets t = this.targets;
		BlurStyle style = configuration.Style;
		float strength = configuration.BlurStrength;
		context.CSSetSampler(0, p.Sampler);

		// Pixelate and crystallize sample the capture directly
		if (style is BlurStyle.Gaussian or BlurStyle.Diamond)
			Gaussian(context, p, t, capture, strength);

		// Capture + half + rectangles -> output
		uint count = (uint)Math.Min(slot->RectCount, FrameSlots.MaxRects);
		if (count > 0)
		{
			MappedSubresource mapped = context.Map(p.Rects, MapMode.WriteDiscard);
			Buffer.MemoryCopy(slot->Rects, (void*)mapped.DataPointer, count * 16, count * 16);
			context.Unmap(p.Rects);
		}

		Upload(context, p.CompositeCb, new CompositeConstants
		{
			Width = width, Height = height, RectCount = count, WholeFrame = (uint)slot->WholeFrame, Feather = Feather,
			Style = (uint)style, BlockSize = MathF.Max(2.0f, strength * PixelsPerStrength), Strength = strength,
			PlateCount = (uint)Math.Min(slot->PlateCount, (int)count), HasLayer = layer != null ? 1u : 0u, CoverageRadius = CoverageRadius,
		});
		context.CSSetShaderResource(3, layer); // Pass binds and unbinds 0-2
		Pass(context, p.Composite, p.CompositeCb, capture, t.HalfSrv, p.RectsSrv, output, width, height);

		context.CSUnsetShaderResources(0, 4);
		context.CSSetShader(null);
	}

	/// <summary>Capture -> half, horizontal half -> temp, vertical temp -> half</summary>
	private static void Gaussian(ID3D11DeviceContext context, Pipeline p, Targets t, ID3D11ShaderResourceView capture, float strength)
	{
		float sigma = Math.Clamp(strength, 0.5f, MaxRadius / 3.0f);
		int radius = Math.Min(MaxRadius, (int)MathF.Ceiling(sigma * 3));

		Upload(context, p.DownsampleCb, new DownsampleConstants
		{
			InvWidth = 1.0f / t.Width, InvHeight = 1.0f / t.Height, Width = t.Width, Height = t.Height,
		});
		Pass(context, p.Downsample, p.DownsampleCb, capture, null, null, t.HalfUav, t.Width, t.Height);

		Upload(context, p.GaussianCb, new GaussianConstants { DirectionX = 1, Width = t.Width, Height = t.Height, Sigma = sigma, Radius = radius });
		Pass(context, p.Gaussian, p.GaussianCb, t.HalfSrv, null, null, t.TempUav, t.Width, t.Height);
		Upload(context, p.GaussianCb, new GaussianConstants { DirectionY = 1, Width = t.Width, Height = t.Height, Sigma = sigma, Radius = radius });
		Pass(context, p.Gaussian, p.GaussianCb, t.TempSrv, null, null, t.HalfUav, t.Width, t.Height);
	}

	private static void Pass(ID3D11DeviceContext context, ID3D11ComputeShader shader, ID3D11Buffer constants, ID3D11ShaderResourceView srv0,
		ID3D11ShaderResourceView? srv1, ID3D11ShaderResourceView? srv2, ID3D11UnorderedAccessView uav, uint width, uint height)
	{
		context.CSUnsetShaderResources(0, 3); // unbind before reusing a texture as UAV
		context.CSSetUnorderedAccessView(0, uav);
		context.CSSetShaderResource(0, srv0);
		context.CSSetShaderResource(1, srv1);
		context.CSSetShaderResource(2, srv2);
		context.CSSetConstantBuffer(0, constants);
		context.CSSetShader(shader);
		context.Dispatch(Math.Max(1, (width + 7) / 8), Math.Max(1, (height + 7) / 8), 1);
		context.CSUnsetUnorderedAccessView(0);
	}

	private static void Upload<T>(ID3D11DeviceContext context, ID3D11Buffer buffer, in T value) where T : unmanaged
	{
		MappedSubresource mapped = context.Map(buffer, MapMode.WriteDiscard);
		*(T*)mapped.DataPointer = value;
		context.Unmap(buffer);
	}

	/// <summary>Shaders, sampler, constant and rectangle buffers</summary>
	private sealed class Pipeline: IDisposable
	{
		public readonly ID3D11ComputeShader Downsample, Gaussian, Composite;
		public readonly ID3D11SamplerState Sampler;
		public readonly ID3D11Buffer DownsampleCb, GaussianCb, CompositeCb, Rects;
		public readonly ID3D11ShaderResourceView RectsSrv;

		public Pipeline(ID3D11Device device)
		{
			this.Downsample = device.CreateComputeShader(Load("downsample"));
			this.Gaussian = device.CreateComputeShader(Load("gaussian"));
			this.Composite = device.CreateComputeShader(Load("composite"));
			this.Sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
			this.DownsampleCb = ConstantBuffer(device, sizeof(DownsampleConstants));
			this.GaussianCb = ConstantBuffer(device, sizeof(GaussianConstants));
			this.CompositeCb = ConstantBuffer(device, sizeof(CompositeConstants));
			this.Rects = device.CreateBuffer(new BufferDescription(FrameSlots.MaxRects * 16, BindFlags.ShaderResource, ResourceUsage.Dynamic,
				CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, 16));
			this.RectsSrv = device.CreateShaderResourceView(this.Rects,
				new ShaderResourceViewDescription(this.Rects, Format.Unknown, 0, FrameSlots.MaxRects));
		}

		private static ID3D11Buffer ConstantBuffer(ID3D11Device device, int size) =>
			device.CreateBuffer(new BufferDescription(((uint)size + 15) & ~15u, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

		private static byte[] Load(string name)
		{
			using Stream stream = typeof(Blur).Assembly.GetManifestResourceStream($"Shaders.{name}.cso") ??
			                      throw new InvalidOperationException($"missing resource Shaders.{name}.cso");
			using MemoryStream memory = new();
			stream.CopyTo(memory);
			return memory.ToArray();
		}

		public void Dispose()
		{
			this.RectsSrv.Dispose();
			this.Rects.Dispose();
			this.CompositeCb.Dispose();
			this.GaussianCb.Dispose();
			this.DownsampleCb.Dispose();
			this.Sampler.Dispose();
			this.Composite.Dispose();
			this.Gaussian.Dispose();
			this.Downsample.Dispose();
		}
	}

	/// <summary>Half resolution blur textures</summary>
	private sealed class Targets: IDisposable
	{
		public readonly uint Width, Height;
		public readonly ID3D11Texture2D Half, Temp;
		public readonly ID3D11ShaderResourceView HalfSrv, TempSrv;
		public readonly ID3D11UnorderedAccessView HalfUav, TempUav;

		public Targets(ID3D11Device device, uint width, uint height)
		{
			this.Width = width;
			this.Height = height;
			Texture2DDescription desc = new(Format.R16G16B16A16_Float, Math.Max(1, width), Math.Max(1, height), 1, 1,
				BindFlags.ShaderResource | BindFlags.UnorderedAccess);
			this.Half = device.CreateTexture2D(desc);
			this.HalfSrv = device.CreateShaderResourceView(this.Half);
			this.HalfUav = device.CreateUnorderedAccessView(this.Half);
			this.Temp = device.CreateTexture2D(desc);
			this.TempSrv = device.CreateShaderResourceView(this.Temp);
			this.TempUav = device.CreateUnorderedAccessView(this.Temp);
		}

		public void Dispose()
		{
			this.HalfUav.Dispose();
			this.HalfSrv.Dispose();
			this.Half.Dispose();
			this.TempUav.Dispose();
			this.TempSrv.Dispose();
			this.Temp.Dispose();
		}
	}

	public void Dispose()
	{
		this.targets?.Dispose();
		this.pipeline?.Dispose();
	}
}

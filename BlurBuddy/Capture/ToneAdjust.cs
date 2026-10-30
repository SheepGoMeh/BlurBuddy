using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace BlurBuddy.Capture;

/// <summary>
/// The scene copies are from before the game's brightness, with ToneAdjust on the final target too. The output goes through
/// the game's ToneAdjust chain once more, its input and output pointed at our textures (UpscaleBuddy UiLayer.QueueToneAdjust)
/// </summary>
public sealed unsafe class ToneAdjust: IDisposable
{
	// PostEffectManager+0x1B0: ToneAdjust, input +0x4028 (= ToneAdjustSource), output +0x4020 (the back buffer)
	private const int ChainOffset = 0x1B0;
	private const int ChainInputOffset = 0x18; // PostEffectChain, Kernel::Texture*
	private const int PartOutputOffset = 0x38; // PostEffectPart, render target 0
	private const string SetChainInputSignature = "40 56 41 55 48 83 EC 28 48 8B 41 18"; // sub_140349BB0(chain, texture)
	private const string SetChainOutputSignature = "40 57 48 83 EC 20 44 8B 41 10"; // sub_140349CB0(chain, texture), last part
	private const string RenderChainSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 65 48 8B 04 25 ?? ?? ?? ?? 48 8B F9";

	// Without ToneAdjust (PostEffectManager+0x4410 == -1) Manager.Render (sub_140362D70) runs the chain in place on the back
	// buffer when Device+0x98 isn't 1, or Device+0x9C is set and Device+0xA0 above 0. Queued before the UI pass, it runs
	// after the 2D UI and before our capture: the finished frame has it, the scene copies don't
	private const int ToneAdjustHandleOffset = 0x4410;
	private const int GammaOffset = 0x98;
	private const int ToneFlagOffset = 0x9C;
	private const int ToneAmountOffset = 0xA0;

	// RenderTargetManager.Initialize's targets: TextureRenderTarget | ReadWrite | UserManaged, allocation class 3
	private const TextureFlags TargetFlags = TextureFlags.TextureRenderTarget | TextureFlags.ReadWrite | TextureFlags.UserManaged;
	private const uint AllocationClass = 3;

	private readonly delegate* unmanaged<PostEffectChain*, KernelTexture*, void> setChainInput;
	private readonly delegate* unmanaged<PostEffectChain*, KernelTexture*, void> setChainOutput;
	private readonly delegate* unmanaged<PostEffectChain*, void> renderChain;
	private readonly bool available;

	// Main thread creates them, the render thread draws through them by the frame slot
	private KernelTexture* source;
	private KernelTexture* target;

	public ToneAdjust()
	{
		try
		{
			this.setChainInput = (delegate* unmanaged<PostEffectChain*, KernelTexture*, void>)Service.SigScanner.ScanText(SetChainInputSignature);
			this.setChainOutput = (delegate* unmanaged<PostEffectChain*, KernelTexture*, void>)Service.SigScanner.ScanText(SetChainOutputSignature);
			this.renderChain = (delegate* unmanaged<PostEffectChain*, void>)Service.SigScanner.ScanText(RenderChainSignature);
			this.available = true;
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "ToneAdjust chain not found, the stream stays before the game's brightness");
		}
	}

	/// <summary>
	/// Main thread, in the context's view and key after the capture: the chain from the slot's source into its target. False
	/// when the brightness is neutral or a texture is missing, the capture streams as is
	/// </summary>
	public bool Queue(FrameSlot* slot, KernelTexture* final)
	{
		PostEffectManager* postEffects = PostEffectManager.Instance();
		if (!this.available || final == null || postEffects == null)
			return false;

		if ((postEffects->Flags & PostEffectFlags.ToneAdjust) == 0)
		{
			if (!InPlace(postEffects))
				return false;
			slot->FinishedToned = 1;
		}

		PostEffectChain* chain = (PostEffectChain*)((byte*)postEffects + ChainOffset);
		byte* last = null;
		for (int i = (int)chain->PartCount - 1; i >= 0 && last == null; i--)
			last = (byte*)chain->Parts[i];
		KernelTexture* output = last == null ? null : *(KernelTexture**)(last + PartOutputOffset);
		if (output == null)
			return false;

		// The target is the back buffer's size, the output the final target's: used only when they match
		KernelTexture* source = Fit(ref this.source, final->ActualWidth, final->ActualHeight);
		KernelTexture* target = Fit(ref this.target, output->ActualWidth, output->ActualHeight);
		if (source == null || target == null)
			return false;

		// cToneMapParam is already this frame's, Manager.Render or Submit set it before the UI pass
		chain->Scale = 1;
		KernelTexture* input = *(KernelTexture**)((byte*)chain + ChainInputOffset);
		this.setChainInput(chain, source);
		this.setChainOutput(chain, target);
		this.renderChain(chain);
		this.setChainInput(chain, input);
		this.setChainOutput(chain, output);
		slot->ToneSource = (nint)source;
		slot->ToneTarget = (nint)target;
		return true;
	}

	/// <summary>Manager.Render's test for its pass on the back buffer</summary>
	private static bool InPlace(PostEffectManager* postEffects)
	{
		byte* device = (byte*)Device.Instance();
		if (device == null || *(long*)((byte*)postEffects + ToneAdjustHandleOffset) != -1)
			return false;

		float gamma = *(float*)(device + GammaOffset);
		return gamma != 1 || (*(int*)(device + ToneFlagOffset) != 0 && *(float*)(device + ToneAmountOffset) > 0);
	}

	/// <summary>A B8G8R8A8 render target of this size in the slot, recreated when it changes; null when creation fails</summary>
	private static KernelTexture* Fit(ref KernelTexture* slot, uint width, uint height)
	{
		if (slot != null && slot->ActualWidth == width && slot->ActualHeight == height)
			return slot;

		// The game releases it once the frames using it are done
		if (slot != null)
			slot->DecRef();
		slot = KernelTexture.CreateTexture2D((int)width, (int)height, 1, TextureFormat.B8G8R8A8_UNORM, TargetFlags, AllocationClass);
		return slot;
	}

	public void Dispose()
	{
		if (this.source != null)
			this.source->DecRef();
		if (this.target != null)
			this.target->DecRef();
		this.source = null;
		this.target = null;
	}
}

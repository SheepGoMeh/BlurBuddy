using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using BlurBuddy.Tracking;

namespace BlurBuddy.Capture;

/// <summary>
/// Queues the capture callback after ToneAdjust, once a frame, and the nameplate layer's flush in front of the 2D UI's bind
/// </summary>
public sealed unsafe class UiCapture: IDisposable
{
	private const int AlternateFinalOffset = 0x4E8; // RenderTargetManager, Kernel::Texture*: ToneAdjustSource / upscaler output

	// ToneAdjust turns the final target into the back buffer in view 31 at 0xFFFFFFFF, after all UI (PostEffectManager.Submit,
	// queued before the UI); with Context+0x2F70 clear the top nibble of view 31 keys is forced to 0xF (UpscaleBuddy UiLayer)
	private const int ToneAdjustView = 31;
	private const uint AfterToneAdjust = 0x0FFFFFFF; // ToneAdjust's own key, queued after it so sorted after it
	private const int ContextSubViewFlagOffset = 0x2F70;

	private delegate void Draw2DDelegate(UIModule* module);

	private delegate void PushBackDelegate(Context* context, void* command);

	private readonly BlurBuddyConfiguration configuration;
	private readonly NameplateLayer layer;
	private readonly FrameSlots slots = new();
	private readonly LoadingFade loadingFade = new();
	private readonly Hook<AtkServer.Delegates.Draw> draw;
	private readonly Hook<Draw2DDelegate> draw2D;
	private readonly Hook<PushBackDelegate> pushBack;
	private volatile CaptureSet set;
	private bool uiDrawn; // AtkServer.Draw ran this frame

	// AtkServer.Draw's context: its bind of the final target without depth is the 2D UI's
	private Context* drawing;
	private bool uiBindSeen;
	private Texture* final; // this frame's, see FinalTarget

	public UiCapture(BlurBuddyConfiguration configuration, NameplateLayer layer)
	{
		this.configuration = configuration;
		this.layer = layer;
		this.set = configuration.Set;
		this.Tracker = new ElementTracker(configuration);
		this.draw = Service.GameInteropProvider.HookFromAddress<AtkServer.Delegates.Draw>(
			(nint)AtkServer.MemberFunctionPointers.Draw, this.DrawDetour);
		this.draw2D = Service.GameInteropProvider.HookFromAddress<Draw2DDelegate>(
			(nint)UIModule.MemberFunctionPointers.Draw2D, this.Draw2DDetour);
		this.pushBack = Service.GameInteropProvider.HookFromAddress<PushBackDelegate>(
			(nint)Context.MemberFunctionPointers.PushBackCommand, this.PushBackDetour);

		this.draw.Enable();
		this.draw2D.Enable();
		this.pushBack.Enable();
	}

	public string Status { get; private set; } = "Waiting for the UI";

	public ElementTracker Tracker { get; }

	/// <summary>Framework thread</summary>
	public void Update() => this.set = this.configuration.Set;

	/// <summary>
	/// The frame's final target as the gate at the end of Manager.Render picks it (ffxiv_rev scene-ui-gate): RTM+0x4E8 when
	/// set, else the back buffer. Not RTM+0x570 inside AtkServer.Draw, which other plugins (UpscaleBuddy) point at their own
	/// UI layer for the call
	/// </summary>
	private static Texture* FinalTarget()
	{
		RenderTargetManager* targets = RenderTargetManager.Instance();
		Texture* alternate = targets == null ? null : *(Texture**)((byte*)targets + AlternateFinalOffset);
		if (alternate != null)
			return alternate;

		SwapChain* swapChain = Device.Instance()->SwapChain;
		return swapChain == null ? null : swapChain->BackBuffer;
	}

	/// <summary>Main thread: the layer for this frame's final target; the 2D UI's bind is found while the UI is built</summary>
	private void DrawDetour(AtkServer* server, bool flag)
	{
		this.final = FinalTarget();
		this.layer.Prepare(this.final);
		this.drawing = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		this.uiBindSeen = false;
		this.uiDrawn = true;
		this.draw.Original(server, flag);
		this.drawing = null;
	}

	private void PushBackDetour(Context* context, void* command)
	{
		// In front of it with the same key: after the nameplates' draws, right before the 2D UI's. AtkServer.Draw binds
		// RTM+0x570 as it is during the call, another plugin's UI layer included; the scene is still saved from the final target
		if (context == this.drawing && !this.uiBindSeen && ((RenderCommand*)command)->Type == RenderCommandType.SetTarget &&
		    IsUiBind((RenderCommandSetTarget*)command))
		{
			this.uiBindSeen = true;
			RenderCallback.Queue(&NameplateLayer.OnUiBegin, (nint)this.final);
		}

		this.pushBack.Original(context, command);
	}

	private static bool IsUiBind(RenderCommandSetTarget* command)
	{
		RenderTargetManager* targets = RenderTargetManager.Instance();
		return targets != null && command->RenderTargets[0].Value == targets->SwapChainBackBuffer && command->DepthBuffer == null;
	}

	private void Draw2DDetour(UIModule* module)
	{
		this.uiDrawn = false;
		this.uiBindSeen = false;
		this.final = FinalTarget();
		this.draw2D.Original(module);
		this.QueueCapture();
	}

	/// <summary>
	/// After ToneAdjust: every UI is on the final target by then, also when another plugin (UpscaleBuddy) drew it into a
	/// layer of its own and puts it back right before ToneAdjust. ToneAdjust only reads the final target
	/// </summary>
	private void QueueCapture()
	{
		FrameSlot* slot = this.slots.Next();
		if (this.uiDrawn)
			this.Tracker.Collect(slot, this.set); // UI hidden: the frame is the scene for every set
		slot->Target = (nint)this.final;
		slot->AfterUiBind = this.uiBindSeen ? 1 : 0;
		slot->Fade = this.set == CaptureSet.Ui ? 0 : this.loadingFade.Current(); // the UI set has the real one
		// Only over full black: anywhere else the final target there can hold UI the scene sets hide
		Device* device = Device.Instance();
		if (slot->Fade >= 1)
			slot->Indicator = LoadingFade.Indicator(device->Width, device->Height);
		Context* context = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		uint key = context->SortKey;
		uint top = (*((byte*)context + ContextSubViewFlagOffset) == 0 ? key | 0xF0000000 : key) & 0xF0000000;
		this.Status = !RenderCallback.Queue(&FrameRenderer.OnRender, (nint)slot, top | AfterToneAdjust, ToneAdjustView)
			? "Failed to queue the capture"
			: this.uiDrawn && !this.uiBindSeen
				? "2D UI bind not found, the stream can show the UI and nameplates"
				: "Capturing";
	}

	public void Stop()
	{
		this.draw.Disable();
		this.draw2D.Disable();
		this.pushBack.Disable();
	}

	public void Dispose()
	{
		this.draw.Dispose();
		this.draw2D.Dispose();
		this.pushBack.Dispose();
		this.slots.Dispose();
	}
}

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
/// Queues the capture callback after AtkServer's UI replay, and the nameplate layer's flush in front of the 2D UI's bind
/// </summary>
public sealed unsafe class UiCapture: IDisposable
{
	private delegate void ProcessUiCommandsDelegate(AtkServer* server, bool a2);

	private delegate void Draw2DDelegate(UIModule* module);

	private delegate void PushBackDelegate(Context* context, void* command);

	private readonly BlurBuddyConfiguration configuration;
	private readonly NameplateLayer layer;
	private readonly FrameSlots slots = new();
	private readonly Hook<AtkServer.Delegates.Draw> draw;
	private readonly Hook<ProcessUiCommandsDelegate> process16;
	private readonly Hook<ProcessUiCommandsDelegate> process32;
	private readonly Hook<Draw2DDelegate> draw2D;
	private readonly Hook<PushBackDelegate> pushBack;
	private volatile CaptureSet set;
	private bool queuedThisFrame;

	// Sort keys of the commands a replay queues, the capture is queued after the highest
	private Context* tracking; // the replaying thread's context, null when not tracking
	private bool anyBefore;
	private uint maxBefore;
	private uint replayMin = uint.MaxValue;
	private uint previousMin = uint.MaxValue;

	// AtkServer.Draw's context: its bind of the final target without depth is the 2D UI's
	private Context* drawing;
	private bool uiBindSeen;

	public UiCapture(BlurBuddyConfiguration configuration, NameplateLayer layer)
	{
		this.configuration = configuration;
		this.layer = layer;
		this.set = configuration.Set;
		this.Tracker = new ElementTracker(configuration);
		this.draw = Service.GameInteropProvider.HookFromAddress<AtkServer.Delegates.Draw>(
			(nint)AtkServer.MemberFunctionPointers.Draw, this.DrawDetour);
		this.process16 = Service.GameInteropProvider.HookFromAddress<ProcessUiCommandsDelegate>(
			(nint)AtkServer.MemberFunctionPointers.ProcessUICommands, (s, a) => this.Detour(this.process16!, s, a));
		this.process32 = Service.GameInteropProvider.HookFromAddress<ProcessUiCommandsDelegate>(
			(nint)AtkServer.MemberFunctionPointers.ProcessUICommandsAlt, (s, a) => this.Detour(this.process32!, s, a));
		this.draw2D = Service.GameInteropProvider.HookFromAddress<Draw2DDelegate>(
			(nint)UIModule.MemberFunctionPointers.Draw2D, this.Draw2DDetour);
		this.pushBack = Service.GameInteropProvider.HookFromAddress<PushBackDelegate>(
			(nint)Context.MemberFunctionPointers.PushBackCommand, this.PushBackDetour);

		this.draw.Enable();
		this.process16.Enable();
		this.process32.Enable();
		this.draw2D.Enable();
		this.pushBack.Enable();
	}

	public string Status { get; private set; } = "Waiting for the UI";

	public ElementTracker Tracker { get; }

	/// <summary>Framework thread</summary>
	public void Update() => this.set = this.configuration.Set;

	/// <summary>Main thread: the layer for this frame's final target; the 2D UI's bind is found while the UI is built</summary>
	private void DrawDetour(AtkServer* server, bool flag)
	{
		RenderTargetManager* targets = RenderTargetManager.Instance();
		this.layer.Prepare(targets == null ? null : targets->SwapChainBackBuffer);
		this.drawing = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		this.uiBindSeen = false;
		this.draw.Original(server, flag);
		this.drawing = null;
		if (!this.uiBindSeen)
			this.Status = "2D UI bind not found, the stream can show the UI and nameplates";
	}

	private void PushBackDetour(Context* context, void* command)
	{
		// In front of it with the same key: after the nameplates' draws, right before the 2D UI's
		if (context == this.drawing && !this.uiBindSeen && ((RenderCommand*)command)->Type == RenderCommandType.SetTarget &&
		    this.layer.IsUiBind((RenderCommandSetTarget*)command))
		{
			this.uiBindSeen = true;
			RenderCallback.Queue(&NameplateLayer.OnUiBegin, (nint)((RenderCommandSetTarget*)command)->RenderTargets[0].Value);
		}

		if (context == this.tracking)
		{
			uint key = context->SortKey;
			this.replayMin = Math.Min(this.replayMin, key);
			this.maxBefore = Math.Max(this.maxBefore, key);
			this.anyBefore = true;
		}

		this.pushBack.Original(context, command);
	}

	/// <summary>Commands run by sort key, equal keys in queue order: the capture goes after the replay's last command</summary>
	private void Detour(Hook<ProcessUiCommandsDelegate> hook, AtkServer* server, bool a2)
	{
		this.queuedThisFrame = true;
		Context* context = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		this.anyBefore = false;
		this.maxBefore = 0;
		this.replayMin = uint.MaxValue;
		this.tracking = context;
		hook.Original(server, a2);
		this.tracking = null;
		uint key = UiSplit.CaptureKey(this.anyBefore, this.maxBefore, this.previousMin, context->SortKey);
		this.previousMin = this.replayMin;
		this.QueueCapture(key);
	}

	private void Draw2DDetour(UIModule* module)
	{
		this.queuedThisFrame = false;
		this.uiBindSeen = false;
		this.draw2D.Original(module);
		if (!this.queuedThisFrame)
			this.QueueCapture(track: false); // UI hidden: the frame is the scene for every set
	}

	private void QueueCapture(uint? sortKey = null, bool track = true)
	{
		FrameSlot* slot = this.slots.Next();
		if (track)
			this.Tracker.Collect(slot, this.set);
		RenderTargetManager* targets = RenderTargetManager.Instance();
		slot->Target = targets == null ? 0 : (nint)targets->SwapChainBackBuffer;
		slot->AfterUiBind = this.uiBindSeen ? 1 : 0;
		this.Status = RenderCallback.Queue(&FrameRenderer.OnRender, (nint)slot, sortKey) ? "Capturing" : "Failed to queue the capture";
	}

	public void Stop()
	{
		this.draw.Disable();
		this.process16.Disable();
		this.process32.Disable();
		this.draw2D.Disable();
		this.pushBack.Disable();
	}

	public void Dispose()
	{
		this.draw.Dispose();
		this.process16.Dispose();
		this.process32.Dispose();
		this.draw2D.Dispose();
		this.pushBack.Dispose();
		this.slots.Dispose();
	}
}

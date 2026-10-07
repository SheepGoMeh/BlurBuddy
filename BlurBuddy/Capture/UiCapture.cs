using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using BlurBuddy.Tracking;

namespace BlurBuddy.Capture;

/// <summary>
/// Queues the capture callback inside AtkServer's UI replay at the selected stage
/// </summary>
public sealed unsafe class UiCapture: IDisposable
{
	// AtkServer clip rect push (ProcessUICommands, ClipRect entries), not in ClientStructs
	private const string PushClipRectSignature = "48 83 EC ?? 44 8B 81 ?? ?? ?? ?? 4C 8B DA 4C 8B C9 41 83 F8";
	private const int MarkerRectOffset = 0xC; // ProcessUICommands passes the command + 0xC
	private const int MarkerSize = 0x20;

	private delegate void ProcessUiCommandsDelegate(AtkServer* server, bool a2);

	private delegate void Draw2DDelegate(UIModule* module);

	private delegate void PushClipRectDelegate(AtkServer* server, int* rect);

	private readonly BlurBuddyConfiguration configuration;
	private readonly FrameSlots slots = new();
	private readonly Hook<ProcessUiCommandsDelegate> process16;
	private readonly Hook<ProcessUiCommandsDelegate> process32;
	private readonly Hook<Draw2DDelegate> draw2D;
	private readonly Hook<PushClipRectDelegate> pushClipRect;
	// ClipRect entry inserted at the split: the replay flushes the nameplate draws before handling it
	private readonly AtkUICommand* marker;
	private volatile CaptureSet set;
	private uint[] keys = new uint[2048];
	private bool[] draws = new bool[2048];
	private AtkUICommandEntry* listCopy;
	private uint listCapacity;
	private int guardFrames;
	private bool queuedThisFrame;

	// Sort keys of the commands this replay queues, the capture's key is picked from them
	private delegate void PushBackDelegate(Context* context, void* command);
	private readonly Hook<PushBackDelegate> pushBack;
	private Context* tracking; // the replaying thread's context, null when not tracking
	private bool anyBefore;
	private uint maxBefore;
	private uint replayMin = uint.MaxValue;
	private uint previousMin = uint.MaxValue;

	public UiCapture(BlurBuddyConfiguration configuration)
	{
		this.configuration = configuration;
		this.set = configuration.Set;
		this.Tracker = new ElementTracker(configuration);
		this.process16 = Service.GameInteropProvider.HookFromAddress<ProcessUiCommandsDelegate>(
			(nint)AtkServer.MemberFunctionPointers.ProcessUICommands, (s, a) => this.Detour(this.process16!, s, a));
		this.process32 = Service.GameInteropProvider.HookFromAddress<ProcessUiCommandsDelegate>(
			(nint)AtkServer.MemberFunctionPointers.ProcessUICommandsAlt, (s, a) => this.Detour(this.process32!, s, a));
		this.draw2D = Service.GameInteropProvider.HookFromAddress<Draw2DDelegate>(
			(nint)UIModule.MemberFunctionPointers.Draw2D, this.Draw2DDetour);
		this.pushClipRect = Service.GameInteropProvider.HookFromAddress<PushClipRectDelegate>(
			Service.SigScanner.ScanText(PushClipRectSignature), this.PushClipRectDetour);

		this.marker = (AtkUICommand*)NativeMemory.AllocZeroed(MarkerSize);
		this.marker->Type = AtkUICommandType.ClipRect;
		*(uint*)((byte*)this.marker + 8) = 1; // push, not pop

		this.process16.Enable();
		this.process32.Enable();
		this.draw2D.Enable();
		this.pushClipRect.Enable();

		this.pushBack = Service.GameInteropProvider.HookFromAddress<PushBackDelegate>(
			(nint)Context.MemberFunctionPointers.PushBackCommand, this.PushBackDetour);
		this.pushBack.Enable();
	}

	private void PushBackDetour(Context* context, void* command)
	{
		if (context == this.tracking)
		{
			uint key = context->SortKey;
			this.replayMin = Math.Min(this.replayMin, key);
			this.maxBefore = Math.Max(this.maxBefore, key);
			this.anyBefore = true;
		}

		this.pushBack.Original(context, command);
	}

	public string Status { get; private set; } = "Waiting for the UI";

	public ElementTracker Tracker { get; }

	/// <summary>Framework thread</summary>
	public void Update() => this.set = this.configuration.Set;

	/// <summary>
	/// Commands run by sort key, and the replay changes the context's key as it switches state;
	/// the capture is queued from inside the replay (marker entry) to sit between the right draws
	/// </summary>
	private void Detour(Hook<ProcessUiCommandsDelegate> hook, AtkServer* server, bool a2)
	{
		this.queuedThisFrame = true;
		AtkUICommandEntry* list = server->UICommandList;
		uint count = server->UICommandCount;
		int split = this.set switch
		{
			CaptureSet.None => 0,
			CaptureSet.Nameplates => this.FindNameplateSplit(list, count),
			_ => (int)count,
		};

		// One replay: a second one would rewrite the index buffer before the GPU reads it
		this.EnsureListCopy(count + 1);
		long entry = sizeof(AtkUICommandEntry);
		Buffer.MemoryCopy(list, this.listCopy, entry * split, entry * split);
		this.listCopy[split].SortKey = split < count ? list[split].SortKey : uint.MaxValue;
		this.listCopy[split].Command = this.marker;
		Buffer.MemoryCopy(list + split, this.listCopy + split + 1, entry * (count - split), entry * (count - split));

		server->UICommandList = this.listCopy;
		server->UICommandCount = count + 1;
		this.anyBefore = false;
		this.maxBefore = 0;
		this.replayMin = uint.MaxValue;
		this.tracking = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		hook.Original(server, a2);
		this.tracking = null;
		this.previousMin = this.replayMin;
		server->UICommandList = list;
		server->UICommandCount = count;
	}

	private void Draw2DDetour(UIModule* module)
	{
		this.queuedThisFrame = false;
		this.draw2D.Original(module);
		if (!this.queuedThisFrame)
			this.QueueCapture(track: false); // UI hidden: the frame is the scene for every set
	}

	private int FindNameplateSplit(AtkUICommandEntry* list, uint count)
	{
		if (count > this.keys.Length)
		{
			this.keys = new uint[count];
			this.draws = new bool[count];
		}

		for (int i = 0; i < count; i++)
		{
			this.keys[i] = list[i].SortKey;
			this.draws[i] = list[i].Command != null && ((uint)list[i].Command->Type & 0xF0) != 0;
		}

		int split = UiSplit.FindSplit(this.keys.AsSpan(0, (int)count), this.draws.AsSpan(0, (int)count));
		if (++this.guardFrames % 300 == 0)
			this.CheckSplit(list, count, split);
		return split;
	}

	private void PushClipRectDetour(AtkServer* server, int* rect)
	{
		if (rect == (int*)((byte*)this.marker + MarkerRectOffset))
		{
			uint current = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext->SortKey;
			uint key = UiSplit.CaptureKey(this.anyBefore, this.maxBefore, this.previousMin, current);
			Context* replaying = this.tracking;
			this.tracking = null; // the capture's own push
			this.QueueCapture(key);
			this.tracking = replaying;
			return;
		}

		this.pushClipRect.Original(server, rect);
	}

	private void EnsureListCopy(uint count)
	{
		if (count <= this.listCapacity)
			return;

		NativeMemory.Free(this.listCopy);
		this.listCapacity = Math.Max(count, 2048);
		this.listCopy = (AtkUICommandEntry*)NativeMemory.Alloc(this.listCapacity, (nuint)sizeof(AtkUICommandEntry));
	}

	/// <summary>Debug guard: a BakePlate atlas quad after the split means the key threshold is wrong</summary>
	private void CheckSplit(AtkUICommandEntry* list, uint count, int split)
	{
		AddonNamePlate* namePlate = (AddonNamePlate*)RaptureAtkUnitManager.Instance()->GetAddonByName("NamePlate");
		if (namePlate == null)
			return;

		Texture* atlas = namePlate->BakePlate.Texture;
		for (int i = split; i < count; i++)
		{
			if (list[i].Command == null || ((uint)list[i].Command->Type & 0xF0) == 0)
				continue;
			if (((AtkUICommandDraw*)list[i].Command)->Texture == atlas)
			{
				Service.PluginLog.Warning($"Nameplate atlas drawn after the split ({i} >= {split}, key 0x{list[i].SortKey:X8})");
				return;
			}
		}
	}

	private void QueueCapture(uint? sortKey = null, bool track = true)
	{
		FrameSlot* slot = this.slots.Next();
		if (track)
			this.Tracker.Collect(slot, this.set);
		RenderTargetManager* targets = RenderTargetManager.Instance();
		slot->Target = targets == null ? 0 : (nint)targets->SwapChainBackBuffer;
		this.Status = RenderCallback.Queue(&FrameRenderer.OnRender, (nint)slot, sortKey) ? "Capturing" : "Failed to queue the capture";
	}

	public void Stop()
	{
		this.process16.Disable();
		this.process32.Disable();
		this.draw2D.Disable();
		this.pushClipRect.Disable();
		this.pushBack.Disable();
	}

	public void Dispose()
	{
		this.process16.Dispose();
		this.process32.Dispose();
		this.draw2D.Dispose();
		this.pushClipRect.Dispose();
		this.pushBack.Dispose();
		this.slots.Dispose();
		NativeMemory.Free(this.listCopy);
		NativeMemory.Free(this.marker);
	}
}

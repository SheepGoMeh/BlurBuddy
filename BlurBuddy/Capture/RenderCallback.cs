using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.Interop;

namespace BlurBuddy.Capture;

/// <summary>
/// Kernel render command 0xE: the render thread calls Function(Argument) in queue order
/// Not in ClientStructs' RenderCommandType, layout from ImmediateContext.ExecuteCommands
/// </summary>
public static unsafe class RenderCallback
{
	private const int CallbackType = 0xE;
	// Bit 0 runs SetDefaultState around the call; the UI replay binds its target once, so leave the state alone
	private const byte Flags = 0;

	[StructLayout(LayoutKind.Explicit, Size = 0x20)]
	private struct CallbackCommand
	{
		[FieldOffset(0x00)] public int Type;
		[FieldOffset(0x08)] public delegate* unmanaged<nint, void> Function;
		[FieldOffset(0x10)] public nint Argument;
		[FieldOffset(0x18)] public byte Flags;
	}

	/// <summary>Main thread; commands run by view, then sort key; the context's current ones unless given</summary>
	public static bool Queue(delegate* unmanaged<nint, void> function, nint argument, uint? sortKey = null, int? view = null)
	{
		Context* context = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
		if (context == null)
			return false;

		CallbackCommand* command = (CallbackCommand*)context->AllocateCommand((ulong)sizeof(CallbackCommand));
		if (command == null)
			return false;

		command->Type = CallbackType;
		command->Function = function;
		command->Argument = argument;
		command->Flags = Flags;

		uint contextKey = context->SortKey;
		int contextView = context->ViewIndex;
		if (sortKey != null)
			context->SortKey = sortKey.Value;
		if (view != null)
			context->ViewIndex = view.Value;
		context->PushBackCommand(command);
		context->SortKey = contextKey;
		context->ViewIndex = contextView;
		return true;
	}
}

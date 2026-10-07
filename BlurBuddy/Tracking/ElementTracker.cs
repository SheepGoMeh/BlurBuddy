using System;
using System.Collections.Generic;
using System.Numerics;

using BlurBuddy.Capture;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace BlurBuddy.Tracking;

/// <summary>
/// Main thread: screen rectangles to blur for the frame being captured
/// </summary>
public sealed unsafe class ElementTracker(BlurBuddyConfiguration configuration)
{
	private const float FullScreenShare = 0.9f; // unlisted fades and loading screens stay clear
	private const int NamePlateCount = 50;
	private const int ComponentTypeBase = 1000; // raw node types of component nodes
	private const int MaxTreeDepth = 64;
	// VisibilityFlags bits are hide reasons (alliance lists outside an alliance), RaptureAtkModule.Draw2D masks 0xFFF
	private const ushort HiddenReasons = 0xFFF;
	private const byte Draw2DFlags = 0x14; // RaptureAtkModule.Draw2D, no CS names
	private const float MergeGap = 8.0f; // pixels between pieces of one addon that still join
	private const uint InvalidEntityId = 0xE0000000;

	private readonly List<string> loadedModules = [];
	private readonly List<(ScreenRect Rect, string Name)> lastRects = [];
	private readonly HashSet<nint> drawnUnits = [];
	private readonly TreeWalk collectWalk = new(false);
	private readonly TreeWalk pickWalk = new(true);
	private enum PlateOwner : byte
	{
		None, // NPCs, not blurred
		Player, // players: their name line (name and FC tag)
		Named, // retainers, mannequins: a label line and their 《name》 line, else the whole plate
		Owned, // minions, pets, chocobos: their name and the 《owner》 line, else nothing identifies
	}

	private readonly PlateOwner[] plateOwners = new PlateOwner[NamePlateCount];

	public string Status { get; private set; } = "";

	public IReadOnlyList<string> LoadedModules => this.loadedModules;

	/// <summary>Rectangles of the last capture with their addon, for the debug overlay</summary>
	public IReadOnlyList<(ScreenRect Rect, string Name)> LastRects => this.lastRects;

	public void Collect(FrameSlot* slot, CaptureSet set)
	{
		this.lastRects.Clear();
		if (set == CaptureSet.None)
			return;

		try
		{
			Device* device = Device.Instance();
			float width = device->Width, height = device->Height;
			if (configuration.BlurNameplates)
				this.CollectNameplates(slot, width, height);
			if (set == CaptureSet.Ui)
				this.CollectModules(slot, width, height);
			this.Status = slot->WholeFrame != 0 ? "Too many elements, blurring the whole frame" : "";
		}
		catch (Exception e)
		{
			slot->WholeFrame = 1;
			this.Status = $"Tracking failed, blurring the whole frame: {e.Message}";
		}
	}

	private void Add(FrameSlot* slot, ScreenRect rect, string name)
	{
		if (rect.IsEmpty)
			return;

		FrameSlots.TryAdd(slot, rect.Left, rect.Top, rect.Right, rect.Bottom);
		this.lastRects.Add((rect, name));
	}

	private void CollectNameplates(FrameSlot* slot, float width, float height)
	{
		AddonNamePlate* addon = (AddonNamePlate*)RaptureAtkUnitManager.Instance()->GetAddonByName("NamePlate");
		if (addon == null)
			throw new InvalidOperationException("NamePlate addon missing");
		if (!addon->AtkUnitBase.IsVisible)
			return;

		this.MarkPlayerPlates();
		for (int i = 0; i < NamePlateCount; i++)
		{
			AddonNamePlate.NamePlateObject* plate = addon->NamePlateObjectArray + i;
			PlateOwner owner = this.plateOwners[i];
			if (owner == PlateOwner.None || !IsShown((AtkResNode*)plate->RootComponentNode))
				continue;

			// The collision node is the game's click area, sized to the baked text; markers (target arrow) identify no one
			ScreenRect text = NodeRect((AtkResNode*)plate->NameplateCollision);
			// Unmeasurable text: the whole plate
			ScreenRect? lineRect = configuration.OnlyPlayerNames ? IdentifyingRect(plate, owner, text) : null;
			ScreenRect rect = lineRect ??
			                  text.Union(NodeRect((AtkResNode*)plate->NameIcon)).Union(NodeRect((AtkResNode*)plate->GaugeContainer));
			this.Add(slot, rect.Inflate(configuration.NameplatePadding).Clamp(width, height), lineRect != null ? "name line" : "plate");
		}
	}

	/// <summary>
	/// The identifying line of the plate's text block (see NameLayout), its height share measured per line;
	/// the plate is baked with per part fonts and no part positions exist. Empty when an owned object's plate has
	/// no owner line (a minion showing only its own name), null when it cannot be measured.
	/// </summary>
	private static ScreenRect? IdentifyingRect(AddonNamePlate.NamePlateObject* plate, PlateOwner owner, ScreenRect area)
	{
		AtkTextNode* node = plate->NameText;
		if (node == null || area.IsEmpty)
			return null;

		ReadOnlySpan<byte> text = node->NodeText.AsSpan();
		if (NameLayout.IdentifyingLine(text, owner != PlateOwner.Player) is not { } line)
			return owner == PlateOwner.Owned ? ScreenRect.Empty : area; // only a pet's own name is safe to keep

		float above = 0, height = 0, total = 0;
		int start = 0;
		for (int index = 0; index < line.Count; index++)
		{
			int length = text[start..].IndexOf((byte)0x0A);
			int end = length < 0 ? text.Length : start + length;
			float lineHeight = Measure(node, text[start..end]).Height;
			if (index < line.Index)
				above += lineHeight;
			else if (index == line.Index)
				height = lineHeight;
			total += lineHeight;
			start = end + 1;
		}

		if (total <= 0 || height <= 0)
			return null;

		float scale = (area.Bottom - area.Top) / total;
		float top = area.Top + (above * scale);
		return new ScreenRect(area.Left, top, area.Right, top + (height * scale));
	}

	/// <summary>Draw size of a piece of SeString in the node's font, copied so it is measured whole</summary>
	private static (ushort Width, ushort Height) Measure(AtkTextNode* node, ReadOnlySpan<byte> piece)
	{
		if (piece.IsEmpty)
			return (0, 0);

		byte* buffer = stackalloc byte[piece.Length + 1];
		piece.CopyTo(new Span<byte>(buffer, piece.Length));
		buffer[piece.Length] = 0;
		ushort width, height;
		node->GetTextDrawSize(&width, &height, buffer);
		return (width, height);
	}

	/// <summary>Nameplates of players and of what they own (minions, pets, battle pets, chocobos), and retainers</summary>
	private void MarkPlayerPlates()
	{
		Array.Clear(this.plateOwners);
		UI3DModule* module = UIModule.Instance()->GetUI3DModule();
		if (module == null)
			return;

		for (int i = 0; i < module->NamePlateObjectInfoCount; i++)
		{
			UI3DModule.ObjectInfo* info = module->NamePlateObjectInfoPointers[i].Value;
			if (info == null || info->GameObject == null || info->NamePlateIndex >= NamePlateCount)
				continue;

			this.plateOwners[info->NamePlateIndex] = OwnerOf(info);
		}
	}

	/// <summary>Players; retainers, mannequins and what players own (minions through CompanionOwnerId, pets and chocobos through OwnerId)</summary>
	private static PlateOwner OwnerOf(UI3DModule.ObjectInfo* info)
	{
		GameObject* gameObject = info->GameObject;
		if (gameObject->ObjectKind == ObjectKind.Pc)
			return PlateOwner.Player;
		if (gameObject->ObjectKind == ObjectKind.Retainer || info->NamePlateObjectKind == UIObjectKind.Retainer)
			return PlateOwner.Named;
		if (gameObject->ObjectKind == ObjectKind.EventNpc)
			return HasCustomName(gameObject) ? PlateOwner.Named : PlateOwner.None;

		uint owner = gameObject->ObjectKind == ObjectKind.Companion ? ((Character*)gameObject)->CompanionOwnerId : gameObject->OwnerId;
		return IsEntity(owner) ? PlateOwner.Owned : PlateOwner.None;
	}

	/// <summary>Event NPC retainers (housing) show a player chosen name instead of their ENpcResident one</summary>
	private static bool HasCustomName(GameObject* gameObject)
	{
		if (!ResidentNames.TryGetValue(gameObject->BaseId, out string? residentName))
		{
			ENpcResident? resident = Service.DataManager.GetExcelSheet<ENpcResident>().GetRowOrDefault(gameObject->BaseId);
			residentName = resident?.Singular.ExtractText() ?? "";
			ResidentNames[gameObject->BaseId] = residentName;
		}

		return !gameObject->NameString.Equals(residentName, StringComparison.OrdinalIgnoreCase);
	}

	private static readonly Dictionary<uint, string> ResidentNames = [];

	private static bool IsEntity(uint entityId) => entityId != 0 && entityId != InvalidEntityId;

	private void CollectModules(FrameSlot* slot, float width, float height)
	{
		AtkUnitManager* manager = &RaptureAtkUnitManager.Instance()->AtkUnitManager;
		this.loadedModules.Clear();
		for (int i = 0; i < manager->AllLoadedUnitsList.Count; i++)
		{
			AtkUnitBase* unit = manager->AllLoadedUnitsList.Entries[i].Value;
			if (unit != null)
				this.loadedModules.Add(unit->NameString);
		}

		ScreenRect screen = new(0, 0, width, height);
		this.CollectDrawnUnits(manager);
		foreach (nint pointer in this.drawnUnits)
		{
			AtkUnitBase* unit = (AtkUnitBase*)pointer;
			string name = unit->NameString;

			TreeWalk walk = this.collectWalk;
			walk.Begin(unit, name, configuration);
			if (walk.Rules.IsUniform && !walk.Rules.Blur)
				continue;

			// What the addon draws: its root box is often larger, a union of distant pieces too
			Walk(walk, unit->RootNode, 0, screen, NodePath.Root, walk.Rules.Blur);
			foreach (ScreenRect rect in ScreenRect.Merge(walk.Pieces, MergeGap))
			{
				// Fades and loading screens only when listed
				if (!walk.Rules.HasRules && rect.Area >= width * height * FullScreenShare)
					continue;
				this.Add(slot, rect, name);
			}
		}
	}

	/// <summary>Smallest drawn node under the mouse</summary>
	public PickedNode? Pick(Vector2 mouse)
	{
		TreeWalk walk = this.pickWalk;
		walk.Picked = null;
		walk.Mouse = mouse;
		Device* device = Device.Instance();
		ScreenRect screen = new(0, 0, device->Width, device->Height);
		this.CollectDrawnUnits(&RaptureAtkUnitManager.Instance()->AtkUnitManager);
		foreach (nint pointer in this.drawnUnits)
		{
			AtkUnitBase* unit = (AtkUnitBase*)pointer;
			string name = unit->NameString;
			walk.Begin(unit, name, configuration);
			Walk(walk, unit->RootNode, 0, screen, NodePath.Root, walk.Rules.Blur);
		}

		return walk.Picked;
	}

	/// <summary>Draw2D's depth layer lists and the loaded list, so neither a closing nor a child addon slips out; nameplates go per plate</summary>
	private void CollectDrawnUnits(AtkUnitManager* manager)
	{
		this.drawnUnits.Clear();
		foreach (ref AtkUnitList layer in manager->DepthLayers)
			this.AddDrawn(ref layer);
		this.AddDrawn(ref manager->AllLoadedUnitsList);
	}

	private void AddDrawn(ref AtkUnitList list)
	{
		for (int i = 0; i < list.Count; i++)
		{
			AtkUnitBase* unit = list.Entries[i].Value;
			if (unit != null && unit->RootNode != null && IsDrawn(unit) && unit->Alpha != 0 && unit->NameString != "NamePlate")
				this.drawnUnits.Add((nint)unit);
		}
	}

	/// <summary>
	/// Draw2D's own test, or shown, or still in its close transition (Show clears when it starts);
	/// without a hide reason (alliance lists outside an alliance)
	/// </summary>
	private static bool IsDrawn(AtkUnitBase* unit)
	{
		AtkUnitBaseVisibilityState state = unit->VisibilityState;
		bool drawn = (unit->Flags1A0 & Draw2DFlags) == Draw2DFlags ||
		             state.HasFlag(AtkUnitBaseVisibilityState.Show) ||
		             (state & (AtkUnitBaseVisibilityState.Hide | AtkUnitBaseVisibilityState.TransitionComplete)) == AtkUnitBaseVisibilityState.Hide;
		return drawn && (unit->VisibilityFlags & HiddenReasons) == 0;
	}

	/// <summary>
	/// Draw tree (child, then previous siblings) so plugin linked nodes (DTR) count too;
	/// hidden or transparent nodes hide their subtree, rules apply to their subtree
	/// </summary>
	private static void Walk(TreeWalk walk, AtkResNode* first, int depth, ScreenRect clip, ulong path, bool blur)
	{
		if (depth > MaxTreeDepth)
			return;

		// A clipping mask cuts the nodes beside it (the minimap's round window over its map)
		for (AtkResNode* node = first; node != null; node = node->PrevSiblingNode)
		{
			if (node->Type == NodeType.ClippingMask && node->IsVisible())
				clip = clip.Intersect(Bounds(node));
		}

		for (AtkResNode* node = first; node != null; node = node->PrevSiblingNode)
		{
			if (!node->IsVisible() || node->Color.A == 0)
				continue;

			ulong nodePath = NodePath.Append(path, node->NodeId);
			bool nodeBlur = walk.Rules.Nodes.TryGetValue(nodePath, out bool ruled) ? ruled
				: walk.PartNodes.TryGetValue((nint)node, out bool part) ? part
				: blur;
			walk.Enter(node);

			ushort type = (ushort)node->Type;
			if (type >= ComponentTypeBase)
			{
				// Components mask their content to their own box (the minimap's 2048 px map, scrolled lists)
				AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
				if (component != null)
					Walk(walk, component->UldManager.RootNode, depth + 1, clip.Intersect(Bounds(node)), nodePath, nodeBlur);
			}
			else
			{
				if (type is (ushort)NodeType.Image or (ushort)NodeType.Text or (ushort)NodeType.NineGrid or (ushort)NodeType.Counter &&
				    DrawsSomething(node, type))
				{
					ScreenRect rect = Bounds(node).Intersect(clip);
					if (!rect.IsEmpty)
						walk.Piece(rect, nodeBlur);
				}

				Walk(walk, node->ChildNode, depth + 1, ClipFor(node, clip), nodePath, nodeBlur);
			}

			walk.Leave();
		}
	}

	/// <summary>One addon's rules with its blurred pieces, or the picker's best hit</summary>
	private sealed class TreeWalk(bool picking)
	{
		public readonly AddonRules Rules = new();
		public readonly Dictionary<nint, bool> PartNodes = [];
		public readonly List<ScreenRect> Pieces = [];
		private readonly List<nint> partBuffer = [];
		private readonly List<uint> ids = [];
		private readonly List<nint> chain = [];
		private string addon = "";

		public Vector2 Mouse;
		public PickedNode? Picked;

		public void Begin(AtkUnitBase* unit, string name, BlurBuddyConfiguration configuration)
		{
			this.addon = name;
			this.Pieces.Clear();
			this.PartNodes.Clear();
			this.Rules.Resolve(configuration.Rules, configuration.BlurUnlisted, name);
			foreach ((string id, bool blur) in this.Rules.KnownParts)
			{
				this.partBuffer.Clear();
				UiParts.Find(name, id)?.Resolve(unit, this.partBuffer);
				foreach (nint node in this.partBuffer)
				{
					if (node != 0)
						this.PartNodes[node] = blur;
				}
			}
		}

		public void Enter(AtkResNode* node)
		{
			if (!picking)
				return;
			this.ids.Add(node->NodeId);
			this.chain.Add((nint)node);
		}

		public void Leave()
		{
			if (!picking)
				return;
			this.ids.RemoveAt(this.ids.Count - 1);
			this.chain.RemoveAt(this.chain.Count - 1);
		}

		public void Piece(ScreenRect rect, bool blur)
		{
			if (!picking)
			{
				if (blur)
					this.Pieces.Add(rect);
				return;
			}

			if (this.Mouse.X < rect.Left || this.Mouse.X >= rect.Right || this.Mouse.Y < rect.Top || this.Mouse.Y >= rect.Bottom)
				return;
			if (this.Picked is { } best && best.Area <= rect.Area)
				return;
			this.Picked = new PickedNode(this.addon, [.. this.ids], [.. this.chain], rect.Area);
		}
	}

	/// <summary>Children of a Clip node are cut to its bounds (masked content like the minimap map)</summary>
	private static ScreenRect ClipFor(AtkResNode* node, ScreenRect clip) =>
		node->NodeFlags.HasFlag(NodeFlags.Clip) ? clip.Intersect(Bounds(node)) : clip;

	/// <summary>Text with characters, images with a texture</summary>
	private static bool DrawsSomething(AtkResNode* node, ushort type) => type switch
	{
		(ushort)NodeType.Text => ((AtkTextNode*)node)->NodeText.Length > 0,
		(ushort)NodeType.Image => ((AtkImageNode*)node)->PartsList != null,
		(ushort)NodeType.NineGrid => ((AtkNineGridNode*)node)->PartsList != null,
		_ => true,
	};

	/// <summary>Visible and not transparent along the whole parent chain</summary>
	private static bool IsShown(AtkResNode* node)
	{
		if (node == null)
			return false;

		for (AtkResNode* n = node; n != null; n = n->ParentNode)
		{
			if (!n->IsVisible() || n->Color.A == 0)
				return false;
		}

		return true;
	}

	/// <summary>Screen bounds, empty when not shown along its parent chain</summary>
	private static ScreenRect NodeRect(AtkResNode* node) => IsShown(node) ? Bounds(node) : ScreenRect.Empty;

	/// <summary>Screen bounds with the parent scale chain</summary>
	public static ScreenRect Bounds(AtkResNode* node)
	{
		if (node->Width == 0 || node->Height == 0)
			return ScreenRect.Empty;

		float scaleX = 1, scaleY = 1;
		for (AtkResNode* n = node; n != null; n = n->ParentNode)
		{
			scaleX *= n->ScaleX;
			scaleY *= n->ScaleY;
		}

		return new ScreenRect(node->ScreenX, node->ScreenY, node->ScreenX + (node->Width * scaleX),
			node->ScreenY + (node->Height * scaleY));
	}
}

/// <summary>Addon, NodeId path and the nodes along it, root first</summary>
public sealed record PickedNode(string Addon, uint[] Ids, nint[] Nodes, float Area);

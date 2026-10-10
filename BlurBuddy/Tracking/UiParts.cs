using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BlurBuddy.Tracking;

/// <summary>Identifying addon parts through CS fields</summary>
public static unsafe class UiParts
{
	public delegate void Resolver(AtkUnitBase* unit, List<nint> into);

	public sealed record Part(string Addon, string Id, string Label, Resolver Resolve);

	private const int MaxDepth = 8;

	// Target info: _TargetInfo when its parts are combined, _TargetInfoMainTarget when split; both show the target of target
	public static readonly Part[] All =
	[
		new("_PartyList", "@MemberNames", "Party member names", PartyMemberNames),
		new("_TargetInfo", "@TargetPlayer", "Target's name (players)", TargetPlayerName),
		new("_TargetInfo", "@TargetOfTargetPlayer", "Target of target's name (players)", TargetOfTargetPlayerName),
		new("_TargetInfoMainTarget", "@TargetPlayer", "Target's name (players)", TargetPlayerName),
		new("_TargetInfoMainTarget", "@TargetOfTargetPlayer", "Target of target's name (players)", TargetOfTargetPlayerName),
	];

	public static Part? Find(string addon, string id)
	{
		foreach (Part part in All)
		{
			if (part.Addon == addon && part.Id == id)
				return part;
		}

		return null;
	}

	private static void PartyMemberNames(AtkUnitBase* unit, List<nint> into)
	{
		foreach (AddonPartyList.PartyListMemberStruct member in ((AddonPartyList*)unit)->PartyMembers)
			into.Add((nint)member.Name);
	}

	/// <summary>The target bar shows the hard target, the soft target without one</summary>
	private static GameObject* Target()
	{
		TargetSystem* targets = TargetSystem.Instance();
		if (targets == null)
			return null;
		return targets->Target != null ? targets->Target : targets->SoftTarget;
	}

	private static void TargetPlayerName(AtkUnitBase* unit, List<nint> into) => PlayerNameNodes(unit, Target(), into);

	private static void TargetOfTargetPlayerName(AtkUnitBase* unit, List<nint> into)
	{
		GameObject* target = Target();
		if (target == null || !target->IsCharacter())
			return;

		GameObjectManager* objects = GameObjectManager.Instance();
		PlayerNameNodes(unit, objects == null ? null : objects->Objects.GetObjectByGameObjectId(((Character*)target)->GetTargetId()), into);
	}

	/// <summary>
	/// Text nodes showing a player's name: no CS fields for these addons, the names are matched instead; enemies and NPCs
	/// never match, so they follow the addon's own rule
	/// </summary>
	private static void PlayerNameNodes(AtkUnitBase* unit, GameObject* gameObject, List<nint> into)
	{
		if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
			return;

		string[] names = NameForms(gameObject->NameString);
		if (names.Length > 0)
			TextNodes(&unit->UldManager, names, into, 0);
	}

	/// <summary>The name in each name display setting: full, surname or forename abbreviated, initials</summary>
	private static string[] NameForms(string name)
	{
		string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2)
			return parts.Length == 0 ? [] : [name];

		string first = parts[0], last = parts[1];
		return [name, $"{first} {last[0]}.", $"{first[0]}. {last}", $"{first[0]}. {last[0]}."];
	}

	private static void TextNodes(AtkUldManager* manager, string[] names, List<nint> into, int depth)
	{
		if (depth > MaxDepth)
			return;

		for (int i = 0; i < manager->NodeListCount; i++)
		{
			AtkResNode* node = manager->NodeList[i];
			if (node == null)
				continue;

			if ((ushort)node->Type >= 1000)
			{
				AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
				if (component != null)
					TextNodes(&component->UldManager, names, into, depth + 1);
			}
			else if (node->Type == NodeType.Text && Shows(((AtkTextNode*)node)->NodeText.ToString(), names))
			{
				into.Add((nint)node);
			}
		}
	}

	private static bool Shows(string text, string[] names)
	{
		foreach (string name in names)
		{
			if (text.Contains(name, StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}

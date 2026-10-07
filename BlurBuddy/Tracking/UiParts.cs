using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BlurBuddy.Tracking;

/// <summary>Identifying addon parts through CS fields</summary>
public static unsafe class UiParts
{
	public delegate void Resolver(AtkUnitBase* unit, List<nint> into);

	public sealed record Part(string Addon, string Id, string Label, Resolver Resolve);

	public static readonly Part[] All =
	[
		new("_PartyList", "@MemberNames", "Party member names", PartyMemberNames),
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
}

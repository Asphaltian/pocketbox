namespace sGBA;

public static class SessionVisibilityInfo
{
	public static string Icon( SessionVisibility v ) => v switch
	{
		SessionVisibility.Public => "public",
		SessionVisibility.FriendsOnly => "people",
		SessionVisibility.InviteOnly => "lock",
		_ => "help"
	};

	public static string NameKey( SessionVisibility v ) => v switch
	{
		SessionVisibility.Public => "#hostwireless.vis.public",
		SessionVisibility.FriendsOnly => "#hostwireless.vis.friends",
		SessionVisibility.InviteOnly => "#hostwireless.vis.invite",
		_ => string.Empty
	};

	public static string HintKey( SessionVisibility v ) => v switch
	{
		SessionVisibility.Public => "#hostwireless.vis.public.hint",
		SessionVisibility.FriendsOnly => "#hostwireless.vis.friends.hint",
		SessionVisibility.InviteOnly => "#hostwireless.vis.invite.hint",
		_ => string.Empty
	};

	public static string ShortLabel( SessionVisibility v ) => v switch
	{
		SessionVisibility.Public => "Public",
		SessionVisibility.FriendsOnly => "Friends",
		SessionVisibility.InviteOnly => "Invite",
		_ => string.Empty
	};
}

// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Identifies which rate limit bucket a request belongs to.
	/// </summary>
	/// <remarks>
	/// Discord rate limits per *route*, where a route is the method plus the endpoint template plus its **major
	/// parameters** - channel id, guild id and webhook id. Anything else in the path, a message id for instance, is
	/// not part of the identity: every edit in one channel shares a bucket regardless of which message is edited.
	///
	/// The server also returns an <c>X-RateLimit-Bucket</c> hash, and two routes can map to the same bucket. This
	/// type does not attempt to merge them. Getting it wrong that way costs an occasional 429, which is handled; the
	/// alternative - inferring shared buckets from a hash we have only seen for one route so far - risks throttling
	/// traffic that was never limited.
	/// </remarks>
	/// <param name="Key">Stable identity of the route, including its major parameters.</param>
	/// <param name="ExemptFromGlobalLimit">
	/// Whether the route is outside the per-token global request ceiling. True only for interaction responses.
	/// </param>
	public readonly record struct DiscordRoute(string Key, bool ExemptFromGlobalLimit = false)
	{
		/// <summary>
		/// Posting a message to a channel.
		/// </summary>
		/// <param name="channelId">Channel snowflake. A major parameter, so each channel gets its own bucket.</param>
		public static DiscordRoute CreateMessage(string channelId)
			=> new DiscordRoute($"POST /channels/{channelId}/messages");

		/// <summary>
		/// Editing a message already posted to a channel.
		/// </summary>
		/// <param name="channelId">Channel snowflake.</param>
		public static DiscordRoute EditMessage(string channelId)
			=> new DiscordRoute($"PATCH /channels/{channelId}/messages/:id");

		/// <summary>
		/// Deleting a message.
		/// </summary>
		/// <param name="channelId">Channel snowflake.</param>
		public static DiscordRoute DeleteMessage(string channelId)
			=> new DiscordRoute($"DELETE /channels/{channelId}/messages/:id");

		/// <summary>
		/// Opening a DM channel with a user.
		/// </summary>
		/// <remarks>Not parameterised by user: Discord buckets this route as a whole.</remarks>
		public static DiscordRoute CreateDirectMessageChannel()
			=> new DiscordRoute("POST /users/@me/channels");

		/// <summary>
		/// Asking where the gateway is.
		/// </summary>
		public static DiscordRoute GetGatewayBot()
			=> new DiscordRoute("GET /gateway/bot");

		/// <summary>
		/// Responding to an interaction.
		/// </summary>
		/// <remarks>
		/// Exempt from the global limit, which is the point of modelling it separately: triage buttons stay
		/// responsive while a broken stream is saturating the notification path.
		/// </remarks>
		/// <param name="interactionId">Interaction snowflake.</param>
		public static DiscordRoute InteractionCallback(string interactionId)
			=> new DiscordRoute($"POST /interactions/{interactionId}/callback", true);
	}
}

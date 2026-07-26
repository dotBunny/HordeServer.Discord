// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Opcodes carried in the <c>op</c> field of every gateway frame.
	/// </summary>
	public static class DiscordGatewayOpcode
	{
		/// <summary>An event. The only opcode that carries <c>t</c> and <c>s</c>.</summary>
		public const int Dispatch = 0;

		/// <summary>A heartbeat. Sent both ways - the server may ask for one early.</summary>
		public const int Heartbeat = 1;

		/// <summary>Starts a new session. Sent by us.</summary>
		public const int Identify = 2;

		/// <summary>Replays a dropped session. Sent by us.</summary>
		public const int Resume = 6;

		/// <summary>The server wants the connection re-established. The session survives.</summary>
		public const int Reconnect = 7;

		/// <summary>The session is not usable. <c>d</c> says whether a resume would be.</summary>
		public const int InvalidSession = 9;

		/// <summary>First frame on any connection, carrying the heartbeat interval.</summary>
		public const int Hello = 10;

		/// <summary>Acknowledges one of our heartbeats. Its absence is how a dead connection is detected.</summary>
		public const int HeartbeatAck = 11;
	}

	/// <summary>
	/// What to do with the session after a connection ends.
	/// </summary>
	public enum DiscordGatewayRecovery
	{
		/// <summary>Reconnect and replay the existing session with <c>RESUME</c>.</summary>
		Resume,

		/// <summary>Reconnect, but the session is gone - start a new one with <c>IDENTIFY</c>.</summary>
		Reidentify,

		/// <summary>Do not reconnect. Something is wrong that reconnecting cannot fix.</summary>
		Fatal,
	}

	/// <summary>
	/// The decisions the gateway makes, separated from the socket that provokes them.
	/// </summary>
	/// <remarks>
	/// Pure functions over a close code and an attempt count, for the same reason <see cref="IDiscordClock"/> exists:
	/// a reconnect policy tested through a real socket is tested by waiting, and the interesting cases - authentication
	/// failure, an expired session, a resume that must not become an identify - are exactly the ones that are hard to
	/// provoke on demand.
	/// </remarks>
	public static class DiscordGatewayPolicy
	{
		/// <summary>
		/// Shortest wait before reconnecting.
		/// </summary>
		public static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(1.0);

		/// <summary>
		/// Longest wait before reconnecting, however many attempts have failed.
		/// </summary>
		/// <remarks>
		/// A build farm that cannot reach Discord should keep trying quietly rather than give up - the notifications
		/// still post over REST, and only interactive triage is degraded - but it should not retry in a tight loop.
		/// </remarks>
		public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(2.0);

		/// <summary>
		/// Decides what a close code means for the session.
		/// </summary>
		/// <remarks>
		/// The three fatal ones are worth knowing by sight, because each is a configuration mistake that no amount of
		/// reconnecting will fix and all three look identical from the outside: <c>4004</c> is a bad bot token,
		/// <c>4013</c> is an intent that does not exist, and <c>4014</c> is a privileged intent the application has
		/// not been granted in the developer portal.
		///
		/// <c>4007</c> and <c>4009</c> are the subtle pair. Both are recoverable, but the session behind them is not:
		/// resuming with a sequence the server has forgotten just earns another <c>4007</c>.
		/// </remarks>
		/// <param name="closeCode">Close code the socket reported, or null if it closed without one.</param>
		/// <returns>What to do next.</returns>
		public static DiscordGatewayRecovery Classify(int? closeCode)
			=> closeCode switch
			{
				// Authentication failed, invalid shard, sharding required, invalid API version, invalid or disallowed
				// intents. All of these are us being wrong, not the network.
				4004 or 4010 or 4011 or 4012 or 4013 or 4014 => DiscordGatewayRecovery.Fatal,

				// Invalid sequence, and session timed out. Reconnect, but the session is unusable.
				4007 or 4009 => DiscordGatewayRecovery.Reidentify,

				// Everything else - including a socket that dropped with no close code at all, which is what a lost
				// network looks like - is a candidate for a resume.
				_ => DiscordGatewayRecovery.Resume,
			};

		/// <summary>
		/// Whether a close code means the plugin should stop trying.
		/// </summary>
		/// <param name="closeCode">Close code the socket reported.</param>
		public static bool IsFatal(int? closeCode) => Classify(closeCode) == DiscordGatewayRecovery.Fatal;

		/// <summary>
		/// How long to wait before the given reconnect attempt.
		/// </summary>
		/// <remarks>
		/// Exponential from <see cref="MinimumBackoff"/>, capped at <see cref="MaximumBackoff"/>, then scaled by a
		/// caller-supplied jitter fraction. The jitter is a parameter rather than an internal <c>Random</c> so the
		/// curve can be asserted exactly; only the caller needs a random number.
		/// </remarks>
		/// <param name="attempt">Consecutive failed attempts so far. The first reconnect is attempt 0.</param>
		/// <param name="jitter">Fraction in [0, 1). The delay is scaled to between half and all of the computed value.</param>
		/// <returns>How long to wait.</returns>
		public static TimeSpan BackoffFor(int attempt, double jitter)
		{
			// Shifting rather than Math.Pow, and clamped before the shift: 1 << 62 is still a number, and
			// TimeSpan.FromSeconds of it is not.
			int exponent = Math.Clamp(attempt, 0, 16);
			double seconds = MinimumBackoff.TotalSeconds * (1 << exponent);

			seconds = Math.Min(seconds, MaximumBackoff.TotalSeconds);

			// Half to full, the "full jitter" shape - never zero, so a server refusing connections instantly cannot
			// become a spin loop, and never longer than the cap.
			return TimeSpan.FromSeconds(seconds * (0.5 + (Math.Clamp(jitter, 0.0, 1.0) * 0.5)));
		}

		/// <summary>
		/// How long to wait before the first heartbeat of a connection.
		/// </summary>
		/// <remarks>
		/// Discord asks for the first beat to be offset by a random fraction of the interval, so that a fleet of bots
		/// reconnecting after an outage does not heartbeat in lockstep. Every beat after this one is on the interval.
		/// </remarks>
		/// <param name="interval">Heartbeat interval from the <c>HELLO</c> frame.</param>
		/// <param name="jitter">Fraction in [0, 1).</param>
		public static TimeSpan FirstHeartbeatDelay(TimeSpan interval, double jitter)
			=> interval * Math.Clamp(jitter, 0.0, 1.0);
	}

	/// <summary>
	/// One frame off the gateway socket.
	/// </summary>
	/// <remarks>
	/// <see cref="Data"/> stays a <see cref="JsonElement"/> because its shape depends entirely on
	/// <see cref="EventName"/>, and the gateway only reads a handful of fields out of a handful of events. Modelling
	/// the rest would be a large amount of code that exists to be ignored.
	/// </remarks>
	public sealed class DiscordGatewayFrame
	{
		/// <summary>
		/// Opcode. See <see cref="DiscordGatewayOpcode"/>.
		/// </summary>
		[JsonPropertyName("op")]
		public int Op { get; set; }

		/// <summary>
		/// Payload, whose shape depends on the opcode and event name.
		/// </summary>
		[JsonPropertyName("d")]
		public JsonElement Data { get; set; }

		/// <summary>
		/// Sequence number, present on dispatches only. Sent back with every heartbeat and with a resume.
		/// </summary>
		[JsonPropertyName("s")]
		public int? Sequence { get; set; }

		/// <summary>
		/// Event name, present on dispatches only - <c>READY</c>, <c>INTERACTION_CREATE</c> and so on.
		/// </summary>
		[JsonPropertyName("t")]
		public string? EventName { get; set; }
	}

	/// <summary>
	/// A dispatch handed to whoever is listening.
	/// </summary>
	/// <param name="EventName">Discord's event name, such as <c>INTERACTION_CREATE</c>.</param>
	/// <param name="Data">The event body.</param>
	public sealed record DiscordGatewayDispatch(string EventName, JsonElement Data);
}

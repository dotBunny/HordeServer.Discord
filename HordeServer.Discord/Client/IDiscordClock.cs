// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// The passage of time, as the Discord client sees it.
	/// </summary>
	/// <remarks>
	/// A seam rather than direct calls to <see cref="DateTime.UtcNow"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>,
	/// so rate limiting can be tested for what it decides rather than for how long it sleeps. Horde has its own
	/// <c>IClock</c>, but that is a larger interface aimed at shared tickers across server instances and would pull
	/// server infrastructure into what is really just arithmetic on response headers.
	/// </remarks>
	public interface IDiscordClock
	{
		/// <summary>
		/// The current UTC time.
		/// </summary>
		DateTime UtcNow { get; }

		/// <summary>
		/// Waits for the given duration.
		/// </summary>
		/// <param name="delay">How long to wait. Zero or negative returns immediately.</param>
		/// <param name="cancellationToken">Cancellation token for the wait.</param>
		Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
	}

	/// <summary>
	/// The real clock.
	/// </summary>
	public sealed class DiscordSystemClock : IDiscordClock
	{
		/// <summary>
		/// Shared instance. The type is stateless, so there is no reason to have more than one.
		/// </summary>
		public static DiscordSystemClock Instance { get; } = new DiscordSystemClock();

		/// <inheritdoc/>
		public DateTime UtcNow => DateTime.UtcNow;

		/// <inheritdoc/>
		public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
			=> delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
	}
}

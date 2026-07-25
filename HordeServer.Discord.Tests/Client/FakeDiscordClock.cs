// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// A clock that jumps instead of waiting, and remembers every jump it was asked to make.
	/// </summary>
	/// <remarks>
	/// Rate limiting is decision-making, not sleeping. Recording the delays makes those decisions assertable, and
	/// advancing the clock by the delay keeps the limiter's own arithmetic honest - a fake that returned immediately
	/// without moving time would let a wait loop spin forever.
	/// </remarks>
	sealed class FakeDiscordClock : IDiscordClock
	{
		public DateTime UtcNow { get; private set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

		public TimeSpan TotalDelay => Delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay);

		public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (delay > TimeSpan.Zero)
			{
				Delays.Add(delay);
				UtcNow += delay;
			}

			return Task.CompletedTask;
		}

		public void Advance(TimeSpan amount) => UtcNow += amount;
	}
}
